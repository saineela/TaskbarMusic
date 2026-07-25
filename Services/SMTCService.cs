using System;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace TaskbarMusic.Services
{
    /// <summary>
    /// Represents current media playback information.
    /// </summary>
    public class MediaInfo
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public double DurationSeconds { get; set; }
        public double PositionSeconds { get; set; }
        public bool IsPlaying { get; set; }

        public override string ToString() => $"{Artist} - {Title}";
    }

    /// <summary>
    /// Service for reading media playback information from Windows System Media Transport Controls.
    /// This works with any media source including Bluetooth-connected Android devices.
    /// </summary>
    public class SMTCService : IDisposable
    {
        private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;

        /// <summary>
        /// Fired when the current media session changes.
        /// </summary>
        public event EventHandler? SessionChanged;

        /// <summary>
        /// Initializes the SMTC service and listens for session changes.
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                Console.WriteLine("[SMTC] Requesting session manager...");
                _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                Console.WriteLine("[SMTC] Session manager obtained");

                _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;

                _currentSession = _sessionManager.GetCurrentSession();

                if (_currentSession != null)
                {
                    Console.WriteLine($"[SMTC] Active session found: {_currentSession.SourceAppUserModelId}");
                }
                else
                {
                    Console.WriteLine("[SMTC] No active media session yet - play music on your phone!");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SMTC] INITIALIZATION ERROR: {ex.Message}");
                Console.WriteLine($"[SMTC] Stack: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Gets current media information including playback position.
        /// </summary>
        public async Task<MediaInfo?> GetCurrentMediaInfoAsync()
        {
            if (_currentSession == null)
                return null;

            try
            {
                var info = new MediaInfo();

                // Get media properties (title, artist, etc.)
                var mediaProperties = await _currentSession.TryGetMediaPropertiesAsync();
                if (mediaProperties != null)
                {
                    info.Title = mediaProperties.Title ?? string.Empty;
                    info.Artist = mediaProperties.Artist ?? string.Empty;
                    info.Album = mediaProperties.AlbumTitle ?? string.Empty;
                }

                // Get playback info (position, status)
                var timelineInfo = _currentSession.GetTimelineProperties();
                if (timelineInfo != null)
                {
                    var position = timelineInfo.Position;
                    var startTime = timelineInfo.StartTime;
                    info.PositionSeconds = Math.Max(0, (position - startTime).TotalSeconds);
                    info.DurationSeconds = Math.Max(0, (timelineInfo.EndTime - timelineInfo.StartTime).TotalSeconds);
                }

                // Get play/pause status
                var playbackInfo = _currentSession.GetPlaybackInfo();
                if (playbackInfo != null)
                {
                    info.IsPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                }

                return info;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SMTC get media info error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Updates the position without re-querying the full media properties.
        /// This is more efficient for frequent position updates.
        /// </summary>
        public double GetPositionSeconds()
        {
            if (_currentSession == null)
                return 0;

            try
            {
                var timelineInfo = _currentSession.GetTimelineProperties();
                if (timelineInfo != null)
                {
                    var position = timelineInfo.Position;
                    var startTime = timelineInfo.StartTime;
                    return Math.Max(0, (position - startTime).TotalSeconds);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SMTC get position error: {ex.Message}");
            }

            return 0;
        }

        private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            try
            {
                _currentSession = sender.GetCurrentSession();

                if (_currentSession != null)
                {
                    Console.WriteLine($"[SMTC] Session changed to: {_currentSession.SourceAppUserModelId}");
                }
                else
                {
                    Console.WriteLine("[SMTC] Session ended");
                }

                SessionChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SMTC] Session change error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_sessionManager != null)
            {
                _sessionManager.CurrentSessionChanged -= OnCurrentSessionChanged;
            }
            _currentSession = null;
            _sessionManager = null;
        }
    }
}
