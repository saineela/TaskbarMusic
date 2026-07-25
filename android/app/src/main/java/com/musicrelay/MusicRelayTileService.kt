package com.musicrelay

import android.content.ComponentName
import android.content.Intent
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import androidx.core.content.ContextCompat

class MusicRelayTileService : TileService() {

    override fun onStartListening() {
        super.onStartListening()
        updateTileState()
    }

    override fun onClick() {
        super.onClick()
        val tile = qsTile ?: return
        
        // Optimistic UI update for instant feedback
        if (tile.state == Tile.STATE_ACTIVE) {
            tile.state = Tile.STATE_INACTIVE
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                tile.subtitle = "Stopping..."
            }
            tile.updateTile()
            stopRelayService()
        } else {
            tile.state = Tile.STATE_ACTIVE
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                tile.subtitle = "Starting..."
            }
            tile.updateTile()
            startRelayService()
        }
    }

    private fun startRelayService() {
        val intent = Intent(this, MusicRelayService::class.java)
        ContextCompat.startForegroundService(this, intent)
    }

    private fun stopRelayService() {
        val intent = Intent(this, MusicRelayService::class.java)
        stopService(intent)
    }

    private fun updateTileState() {
        val tile = qsTile ?: return
        val isRunning = MusicRelayService.isRunning
        
        tile.state = if (isRunning) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE
        tile.label = getString(R.string.app_name)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            tile.subtitle = if (isRunning) "Running" else "Stopped"
        }
        tile.updateTile()
    }

    companion object {
        fun updateTile(context: android.content.Context) {
            requestListeningState(context, ComponentName(context, MusicRelayTileService::class.java))
        }
    }
}
