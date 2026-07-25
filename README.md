<p align="center">
    <img src="docs/images/logo.png" width="200" title="TaskbarMusic logo">
</p>

# TaskbarMusic

TaskbarMusic is a Windows desktop application that displays real-time song lyrics directly on your Windows taskbar. Built with WPF (.NET 8), it seamlessly integrates with both Windows System Media Transport Controls (SMTC) and a companion Android app via WebSocket relay, giving you beautifully synced lyrics at a glance — no need to alt-tab or pick up your phone.

> **Coming soon**: Android companion app on Google Play — in the meantime, you can build it from the `android/` source.

## ❓ The why

Have you ever been listening to music on your phone, wished you could glance at your PC screen to see what song is playing or read along with the lyrics — only to realize there's no easy way? I found myself constantly picking up my phone just to check the song title or sing along, and existing solutions either required premium subscriptions, clunky browser tabs, or didn't work across devices.

TaskbarMusic was born from that frustration. It's a lean, always-on-top window that sits quietly in your taskbar, automatically fetching and scrolling lyrics for whatever you're listening to — whether it's playing on your PC or streaming to your phone via Bluetooth. It pulls from multiple lyrics sources, caches everything locally, and even supports transliteration for non-Latin scripts. And it will always be free and open-source.

## 🌟 Features

### Lyrics Display & Sync

- **Real-time Taskbar Lyrics** — Synced lyrics scroll line-by-line directly on your taskbar. No window switching needed
- **Multi-Source Lyrics Fetching** — Automatically searches LRCLIB, BetterLyrics (TTML), Musixmatch, YouTube, and Spotify for the best available lyrics
- **Smart Lyric Matching** — Fuzzy-matches artist, title, album, and duration to find the most accurate lyrics
- **Manual Tuning Mode** — Tap the spacebar along with the beat to create perfectly synced lyrics for any song
- **Fine Offset Adjustment** — Adjust lyrics timing by ±0.05s or ±0.2s with instant feedback
- **Per-Song Offset Memory** — Timing offsets are persisted per song via SQLite cache
- **Synced & Plain Text Support** — Works with both timed (LRC/TTML) and untimed lyrics (with auto-generated 3s/line fallback)
- **Smooth Fade Transitions** — Lyrics fade in/out between lines for a polished reading experience

### Transliteration

- **Multi-Script Transliteration** — Convert lyrics to Latin script or 11+ Indian languages (Devanagari, Telugu, Tamil, Malayalam, Kannada, Bengali, Gujarati, Gurmukhi, Odia, Sinhala)
- **CJK & Korean Support** — Japanese (kana/kanji → romaji), Chinese (→ pinyin via Unidecode), Korean (hangul → romanized)
- **Pre-computed Batch Conversion** — Entire songs are transliterated at once for seamless, lag-free switching
- **Hover-to-View Original** — Hover over transliterated lyrics to see the original script in a tooltip

### Windows Integration

- **SMTC (System Media Transport Controls)** — Automatically detects media playback from any Windows app (Spotify, Chrome, etc.)
- **Persistent Taskbar Positioning** — Window is permanently locked to the taskbar at X=55, Y=1140. Fights Start menu, search, and tray overlays every 500ms
- **Always-on-Top** — Win32-level topmost flag ensures lyrics are always visible
- **Virtual Desktop Aware** — Pins the lyrics window to all virtual desktops (no losing your lyrics when switching desktops)
- **System Tray Integration** — Minimize to tray, double-click to restore, with balloon notifications
- **Startup with Windows** — Option to auto-launch on login via registry

### Android Phone Relay

- **WebSocket Relay** — Connect your Android phone to send real-time music metadata to your PC
- **Auto-Sync** — Lyric scrolling is synchronized with phone playback via a 2-second scheduled handshake
- **Heartbeat Monitoring** — Detects phone disconnection after 35s and clears lyrics automatically
- **Reconnection Backoff** — Smart reconnect with escalating delays (5s → 10s → 20s → 30s → 60s)
- **Custom Relay Server** — Configure your own WebSocket relay server URL
- **Multi-Pair Support** — The relay server supports multiple device pairs, each with their own phone and laptop tokens
- **Global LRC Cache** — Share tuned lyrics across all devices via the relay server's centralized cache

### Server & Web Management

- **Docker-Powered Relay Server** — One-command setup using `server-setup.sh` deploys a production-ready WebSocket relay on any Linux server
- **Web UI Dashboard** — Flask-based management interface with user accounts, pair management, and admin controls
- **Terminal Manager** — The `taskbarmusic` CLI lets you create/delete pairs, view tokens, restart services, manage users, and back up data — all from the terminal
- **LRC Cache Browser** — View, search, and delete global cached lyrics directly from the web UI with real-time disk usage stats
- **Docker Logs Viewer** — Stream the last 200 lines of relay logs from your browser
- **User Registration** — Optional self-registration with admin toggle
- **Password Reset** — Reset any user's password from the terminal manager
- **Smart Data Migration** — Automatically migrates legacy `.env` token configs to the modern multi-pair `pairs.json` format
- **Safe Upgrades** — Running the installer again preserves all users, pairs, lyrics, and settings with automatic backups

### Local Storage & Caching

- **SQLite Lyrics Cache** — All fetched and tuned lyrics are cached locally for offline access and instant replay
- **Global Server LRC Cache** — Tuned lyrics are uploaded to the relay server and shared across all connected devices
- **Cache Management** — View, browse, and delete cached songs from the right-click menu
- **Cache Statistics** — Track how many songs are cached, newest/oldest entries

### Demo Mode

- **Auto Demo** — Shows rotating sample messages when nothing is playing (e.g., "♪ TaskbarMusic Ready", "♪ Connect your phone")
- **Seamless Handoff** — Automatically exits demo mode when music starts

## 📋 Prerequisites

- **Windows 10 or 11** (64-bit) — for the TaskbarMusic desktop app
- **.NET 8 Runtime** — Bundled with the self-contained build; no separate install needed
- **Android phone** — For relay features, an Android device running the companion app
- **Linux server** (optional) — For self-hosting the WebSocket relay server with Web UI
- **Docker & Docker Compose** (optional) — Required on the server for the relay + web UI stack

## 🚀 Getting Started

### Option 1: Pre-built Binary

**Step 1: Download the latest release**
Download `TaskbarMusic.exe` from the [Releases](../../releases) page.

**Step 2: Run**
Just double-click `TaskbarMusic.exe`. The application will appear in your taskbar area, locked in position.

> **Note**: Windows SmartScreen may show a warning since the binary is unsigned. Click "More info" → "Run anyway" to proceed.

**Step 3: Play some music!**
- Music playing on your PC (via Spotify, Chrome, etc.) will be detected automatically via SMTC
- To connect your Android phone, configure a device token via the right-click menu

### Option 2: Build from Source

**Step 1: Clone the repository**

```bash
git clone https://github.com/yourusername/TaskbarMusic.git
cd TaskbarMusic
```

**Step 2: Build**

```bash
dotnet publish TaskbarMusic.csproj -c Release -o builds --runtime win-x64 --self-contained true
```

**Step 3: Run**

```bash
.\builds\TaskbarMusic.exe
```

### Quick Build Script

A `build.bat` script is included in the project root. Double-click it to build a single-file portable EXE in the `builds/` folder.

### Option 3: Self-Hosted Relay Server (Docker)

If you want to use the Android relay features, you need a WebSocket relay server called **Music WS**. Deploy it on any Linux server with Docker using a single command:

**Step 1: SSH into your Linux server**

```bash
ssh user@your-server
```

**Step 2: One-command install**

```bash
curl -fsSL https://raw.githubusercontent.com/saineela/TaskbarMusic/main/Music-ws%20Server/server-setup.sh | sudo bash
```

**Step 3: Complete the installation**

The script will:
1. Install prerequisites (jq) if missing
2. Set up the WebSocket relay on port **8090**
3. Set up the Web UI on port **5000**
4. Generate an admin password (shown at the end)
5. Install the `taskbarmusic` terminal manager command

Sample output:
```
============================================
  TASKBARMUSIC INSTALLATION COMPLETE
============================================
  Relay:   RUNNING
  Web UI:  RUNNING
  Pairs:   0
  Manager: taskbarmusic
  Web UI:  http://<server-ip>:5000
  Admin:   admin@taskbarmusic.local / VGFrZSB0aGF0IQ==
============================================
```

**Step 4: Create your first device pair**

Via the terminal manager:
```bash
sudo taskbarmusic
# → Choose option 1) Create Pair
# → Enter a name (e.g., "My Devices")
# → Note the Phone Token and Laptop Token
```

Or via the Web UI: `http://<server-ip>:5000` → Login → Create Pair

**Step 5: Configure your devices**

- **Windows app**: Right-click the taskbar icon → **Device Token** → paste the **Laptop Token**
- **Android app**: Enter the **Phone Token** in the app settings

### Android Companion App

The Android source code is in the `android/` directory. To set it up:

1. Open `android/` in Android Studio
2. Build and install the app on your phone
3. Connect both devices to the same WebSocket relay server
4. Enter the device token in TaskbarMusic's right-click menu → **Device Token**

## 🎮 Usage

### Right-Click Menu

| Menu Item | Description |
|-----------|-------------|
| 🔤 **Transliterate** | Enable/disable transliteration and select target script |
| 🔄 **Resync** | Re-synchronize lyrics with phone playback |
| ⏱ **Adjust Offset** | Fine-tune lyrics timing with ±0.05s precision |
| 🎵 **Tune Lyrics** | Tap spacebar along with the beat to create synced lyrics |
| 📂 **View Cached** | Browse and delete cached lyrics |
| 📊 **Cache Stats** | View cache usage statistics |
| 👻 **Minimize to Tray** | Hide to system tray |
| 🌐 **WebSocket URL** | Configure a custom WebSocket relay server URL |
| 🔗 **Device Token** | Enter or change your WebSocket device token |
| ❌ **Exit** | Quit the application |

### Tuning Mode

1. Right-click → **Tune Lyrics** while a song with loaded lyrics is playing
2. **Tap the spacebar** (or click the tap button) in time with each line of the song
3. When all lines are tapped, the synced timestamps are saved to the cache
4. The song will now play with perfectly synced lyrics!

### Playing on PC

If music is playing on your PC (Spotify, Chrome, Windows Media Player, etc.), TaskbarMusic will automatically detect it via SMTC. No phone needed.

### Playing on Phone

1. Install the Android companion app
2. Ensure both devices can reach your WebSocket relay server
3. Configure the device token in TaskbarMusic (right-click → **Device Token**)
4. Start playing music on your phone — lyrics will appear on your PC taskbar

### Web UI Dashboard

Once the relay server is deployed, open `http://<server-ip>:5000` in your browser:

| Page | Description |
|------|-------------|
| **Dashboard** | View and manage your device pairs. Create new pairs, copy tokens, or delete pairs |
| **Change Password** | Update your account password |
| **Logs** | Stream the last 200 lines of relay Docker logs in real-time |
| **LRC Cache** | Browse all globally cached lyrics files with file sizes. Monitor server disk usage with a progress bar |
| **Admin Panel** | Toggle user registration on/off (admin only) |
| **Login / Register** | Authenticate with your account. Registration can be disabled by the admin |

### Terminal Manager

Run `sudo taskbarmusic` on the server for a full management menu:

```
============================================
        Nitro Music WS Manager
============================================
1) Create Pair
2) Delete Pair
3) Rename Pair
4) Show Tokens
5) Restart WebSocket
6) Backup pairs.json
7) Manage Users
8) Exit
```

| Option | Description |
|--------|-------------|
| **1) Create Pair** | Generate a new device pair with random phone + laptop tokens |
| **2) Delete Pair** | Remove a pair and restart the relay container |
| **3) Rename Pair** | Rename an existing pair |
| **4) Show Tokens** | Display all tokens for all pairs |
| **5) Restart** | Restart the WebSocket relay Docker container |
| **6) Backup** | Create a timestamped backup of `pairs.json` |
| **7) Manage Users** | List all web UI users and reset passwords |

All credentials are also saved to `MUSIC_WS_CREDENTIALS.txt` in the server's work directory.

## 🔧 Configuration

### Device Token

The device token is the primary authentication credential for connecting to a WebSocket relay server. Set it via:
- Right-click menu → **Device Token**

### Custom WebSocket URL

If you're running your own WebSocket relay server, configure it via:
- Right-click menu → **WebSocket URL**

### Transliteration

Enable/disable transliteration and choose a target script from the **Transliterate** submenu. Settings persist across restarts.

### Startup with Windows

Toggle **Startup on Login** from the system tray icon's right-click menu.

### Server Configuration

All server configuration lives under `/opt/music-ws/` on the host:

| File | Description |
|------|-------------|
| `/opt/music-ws/app/server.js` | WebSocket relay server (Node.js) |
| `/opt/music-ws/app/pairs.json` | Device pair tokens and names |
| `/opt/music-ws/ws-manager.py` | Terminal manager application |
| `/opt/music-ws/MUSIC_WS_CREDENTIALS.txt` | Human-readable credentials dump |
| `/opt/music-ws/lrc_cache/` | Global LRC lyrics cache directory |
| `/opt/music-ws/webapp/app.py` | Flask web UI application |
| `/opt/music-ws/webapp/data/users.db` | Web UI user database (SQLite) |
| `/opt/music-ws/docker-compose.yml` | Docker Compose for the relay |
| `/opt/music-ws/webapp/docker-compose.web.yml` | Docker Compose for the web UI |

## 📷 Preview

*Screenshots coming soon! Contributions welcome — if you've set up TaskbarMusic, consider submitting a screenshot.*

## 🤝 Android App Details

The Android companion app (`android/`) provides:

- **MusicRelayService** — Foreground service that captures music metadata from notifications and MediaSession
- **WebSocket Relay** — Sends track info, position updates, and heartbeats to the relay server
- **Quick Settings Tile** — One-tap toggle to start/stop the relay service
- **Auto-Reconnect** — Automatically reconnects on network changes
- **Notification Listener** — Captures playback metadata from any music app's notification

### Building the Android App

```bash
cd android
./gradlew assembleDebug
```

## 🛠️ Project Structure

- **MainWindow.xaml / .cs** — Core WPF window, UI logic, and event handling
- **Services/**
  - **SMTCService.cs** — Windows System Media Transport Controls integration
  - **WebSocketService.cs** — WebSocket client with auto-reconnect and LRC cache support
  - **LyricsService.cs** — Multi-source lyrics fetching with 7-step fallback pipeline
  - **LyricsCache.cs** — SQLite-based lyrics cache with fuzzy matching
  - **LyricsMatcher.cs** — Lyrics candidate scoring and validation
  - **LrcParser.cs** — LRC format parser
  - **TransliterationService.cs** — Multi-script transliteration (CJK, Indic, etc.)
  - **AksharamukhaService.cs** — Python bridge for Aksharamukha API transliteration
  - **ConfigService.cs** — JSON-based configuration persistence
  - **StartupService.cs** — Windows startup registration
  - **VirtualDesktopService.cs** — Virtual desktop pinning via Win32/COM
  - **StringNormalizer.cs** — Text normalization for lyrics matching
- **Models/**
  - **LyricLine.cs** — Lyric line data model
  - **LyricsCandidate.cs** — Lyrics search result model
  - **WebSocketMessage.cs** — WebSocket message model
- **android/** — Android companion app (Kotlin)
  - **MusicRelayService.kt** — Foreground relay service
  - **MusicNotificationListener.kt** — Notification capture
  - **MediaSessionMonitor.kt** — Media session monitoring
  - **MainActivity.kt** — Configuration UI
- **Music-ws Server/**
  - **server-setup.sh** — Full Docker-based installer for the relay server, web UI, and terminal manager
- **TaskbarMusic.csproj** — .NET 8 WPF project configuration
- **transliterate_bridge.py** — Python transliteration bridge script
- **build.bat** — Convenience script to build the single-file Windows EXE

## 🧪 Development

### Building

```bash
dotnet build TaskbarMusic.csproj
```

### Single-File Portable Build

```bash
dotnet publish TaskbarMusic.csproj -c Release -o builds --runtime win-x64 --self-contained true
```

Or just run `build.bat`.

### Debug Logging

Logs are written to the debug console. Use [DebugView](https://learn.microsoft.com/en-us/sysinternals/downloads/debugview) or run from a terminal to see output:
```
[TaskbarMusic] Window loaded!
[TaskbarMusic] Media: Artist — Song Title (180s)
[TaskbarMusic] Found 42 lyrics for: Song Title (provider=lrclib)
```

### Transliteration Debug Log

When transliteration is active, detailed logs are written to `%APPDATA%\TaskbarMusic\akshara_debug.log`.

## 🔄 Architecture

```
┌─────────────┐     WebSocket      ┌──────────────────┐     Web UI      ┌──────────────┐
│  Android     │◄────────────────►│  Relay Server     │◄───────────────►│  Web Browser  │
│  Phone App   │    (track/pos/    │  (Node.js :8090)  │    (Flask :5000) │  (Dashboard,  │
└─────────────┘     heartbeat)     └──────┬───────────┘                  │   Logs,       │
                                          │                              │   LRC Cache)  │
                                          │ WebSocket                    └──────────────┘
┌─────────────┐     SMTC API      ┌──────▼───────────┐
│  Windows     │◄────────────────►│  TaskbarMusic     │
│  Media Apps  │    (metadata)     │  (WPF App)        │
└─────────────┘                   └──────┬───────────┘
                                         │
                             ┌───────────▼───────────┐
                             │  Lyrics Sources         │
                             │  LRCLIB → BetterLyrics  │
                             │  Musixmatch → YouTube   │
                             │  Spotify → Local Cache  │
                             └───────────┬───────────┘
                                         │
                             ┌───────────▼───────────┐
                             │  SQLite Cache           │
                             │  (local + server LRC)   │
                             └───────────────────────┘
```

### Server Architecture (Docker Stack)

```
┌─────────────────────────────────────────────────────┐
│                  Linux Server                        │
│  ┌─────────────────────────┐  ┌──────────────────┐  │
│  │ music-ws (Node.js)      │  │ music-ws-web      │  │
│  │ Port 8090               │  │ (Python Flask)    │  │
│  │ WebSocket Relay         │  │ Port 5000         │  │
│  │ - Multi-pair auth       │  │ Web UI            │  │
│  │ - Token-based auth      │  │ - Dashboard       │  │
│  │ - LRC upload/serve      │  │ - Pair management  │  │
│  │ - Message forwarding    │  │ - LRC Cache view  │  │
│  └──────────┬──────────────┘  │ - Docker logs     │  │
│             │                 │ - Admin panel      │  │
│             │                 └──────────────────┘  │
│             │                                        │
│  ┌──────────▼────────────────────────────────────┐  │
│  │ /opt/music-ws/                                │  │
│  │  ├── app/                                     │  │
│  │  │   ├── server.js      (WebSocket relay)     │  │
│  │  │   ├── pairs.json     (device tokens)       │  │
│  │  │   └── .env           (legacy config)       │  │
│  │  ├── webapp/            (Flask web UI)        │  │
│  │  ├── lrc_cache/         (global LRC cache)    │  │
│  │  ├── ws-manager.py      (terminal manager)    │  │
│  │  └── backups/           (auto-backups)        │  │
│  └───────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────┘
```

### Lyrics Fetching Pipeline

1. **In-memory cache** — Previously loaded lyrics for the current song
2. **Local SQLite cache** — Persisted lyrics from previous sessions
3. **Server LRC cache** — Shared lyrics from the WebSocket relay server
4. **LRCLIB get** — Direct lookup via `lrclib.net/api/get`
5. **LRCLIB search** — Fuzzy search via `lrclib.net/api/search`
6. **BetterLyrics** — Real synced TTML lyrics
7. **Musixmatch (plain text)** — Plain text with 3s/line artificial timing
8. **YouTube (plain text)** — Plain text with 3s/line artificial timing
9. **Spotify** — Timed lyrics via self-hosted spotify-lyrics-api

## 📝 License

This project is licensed under the GNU General Public License v3.0 — see the [LICENSE](LICENSE) file for details.

## 👏 Acknowledgements

- [LRCLIB](https://lrclib.net) — Open-source lyrics database
- [BetterLyrics](https://github.com/akashrchandran/spotify-lyrics-api) — Spotify lyrics API
- [lyrics-api](https://github.com/lewdhutao/lyrics-api) — Aggregated lyrics API
- [Unidecode.NET](https://github.com/morelinq/Unidecode.NET) — Unicode transliteration (fallback)
- [Aksharamukha](https://aksharamukha.appspot.com/) — Indic script transliteration
- [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) — System tray functionality
- [System.Data.SQLite](https://system.data.sqlite.org/) — Local lyrics cache
- [Flask](https://flask.palletsprojects.com/) — Web UI framework
- [Bootstrap](https://getbootstrap.com/) — Web UI CSS framework
- [Docker](https://www.docker.com/) — Containerized server deployment
- [Node.js](https://nodejs.org/) — WebSocket relay runtime
- .NET Team — WPF and .NET 8 framework

## 📞 Contact

Have questions, feedback, or feature requests? Feel free to open an issue on this repository.

---

Made with ❤️ for music lovers who want lyrics at a glance.
