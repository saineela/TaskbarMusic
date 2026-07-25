using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TaskbarMusic.Models;
using TaskbarMusic.Services;
using static TaskbarMusic.Services.WebSocketService;

namespace TaskbarMusic
{
    public partial class MainWindow : Window
    {
        // --- State ---
        private bool _isMinimizedToTray = false;

        // --- Services ---
        private readonly SMTCService _smtcService;
        private readonly LyricsService _lyricsService;
        private readonly WebSocketService _webSocketService;

        // --- Lyrics state ---
        private List<LyricLine> _currentLyrics = new();
        private List<LyricLine>? _transliteratedLyrics; // pre-computed transliterated copy
        private int _currentLineIndex = -1;
        private MediaInfo? _lastMediaInfo;
        private string _lastTrackKey = string.Empty;

        // --- WebSocket state ---
        private volatile bool _webSocketConnected = false;
        private DateTime _lastHeartbeatTime = DateTime.MinValue;
        private bool _phoneAlive = false;

        // --- Scheduled sync position tracking ---
        private DateTime? _syncStartTime = null;
        private double _syncBasePosition = 0.0;
        private bool _isPlaying = false;
        private bool _trackReceivedSinceConnect = false;

        // --- Transliteration ---
        private bool _transliterationEnabled = false;
        private TransliterationService.TransliterationTarget _transliterationTarget = TransliterationService.TransliterationTarget.Latin;

        // --- Tuning mode ---
        private bool _isTuningMode = false;
        private int _tuningLineIndex = 0;
        private List<double> _tuningTimestamps = new();
        private DateTime _tuningStartTime = DateTime.MinValue;
        private bool _isPlainTextLyrics = false;

        // --- This Device Mode ---
        private bool _thisDeviceMode = false;

        // --- Permanently locked position (hardcoded: 55, 1140) ---
        private double _lockedX = 55;
        private double _lockedY = 1140;

        // --- Timers ---
        private readonly DispatcherTimer _positionTimer;
        private readonly DispatcherTimer _refreshTimer;
        private readonly DispatcherTimer _demoTimer;
        private readonly DispatcherTimer _lockTimer;
        private readonly DispatcherTimer _heartbeatWatchdog;
        private readonly DispatcherTimer _connectionWatchdog;
        private System.Threading.Timer? _bgHammerTimer; // Background thread — unfightable by UI
        private IntPtr _hwnd = IntPtr.Zero;
        private bool _demoActive = true;
        private int _demoIndex = 0;
        private bool _isExiting = false;

        private readonly string[] _demoLyrics = new[]
        {
            "♪ TaskbarMusic Ready",
            "♪ Connect your phone via TaskbarMusic app",
            "♪ Play a song to see lyrics",
            "♪ Right-click for menu"
        };

        public MainWindow()
        {
            InitializeComponent();

            _smtcService = new SMTCService();
            _webSocketService = new WebSocketService();
            _lyricsService = new LyricsService(_webSocketService);

            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _positionTimer.Tick += PositionTimer_Tick;

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _refreshTimer.Tick += RefreshTimer_Tick;

            _demoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _demoTimer.Tick += DemoTimer_Tick;

            // Lock timer: force position + visibility every 500ms. Fights Start menu/search/tray.
            _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _lockTimer.Tick += (s, e) => ForcePositionAndVisibility();

            // Heartbeat watchdog: if no heartbeat for 35s, phone is gone — clear lyrics
            _heartbeatWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _heartbeatWatchdog.Tick += HeartbeatWatchdog_Tick;

            // Connection watchdog: ensure WebSocket stays connected (every 10s)
            _connectionWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _connectionWatchdog.Tick += async (s, e) =>
            {
                if (!_isExiting && _webSocketService.HasToken)
                    await _webSocketService.EnsureConnectedAsync();
            };

            Loaded += MainWindow_Loaded;
            StartupMenuItem.IsChecked = StartupService.IsStartupEnabled();

            // Tuning mode button handlers
            TuningTapButton.Click += TuningTapButton_Click;
            TuningCancelButton.Click += TuningCancelButton_Click;
            PreviewKeyDown += OnTuningPreviewKeyDown;
        }

        // ==================== INITIALIZATION ====================

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("[TaskbarMusic] Window loaded!");

            var config = ConfigService.Load();
            Left = _lockedX;
            Top = _lockedY;

            Console.WriteLine($"[TaskbarMusic] Position locked at X={_lockedX}, Y={_lockedY}");

            // Load persisted transliteration state
            _transliterationEnabled = config.TransliterationEnabled;
            if (Enum.TryParse<TransliterationService.TransliterationTarget>(config.TransliterationTarget, out var savedTarget))
            {
                _transliterationTarget = savedTarget;
            }
            Console.WriteLine($"[TaskbarMusic] Loaded transliteration state: enabled={_transliterationEnabled}, target={_transliterationTarget}");

            // Load persisted This Device Mode
            _thisDeviceMode = config.ThisDeviceMode;
            ThisDeviceMenuItem.IsChecked = _thisDeviceMode;
            if (_thisDeviceMode)
            {
                UpdateThisDeviceIndicator();
                Console.WriteLine("[TaskbarMusic] This Device Mode enabled — using local SMTC");
            }

            // Load custom WebSocket URL and apply it to the service
            if (!string.IsNullOrEmpty(config.CustomWebSocketUrl))
            {
                _webSocketService.SetCustomUrl(config.CustomWebSocketUrl);
                Console.WriteLine($"[TaskbarMusic] Loaded custom WebSocket URL: {config.CustomWebSocketUrl}");
            }

            HitArea.Cursor = Cursors.Arrow;
            StatusText.Text = "♪ TaskbarMusic Ready";
            ApplyAlwaysOnTop();

            _smtcService.SessionChanged += OnSessionChanged;
            _webSocketService.TrackReceived += OnWsTrackReceived;
            _webSocketService.ResumeRequestReceived += OnWsResumeRequestReceived;
            _webSocketService.PositionReceived += OnWsPositionReceived;
            _webSocketService.ConnectionStateChanged += OnWsConnectionStateChanged;
            _webSocketService.HeartbeatReceived += OnWsHeartbeatReceived;
            _webSocketService.PhoneDisconnected += OnWsPhoneDisconnected;
            Console.WriteLine("[TaskbarMusic] Initializing SMTC...");
            await _smtcService.InitializeAsync();

            if (!string.IsNullOrEmpty(config.DeviceToken))
            {
                Console.WriteLine($"[TaskbarMusic] Connecting to relay (token={ConfigService.GetTokenPreview(config.DeviceToken)})");
                await _webSocketService.ConnectAsync(config.DeviceToken);
            }
            else
            {
                Console.WriteLine("[TaskbarMusic] No device token configured - using SMTC only");
            }

            _positionTimer.Start();
            _refreshTimer.Start();
            _demoTimer.Start();
            _lockTimer.Start();
            _heartbeatWatchdog.Start();
            _connectionWatchdog.Start();
            // Store HWND once for background timer (avoids WindowInteropHelper every tick)
            _hwnd = new WindowInteropHelper(this).Handle;

            // Set tray icon from embedded .ico resource
            try
            {
                var iconUri = new Uri("pack://application:,,,/Resources/AppLogo.ico", UriKind.Absolute);
                var iconInfo = Application.GetResourceStream(iconUri);
                if (iconInfo?.Stream != null)
                {
                    TrayIcon.Icon = new System.Drawing.Icon(iconInfo.Stream);
                    Console.WriteLine("[TaskbarMusic] Tray icon loaded from embedded .ico");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TaskbarMusic] Tray icon not available: {ex.Message}");
            }

            // Background timer: SetWindowPos every 150ms from a non-UI thread.
            // The UI thread gets blocked during Start menu animations — but
            // SetWindowPos is a raw Win32 call safe from any thread.
            _bgHammerTimer = new System.Threading.Timer(_ =>
            {
                if (_isMinimizedToTray || _hwnd == IntPtr.Zero) return;
                SetWindowPos(_hwnd, HWND_TOPMOST,
                    (int)_lockedX, (int)_lockedY, 0, 0,
                    SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }, null, 150, 150);

            await RefreshMediaInfoAsync();
        }

        // ==================== SMTC / BLUETOOTH MEDIA DETECTION ====================

        private async void RefreshTimer_Tick(object? sender, EventArgs e)
        {
            // In This Device Mode, always poll SMTC regardless of WebSocket state
            if (_webSocketConnected && !_thisDeviceMode) return;
            await RefreshMediaInfoAsync();
        }

        private async System.Threading.Tasks.Task RefreshMediaInfoAsync()
        {
            try
            {
                var mediaInfo = await _smtcService.GetCurrentMediaInfoAsync();
                if (mediaInfo == null || string.IsNullOrWhiteSpace(mediaInfo.Title))
                {
                    ShowWaitingMessage();
                    return;
                }

                Console.WriteLine($"[TaskbarMusic] Media: {mediaInfo.Artist} - {mediaInfo.Title} ({mediaInfo.DurationSeconds:F0}s)");
                var trackKey = $"{mediaInfo.Artist}|{mediaInfo.Title}|{mediaInfo.DurationSeconds:F0}";

                if (trackKey != _lastTrackKey)
                {
                    // Auto-exit tuning mode if song changes
                    if (_isTuningMode) ExitTuningMode(false);

                    _lastTrackKey = trackKey;
                    _lastMediaInfo = mediaInfo;
                    _currentLineIndex = -1;
                    _syncStartTime = null;
                    StopDemo();

                    StatusText.Text = $"♪ {mediaInfo.Artist} — {mediaInfo.Title}";
                    StatusText.Foreground = new SolidColorBrush(Colors.White);
                    await FetchLyricsAsync(mediaInfo);
                }
                else if (_syncStartTime != null)
                {
                    // Only track play/pause transitions from SMTC.
                    // Do NOT reset the local clock on every tick — that would cap
                    // position at ~1s and prevent lyrics from advancing past line 2.
                    // The PositionTimer's free-running clock is the source of truth.
                    if (_isPlaying != mediaInfo.IsPlaying)
                    {
                        // Save the current position at the moment of state change
                        var currentPos = _syncBasePosition +
                            (_isPlaying ? (DateTime.UtcNow - _syncStartTime.Value).TotalSeconds : 0);
                        _syncBasePosition = Math.Max(0, currentPos);
                        _syncStartTime = DateTime.UtcNow;
                        _isPlaying = mediaInfo.IsPlaying;
                        Console.WriteLine($"[TaskbarMusic] SMTC: {(_isPlaying ? "resumed" : "paused")} @ {currentPos:F1}s");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TaskbarMusic] Refresh error: {ex.Message}");
            }
        }

        private async void OnSessionChanged(object? sender, EventArgs e)
        {
            // Don't clear _lastTrackKey — that would cause an infinite loop where
            // every SMTC session change re-triggers FetchLyricsAsync, perpetually
            // resetting StatusText to "♪ Loading lyrics..." before lyrics can render.
            // Let RefreshMediaInfoAsync deduplicate natively via trackKey.
            Console.WriteLine("[TaskbarMusic] Session changed, refreshing...");
            await RefreshMediaInfoAsync();
        }

        // ==================== LRCLIB LYRICS FETCHING ====================

        private async System.Threading.Tasks.Task FetchLyricsAsync(MediaInfo mediaInfo)
        {
            StatusText.Text = "♪ Loading lyrics...";
            StatusText.Foreground = new SolidColorBrush(Colors.White);

            try
            {
                var lyrics = await _lyricsService.GetLyricsAsync(
                    mediaInfo.Title, mediaInfo.Artist,
                    mediaInfo.DurationSeconds, mediaInfo.Album);

                _currentLyrics = lyrics;
                _currentLineIndex = -1;
                _transliteratedLyrics = null;

                if (lyrics.Count > 0)
                {
                    Console.WriteLine($"[TaskbarMusic] Found {lyrics.Count} lyrics for: {mediaInfo.Title} (provider={_lyricsService.CurrentProvider})");

                    // Detect plain text lyrics and show/hide orange indicator
                    _isPlainTextLyrics = _lyricsService.IsPlainTextLyrics;
                    if (_isPlainTextLyrics)
                    {
                        PlainTextIndicator.Visibility = Visibility.Visible;
                        Console.WriteLine("[TaskbarMusic] Plain text lyrics detected — orange indicator shown");
                    }
                    else
                    {
                        PlainTextIndicator.Visibility = Visibility.Collapsed;
                    }

                    // Pre-compute transliterations for the entire song
                    if (_transliterationEnabled)
                    {
                        var batchTexts = lyrics.Select(l => l.Text ?? "").ToArray();
                        var target = _transliterationTarget;
                        var batchResults = await System.Threading.Tasks.Task.Run(() =>
                            TransliterationService.ConvertToTargetBatch(batchTexts, target));
                        _transliteratedLyrics = lyrics.Select((l, i) => new LyricLine
                        {
                            TimeSeconds = l.TimeSeconds,
                            Text = i < batchResults.Length ? batchResults[i] : l.Text
                        }).ToList();
                        Console.WriteLine($"[TaskbarMusic] Pre-computed {_transliteratedLyrics.Count} transliterated lines → {TransliterationService.TargetLabels[_transliterationTarget]}");
                    }
                    else
                    {
                        Console.WriteLine("[TaskbarMusic] Song is English — skipping transliteration pre-compute");
                    }

                    StatusText.Text = "♪ Synced lyrics loaded!";
                    StatusText.Foreground = new SolidColorBrush(Colors.White);

                    // In SMTC mode (no WebSocket phone), _syncStartTime is never set
                    // by the WebSocket event handlers. We set it here so PositionTimer
                    // has a baseline and can scroll through lyrics line-by-line.
                    if (_syncStartTime == null)
                    {
                        _syncBasePosition = 0;
                        _syncStartTime = DateTime.UtcNow;
                        _isPlaying = true;
                        Console.WriteLine("[TaskbarMusic] SMTC sync started — lyrics will scroll");
                    }
                }
                else
                {
                    Console.WriteLine($"[TaskbarMusic] No lyrics found for: {mediaInfo.Title}");
                    PlainTextIndicator.Visibility = Visibility.Collapsed;
                    _isPlainTextLyrics = false;
                    StatusText.Text = $"♪ {mediaInfo.Artist} — {mediaInfo.Title}";
                    StatusText.Foreground = new SolidColorBrush(Colors.White);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TaskbarMusic] Lyrics fetch error: {ex.Message}");
                PlainTextIndicator.Visibility = Visibility.Collapsed;
                _isPlainTextLyrics = false;
                StatusText.Text = $"♪ {mediaInfo.Artist} — {mediaInfo.Title}";
                StatusText.Foreground = new SolidColorBrush(Colors.White);
            }
        }

        // ==================== REAL-TIME LYRICS SYNC ====================

        private bool _isAnimating = false;

        private void PositionTimer_Tick(object? sender, EventArgs e)
        {
            if (_currentLyrics.Count == 0 || _demoActive || _isAnimating || _isTuningMode) return;
            if (_syncStartTime == null) return;

            var elapsed = _isPlaying ? (DateTime.UtcNow - _syncStartTime.Value).TotalSeconds : 0;
            var position = _syncBasePosition + Math.Max(0, elapsed);
            var lineIndex = LrcParser.FindCurrentLineIndex(_currentLyrics, position);

            if (lineIndex != _currentLineIndex && lineIndex >= 0)
            {
                _currentLineIndex = lineIndex;
                _isAnimating = true;

                // Pick text from the right list based on transliteration mode
                var displayText = _transliterationEnabled && _transliteratedLyrics != null
                    ? _transliteratedLyrics[lineIndex].Text
                    : _currentLyrics[lineIndex].Text;

                FadeTransition(displayText);
            }
        }

        private string? _lastDisplayedLyricText = null;

        private void FadeTransition(string displayText)
        {
            // Track original text for ToolTip when transliteration is active
            // (transliteration is resolved upstream in PositionTimer_Tick)
            string originalText = displayText;
            if (_transliterationEnabled && _currentLineIndex >= 0 && _currentLineIndex < _currentLyrics.Count)
                originalText = _currentLyrics[_currentLineIndex].Text;
            _lastDisplayedLyricText = originalText;

            if (StatusText.Opacity > 0.01)
            {
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
                fadeOut.Completed += (s, e) =>
                {
                    StatusText.Text = displayText;
                    StatusText.ToolTip = _transliterationEnabled && displayText != originalText ? originalText : null;
                    var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
                    fadeIn.Completed += (s2, e2) => { _isAnimating = false; };
                    StatusText.BeginAnimation(OpacityProperty, fadeIn);
                };
                StatusText.BeginAnimation(OpacityProperty, fadeOut);
            }
            else
            {
                StatusText.Text = displayText;
                StatusText.ToolTip = _transliterationEnabled && displayText != originalText ? originalText : null;
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
                fadeIn.Completed += (s, e) => { _isAnimating = false; };
                StatusText.BeginAnimation(OpacityProperty, fadeIn);
            }
        }

        /// <summary>
        /// Switches to transliterated text instantly using pre-computed cache.
        /// Called when toggling transliteration ON while a lyric is shown.
        /// </summary>
        private void ApplyTranslitToCurrentLyric()
        {
            if (_currentLineIndex >= 0 && _transliteratedLyrics != null &&
                _currentLineIndex < _transliteratedLyrics.Count)
            {
                var translit = _transliteratedLyrics[_currentLineIndex].Text;
                var original = _currentLyrics[_currentLineIndex].Text;
                if (translit != original)
                {
                    StatusText.Text = translit;
                    StatusText.ToolTip = original;
                }
                UpdateTranslitIndicator();
            }
            else if (_lastDisplayedLyricText != null)
            {
                // Fallback: real-time conversion using current target
                var translit = TransliterationService.ConvertToTarget(_lastDisplayedLyricText, _transliterationTarget);
                if (translit != _lastDisplayedLyricText)
                {
                    StatusText.Text = translit;
                    StatusText.ToolTip = _lastDisplayedLyricText;
                }
                UpdateTranslitIndicator();
            }
        }

        /// <summary>
        /// Restores the original lyric text instantly using pre-computed cache.
        /// Called when toggling transliteration OFF while a lyric is shown.
        /// </summary>
        private void RestoreOriginalLyric()
        {
            if (_currentLineIndex >= 0 && _currentLineIndex < _currentLyrics.Count)
            {
                StatusText.Text = _currentLyrics[_currentLineIndex].Text;
                StatusText.ToolTip = null;
            }
            else if (_lastDisplayedLyricText != null)
            {
                StatusText.Text = _lastDisplayedLyricText;
                StatusText.ToolTip = null;
            }
            UpdateTranslitIndicator();
        }

        /// <summary>
        /// No-op — the transliteration indicator was removed.
        /// </summary>
        private void UpdateTranslitIndicator() { }

        /// <summary>
        /// Updates the connection indicator to show This Device Mode is active (blue).
        /// </summary>
        private void UpdateThisDeviceIndicator()
        {
            ConnectionIndicator.Visibility = Visibility.Visible;
            ConnectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(0x4F, 0xA8, 0xED));
            ConnectionIndicator.ToolTip = "💻 This Device Mode — using local SMTC";
        }

        /// <summary>
        /// Reverts the connection indicator when This Device Mode is disabled.
        /// If WebSocket is connected, shows green (connected); otherwise gray.
        /// </summary>
        private void ClearThisDeviceIndicator()
        {
            if (_webSocketConnected)
            {
                ConnectionIndicator.Fill = new SolidColorBrush(Colors.LightGreen);
                ConnectionIndicator.ToolTip = null;
                StatusText.Text = "";  // Blank — waiting for phone data
                StatusText.Foreground = new SolidColorBrush(Colors.White);
            }
            else
            {
                ConnectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
                ConnectionIndicator.ToolTip = null;
            }
        }

        // ==================== VIEW CACHED DIALOG ====================

        private void ShowCachedLyricsDialog()
        {
            var entries = _lyricsService.GetAllCachedEntries();

            var dialog = new System.Windows.Window
            {
                Title = $"View Cached — {entries.Count} song(s)",
                Width = 560, Height = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow
            };

            var outerPanel = new System.Windows.Controls.DockPanel { Margin = new Thickness(10) };

            if (entries.Count == 0)
            {
                var emptyLabel = new System.Windows.Controls.TextBlock
                {
                    Text = "No cached songs yet. Play some music!",
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 40, 0, 0)
                };
                outerPanel.Children.Add(emptyLabel);
            }
            else
            {
                var scroll = new System.Windows.Controls.ScrollViewer
                {
                    VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                    Margin = new Thickness(0, 0, 0, 8)
                };

                var listPanel = new System.Windows.Controls.StackPanel();
                int itemIndex = 0;

                foreach (var entry in entries)
                {
                    var row = new System.Windows.Controls.Border
                    {
                        BorderBrush = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44)),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Padding = new Thickness(6, 4, 6, 4),
                        Background = itemIndex % 2 == 0
                            ? new System.Windows.Media.SolidColorBrush(
                                System.Windows.Media.Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF))
                            : System.Windows.Media.Brushes.Transparent
                    };
                    itemIndex++;

                    var rowGrid = new System.Windows.Controls.Grid();
                    rowGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                    rowGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });

                    var infoStack = new System.Windows.Controls.StackPanel();
                    var titleBlock = new System.Windows.Controls.TextBlock
                    {
                        Text = $"{entry.Artist} — {entry.Title}",
                        FontWeight = System.Windows.FontWeights.SemiBold,
                        FontSize = 12,
                        TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
                    };
                    var detailBlock = new System.Windows.Controls.TextBlock
                    {
                        Text = $"⏱ {entry.DurationLabel}  |  🕒 {entry.CachedAgo}",
                        FontSize = 10,
                        Foreground = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)),
                        Margin = new Thickness(0, 1, 0, 0)
                    };
                    infoStack.Children.Add(titleBlock);
                    infoStack.Children.Add(detailBlock);
                    System.Windows.Controls.Grid.SetColumn(infoStack, 0);
                    rowGrid.Children.Add(infoStack);

                    var delBtn = new System.Windows.Controls.Button
                    {
                        Content = "🗑",
                        Width = 28, Height = 28,
                        ToolTip = "Delete this cached entry",
                        Cursor = System.Windows.Input.Cursors.Hand,
                        FontSize = 12,
                        Padding = new Thickness(0),
                        VerticalAlignment = System.Windows.VerticalAlignment.Center,
                        Margin = new Thickness(6, 0, 0, 0)
                    };
                    var capturedKey = entry.CacheKey;
                    delBtn.Click += (s, args) =>
                    {
                        _lyricsService.DeleteCachedEntry(capturedKey);
                        dialog.Close();
                        ShowCachedLyricsDialog(); // Refresh
                    };
                    System.Windows.Controls.Grid.SetColumn(delBtn, 1);
                    rowGrid.Children.Add(delBtn);

                    row.Child = rowGrid;
                    listPanel.Children.Add(row);
                }

                scroll.Content = listPanel;
                System.Windows.Controls.DockPanel.SetDock(scroll, System.Windows.Controls.Dock.Top);
                outerPanel.Children.Add(scroll);
            }

            var bottomPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 0)
            };

            var closeBtn = new System.Windows.Controls.Button
            {
                Content = "Close", Width = 70, Height = 26, IsCancel = true,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            closeBtn.Click += (s, args) => dialog.Close();
            bottomPanel.Children.Add(closeBtn);

            System.Windows.Controls.DockPanel.SetDock(bottomPanel, System.Windows.Controls.Dock.Bottom);
            outerPanel.Children.Add(bottomPanel);

            dialog.Content = outerPanel;
            dialog.ShowDialog();
        }

        // ==================== WEBSOCKET EVENT HANDLERS ====================

        private async void OnWsTrackReceived(object? sender, TrackReceivedEventArgs e)
        {
            // In This Device Mode, ignore phone track data — use local SMTC instead
            if (_thisDeviceMode)
            {
                Console.WriteLine("[TaskbarMusic] WS Track ignored (This Device Mode active)");
                return;
            }

            // Auto-exit tuning mode if a new track arrives
            if (_isTuningMode) ExitTuningMode(false);

            // Any message from phone = phone is alive
            _lastHeartbeatTime = DateTime.UtcNow;
            if (!_phoneAlive) { _phoneAlive = true; OnPhoneCameBack(); }

            Console.WriteLine($"[TaskbarMusic] WS Track: {e.Artist} - {e.Title}");
            var trackKey = $"{e.Artist}|{e.Title}|{e.Duration:F0}";
            _trackReceivedSinceConnect = true;

            if (trackKey != _lastTrackKey)
            {
                _lastTrackKey = trackKey;
                _syncStartTime = null;
                _isPlaying = false;

                _lastMediaInfo = new MediaInfo
                {
                    Artist = e.Artist, Title = e.Title,
                    Album = e.Album, DurationSeconds = e.Duration
                };
                _currentLineIndex = -1;
                StopDemo();

                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = $"♪ Syncing — {e.Artist} — {e.Title}";
                    StatusText.Foreground = new SolidColorBrush(Colors.Cyan);
                });

                await FetchLyricsAsync(_lastMediaInfo);

                var playAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 2000;
                await _webSocketService.SendScheduleAsync(playAtMs, 0);

                var waitMs = playAtMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (waitMs > 0) await Task.Delay((int)waitMs);

                _syncBasePosition = 0;
                _syncStartTime = DateTimeOffset.FromUnixTimeMilliseconds(playAtMs).UtcDateTime;
                _isPlaying = true;

                Dispatcher.Invoke(() =>
                {
                    ConnectionIndicator.Fill = new SolidColorBrush(Colors.LightGreen);
                    StatusText.Text = $"♪ {e.Artist} — {e.Title}";
                    StatusText.Foreground = new SolidColorBrush(Colors.White);
                });
            }
        }

        private async void OnWsResumeRequestReceived(object? sender, SyncStartEventArgs e)
        {
            // In This Device Mode, ignore phone resume requests — use local SMTC instead
            if (_thisDeviceMode)
            {
                Console.WriteLine("[TaskbarMusic] WS Resume request ignored (This Device Mode active)");
                return;
            }

            // Any message from phone = phone is alive
            _lastHeartbeatTime = DateTime.UtcNow;
            if (!_phoneAlive) { _phoneAlive = true; OnPhoneCameBack(); }

            Console.WriteLine($"[TaskbarMusic] Resume request at position {e.Position:F1}s");

            var resumeAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 2000;
            await _webSocketService.SendScheduleAsync(resumeAtMs, e.Position);

            var waitMs = resumeAtMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (waitMs > 0) await Task.Delay((int)waitMs);

            _syncBasePosition = e.Position;
            _syncStartTime = DateTimeOffset.FromUnixTimeMilliseconds(resumeAtMs).UtcDateTime;
            _isPlaying = true;

            Dispatcher.Invoke(() =>
            {
                StopDemo();
                ConnectionIndicator.Fill = new SolidColorBrush(Colors.LightGreen);
                if (_lastMediaInfo != null && !string.IsNullOrEmpty(_lastMediaInfo.Title))
                {
                    StatusText.Text = $"♪ {_lastMediaInfo.Artist} — {_lastMediaInfo.Title}";
                    StatusText.Foreground = new SolidColorBrush(Colors.White);
                }
            });
        }

        private void OnWsPositionReceived(object? sender, PositionReceivedEventArgs e)
        {
            // In This Device Mode, ignore phone position updates
            if (_thisDeviceMode) return;

            // Any message from phone = phone is alive
            _lastHeartbeatTime = DateTime.UtcNow;
            if (!_phoneAlive) { _phoneAlive = true; OnPhoneCameBack(); }

            if (!e.Playing)
            {
                _syncBasePosition = e.Position;
                _isPlaying = false;
                Dispatcher.Invoke(() =>
                    ConnectionIndicator.Fill = new SolidColorBrush(Colors.Yellow));
            }
        }

        private void OnWsConnectionStateChanged(object? sender, bool connected)
        {
            _webSocketConnected = connected;

            Dispatcher.Invoke(() =>
            {
                if (connected)
                {
                    StopDemo();
                    if (_thisDeviceMode)
                    {
                        // In This Device Mode, don't blank the display — SMTC is in control
                        UpdateThisDeviceIndicator();
                    }
                    else
                    {
                        ConnectionIndicator.Fill = new SolidColorBrush(Colors.LightGreen);
                        StatusText.Text = "";  // Blank until phone sends data
                        StatusText.Foreground = new SolidColorBrush(Colors.White);
                    }
                }
                else
                {
                    if (!_thisDeviceMode)
                        ClearPhoneState();
                }
            });

            if (connected && !_trackReceivedSinceConnect)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500);
                    if (!_trackReceivedSinceConnect)
                    {
                        await _webSocketService.SendRequestTrackAsync();
                        Console.WriteLine("[TaskbarMusic] Sent request_track to phone");
                    }
                });
            }
        }

        /// <summary>
        /// Phone explicitly disconnected (user stopped the service) — clear immediately.
        /// </summary>
        private void OnWsPhoneDisconnected(object? sender, EventArgs e)
        {
            if (_thisDeviceMode)
            {
                Console.WriteLine("[TaskbarMusic] Phone disconnected (ignored — This Device Mode active)");
                return;
            }
            Console.WriteLine("[TaskbarMusic] Phone sent disconnect — clearing now");
            ClearPhoneState();
        }
        private void OnWsHeartbeatReceived(object? sender, EventArgs e)
        {
            _lastHeartbeatTime = DateTime.UtcNow;
            if (!_phoneAlive)
            {
                _phoneAlive = true;
                if (!_thisDeviceMode)
                    OnPhoneCameBack();
            }
        }

        /// <summary>
        /// Phone came back after being gone — restart demo if nothing is playing.
        /// </summary>
        private void OnPhoneCameBack()
        {
            Console.WriteLine("[TaskbarMusic] Phone reconnected — resuming");
            Dispatcher.Invoke(() =>
            {
                ConnectionIndicator.Fill = new SolidColorBrush(Colors.LightGreen);
                if (_lastMediaInfo != null)
                    StatusText.Text = $"♪ {_lastMediaInfo.Artist} — {_lastMediaInfo.Title}";
                else
                    StatusText.Text = "";
            });
        }

        /// <summary>
        /// Clears all phone-related state when phone disconnects or heartbeat times out.
        /// </summary>
        private void ClearPhoneState()
        {
            _syncStartTime = null;
            _isPlaying = false;
            _trackReceivedSinceConnect = false;
            _phoneAlive = false;
            _lastMediaInfo = null;
            _currentLyrics.Clear();
            _transliteratedLyrics = null;
            _currentLineIndex = -1;
            _lastTrackKey = string.Empty;
            _isPlainTextLyrics = false;

            Dispatcher.Invoke(() =>
            {
                ConnectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
                PlainTextIndicator.Visibility = Visibility.Collapsed;
                StatusText.Text = "";  // Blank — don't show anything
            });
        }

        /// <summary>
        /// Watchdog: if no heartbeat for 35s, phone is gone. Clear lyrics and wait.
        /// </summary>
        private void HeartbeatWatchdog_Tick(object? sender, EventArgs e)
        {
            if (!_webSocketConnected || !_phoneAlive) return;

            var missedMs = (DateTime.UtcNow - _lastHeartbeatTime).TotalMilliseconds;
            if (missedMs > 35000) // Heartbeat is every 30s, give 5s grace
            {
                Console.WriteLine($"[TaskbarMusic] Heartbeat missed for {missedMs / 1000:F0}s — phone gone");
                if (!_thisDeviceMode)
                    ClearPhoneState();
            }
        }
        // ==================== CUSTOM WEBSOCKET URL DIALOG ====================

        private void ShowWebSocketUrlDialog()
        {
            var config = ConfigService.Load();
            var currentUrl = config.CustomWebSocketUrl;

            var dialog = new System.Windows.Window
            {
                Title = "WebSocket URL", Width = 550, Height = 210,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow
            };

            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(15) };
            var label = new System.Windows.Controls.TextBlock
            {
                Text = "Custom WebSocket relay URL (leave empty to use default):",
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap
            };
            var hintLabel = new System.Windows.Controls.TextBlock
            {
                Text = "Enter just the server URL (e.g. ws://your-server.com:8090). The device token is appended automatically.",
                FontSize = 11,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)),
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };
            var textBox = new System.Windows.Controls.TextBox
            {
                Text = currentUrl,
                Margin = new Thickness(0, 0, 0, 12),
                Padding = new Thickness(4),
                MaxLines = 1
            };
            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var saveBtn = new System.Windows.Controls.Button
            { Content = "Save", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            saveBtn.Click += async (s, args) =>
            {
                var url = textBox.Text.Trim();
                ConfigService.SaveCustomWebSocketUrl(url);
                _webSocketService.SetCustomUrl(url);
                if (!string.IsNullOrEmpty(url) && _webSocketService.HasToken)
                {
                    // Reconnect with new URL
                    await _webSocketService.DisconnectAsync();
                    try { await _webSocketService.ConnectAsync(config.DeviceToken); }
                    catch (Exception ex) { Console.WriteLine($"[TaskbarMusic] WebSocket reconnect error: {ex.Message}"); }
                }
                Console.WriteLine($"[TaskbarMusic] Custom WebSocket URL saved: {(string.IsNullOrEmpty(url) ? "(cleared)" : url)}");
                dialog.Close();
            };

            var clearBtn = new System.Windows.Controls.Button
            { Content = "Clear", Width = 80, Margin = new Thickness(0, 0, 8, 0) };
            clearBtn.Click += async (s, args) =>
            {
                ConfigService.SaveCustomWebSocketUrl(string.Empty);
                _webSocketService.SetCustomUrl(string.Empty);
                if (_webSocketService.HasToken)
                {
                    await _webSocketService.DisconnectAsync();
                    try { await _webSocketService.ConnectAsync(config.DeviceToken); }
                    catch (Exception ex) { Console.WriteLine($"[TaskbarMusic] WebSocket reconnect error: {ex.Message}"); }
                }
                Console.WriteLine("[TaskbarMusic] Custom WebSocket URL cleared");
                dialog.Close();
            };

            var cancelBtn = new System.Windows.Controls.Button
            { Content = "Cancel", Width = 80, IsCancel = true };
            cancelBtn.Click += (s, args) => dialog.Close();

            buttonPanel.Children.Add(clearBtn);
            buttonPanel.Children.Add(saveBtn);
            buttonPanel.Children.Add(cancelBtn);
            panel.Children.Add(label);
            panel.Children.Add(hintLabel);
            panel.Children.Add(textBox);
            panel.Children.Add(buttonPanel);
            dialog.Content = panel;
            dialog.Loaded += (s, e) => textBox.Focus();
            dialog.ShowDialog();
        }

        // ==================== DEVICE TOKEN DIALOG ====================

        private void ShowDeviceTokenDialog()
        {
            var config = ConfigService.Load();
            var currentToken = config.DeviceToken;

            var dialog = new System.Windows.Window
            {
                Title = "Device Token", Width = 500, Height = 190,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow
            };

            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(15) };
            var label = new System.Windows.Controls.TextBlock
            {
                Text = "Device token (DNS resolves server IP automatically):",
                Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap
            };
            var textBox = new System.Windows.Controls.TextBox
            {
                Text = currentToken, Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(4)
            };
            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var connectBtn = new System.Windows.Controls.Button
            { Content = "Connect", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            connectBtn.Click += async (s, args) =>
            {
                var token = textBox.Text.Trim();
                ConfigService.SaveDeviceToken(token);
                await _webSocketService.DisconnectAsync();
                if (!string.IsNullOrEmpty(token))
                {
                    try { await _webSocketService.ConnectAsync(token); }
                    catch (Exception ex) { Console.WriteLine($"[TaskbarMusic] Connect error: {ex.Message}"); }
                }
                dialog.Close();
            };

            var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Width = 80, IsCancel = true };
            cancelBtn.Click += (s, args) => dialog.Close();

            var disconnectBtn = new System.Windows.Controls.Button
            { Content = "Disconnect", Width = 80, Margin = new Thickness(0, 0, 8, 0) };
            disconnectBtn.Click += async (s, args) =>
            {
                await _webSocketService.DisconnectAsync();
                _webSocketService.ClearToken();
                ConfigService.SaveDeviceToken(string.Empty);
                dialog.Close();
            };

            buttonPanel.Children.Add(disconnectBtn);
            buttonPanel.Children.Add(connectBtn);
            buttonPanel.Children.Add(cancelBtn);
            panel.Children.Add(label);
            panel.Children.Add(textBox);
            panel.Children.Add(buttonPanel);
            dialog.Content = panel;
            dialog.ShowDialog();
        }

        // ==================== LYRICS OFFSET DIALOG ====================

        private void ShowOffsetDialog()
        {
            var offset = _lyricsService.GetCurrentOffset();

            var dialog = new System.Windows.Window
            {
                Title = "Adjust Lyrics Offset", Width = 340, Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow
            };

            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };

            var infoLabel = new System.Windows.Controls.TextBlock
            {
                Text = _lastMediaInfo != null
                    ? $"Song: {_lastMediaInfo.Artist} — {_lastMediaInfo.Title}"
                    : "No song playing",
                Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap
            };

            var offsetLabel = new System.Windows.Controls.TextBlock
            {
                Text = $"Current offset: {(offset >= 0 ? "+" : "")}{offset:F2}s",
                FontSize = 18, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 10)
            };

            var btnRow = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            // Fine backward (-0.05s)
            var backFineBtn = new System.Windows.Controls.Button
            { Content = "◀◀", Width = 42, Height = 32, Margin = new Thickness(0, 0, 6, 0),
              ToolTip = "Back 0.05s" };
            backFineBtn.Click += async (s, args) => await ApplyOffsetAndRefreshAsync(-0.05, offsetLabel);

            // Coarse backward (-0.2s)
            var backBtn = new System.Windows.Controls.Button
            { Content = "◀", Width = 42, Height = 32, Margin = new Thickness(0, 0, 6, 0),
              ToolTip = "Back 0.2s" };
            backBtn.Click += async (s, args) => await ApplyOffsetAndRefreshAsync(-0.2, offsetLabel);

            // Reset
            var resetBtn = new System.Windows.Controls.Button
            { Content = "0", Width = 42, Height = 32, Margin = new Thickness(0, 0, 6, 0),
              ToolTip = "Reset to 0" };
            resetBtn.Click += async (s, args) =>
            {
                var current = _lyricsService.GetCurrentOffset();
                await ApplyOffsetAndRefreshAsync(-current, offsetLabel);
            };

            // Coarse forward (+0.2s)
            var fwdBtn = new System.Windows.Controls.Button
            { Content = "▶", Width = 42, Height = 32, Margin = new Thickness(0, 0, 6, 0),
              ToolTip = "Forward 0.2s" };
            fwdBtn.Click += async (s, args) => await ApplyOffsetAndRefreshAsync(+0.2, offsetLabel);

            // Fine forward (+0.05s)
            var fwdFineBtn = new System.Windows.Controls.Button
            { Content = "▶▶", Width = 42, Height = 32,
              ToolTip = "Forward 0.05s" };
            fwdFineBtn.Click += async (s, args) => await ApplyOffsetAndRefreshAsync(+0.05, offsetLabel);

            btnRow.Children.Add(backFineBtn);
            btnRow.Children.Add(backBtn);
            btnRow.Children.Add(resetBtn);
            btnRow.Children.Add(fwdBtn);
            btnRow.Children.Add(fwdFineBtn);

            var closeBtn = new System.Windows.Controls.Button
            { Content = "Close", Width = 70, Height = 28, IsCancel = true,
              HorizontalAlignment = HorizontalAlignment.Center,
              Margin = new Thickness(0, 14, 0, 0) };
            closeBtn.Click += (s, args) => dialog.Close();

            panel.Children.Add(infoLabel);
            panel.Children.Add(offsetLabel);
            panel.Children.Add(btnRow);
            panel.Children.Add(closeBtn);
            dialog.Content = panel;
            dialog.ShowDialog();
        }

        private async System.Threading.Tasks.Task ApplyOffsetAndRefreshAsync(double delta, System.Windows.Controls.TextBlock label)
        {
            _currentLyrics = _lyricsService.AdjustOffset(delta);
            _currentLineIndex = -1;
            if (_currentLyrics.Count > 0)
            {
                var batchTexts = _currentLyrics.Select(l => l.Text ?? "").ToArray();
                var target = _transliterationTarget;
                var batchResults = await System.Threading.Tasks.Task.Run(() =>
                    TransliterationService.ConvertToTargetBatch(batchTexts, target));
                _transliteratedLyrics = _currentLyrics.Select((l, i) => new LyricLine
                {
                    TimeSeconds = l.TimeSeconds,
                    Text = i < batchResults.Length ? batchResults[i] : l.Text
                }).ToList();
                Console.WriteLine($"[TaskbarMusic] Rebuilt transliterations → {TransliterationService.TargetLabels[_transliterationTarget]}");
            }
            else
            {
                _transliteratedLyrics = null;
            }

            var newOffset = _lyricsService.GetCurrentOffset();
            label.Text = $"Current offset: {(newOffset >= 0 ? "+" : "")}{newOffset:F2}s";
            Console.WriteLine($"[TaskbarMusic] Offset adjusted by {delta:F2}s → total {newOffset:F2}s");
        }

        // ==================== CACHE INFO ====================

        private void ShowCacheInfo()
        {
            var stats = _lyricsService.GetCacheStats();

            MessageBox.Show(
                $"📊 Cache Stats\n\n" +
                $"Cached songs: {stats.count}\n" +
                $"Newest entry: {stats.newest}\n" +
                $"Oldest entry: {stats.oldest}\n\n" +
                $"Location: %APPDATA%\\TaskbarMusic\\lyrics_cache.sqlite",
                "Cache Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void StopDemo()
        {
            if (_demoActive) { _demoActive = false; _demoTimer.Stop(); }
        }

        private void StartDemo()
        {
            if (!_demoActive)
            {
                _demoActive = true; _demoIndex = 0;            _currentLyrics.Clear();
            _transliteratedLyrics = null;
            _currentLineIndex = -1;
            _lastTrackKey = string.Empty;
            _demoTimer.Start();
            }
        }

        private void DemoTimer_Tick(object? sender, EventArgs e)
        {
            if (!_demoActive) return;
            StatusText.Text = _demoLyrics[_demoIndex];
            StatusText.Foreground = new SolidColorBrush(Colors.White);
            _demoIndex = (_demoIndex + 1) % _demoLyrics.Length;
        }

        private void ShowWaitingMessage()
        {
            if (_demoActive || (_webSocketConnected && !_thisDeviceMode)) return;
            StartDemo();
        }

        // ==================== WINDOW INTERACTION ====================

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExiting) { e.Cancel = true; MinimizeToTray(); return; }
            _webSocketService?.Dispose();
        }

        // No dragging — permanently locked. Left-click does nothing.
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { }

        // No double-click to unlock. Permanently locked.
        private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e) { }

        /// <summary>
        /// Rebuilds the transliterated lyrics using the current target language.
        /// Called when the target changes while a song is loaded.
        /// Runs the heavy Python subprocess on a background thread.
        /// </summary>
        private async System.Threading.Tasks.Task RegenerateTransliterationsAsync()
        {
            if (_currentLyrics.Count > 0)
            {
                var batchTexts = _currentLyrics.Select(l => l.Text ?? "").ToArray();
                var target = _transliterationTarget;
                var batchResults = await System.Threading.Tasks.Task.Run(() =>
                    TransliterationService.ConvertToTargetBatch(batchTexts, target));
                _transliteratedLyrics = _currentLyrics.Select((l, i) => new LyricLine
                {
                    TimeSeconds = l.TimeSeconds,
                    Text = i < batchResults.Length ? batchResults[i] : l.Text
                }).ToList();
                Console.WriteLine($"[TaskbarMusic] Regenerated transliterations → {TransliterationService.TargetLabels[_transliterationTarget]}");

                // Update current display if transliteration is on
                if (_transliterationEnabled)
                    ApplyTranslitToCurrentLyric();
            }
        }

        private void Window_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var menu = new System.Windows.Controls.ContextMenu();

            // ── Transliteration submenu ──
            var translitMenu = new System.Windows.Controls.MenuItem { Header = "🔤 Transliterate" };

            var toggleItem = new System.Windows.Controls.MenuItem
            {
                Header = "Enable",
                IsCheckable = true,
                IsChecked = _transliterationEnabled
            };
            toggleItem.Checked += async (s, args) =>
            {
                _transliterationEnabled = true;
                ConfigService.SaveTransliterationState(_transliterationEnabled, _transliterationTarget.ToString());
                await RegenerateTransliterationsAsync(); // ensures fresh with current target
                ApplyTranslitToCurrentLyric();
            };
            toggleItem.Unchecked += (s, args) =>
            {
                _transliterationEnabled = false;
                ConfigService.SaveTransliterationState(_transliterationEnabled, _transliterationTarget.ToString());
                RestoreOriginalLyric();
            };
            translitMenu.Items.Add(toggleItem);
            translitMenu.Items.Add(new System.Windows.Controls.Separator());

            // Add a radio item for each target language
            foreach (TransliterationService.TransliterationTarget target in Enum.GetValues<TransliterationService.TransliterationTarget>())
            {
                var label = TransliterationService.TargetLabels[target];
                var targetItem = new System.Windows.Controls.MenuItem
                {
                    Header = label,
                    IsCheckable = true,
                    IsChecked = _transliterationTarget == target
                };
                var capturedTarget = target;
                targetItem.Click += async (s, args) =>
                {
                    if (_transliterationTarget == capturedTarget) return;
                    _transliterationTarget = capturedTarget;
                    await RegenerateTransliterationsAsync();
                    // When changing target, auto-enable transliteration
                    if (!_transliterationEnabled)
                    {
                        _transliterationEnabled = true;
                        toggleItem.IsChecked = true;
                    }
                    ConfigService.SaveTransliterationState(_transliterationEnabled, _transliterationTarget.ToString());
                    ApplyTranslitToCurrentLyric();
                };
                translitMenu.Items.Add(targetItem);
            }

            var resyncItem = new System.Windows.Controls.MenuItem { Header = "🔄 Resync" };
            resyncItem.Click += async (s, args) => await ResyncAsync();

            var offsetItem = new System.Windows.Controls.MenuItem { Header = "⏱ Adjust Offset" };
            offsetItem.Click += (s, args) => ShowOffsetDialog();

            var viewCacheItem = new System.Windows.Controls.MenuItem { Header = "📂 View Cached" };
            viewCacheItem.Click += (s, args) => ShowCachedLyricsDialog();

            var cacheItem = new System.Windows.Controls.MenuItem { Header = "📊 Cache Stats" };
            cacheItem.Click += (s, args) => ShowCacheInfo();

            var hideItem = new System.Windows.Controls.MenuItem { Header = "👻 Minimize to Tray" };
            hideItem.Click += (s, args) => MinimizeToTray();

            var wsItem = new System.Windows.Controls.MenuItem { Header = "🔗 Device Token" };
            wsItem.Click += (s, args) => ShowDeviceTokenDialog();

            var exitItem = new System.Windows.Controls.MenuItem { Header = "❌ Exit" };
            exitItem.Click += (s, args) => ExitApp();

            // ── This Device Mode toggle ──
            var thisDeviceItem = new System.Windows.Controls.MenuItem
            {
                Header = "💻 This Device",
                IsCheckable = true,
                IsChecked = _thisDeviceMode,
                ToolTip = "Detect music playing on this Windows device via SMTC instead of phone relay"
            };
            thisDeviceItem.Checked += (s, args) =>
            {
                _thisDeviceMode = true;
                ThisDeviceMenuItem.IsChecked = true;
                ConfigService.SaveThisDeviceMode(true);
                UpdateThisDeviceIndicator();
                Console.WriteLine("[TaskbarMusic] This Device Mode enabled (from right-click)");
                _ = RefreshMediaInfoAsync();
            };
            thisDeviceItem.Unchecked += (s, args) =>
            {
                _thisDeviceMode = false;
                ThisDeviceMenuItem.IsChecked = false;
                ConfigService.SaveThisDeviceMode(false);
                ClearThisDeviceIndicator();
                Console.WriteLine("[TaskbarMusic] This Device Mode disabled (from right-click)");
            };

            // Tune Lyrics — available for any track with loaded lyrics
            var tuneItem = new System.Windows.Controls.MenuItem
            {
                Header = "🎵 Tune Lyrics",
                Visibility = !_isTuningMode && _currentLyrics.Count > 0
                    ? Visibility.Visible : Visibility.Collapsed,
                ToolTip = "Tap along to the beat to create perfectly synced lyrics"
            };
            tuneItem.Click += (s, args) => EnterTuningMode();

            menu.Items.Add(translitMenu);
            menu.Items.Add(new System.Windows.Controls.Separator());
            menu.Items.Add(thisDeviceItem);
            menu.Items.Add(new System.Windows.Controls.Separator());
            menu.Items.Add(resyncItem);
            menu.Items.Add(offsetItem);
            menu.Items.Add(viewCacheItem);
            menu.Items.Add(new System.Windows.Controls.Separator());
            menu.Items.Add(tuneItem);
            menu.Items.Add(cacheItem);
            menu.Items.Add(hideItem);
            menu.Items.Add(new System.Windows.Controls.Separator());
            var wsUrlItem = new System.Windows.Controls.MenuItem { Header = "🌐 WebSocket URL" };
            wsUrlItem.Click += (s, args) => ShowWebSocketUrlDialog();
            menu.Items.Add(wsUrlItem);
            menu.Items.Add(wsItem);
            menu.Items.Add(new System.Windows.Controls.Separator());
            menu.Items.Add(exitItem);
            menu.IsOpen = true;
        }

        /// <summary>
        /// Resyncs the lyrics display with the phone by pausing and re-scheduling
        /// playback at the current position + 2 seconds on both Windows and Android.
        /// </summary>
        private async System.Threading.Tasks.Task ResyncAsync()
        {
            Console.WriteLine("[TaskbarMusic] Resync requested");

            // Calculate current position from sync state
            double currentPosition;
            if (_syncStartTime != null && _isPlaying)
            {
                var elapsed = (DateTime.UtcNow - _syncStartTime.Value).TotalSeconds;
                currentPosition = _syncBasePosition + Math.Max(0, elapsed);
            }
            else
            {
                currentPosition = _syncBasePosition;
            }

            Console.WriteLine($"[TaskbarMusic] Resync at position {currentPosition:F1}s");

            var resumeAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 2000;

            // Send schedule to phone if connected
            if (_webSocketConnected)
            {
                await _webSocketService.SendScheduleAsync(resumeAtMs, currentPosition);
            }

            var waitMs = resumeAtMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (waitMs > 0) await Task.Delay((int)waitMs);

            // Reset sync state to resume from current position
            _currentLineIndex = -1;
            _syncBasePosition = currentPosition;
            _syncStartTime = DateTimeOffset.FromUnixTimeMilliseconds(resumeAtMs).UtcDateTime;
            _isPlaying = true;

            StopDemo();

            StatusText.Text = _lastMediaInfo != null
                ? $"♪ {_lastMediaInfo.Artist} — {_lastMediaInfo.Title}"
                : "♪ Resynced";
            StatusText.Foreground = new SolidColorBrush(Colors.White);

            Console.WriteLine("[TaskbarMusic] Resync complete");
        }

        // ==================== TUNING MODE ====================

        /// <summary>
        /// Enters tuning mode: pauses normal lyric scrolling, shows tap button,
        /// and lets the user manually sync each line by tapping on the beat.
        /// </summary>
        private async void EnterTuningMode()
        {
            if (_currentLyrics.Count == 0) return;

            _isTuningMode = true;
            _tuningLineIndex = 0;
            _tuningTimestamps = new List<double>();
            _tuningStartTime = DateTime.UtcNow; // Clock starts now — taps record elapsed time

            // Hide normal display, show tuning display
            NormalDisplay.Visibility = Visibility.Collapsed;
            TuningDisplay.Visibility = Visibility.Visible;
            PlainTextIndicator.Visibility = Visibility.Collapsed;

            // Show the first line immediately (may use raw text if transliterations not ready)
            ShowCurrentTuningLine();

            // Pause the sync timer so PositionTimer doesn't interfere
            _isPlaying = false;

            // Pre-compute transliterations in the background, then refresh the display
            if (_transliterationEnabled && _transliteratedLyrics == null && _currentLyrics.Count > 0)
            {
                var batchTexts = _currentLyrics.Select(l => l.Text ?? "").ToArray();
                var target = _transliterationTarget;
                var batchResults = await System.Threading.Tasks.Task.Run(() =>
                    TransliterationService.ConvertToTargetBatch(batchTexts, target));
                _transliteratedLyrics = _currentLyrics.Select((l, i) => new LyricLine
                {
                    TimeSeconds = l.TimeSeconds,
                    Text = i < batchResults.Length ? batchResults[i] : l.Text
                }).ToList();
                Console.WriteLine($"[TaskbarMusic] Tuning: pre-computed {_transliteratedLyrics.Count} transliterated lines → {TransliterationService.TargetLabels[_transliterationTarget]}");
                // Refresh the first line with transliterated text
                if (_isTuningMode && _tuningLineIndex == 0)
                    ShowCurrentTuningLine();
            }

            Console.WriteLine($"[TaskbarMusic] Entered tuning mode — {_currentLyrics.Count} lines to sync");
        }

        /// <summary>
        /// Exits tuning mode, optionally saving the recorded timings.
        /// </summary>
        private async void ExitTuningMode(bool save)
        {
            if (!_isTuningMode) return;

            _isTuningMode = false;
            NormalDisplay.Visibility = Visibility.Visible;
            TuningDisplay.Visibility = Visibility.Collapsed;

            if (save && _tuningTimestamps.Count > 0)
            {
                await FinishTuningAsync();
            }
            else
            {
                // Restore plain text indicator if lyrics are still plain text
                if (_isPlainTextLyrics)
                    PlainTextIndicator.Visibility = Visibility.Visible;
            }

            Console.WriteLine($"[TaskbarMusic] Exited tuning mode (save={save}, timestamps={_tuningTimestamps.Count})");
        }

        /// <summary>
        /// Shows the current lyric line and updates the progress indicator.
        /// </summary>
        private void ShowCurrentTuningLine()
        {
            if (_tuningLineIndex < _currentLyrics.Count)
            {
                // Show transliterated text when available, fall back to original
                var text = _transliterationEnabled && _transliteratedLyrics != null
                    ? _transliteratedLyrics[_tuningLineIndex].Text
                    : _currentLyrics[_tuningLineIndex].Text;
                TuningLineText.Text = text;
                TuningProgressText.Text = $"🎵 Tuning: Line {_tuningLineIndex + 1}/{_currentLyrics.Count}";
                TuningTapButton.IsEnabled = true;
            }
            else
            {
                // All lines done — auto-finish
                TuningLineText.Text = "✓ All lines synced!";
                TuningProgressText.Text = "🎵 Tuning: Complete";
                TuningTapButton.IsEnabled = false;
                ExitTuningMode(true);
            }
        }

        /// <summary>
        /// Records the current timestamp and advances to the next line.
        /// </summary>
        private void RecordTuningTimestamp()
        {
            if (!_isTuningMode || _tuningLineIndex >= _currentLyrics.Count) return;

            // Record elapsed time since tuning started
            var timestamp = (DateTime.UtcNow - _tuningStartTime).TotalSeconds;
            Console.WriteLine($"[TaskbarMusic] Tuning: line {_tuningLineIndex + 1} → {timestamp:F2}s");

            _tuningTimestamps.Add(timestamp);
            _tuningLineIndex++;
            ShowCurrentTuningLine();
        }

        /// <summary>
        /// Tap button click handler.
        /// </summary>
        private void TuningTapButton_Click(object sender, RoutedEventArgs e)
        {
            RecordTuningTimestamp();
        }

        /// <summary>
        /// Cancel button click handler.
        /// </summary>
        private void TuningCancelButton_Click(object sender, RoutedEventArgs e)
        {
            ExitTuningMode(false);
        }

        /// <summary>
        /// PreviewKeyDown (tunneling) handler for tuning mode.
        /// More reliable than KeyDown on borderless windows — doesn't need focus.
        /// </summary>
        private void OnTuningPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_isTuningMode) return;

            if (e.Key == Key.Space)
            {
                e.Handled = true;
                RecordTuningTimestamp();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ExitTuningMode(false);
            }
        }

        /// <summary>
        /// Builds LRC content from the recorded timestamps, saves to cache,
        /// and refreshes the lyrics display with proper synced timings.
        /// </summary>
        private async System.Threading.Tasks.Task FinishTuningAsync()
        {
            if (_tuningTimestamps.Count == 0 || _currentLyrics.Count == 0) return;

            try
            {
                // Build LRC content from recorded timestamps
                var lrcBuilder = new System.Text.StringBuilder();
                int count = Math.Min(_tuningTimestamps.Count, _currentLyrics.Count);
                for (int i = 0; i < count; i++)
                {
                    var ts = _tuningTimestamps[i];
                    int min = (int)(ts / 60);
                    double sec = ts % 60;
                    lrcBuilder.AppendLine($"[{min:D2}:{sec:00.00}] {_currentLyrics[i].Text}");
                }
                var newLrc = lrcBuilder.ToString().TrimEnd();

                Console.WriteLine($"[TaskbarMusic] Tuning complete — saving {count} lines of synced LRC");

                // Save via LyricsService (updates cache + reloads in-memory)
                _currentLyrics = _lyricsService.SaveTunedLyrics(newLrc);
                _transliteratedLyrics = null;
                _currentLineIndex = -1;
                _isPlainTextLyrics = false;
                PlainTextIndicator.Visibility = Visibility.Collapsed;

                // Rebuild transliterations if needed
                if (_transliterationEnabled && _currentLyrics.Count > 0)
                {
                    var batchTexts = _currentLyrics.Select(l => l.Text ?? "").ToArray();
                    var target = _transliterationTarget;
                    var batchResults = await System.Threading.Tasks.Task.Run(() =>
                        TransliterationService.ConvertToTargetBatch(batchTexts, target));
                    _transliteratedLyrics = _currentLyrics.Select((l, i) => new LyricLine
                    {
                        TimeSeconds = l.TimeSeconds,
                        Text = i < batchResults.Length ? batchResults[i] : l.Text
                    }).ToList();
                }

                // Reset sync state so lyrics start scrolling from the beginning
                _syncBasePosition = 0;
                _syncStartTime = DateTime.UtcNow;
                _isPlaying = true;

                StatusText.Text = "♪ Tuned lyrics saved!";
                StatusText.Foreground = new SolidColorBrush(Colors.White);

                Console.WriteLine("[TaskbarMusic] Tuned lyrics saved and reloaded successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TaskbarMusic] Tuning save error: {ex.Message}");
                StatusText.Text = "♪ Tuning failed — try again";
                StatusText.Foreground = new SolidColorBrush(Colors.Orange);
                _isPlainTextLyrics = true;
                PlainTextIndicator.Visibility = Visibility.Visible;
            }
        }

        // ==================== SYSTEM TRAY ====================

        private void TrayIcon_DoubleClick(object sender, RoutedEventArgs e)
        {
            if (_isMinimizedToTray) ShowFromTray();
            else MinimizeToTray();
        }

        private void TrayShowHide_Click(object sender, RoutedEventArgs e)
        {
            if (_isMinimizedToTray) ShowFromTray();
            else MinimizeToTray();
        }

        private void TrayThisDevice_Click(object sender, RoutedEventArgs e)
        {
            _thisDeviceMode = ThisDeviceMenuItem.IsChecked;
            ConfigService.SaveThisDeviceMode(_thisDeviceMode);

            if (_thisDeviceMode)
            {
                UpdateThisDeviceIndicator();
                Console.WriteLine("[TaskbarMusic] This Device Mode enabled — switching to local SMTC");
                // Immediately poll SMTC for current media
                _ = RefreshMediaInfoAsync();
            }
            else
            {
                ClearThisDeviceIndicator();
                Console.WriteLine("[TaskbarMusic] This Device Mode disabled — resuming phone relay");
                // If WebSocket is connected and has a track, that will take over via events
                // If not connected, SMTC refresh timer will handle fallback
            }
        }

        private void TrayStartup_Click(object sender, RoutedEventArgs e)
            => StartupService.SetStartup(StartupMenuItem.IsChecked);

        private void TrayExit_Click(object sender, RoutedEventArgs e) => ExitApp();

        private void MinimizeToTray()
        {
            _isMinimizedToTray = true;
            Hide();
            TrayIcon.ShowBalloonTip("TaskbarMusic",
                "Running in background. Double-click tray to restore.",
                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
        }

        private void ShowFromTray()
        {
            _isMinimizedToTray = false;
            Show();
            Activate();
            ApplyAlwaysOnTop();
        }

        private void ExitApp()
        {
            _isExiting = true;
            _bgHammerTimer?.Dispose();
            _heartbeatWatchdog.Stop();
            _connectionWatchdog.Stop();
            _lockTimer.Stop();
            _webSocketService?.Dispose();
            TrayIcon.Dispose();
            Application.Current.Shutdown();
        }

        // ==================== POSITION LOCK ====================

        private void ApplyAlwaysOnTop()
        {
            var handle = new WindowInteropHelper(this).Handle;
            VirtualDesktopService.PinToAllDesktops(handle);
            SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        /// <summary>
        /// Fights Start menu, search, and system tray every 500ms.
        /// Forces the window back to LockedX/LockedY, ensures visibility and topmost.
        /// </summary>
        private void ForcePositionAndVisibility()
        {
            if (_isMinimizedToTray) return;

            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            // Bring back if Start menu hid it
            if (Visibility != Visibility.Visible)
            {
                Show();
                Console.WriteLine("[TaskbarMusic] Window was hidden — restored");
            }

            // Ensure topmost
            if (!Topmost) Topmost = true;

            // Force exact position + topmost + visibility
            SetWindowPos(handle, HWND_TOPMOST,
                (int)_lockedX, (int)_lockedY, 0, 0,
                SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);

            VirtualDesktopService.PinToAllDesktops(handle);
        }

        #region Win32 Interop

        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        #endregion
    }
}
