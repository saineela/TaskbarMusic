package com.musicrelay

import android.content.ComponentName
import android.content.Context
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.Log

class MediaSessionMonitor(
    private val context: Context,
    private val onUpdate: (MediaInfo?) -> Unit
) {
    companion object {
        private const val TAG = "MediaMonitor"
    }

    data class MediaInfo(
        val artist: String,
        val title: String,
        val album: String,
        val duration: Double,   // seconds
        val position: Double,   // seconds
        val isPlaying: Boolean
    )

    private var mediaSessionManager: MediaSessionManager? = null
    private var listenerComponent: ComponentName? = null
    private val handler = Handler(Looper.getMainLooper())
    private var lastActiveSessions: List<MediaController> = emptyList()
    private var currentPosition: Double = 0.0
    private var isPlaying: Boolean = false

    // Manual position tracking for media apps that report epoch timestamps
    // instead of song-relative position in PlaybackState.getPosition()
    private var playbackStartRealtime: Long = 0L  // SystemClock.elapsedRealtime() when playback started

    // Periodic session re-query runnable (safety net when session listener doesn't fire)
    private val sessionRequeryRunnable = object : Runnable {
        override fun run() {
            val comp = listenerComponent
            if (comp == null) {
                Log.w(TAG, "Re-query skipped: listenerComponent not set yet")
                handler.postDelayed(this, 10_000) // Keep trying
                return
            }
            if (lastActiveSessions.isEmpty()) {
                try {
                    val sessions = mediaSessionManager?.getActiveSessions(comp)
                    if (!sessions.isNullOrEmpty()) {
                        Log.d(TAG, "Re-query found ${sessions.size} active session(s)")
                        onSessionsChanged(sessions)
                    }
                } catch (e: SecurityException) {
                    // Notification access still not granted
                }
            }
            handler.postDelayed(this, 10_000) // Re-check every 10 seconds
        }
    }

    private val sessionListener = MediaSessionManager.OnActiveSessionsChangedListener { controllers ->
        Log.d(TAG, "Sessions changed: ${controllers?.size ?: 0}")
        onSessionsChanged(controllers)
    }

    private val playbackStateCallback = object : MediaController.Callback() {
        override fun onPlaybackStateChanged(state: PlaybackState?) {
            state?.let {
                val wasPlaying = isPlaying
                isPlaying = it.state == PlaybackState.STATE_PLAYING

                val rawPosMs = it.position

                // Some media apps report epoch timestamps (System.currentTimeMillis())
                // instead of song-relative position. Detect and handle both cases.
                if (rawPosMs > 1_700_000_000_000L) {
                    // Epoch timestamp — use manual elapsed-time tracking
                    if (isPlaying) {
                        if (!wasPlaying || playbackStartRealtime == 0L) {
                            playbackStartRealtime = SystemClock.elapsedRealtime()
                            currentPosition = 0.0
                        }
                    }
                } else if (rawPosMs > 0 && rawPosMs < 86_400_000) {
                    // Normal song-relative position in milliseconds
                    currentPosition = rawPosMs.toDouble() / 1000.0
                    playbackStartRealtime = 0L // Disable manual tracking
                } else if (isPlaying && !wasPlaying) {
                    // Position is 0 or unknown, fall back to manual tracking
                    playbackStartRealtime = SystemClock.elapsedRealtime()
                    currentPosition = 0.0
                }

                Log.d(TAG, "Playback state: playing=$isPlaying, pos=${currentPosition}s, rawPosMs=$rawPosMs")
                requestUpdate()
            }
        }

        override fun onMetadataChanged(metadata: android.media.MediaMetadata?) {
            Log.d(TAG, "Metadata changed")
            requestUpdate()
        }
    }

    fun start() {
        mediaSessionManager = context.getSystemService(Context.MEDIA_SESSION_SERVICE) as? MediaSessionManager
        if (mediaSessionManager == null) {
            Log.e(TAG, "MediaSessionManager not available")
            return
        }

        // Use our NotificationListenerService component to access active sessions
        listenerComponent = ComponentName(context, MusicNotificationListener::class.java)
        val comp = listenerComponent!!

        try {
            mediaSessionManager?.addOnActiveSessionsChangedListener(sessionListener, comp, handler)
            // Get initial sessions using the listener component
            val sessions = mediaSessionManager?.getActiveSessions(comp)
            onSessionsChanged(sessions)
            Log.d(TAG, "Monitoring started with notification listener")

            // Start periodic re-query as safety net (some devices don't fire session listener reliably)
            handler.postDelayed(sessionRequeryRunnable, 5_000)
        } catch (e: SecurityException) {
            Log.e(TAG, "Notification access required! Open Settings → Apps → Special access → Notification access → Music Relay")
            onUpdate(null)
        }
    }

    fun stop() {
        handler.removeCallbacks(sessionRequeryRunnable)
        mediaSessionManager?.removeOnActiveSessionsChangedListener(sessionListener)
        unregisterCallback()
        lastActiveSessions = emptyList()
        Log.d(TAG, "Monitoring stopped")
    }

    fun requestUpdate() {
        // If we lost track of sessions (e.g., phone was locked), re-query
        if (lastActiveSessions.isEmpty()) {
            val comp = listenerComponent ?: return
            try {
                val sessions = mediaSessionManager?.getActiveSessions(comp)
                if (!sessions.isNullOrEmpty()) {
                    Log.d(TAG, "requestUpdate: re-discovered ${sessions.size} session(s)")
                    onSessionsChanged(sessions)
                }
            } catch (e: SecurityException) {
                // Notification access not granted
            }
        }

        val info = getCurrentMediaInfo()
        onUpdate(info)
    }

    // ==================== Transport Controls (for sync) ====================

    /**
     * Pauses playback on the current media session (for sync protocol).
     */
    fun pausePlayback() {
        val controller = lastActiveSessions.firstOrNull()
        if (controller != null) {
            try {
                controller.transportControls.pause()
                Log.d(TAG, "Playback paused for sync")
            } catch (e: Exception) {
                Log.e(TAG, "Failed to pause: ${e.message}")
            }
        } else {
            Log.w(TAG, "No active session to pause")
        }
    }

    /**
     * Seeks to the specified position in seconds on the current media session.
     * Best-effort: some media apps (e.g. YouTube Music) may not support seeking.
     */
    fun seekTo(positionSeconds: Double) {
        val controller = lastActiveSessions.firstOrNull()
        if (controller != null) {
            try {
                val posMs = (positionSeconds * 1000).toLong()
                controller.transportControls.seekTo(posMs)
                // Update manual tracking so position reports are accurate after seek
                currentPosition = positionSeconds
                playbackStartRealtime = SystemClock.elapsedRealtime()
                Log.d(TAG, "Seek to ${positionSeconds}s for sync")
            } catch (e: Exception) {
                Log.w(TAG, "Seek not supported or failed: ${e.message}")
            }
        } else {
            Log.w(TAG, "No active session to seek")
        }
    }

    /**
     * Resumes playback on the current media session (after sync).
     */
    fun resumePlayback() {
        val controller = lastActiveSessions.firstOrNull()
        if (controller != null) {
            try {
                controller.transportControls.play()
                Log.d(TAG, "Playback resumed after sync")
            } catch (e: Exception) {
                Log.e(TAG, "Failed to resume: ${e.message}")
            }
        } else {
            Log.w(TAG, "No active session to resume")
        }
    }

    /**
     * Returns the best-known current playback position in seconds.
     */
    fun getSnapshotPosition(): Double {
        // Advance manual tracking if active before returning
        if (isPlaying && playbackStartRealtime > 0) {
            val elapsed = (SystemClock.elapsedRealtime() - playbackStartRealtime) / 1000.0
            return currentPosition + elapsed
        }
        return currentPosition
    }

    private fun onSessionsChanged(sessions: List<MediaController>?) {
        // Unregister from old sessions
        unregisterCallback()

        val controllers = sessions ?: emptyList()
        lastActiveSessions = controllers

        if (controllers.isNotEmpty()) {
            // Use the first (most recent) active session
            val controller = controllers.first()
            Log.d(TAG, "Using session: ${controller.packageName}")
            controller.registerCallback(playbackStateCallback, handler)
            requestUpdate()
        } else {
            onUpdate(null)
        }
    }

    private fun unregisterCallback() {
        lastActiveSessions.forEach { controller ->
            try {
                controller.unregisterCallback(playbackStateCallback)
            } catch (e: Exception) {
                // Ignore
            }
        }
    }

    private fun getCurrentMediaInfo(): MediaInfo? {
        val controller = lastActiveSessions.firstOrNull() ?: return null

        try {
            val metadata = controller.metadata
            if (metadata == null) {
                Log.d(TAG, "No metadata available")
                return null
            }

            val artist = metadata.getString(android.media.MediaMetadata.METADATA_KEY_ARTIST) ?: ""
            val title = metadata.getString(android.media.MediaMetadata.METADATA_KEY_TITLE) ?: ""
            val album = metadata.getString(android.media.MediaMetadata.METADATA_KEY_ALBUM) ?: ""
            val durationMs = metadata.getLong(android.media.MediaMetadata.METADATA_KEY_DURATION)
            val durationSec = if (durationMs > 0) durationMs.toDouble() / 1000.0 else 0.0

            val playbackState = controller.playbackState
            val playing = playbackState?.state == PlaybackState.STATE_PLAYING

            // Advance manual position tracking if playback is active
            if (playing && playbackStartRealtime > 0) {
                val elapsed = (SystemClock.elapsedRealtime() - playbackStartRealtime) / 1000.0
                // Wrap around duration if we know it
                currentPosition = if (durationSec > 0) elapsed % durationSec else elapsed
            }

            if (title.isNotEmpty()) {
                return MediaInfo(
                    artist = artist,
                    title = title,
                    album = album,
                    duration = durationSec,
                    position = currentPosition,
                    isPlaying = playing
                )
            }
        } catch (e: Exception) {
            Log.e(TAG, "Error getting media info: ${e.message}")
        }

        return null
    }
}
