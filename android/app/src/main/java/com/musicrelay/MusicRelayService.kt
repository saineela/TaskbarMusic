package com.musicrelay

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.SharedPreferences
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.service.notification.StatusBarNotification
import android.util.Log
import androidx.core.app.NotificationCompat
import kotlinx.coroutines.*
import org.json.JSONObject

class MusicRelayService : Service() {

    companion object {
        private const val TAG = "MusicRelayService"
        private const val NOTIFICATION_ID = 1001
        private const val CHANNEL_ID = "music_relay_service"
        private const val PREFS_NAME = "music_relay_prefs"
        private const val KEY_WS_URL = "custom_websocket_url"

        // Default WebSocket relay URL (fallback if no custom URL is set)
        private const val DEFAULT_WS_URL =
            "ws://dns.securehomesolutions.uk:8090/?token=98c27f407fb261e51915c1354cbe4e3d218fadb79e5220e9de3cdf525d0ebfe8"

        private const val POSITION_UPDATE_INTERVAL_MS = 2000L
        private const val HEARTBEAT_INTERVAL_MS = 30000L
        private const val SYNC_TIMEOUT_MS = 10000L  // Max wait for schedule from Windows before auto-resume

        var isRunning = false
            private set
    }

    private var wakeLock: PowerManager.WakeLock? = null
    private val serviceScope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    private var webSocketClient: RelayWebSocketClient? = null
    private var mediaMonitor: MediaSessionMonitor? = null

    private var lastArtist = ""
    private var lastTitle = ""
    private var lastAlbum = ""
    private var lastDuration = 0.0
    private var lastPlaying = false  // Track play/pause state for manual pause detection
    private var lastSentPosition = -1.0  // Dedup: only send position if it changed
    private var resumeSyncJob: Job? = null  // Cancellable job for delayed resume sync_start
    @Volatile
    private var isSyncing = false  // True while sync protocol is in progress
    private var roleVerified = false  // True after server confirms role=phone

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        isRunning = true
        MusicRelayTileService.updateTile(this)
        createNotificationChannel()
        startForeground(NOTIFICATION_ID, buildNotification("Connecting…"))
        acquireWakeLock()
        connectWebSocket()
        // Media monitoring starts AFTER WebSocket connects — see onConnected below
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        return START_STICKY
    }

    override fun onDestroy() {
        isRunning = false
        MusicRelayTileService.updateTile(this)
        // Send disconnect notice before shutting down so Windows clears immediately
        webSocketClient?.send("{\"type\":\"disconnect\"}")
        serviceScope.cancel()
        mediaMonitor?.stop()
        webSocketClient?.disconnect()
        releaseWakeLock()
        super.onDestroy()
    }

    private fun startMediaMonitoring() {
        mediaMonitor = MediaSessionMonitor(this) { info ->
            if (info != null) {
                handleMediaUpdate(info)
            } else {
                Log.d(TAG, "No active media session")
            }
        }
        mediaMonitor?.start()
    }

    private fun forceSendCurrentTrack() {
        // Clear last-known track so handleMediaUpdate always sends,
        // then request a fresh media update
        lastArtist = ""
        lastTitle = ""
        lastAlbum = ""
        lastDuration = 0.0
        mediaMonitor?.requestUpdate()
        Log.d(TAG, "Force-sent current track (last cleared)")
    }

    private fun handleMediaUpdate(info: MediaSessionMonitor.MediaInfo) {
        // If a sync is already in progress, don't interfere
        if (isSyncing) return

        // Check if track changed → start sync protocol
        if (info.artist != lastArtist || info.title != lastTitle) {
            lastArtist = info.artist
            lastTitle = info.title
            lastAlbum = info.album
            lastDuration = info.duration
            lastPlaying = info.isPlaying

            Log.d(TAG, "New track detected: ${info.artist} - ${info.title}")
            startSyncProtocol(info)
            return
        }

        // Track didn't change — check for manual pause/resume
        if (info.isPlaying != lastPlaying) {
            lastPlaying = info.isPlaying

            if (!info.isPlaying) {
                // Cancel any pending resume sync (handles rapid pause/resume)
                resumeSyncJob?.cancel()
                resumeSyncJob = null

                // User manually paused → send position freeze
                val pos = mediaMonitor?.getSnapshotPosition() ?: info.position
                val roundedPos = Math.round(pos * 10.0) / 10.0  // Round to 1 decimal
                lastSentPosition = roundedPos
                webSocketClient?.send(buildPositionJson(roundedPos, false))
                Log.d(TAG, "Manual pause at ${roundedPos}s — sent freeze")
            } else {
                // User manually resumed → pause first, then schedule-based resume
                resumeSyncJob?.cancel()
                resumeSyncJob = serviceScope.launch {
                    isSyncing = true
                    try {
                        // 1. Pause immediately — music must not play ahead of Windows
                        mediaMonitor?.pausePlayback()
                        delay(200)  // Let pause settle

                        val pos = mediaMonitor?.getSnapshotPosition() ?: info.position
                        val roundedPos = Math.round(pos * 10.0) / 10.0
                        lastSentPosition = roundedPos
                        updateNotification("Resume — re-syncing")
                        webSocketClient?.send(buildResumeRequestJson(roundedPos))
                        Log.d(TAG, "Manual resume at ${roundedPos}s — paused, sent resume_request")

                        // 2. Wait for schedule from Windows (with timeout)
                        val startMs = System.currentTimeMillis()
                        while (isSyncing && (System.currentTimeMillis() - startMs) < SYNC_TIMEOUT_MS) {
                            delay(200)
                        }
                        if (isSyncing) {
                            Log.w(TAG, "Resume: schedule timeout — auto-resuming")
                            mediaMonitor?.resumePlayback()
                            updateNotification("Connected to relay")
                        }
                    } catch (e: Exception) {
                        Log.e(TAG, "Resume sync error: ${e.message}")
                        try { mediaMonitor?.resumePlayback() } catch (_: Exception) {}
                    } finally {
                        isSyncing = false
                    }
                }
            }
        }

        // Continuous position streaming (every 2s) — only when playing
        if (info.isPlaying && !isSyncing) {
            val pos = mediaMonitor?.getSnapshotPosition() ?: info.position
            val roundedPos = Math.round(pos * 10.0) / 10.0
            // Dedup: only send if position changed by at least 0.1s
            if (Math.abs(roundedPos - lastSentPosition) >= 0.1) {
                lastSentPosition = roundedPos
                webSocketClient?.send(buildPositionJson(roundedPos, true))
            }
        }
    }

    /**
     * Sync protocol (new track):
     * 1. Pause playback
     * 2. Send track metadata to Windows
     * 3. Wait for "schedule" message from Windows (with timeout)
     * 4. On schedule: wait until scheduled time → resume playback
     * 5. On timeout: auto-resume without sync
     */
    private fun startSyncProtocol(info: MediaSessionMonitor.MediaInfo) {
        serviceScope.launch {
            isSyncing = true
            try {
                // 1. Pause and send track
                mediaMonitor?.pausePlayback()
                updateNotification("Syncing — paused")
                Log.d(TAG, "Sync: paused, sending track")

                webSocketClient?.send(buildTrackJson(info.artist, info.title, info.album, info.duration))
                Log.d(TAG, "Sync: track sent — waiting for schedule from Windows")

                // 2. Wait for schedule (handled by onMessage) with timeout
                val startMs = System.currentTimeMillis()
                while (isSyncing && (System.currentTimeMillis() - startMs) < SYNC_TIMEOUT_MS) {
                    delay(200)
                }

                // 3. Timeout fallback — resume without sync
                if (isSyncing) {
                    Log.w(TAG, "Sync: schedule timeout — auto-resuming")
                    mediaMonitor?.resumePlayback()
                    updateNotification("Connected to relay")
                }
            } catch (e: Exception) {
                Log.e(TAG, "Sync error: ${e.message}")
                try { mediaMonitor?.resumePlayback() } catch (_: Exception) {}
            } finally {
                isSyncing = false
            }
        }
    }

    private fun getWsUrl(): String {
        val prefs = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        val customUrl = prefs.getString(KEY_WS_URL, null)
        if (!customUrl.isNullOrBlank()) {
            Log.d(TAG, "Using custom WebSocket URL: $customUrl")
            return customUrl
        }
        Log.d(TAG, "Using default WebSocket URL")
        return DEFAULT_WS_URL
    }

    private fun connectWebSocket() {
        val wsUrl = getWsUrl()
        webSocketClient = RelayWebSocketClient(
            url = wsUrl,
            onConnected = {
                Log.d(TAG, "WebSocket connected — waiting for role verification")
                updateNotification("Connected — verifying role…")
                // Role verified by onMessage handler when server sends connected/role
            },
            onRoleVerified = {
                roleVerified = true
                Log.d(TAG, "Role verified as phone — starting media monitoring")
                updateNotification("Connected to relay")
                if (mediaMonitor == null) {
                    startMediaMonitoring()
                }
                // Force-send current track
                serviceScope.launch {
                    delay(300)
                    forceSendCurrentTrack()
                    if (lastArtist.isEmpty() && lastTitle.isEmpty()) {
                        delay(1700)
                        forceSendCurrentTrack()
                    }
                    if (lastArtist.isEmpty() && lastTitle.isEmpty()) {
                        delay(8000)
                        forceSendCurrentTrack()
                    }
                }
            },
            onDisconnected = {
                roleVerified = false
                Log.d(TAG, "WebSocket disconnected")
                updateNotification("Reconnecting…")
            },
            onMessage = { msg ->
                Log.d(TAG, "Received: $msg")
                handleIncomingMessage(msg)
            }
        )
        webSocketClient?.connect()

        // Track-change detection + position streaming (2s interval)
        serviceScope.launch {
            while (isActive) {
                delay(POSITION_UPDATE_INTERVAL_MS)
                if (!isSyncing && roleVerified) {
                    mediaMonitor?.requestUpdate()
                }
            }
        }

        // Heartbeat timer
        serviceScope.launch {
            while (isActive) {
                delay(HEARTBEAT_INTERVAL_MS)
                webSocketClient?.send(buildHeartbeatJson())
                Log.d(TAG, "Sent heartbeat")
            }
        }
    }

    // ==================== Incoming Message Handler ====================

    /**
     * Handles schedule messages from Windows.
     * Schedule: {"type":"schedule","action":"play","at":<epoch_ms>,"position":<seconds>}
     */
    private fun handleIncomingMessage(msg: String) {
        // Handle schedule messages from Windows
        if (msg.contains("\"type\":\"schedule\"")) {
            try {
                val json = JSONObject(msg)
                val at = json.getLong("at")
                val delayMs = at - System.currentTimeMillis()

                Log.d(TAG, "Schedule received: at=$at, delay=${delayMs}ms")

                // Read the position field (added for resync support)
                val position = if (json.has("position")) json.getDouble("position") else -1.0

                serviceScope.launch {
                    // Seek to the correct position BEFORE waiting for the schedule
                    if (position >= 0) {
                        mediaMonitor?.seekTo(position)
                        Log.d(TAG, "Schedule: seek to ${position}s")
                    }

                    if (delayMs > 0) {
                        delay(delayMs)
                    } else if (delayMs < -500) {
                        Log.w(TAG, "Schedule was ${-delayMs}ms late — playing immediately")
                    }

                    mediaMonitor?.resumePlayback()
                    updateNotification("Connected to relay")
                    isSyncing = false
                    Log.d(TAG, "Playback resumed on schedule")
                }
            } catch (e: Exception) {
                Log.e(TAG, "Failed to parse schedule: ${e.message}")
            }
            return
        }

        // Handle request_track — Windows just connected and missed the track message
        if (msg.contains("\"type\":\"request_track\"")) {
            Log.d(TAG, "request_track received — re-sending current track")
            serviceScope.launch {
                delay(200)
                forceSendCurrentTrack()
            }
            return
        }
    }

    // ==================== JSON Builders (compact single-line) ====================

    private fun buildTrackJson(artist: String, title: String, album: String, duration: Double): String {
        return """{"type":"track","artist":${escapeJson(artist)},"title":${escapeJson(title)},"album":${escapeJson(album)},"duration":${Math.round(duration)}}"""
    }

    private fun buildPositionJson(position: Double, playing: Boolean): String {
        return """{"type":"position","position":$position,"playing":$playing}"""
    }

    private fun buildResumeRequestJson(position: Double): String {
        return """{"type":"resume_request","position":$position}"""
    }

    private fun buildHeartbeatJson(): String {
        return """{"type":"heartbeat"}"""
    }

    private fun escapeJson(s: String): String {
        return "\"${s.replace("\\", "\\\\").replace("\"", "\\\"")}\""
    }

    // ==================== Notification ====================

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                getString(R.string.notification_channel_name),
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "Music relay service notifications"
                setShowBadge(false)
            }
            val manager = getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(channel)
        }
    }

    private fun buildNotification(text: String): Notification {
        val intent = Intent(this, MainActivity::class.java)
        val pendingIntent = PendingIntent.getActivity(
            this, 0, intent, PendingIntent.FLAG_IMMUTABLE
        )

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle(getString(R.string.notification_title))
            .setContentText(text)
            .setSmallIcon(android.R.drawable.ic_media_play)
            .setContentIntent(pendingIntent)
            .setOngoing(true)
            .build()
    }

    private fun updateNotification(text: String) {
        val manager = getSystemService(NotificationManager::class.java)
        manager.notify(NOTIFICATION_ID, buildNotification(text))
    }

    // ==================== Wake Lock ====================

    private fun acquireWakeLock() {
        val powerManager = getSystemService(POWER_SERVICE) as PowerManager
        wakeLock = powerManager.newWakeLock(
            PowerManager.PARTIAL_WAKE_LOCK,
            "MusicRelay::ServiceWakeLock"
        ).apply {
            acquire(10 * 60 * 1000L) // 10 minutes max
        }
    }

    private fun releaseWakeLock() {
        wakeLock?.let {
            if (it.isHeld) it.release()
        }
        wakeLock = null
    }
}
