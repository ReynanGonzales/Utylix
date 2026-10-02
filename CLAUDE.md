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
    `RecorderPage`, `SnipWindow` + `Capture/`, `PlayerWindow` (VLC), `MusicWindow`, `ViewerWindow`, `ArchiveWindow`, `SettingsWindow`,
    `PdfWindow` (+ `.Edit` / `.EditText` / `.Text` / `.Forms` partial files), `PdfReduceWindow`, `PdfPrintWindow`, `PdfSignatureWindow`
    ("Utylix Editor", `--pdf`). `UMessage` replaces every `MessageBox` (themed; `UMessage.Ask` for buttons that say what they do).
  - `Engine/` - `Download*.cs` (segmented HTTP, yt-dlp media, torrent), `TorrentService.cs` (MonoTorrent), `Manager.cs` (list, config,
    scheduler), `AppUpdater.cs`, `VlcEngine.cs`, `Tools.cs` (yt-dlp/ffmpeg), `Pdfium.cs` + `PdfFile.cs` / `PdfFile.Forms.cs` (PDFium),
    `PdfCompressor.cs`, `PdfMarks.cs` (writing edits / comments into pages), `PdfText.cs` (text, links, bookmarks), `PdfTextEdit.cs` (changing existing text).
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
- **PDF**: PDFium (`pdfium.dll` from bblanchon.PDFium.Win32, own P/Invokes in `Engine/Pdfium.cs`) is not thread-safe: every call goes
  through `lock (Pdfium.Sync)`. "Reduce file size" reads each picture with PDFium, but writes with PDFsharp (matching pictures by a SHA-256 of
  their stored bytes): PDFium can't replace a picture without leaving the old one in the file. Protected PDFs are only changed with the
  owner password and get the same passwords/permissions back; "Smallest" (pages re-made as pictures with PDFium) is refused for them.
  Never let a reduced copy come out less protected than the original. Coordinates of the editor are points from the top-left of the page
  *as shown*; `PageMapping` (PdfText.cs) converts to page space via FPDF_DeviceToPage (handles /Rotate and crop boxes - test turned pages).
  Edits are flattened into the page (FPDFPage_GenerateContent); comments (highlight/underline/strike/notes) are real annotations.
  Popups (ToolTip, ContextMenu, ComboBox lists) inherit the dark window's white text: give them themed styles (App.xaml ThemedMenu...),
  and note the app-wide implicit TextBlock style beats inherited Foreground inside templates (set Foreground on the TextBlock itself).
  Still planned: target-size reduce, Explorer shortcuts, phone-photo cleanup, resize pages, stamps, redaction, organize/merge/split,
  pictures <-> PDF, OCR, page numbers / watermark, PDF -> Word / Excel.
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
Prefers hands-on verified changes, often writes in Taglish, tests by using the app daily, runs it from the tray, builds in `D:\Utylix`.
Releases are made by the owner on GitHub (upload `Utylix.exe`). On the work PC Utylix is installed per user in
`%LOCALAPPDATA%\Programs\Utylix` (Start menu / desktop / startup / "Open with" all run that copy).
Standing wishes (all said explicitly):
- **Readable text everywhere**: check every popup, list, menu, tooltip, selection state in light AND dark Windows theme before handing over.
- **After every build, update the installed copy** (stop only that process - never the FanHelper -, copy the new build over it, start it with
  `--minimized`). Utylix is single-instance: an old running copy silently "wins" otherwise.
- **Keep Utylix light**: heavy engines load on first use; nothing new polls in the background; measure idle memory / CPU after big features.
- **Push often**: commit + push after every finished step and keep the plan below current - sessions can end suddenly (usage limit), and
  the owner continues at home on another PC.

## Plan (kept current - continue from here)
State on 2026-10-02 (v1.4.0, not yet released on GitHub):
- Done: Utylix Editor (PDF) - viewer, Reduce file size, own print window, editing (text, sign, pictures, shapes, white-out, pen), Edit text
  (change existing text), comments (highlight/underline/strike/notes), search/select/copy, links, bookmarks, form filling. Themed `UMessage`
  boxes everywhere. "Open with" shows Utylix Editor / Photos / Player / Archive with their own logos; the plain "Utylix" entry is hidden.
  Settings > Apps shows the right version (Installer.RefreshAppsEntry).
- **In progress - packaging as a program folder** (decided with the owner): instead of one single-file exe, publish self-contained into a
  folder (Utylix.exe + DLLs + .NET, ~150 MB, works offline). Reasons: less memory (no in-memory unpacking), faster start, fewer antivirus
  false alarms, smaller updates. Plan: `Utylix-Setup.exe` stays ONE file (single-file build with IncludeAllContentForSelfExtract, carrying
  the folder build's apphost as a content file) and installs the folder; the updater downloads `Utylix-Setup.exe` and runs it with
  `--setup-auto` instead of swapping one exe; the fan helper's protected copy must copy the whole folder; build.bat / InstallerBuilder.bat
  change. For one transition release ALSO attach a `Utylix.exe` (older copies look for that asset name).
- Next, in order: reduce to a target size ("under 2 MB"), Explorer right-click shortcuts (reduce, combine, pictures -> PDF), phone-photo
  cleanup (crop, straighten, black & white), resize pages (A4 / Letter / Long 8.5x13), stamps + date tool, real redaction, organize pages /
  merge / split / pictures <-> PDF, OCR (Windows.Media.Ocr, offline), page numbers / watermark / header-footer, PDF -> Word and -> Excel.
  Later maybe: paragraph re-flow editing, making new form fields, certificate signatures, batch processing.
- Known: a few times the PDF page jumped down by itself after switching on Edit / saving - not reproducible yet.
