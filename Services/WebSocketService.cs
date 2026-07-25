using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TaskbarMusic.Models;

namespace TaskbarMusic.Services
{
    /// <summary>
    /// Events raised when WebSocket messages are received from the Android phone.
    /// </summary>
    public class TrackReceivedEventArgs : EventArgs
    {
        public string Artist { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public double Duration { get; set; }
        /// <summary>Playback position when the phone paused for sync (seconds).</summary>
        public double PausedPosition { get; set; }
    }

    public class PositionReceivedEventArgs : EventArgs
    {
        public double Position { get; set; }
        public bool Playing { get; set; }
    }

    /// <summary>
    /// Raised when the phone resumes playback after a sync pause.
    /// The Windows app should use the arrival time of this event as t=0 for its lyrics clock.
    /// </summary>
    public class SyncStartEventArgs : EventArgs
    {
        /// <summary>UTC timestamp of when sync_start was received (set by receiver).</summary>
        public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
        /// <summary>The playback position (seconds) at which the phone resumed.</summary>
        public double Position { get; set; }
    }

    /// <summary>
    /// WebSocket client service that connects to the relay server to receive
    /// music metadata from an Android phone's MediaSession.
    /// 
    /// Spec:
    /// - "track": New track metadata
    /// - "resume_request": Phone wants to resume — Windows sends back schedule
    /// - "position": Position update (position in seconds, playing boolean)
    /// - "heartbeat": Keep-alive from phone
    /// </summary>
    public class WebSocketService : IDisposable
    {
        // Direct WebSocket relay URL — configure via right-click menu > WebSocket URL
        private const string DefaultWsBaseUrl = "";

        // Custom URL override (set via right-click menu, persisted in config)
        private string _customWebSocketUrl = string.Empty;

        // Reconnect backoff: 5s, 10s, 20s, 30s, 60s
        private static readonly int[] ReconnectDelays = { 5000, 10000, 20000, 30000, 60000 };

        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _cts;
        private string _deviceToken = string.Empty;
        private bool _isConnected;
        private bool _isDisposed;
        private int _retryIndex = 0;
        private bool _isConnecting; // Guard against concurrent connect attempts

        // Pending LRC requests — keyed by song name, completed when lrc_response arrives
        private readonly Dictionary<string, TaskCompletionSource<(bool found, string? content)>> _pendingLrcRequests = new();

        /// <summary>
        /// Raised when a new track is received from the phone.
        /// </summary>
        public event EventHandler<TrackReceivedEventArgs>? TrackReceived;

        /// <summary>
        /// Raised when a position update is received.
        /// </summary>
        public event EventHandler<PositionReceivedEventArgs>? PositionReceived;

        /// <summary>
        /// Raised when connection state changes.
        /// </summary>
        public event EventHandler<bool>? ConnectionStateChanged;

        /// <summary>
        /// Raised when a heartbeat is received (phone is alive).
        /// </summary>
        public event EventHandler? HeartbeatReceived;

    /// <summary>
    /// Raised when the phone requests a resume sync ("resume_request" message).
    /// Windows should calculate a schedule time and send it back.
    /// </summary>
    public event EventHandler<SyncStartEventArgs>? ResumeRequestReceived;    public bool IsConnected => _isConnected;

        /// <summary>
        /// Returns true if a device token is configured (can attempt connection).
        /// </summary>
        public bool HasToken => !string.IsNullOrEmpty(_deviceToken);

        /// <summary>
        /// Raised when the phone sent an explicit disconnect before stopping.
        /// Windows should clear lyrics immediately (no need to wait for heartbeat watchdog).
        /// </summary>
        public event EventHandler? PhoneDisconnected;

        /// <summary>
        /// Sets a custom WebSocket URL override. If empty, WebSocket functionality
        /// is disabled until a URL is configured via the right-click menu.
        /// </summary>
        public void SetCustomUrl(string url)
        {
            _customWebSocketUrl = url;
            Console.WriteLine($"[WebSocket] Custom URL set: {(string.IsNullOrEmpty(url) ? "(cleared — WebSocket disabled until URL is configured)" : url)}");
        }

        /// <summary>
        /// Builds the WebSocket URL from token. Uses custom URL as the base if set,
        /// otherwise uses the default relay. Always appends the device token.
        /// Returns null if neither a custom URL nor the default URL is configured.
        /// </summary>
        public string? BuildWebSocketUrl(string deviceToken)
        {
            var baseUrl = !string.IsNullOrEmpty(_customWebSocketUrl)
                ? _customWebSocketUrl.TrimEnd('/')
                : DefaultWsBaseUrl;

            if (string.IsNullOrEmpty(baseUrl))
            {
                Console.WriteLine("[WebSocket] No WebSocket URL configured — set one via right-click menu > WebSocket URL");
                return null;
            }

            return $"{baseUrl}/?token={deviceToken}";
        }

        /// <summary>
        /// Connects to the WebSocket relay server.
        /// </summary>
        public async Task ConnectAsync(string deviceToken)
        {
            if (_isConnected)
                return;

            _deviceToken = deviceToken;
            _cts = new CancellationTokenSource();

            var url = BuildWebSocketUrl(deviceToken);
            if (url == null)
            {
                Console.WriteLine("[WebSocket] Cannot connect — no WebSocket URL configured");
                return;
            }

            await ConnectWithUrlAsync(url);
        }

        private async Task ConnectWithUrlAsync(string url)
        {
            // Guard against concurrent connection attempts (watchdog + reconnect backoff)
            if (_isConnecting)
            {
                Console.WriteLine("[WebSocket] Connect already in progress — skipping");
                return;
            }
            _isConnecting = true;
            try
            {
                _webSocket?.Dispose();
                _webSocket = new ClientWebSocket();

                try
                {
                    Console.WriteLine($"[WebSocket] Connecting to {url}");
                    Console.WriteLine($"[WebSocket] Token: {ConfigService.GetTokenPreview(_deviceToken)}");
                    await _webSocket.ConnectAsync(new Uri(url), _cts!.Token);

                    _isConnected = true;
                    _retryIndex = 0; // Reset backoff on successful connect
                    ConnectionStateChanged?.Invoke(this, true);
                    Console.WriteLine("[WebSocket] Connected!");

                    // Start receive loop
                    _ = ReceiveLoopAsync();

                    // Send subscribe message so the relay knows we want phone data
                    await SendJsonAsync("{\"type\":\"subscribe\"}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebSocket] Connection failed: {ex.Message}");
                    _isConnected = false;
                    ConnectionStateChanged?.Invoke(this, false);
                    _ = ScheduleReconnectAsync();
                }
            }
            finally
            {
                _isConnecting = false;
            }
        }

        /// <summary>
        /// Disconnects from the WebSocket server.
        /// </summary>
        public async Task DisconnectAsync()
        {
            if (_webSocket == null)
                return;

            try
            {
                _cts?.Cancel();

                if (_webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Client disconnecting",
                        CancellationToken.None
                    );
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebSocket] Disconnect error: {ex.Message}");
            }
            finally
            {
                _isConnected = false;
                ConnectionStateChanged?.Invoke(this, false);
            }
        }

        /// <summary>
        /// Continuously receives messages from the WebSocket server.
        /// </summary>
        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[4096];

            try
            {
                while (_webSocket?.State == WebSocketState.Open && !(_cts?.IsCancellationRequested ?? true))
                {
                    var messageBuilder = new StringBuilder();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _webSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            _cts?.Token ?? CancellationToken.None
                        );

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            var closeDesc = result.CloseStatusDescription ?? "(no description)";
                            var closeCode = result.CloseStatus ?? System.Net.WebSockets.WebSocketCloseStatus.Empty;
                            Console.WriteLine($"[WebSocket] Server closed connection — code={closeCode}, reason=\"{closeDesc}\"");
                            _isConnected = false;
                            ConnectionStateChanged?.Invoke(this, false);
                            _ = ScheduleReconnectAsync();
                            return;
                        }

                        messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    var messageJson = messageBuilder.ToString();
                    ProcessMessage(messageJson);
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[WebSocket] Receive loop cancelled");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebSocket] Receive error: {ex.Message}");
                _isConnected = false;
                ConnectionStateChanged?.Invoke(this, false);
                _ = ScheduleReconnectAsync();
            }
        }

        /// <summary>
        /// Processes an incoming JSON message and raises appropriate events.
        /// </summary>
        private void ProcessMessage(string json)
        {
            try
            {
                var message = JsonSerializer.Deserialize<WebSocketMessage>(json);
                if (message == null)
                {
                    Console.WriteLine($"[WebSocket] Failed to deserialize: {json}");
                    return;
                }

                Console.WriteLine($"[WebSocket] Raw: {json}");
                switch (message.Type.ToLowerInvariant())
                {
                    case "connected":
                        Console.WriteLine($"[WebSocket] Server confirmed connection, role={message.Role}");
                        // Subscribe to receive phone data
                        _ = SendJsonAsync("{\"type\":\"subscribe\"}");
                        break;

                    case "track":
                        if (!string.IsNullOrEmpty(message.Artist) && !string.IsNullOrEmpty(message.Title))
                        {
                            Console.WriteLine($"[WebSocket] Track: {message.Artist} - {message.Title} (pos={message.Position ?? 0:F1}s)");
                            TrackReceived?.Invoke(this, new TrackReceivedEventArgs
                            {
                                Artist = message.Artist,
                                Title = message.Title,
                                Album = message.Album ?? string.Empty,
                                Duration = message.Duration ?? 0,
                                PausedPosition = message.Position ?? 0
                            });
                        }
                        break;

                    case "resume_request":
                        Console.WriteLine($"[WebSocket] Resume request at position {message.Position ?? 0:F1}s");
                        ResumeRequestReceived?.Invoke(this, new SyncStartEventArgs
                        {
                            ReceivedAt = DateTime.UtcNow,
                            Position = message.Position ?? 0
                        });
                        break;

                    case "position":
                        Console.WriteLine($"[WebSocket] Position: {message.Position ?? 0:F1}s, playing={message.Playing}");
                        PositionReceived?.Invoke(this, new PositionReceivedEventArgs
                        {
                            Position = message.Position ?? 0,
                            Playing = message.Playing ?? true
                        });
                        break;

                    case "disconnect":
                        Console.WriteLine("[WebSocket] Phone sent disconnect — clearing lyrics");
                        PhoneDisconnected?.Invoke(this, EventArgs.Empty);
                        break;

                    case "heartbeat":
                        Console.WriteLine("[WebSocket] Heartbeat received");
                        HeartbeatReceived?.Invoke(this, EventArgs.Empty);
                        break;

                    case "lrc_response":
                        HandleLrcResponse(message);
                        break;

                    default:
                        Console.WriteLine($"[WebSocket] Unknown type: {message.Type}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebSocket] Process error: {ex.Message}");
            }
        }

        /// <summary>
        /// Ensures the WebSocket is connected. If already connected, does nothing.
        /// If not connected but a token is configured, attempts a fresh connection.
        /// Call this periodically from a watchdog timer.
        /// </summary>
        public async Task EnsureConnectedAsync()
        {
            if (_isDisposed || string.IsNullOrEmpty(_deviceToken))
                return;
            if (_isConnecting)
                return;
            if (_isConnected && _webSocket?.State == WebSocketState.Open)
                return;

            var url = BuildWebSocketUrl(_deviceToken);
            if (url == null)
            {
                Console.WriteLine("[WebSocket] Watchdog: cannot reconnect — no WebSocket URL configured");
                return;
            }

            Console.WriteLine("[WebSocket] Watchdog: not connected — reconnecting...");
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _retryIndex = 0; // Reset backoff for fresh attempt
            await ConnectWithUrlAsync(url);
        }

        /// <summary>
        /// Clears the in-memory device token. Call after <see cref="DisconnectAsync"/> to
        /// prevent reconnection from the watchdog or reconnect loop.
        /// </summary>
        public void ClearToken()
        {
            _deviceToken = string.Empty;
        }

        /// <summary>
        /// Reconnects with fixed-step backoff (5s, 10s, 20s, 30s, 60s).
        /// Re-resolves DNS before each reconnect attempt.
        /// </summary>
        private async Task ScheduleReconnectAsync()
        {
            if (_isDisposed || string.IsNullOrEmpty(_deviceToken))
                return;

            var url = BuildWebSocketUrl(_deviceToken);
            if (url == null)
            {
                Console.WriteLine("[WebSocket] ScheduleReconnect: cannot reconnect — no WebSocket URL configured");
                return;
            }

            var delayMs = ReconnectDelays[Math.Min(_retryIndex, ReconnectDelays.Length - 1)];
            Console.WriteLine($"[WebSocket] Reconnecting in {delayMs}ms (attempt {_retryIndex + 1})...");
            await Task.Delay(delayMs, _cts?.Token ?? CancellationToken.None);

            _retryIndex++;

            if (!_isDisposed && !_isConnected)
            {
                await ConnectWithUrlAsync(url);
            }
        }

        /// <summary>
        /// Requests cached LRC from the server's global cache.
        /// Returns (found, lrcContent). found=false if not on server.
        /// </summary>
        public async Task<(bool found, string? content)> RequestLrcFromServerAsync(string song)
        {
            if (!_isConnected || _webSocket?.State != WebSocketState.Open)
                return (false, null);

            var tcs = new TaskCompletionSource<(bool found, string? content)>();
            lock (_pendingLrcRequests)
            {
                // Don't send duplicate requests for the same song while one is in flight
                if (_pendingLrcRequests.ContainsKey(song))
                    return (false, null);
                _pendingLrcRequests[song] = tcs;
            }

            try
            {
                var payload = JsonSerializer.Serialize(new { type = "lrc_request", song });
                await SendJsonAsync(payload);

                // Wait up to 5 seconds for the response
                var timeoutTask = Task.Delay(5000);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    Console.WriteLine($"[WebSocket] LRC request timed out for: {song}");
                    lock (_pendingLrcRequests)
                        _pendingLrcRequests.Remove(song);
                    return (false, null);
                }

                return await tcs.Task;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebSocket] LRC request error: {ex.Message}");
                lock (_pendingLrcRequests)
                    _pendingLrcRequests.Remove(song);
                return (false, null);
            }
        }

        /// <summary>
        /// Uploads LRC content to the server's global cache.
        /// Fire-and-forget — does not wait for confirmation.
        /// </summary>
        public async Task SendLrcUploadAsync(string song, string content)
        {
            if (!_isConnected || _webSocket?.State != WebSocketState.Open)
                return;

            try
            {
                var payload = JsonSerializer.Serialize(new { type = "lrc_upload", song, content });
                await SendJsonAsync(payload);
                Console.WriteLine($"[WebSocket] LRC uploaded: {song}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebSocket] LRC upload error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _cts?.Cancel();
            _webSocket?.Dispose();
            _cts?.Dispose();
        }

        /// <summary>
        /// Handles incoming lrc_response messages, completing pending TaskCompletionSources.
        /// </summary>
        private void HandleLrcResponse(WebSocketMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.Song))
            {
                Console.WriteLine("[WebSocket] lrc_response received without song — ignoring");
                return;
            }

            var song = message.Song;
            var found = !string.IsNullOrWhiteSpace(message.Content);
            Console.WriteLine($"[WebSocket] LRC response: song={song}, found={found}");

            TaskCompletionSource<(bool found, string? content)>? tcs;
            lock (_pendingLrcRequests)
            {
                if (_pendingLrcRequests.TryGetValue(song, out tcs))
                    _pendingLrcRequests.Remove(song);
            }

            if (tcs != null)
            {
                tcs.TrySetResult((found, message.Content));
            }
        }

        /// <summary>
        /// Sends a schedule message to the phone: play at this exact time.
        /// </summary>
        public async Task SendScheduleAsync(long atMs, double position)
        {
            var json = $"{{\"type\":\"schedule\",\"action\":\"play\",\"at\":{atMs},\"position\":{position}}}";
            await SendJsonAsync(json);
        }

        /// <summary>
        /// Requests the current track from the phone. Used when Windows connects
        /// after the phone already sent the track (relay doesn't cache messages).
        /// </summary>
        public async Task SendRequestTrackAsync()
        {
            await SendJsonAsync("{\"type\":\"request_track\"}");
        }

        /// <summary>
        /// Sends a JSON string over the WebSocket.
        /// </summary>
        private async Task SendJsonAsync(string json)
        {
            if (_webSocket?.State != WebSocketState.Open)
                return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
                Console.WriteLine($"[WebSocket] Sent: {json}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebSocket] Send error: {ex.Message}");
            }
        }


    }
}
