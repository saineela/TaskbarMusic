package com.musicrelay

import android.util.Log
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.util.concurrent.TimeUnit

class RelayWebSocketClient(
    private val url: String,
    private val onConnected: () -> Unit = {},
    private val onRoleVerified: () -> Unit = {},
    private val onDisconnected: () -> Unit = {},
    private val onMessage: (String) -> Unit = {}
) {
    companion object {
        private const val TAG = "RelayWS"
        // Reconnect backoff: 5s, 10s, 20s, 30s, 60s
        private val reconnectDelays = longArrayOf(5000, 10000, 20000, 30000, 60000)
    }

    private val client = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.SECONDS) // No read timeout for WebSocket
        .pingInterval(30, TimeUnit.SECONDS)
        .build()

    private var webSocket: WebSocket? = null
    private var isConnected = false
    private var retryCount = 0

    fun connect() {
        connectWithUrl(url)
    }

    private fun connectWithUrl(wsUrl: String) {
        Log.d(TAG, "Connecting to $wsUrl")
        Log.d(TAG, "Token preview: ${getTokenPreview(wsUrl)}")
        val request = Request.Builder()
            .url(wsUrl)
            .build()

        webSocket = client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                isConnected = true
                retryCount = 0  // Reset backoff on successful connect
                Log.d(TAG, "Connected to relay server")
                onConnected()
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                Log.d(TAG, "Received: $text")
                // Check for role verification handshake
                if (text.contains("\"type\":\"connected\"") && text.contains("\"role\":\"phone\"")) {
                    onRoleVerified()
                }
                onMessage(text)
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                Log.d(TAG, "Closing: $code $reason")
                webSocket.close(1000, null)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                isConnected = false
                Log.d(TAG, "Closed: $code $reason")
                onDisconnected()
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                isConnected = false
                Log.e(TAG, "Connection failed: ${t.message}")
                onDisconnected()

                val delayMs = reconnectDelays[retryCount.coerceAtMost(reconnectDelays.lastIndex)]
                retryCount++
                Log.d(TAG, "Reconnecting in ${delayMs}ms (attempt ${retryCount})")
                Thread.sleep(delayMs)
                if (!isConnected) {
                    connectWithUrl(url)
                }
            }
        })
    }

    fun send(message: String) {
        if (isConnected) {
            webSocket?.send(message)
        } else {
            Log.w(TAG, "Cannot send, not connected")
        }
    }

    fun disconnect() {
        isConnected = false
        webSocket?.close(1000, "Client disconnecting")
        client.dispatcher.executorService.shutdown()
        client.connectionPool.evictAll()
    }

    private fun getTokenPreview(url: String): String {
        return try {
            val uri = java.net.URI(url)
            val query = uri.query ?: return "(no query)"
            val tokenIdx = query.indexOf("token=")
            if (tokenIdx < 0) return "(no token)"
            val token = query.substring(tokenIdx + 6)
                .substringBefore("&")
            if (token.length > 8)
                "${token.take(8)}...${token.takeLast(8)}"
            else token
        } catch (e: Exception) {
            "(error)"
        }
    }
}
