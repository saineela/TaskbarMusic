package com.musicrelay

import android.content.Context
import android.content.Intent
import android.content.SharedPreferences
import android.os.Bundle
import android.provider.Settings
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import com.google.android.material.textfield.TextInputEditText
import com.google.android.material.textfield.TextInputLayout
import com.musicrelay.databinding.ActivityMainBinding

class MainActivity : AppCompatActivity() {

    companion object {
        private const val PREFS_NAME = "music_relay_prefs"
        private const val KEY_WS_URL = "custom_websocket_url"
    }

    private lateinit var binding: ActivityMainBinding
    private var isServiceRunning = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        // Check notification access on first launch
        checkNotificationAccess()

        updateUI()

        binding.btnToggleService.setOnClickListener {
            if (isServiceRunning) {
                stopRelayService()
            } else {
                // Ensure notification access before starting
                if (!hasNotificationAccess()) {
                    showNotificationAccessDialog()
                } else {
                    startRelayService()
                }
            }
            updateUI()
        }

        binding.btnWsUrl.setOnClickListener {
            showWebSocketUrlDialog()
        }
    }

    override fun onResume() {
        super.onResume()
        isServiceRunning = MusicRelayService.isRunning
        updateUI()
    }

    private fun hasNotificationAccess(): Boolean {
        val listeners = Settings.Secure.getString(
            contentResolver,
            "enabled_notification_listeners"
        )
        return listeners?.contains(packageName) == true
    }

    private fun checkNotificationAccess() {
        if (!hasNotificationAccess()) {
            showNotificationAccessDialog()
        }
    }

    private fun showNotificationAccessDialog() {
        MaterialAlertDialogBuilder(this)
            .setTitle("Notification Access Required")
            .setMessage("TaskbarMusic needs notification access to detect what song is playing.\n\nYou'll be taken to Settings — enable \"TaskbarMusic\" and come back.")
            .setPositiveButton("Open Settings") { _, _ ->
                val intent = Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS)
                startActivity(intent)
            }
            .setNegativeButton("Skip") { _, _ ->
                // User can still start the service, but media won't be captured
            }
            .show()
    }

    private fun showWebSocketUrlDialog() {
        val prefs = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        val currentUrl = prefs.getString(KEY_WS_URL, "") ?: ""

        val input = TextInputEditText(this).apply {
            setText(currentUrl)
            hint = "ws://your-server.com:8090/?token=..."
        }

        val layout = TextInputLayout(this).apply {
            addView(input)
            hint = "WebSocket URL"
            isHintEnabled = true
            setPadding(40, 16, 40, 0)
        }

        MaterialAlertDialogBuilder(this)
            .setTitle("Custom WebSocket URL")
            .setMessage("Enter a custom WebSocket relay URL (leave empty to use default):")
            .setView(layout)
            .setPositiveButton("Save") { _, _ ->
                val url = input.text?.toString()?.trim() ?: ""
                prefs.edit().putString(KEY_WS_URL, url).apply()
                // If service is running, restart it so the new URL takes effect
                if (MusicRelayService.isRunning) {
                    stopRelayService()
                    startRelayService()
                }
            }
            .setNegativeButton("Cancel", null)
            .setNeutralButton("Clear") { _, _ ->
                prefs.edit().putString(KEY_WS_URL, "").apply()
                if (MusicRelayService.isRunning) {
                    stopRelayService()
                    startRelayService()
                }
            }
            .show()
    }

    private fun startRelayService() {
        val intent = Intent(this, MusicRelayService::class.java)
        ContextCompat.startForegroundService(this, intent)
        isServiceRunning = true
    }

    private fun stopRelayService() {
        val intent = Intent(this, MusicRelayService::class.java)
        stopService(intent)
        isServiceRunning = false
    }

    private fun updateUI() {
        if (isServiceRunning) {
            binding.btnToggleService.text = getString(R.string.stop_service)
            if (hasNotificationAccess()) {
                binding.statusText.text = getString(R.string.connected_text)
                binding.statusText.setTextColor(ContextCompat.getColor(this, R.color.status_connected))
            } else {
                binding.statusText.text = getString(R.string.no_notification_access)
                binding.statusText.setTextColor(ContextCompat.getColor(this, R.color.status_disconnected))
            }
        } else {
            binding.btnToggleService.text = getString(R.string.start_service)
            if (!hasNotificationAccess()) {
                binding.statusText.text = getString(R.string.no_notification_access)
                binding.statusText.setTextColor(ContextCompat.getColor(this, R.color.status_disconnected))
            } else {
                binding.statusText.text = getString(R.string.disconnected_text)
                binding.statusText.setTextColor(ContextCompat.getColor(this, R.color.status_disconnected))
            }
        }
    }
}
