# Utylix - notes for whoever (or whichever Claude) continues this

Utylix is a personal all-in-one Windows utility app: downloads (HTTP, video sites, torrents), converter, screen recorder, snip tool,
video player, music player, photo viewer, archives, background remover, brightness, fan control, a Dashboard, and a Chrome/Edge/Brave
extension. C# .NET 10, WPF (+ WinForms interop). The owner uses it daily from the tray, so keep it light and safe.

Repository: https://github.com/ReynanGonzales/Utylix (public). `main` is the branch to work from (`utylix-feature-update` is kept equal).
Releases carry the program: the updater downloads the asset named exactly `Utylix.exe` from the *latest* release. See README.md for the
user-facing description of every feature.

## Layout
- `app/` - the program. Namespace is `IdmClone` (old name) for the UI, `IdmClone.Engine` for the engines.
  - `App.xaml.cs` startup, tray menu, command-line modes, single instance + per-user port (6800+, `port.txt`), `HideForCapture`.
  - `ShellWindow` (main tabs), `DashboardPage` (opened by the logo), `FansPage` + `Fans/` (fan control), `DownloadsPage`, `ConverterPage`,
    `RecorderPage`, `SnipWindow` + `Capture/`, `PlayerWindow` (VLC), `MusicWindow`, `ViewerWindow`, `ArchiveWindow`, `SettingsWindow`.
  - `Engine/` - `Download*.cs` (segmented HTTP, yt-dlp media, torrent), `TorrentService.cs` (MonoTorrent), `Manager.cs` (list, config,
    scheduler), `AppUpdater.cs`, `VlcEngine.cs`, `Tools.cs` (yt-dlp/ffmpeg).
  - `ApiServer.cs` local API on 127.0.0.1 used by the extension and by second copies of the exe (`/api/add`, `/api/tool`, ...).
  - `ShellMenu.cs` Explorer/registry integration (idempotent: it must not rewrite unchanged values, or Windows distrusts default apps).
  - `Installer.cs` + `InstallWizard.cs`: Utylix.exe doubles as the installer (`Utylix-Setup.exe`, `--setup`) and uninstaller (`--uninstall`).
- `extension/` - browser extension, embedded into the exe and unpacked to `%APPDATA%\Utylix\extension`. Raise `manifest.json` "version" when it
  changes: the extension reloads itself when the app (ping) reports a newer version.
- `branding/` icons and logos. `build.bat` / `InstallerBuilder.bat` build the exe / installer. `Uninstall.bat`.

## Build, run, release
```
cd app
dotnet build Utylix.csproj -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```
Output `app/publish/Utylix.exe` (~87 MB, self-contained). The first run of a new build unpacks itself (slow once).
Release = raise `<Version>` in `app/Utylix.csproj`, rebuild, create a GitHub release whose tag is `v<Version>` (the tag MUST equal the exe's version or
the updater loops/never offers), attach `Utylix.exe` (and `Utylix-Setup.exe`, same file). Not a pre-release. `gh` is not installed on the owner's PC.

## Things that bit us (read before changing these areas)
- **Editing C# through shell heredocs mangles backslashes** (`\\` collapses). Use the Edit/Write tools for code with Windows paths or registry keys.
- **Registry (`ShellMenu`)**: only write what changed (`SetIfDifferent`); never delete/recreate verb keys on every start. Utylix must not override
  an existing user choice (uTorrent, VLC) for default apps; it only registers itself in "Open with" / Default apps.
- **Fan control** needs administrator rights: an elevated helper (`--fan-helper`) talks over a named pipe. A scheduled task "Utylix Fan Helper"
  (installed once with one UAC prompt) runs a protected copy in `C:\Program Files\Utylix\FanHelper`. `FanTask.Generation` must be raised if the
  helper protocol changes (an older copy otherwise keeps working after an update). The helper reverts every fan to automatic if Utylix stops,
  disconnects, or goes silent for 10 s. Never relax those safeties. Board here: ASUS PRIME B550M-A (Nuvoton NCT6798D), GPU fan via NVIDIA.
- **libvlc** must only be called from one dedicated thread per window (PlayerWindow/MusicWindow "player thread"); calling it on the UI thread can deadlock.
- **Torrents** (MonoTorrent 3.0.2): no uTP, its DHT bootstraps poorly on some networks; trackers are tried one after another, so `Download.Torrent.cs`
  adds a few public trackers to magnet links and `TorrentService` raises half-open connections / shortens the timeout. Weak swarms are slow by nature.
- **Downloads**: servers that limit connections answer 403/429/503 to the extra ones; `Download.cs` sheds those connections (`TryShed`) instead of failing.
- **Snip**: `App.HideForCapture` hides Utylix windows but keeps the video player and photo viewer visible.
- **Taskbar**: Player/Archives/Music/Photos use their own AppUserModelID (`WindowTheme.OwnTaskbarButton`).
- A second copy of the exe started with a tool flag (`--play`, `--view`, `--torrent`, `--snip`, ...) forwards to the running copy through `/api/tool`.
- Test copies started from an elevated shell run elevated (the app then warns that drag-and-drop is blocked). Use `--no-register` for test runs so they do
  not touch the real Explorer/browser registrations, and `--port <n> --data <dir>` for an isolated copy.

## How it was tested
Mostly by driving the real windows with PowerShell UI Automation (find by AutomationId, `InvokePattern`, `SelectionItemPattern`; WPF chips and some buttons
need real mouse clicks with the window in front) and by screenshots. Always delete an old screenshot before re-running so a stale one is not misread.
Legal test torrents: Sintel / Big Buck Bunny / Tears of Steel (WebTorrent), Debian DVD `.torrent` (many seeds).

## Open ideas / known gaps
- Snip window text is hard to read when Windows is not in dark mode (reported, not fixed).
- Music player: no equalizer, ReplayGain, gapless guarantee, or folder library; video player has no madVR-style enhancement.
- Installer page to import a settings backup; optional PawnIO driver install for fan sensors; a QR maker; an extension reload notice.
- Animated GIFs show only the first frame in the photo viewer.

## The owner
Prefers hands-on verified changes, often writes in Taglish, tests by using the app daily, runs it from the tray (installed in
`C:\Program Files\Utylix`), builds in `D:\Utylix`. Releases are made by the owner on GitHub (upload `Utylix.exe`).
