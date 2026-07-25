package com.musicrelay

import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import android.util.Log

/**
 * Minimal NotificationListenerService required for MediaSessionMonitor
 * to access active media sessions via getActiveSessions().
 *
 * The user must grant notification access in:
 * Settings → Apps → Special app access → Notification access → Music Relay
 */
class MusicNotificationListener : NotificationListenerService() {

    companion object {
        private const val TAG = "NotifyListener"
    }

    override fun onListenerConnected() {
        super.onListenerConnected()
        Log.d(TAG, "Notification listener connected — media monitoring enabled")
    }

    override fun onListenerDisconnected() {
        Log.d(TAG, "Notification listener disconnected — media monitoring disabled")
    }

    override fun onNotificationPosted(sbn: StatusBarNotification?) {
        // Not used — we only need MediaSession access, not notification content
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification?) {
        // Not used
    }
}
