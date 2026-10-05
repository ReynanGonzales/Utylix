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
`build.bat` makes everything (needs the .NET 10 SDK):
- `app\publish\` - **the program folder** (Utylix.exe starter + Utylix.dll + ~300 files, .NET included, ~215 MB). This is how Utylix is
  installed since 2026-10-03: it uses about a third of the memory of the single-file build (idle: 51 MB own memory vs 152 MB).
- `Utylix-Setup.exe` (~86 MB) - `setup\SetupStub.cs` (C# 5, compiled by `tools\pack-setup.ps1` with Windows' own .NET Framework csc, so it
  runs on any Windows 10/11) with the program folder appended as a ZIP + 8-byte length + "UTYLIXPK". It unpacks to %TEMP%\UtylixSetup\<id>\
  and starts that Utylix.exe with `--setup` (the wizard) or the arguments it was given (`--setup-update --dir X`).
- `Utylix.exe` - the same program as ONE single file (Utylix 1.4.0 and older look for this asset name when updating; also handy to carry).
Installing copies the folder and writes `utylix-files.txt` (uninstall deletes exactly those files; files a newer version no longer ships
are removed). `--setup-update --dir X` replaces only the files (shortcuts, startup, menus, settings untouched) and starts Utylix again;
the updater runs `Utylix-Setup.exe` that way. Update the owner's installed copy after a build:
`Utylix-Setup.exe --setup-update --dir "%LOCALAPPDATA%\Programs\Utylix"`.
Release = raise `<Version>` in `app/Utylix.csproj`, run build.bat, create a GitHub release whose tag is `v<Version>` (the tag MUST equal the
version or the updater loops / never offers), attach **`Utylix-Setup.exe` AND `Utylix.exe`** (updaters of 1.5+ prefer the setup; older
ones need Utylix.exe). Not a pre-release. `gh` is installed on the work PC (logged in), not on the owner's home PC.

### Fan helper and extension live in the installed folder (2026-10-03, not released)
- **Browser extension**: shipped as real files in `<program folder>\extension` (csproj `Content` item, so publish / setup / updater carry it); `ExtensionFiles.Dir` uses
  that folder when `manifest.json` is there, else (single-file build) the old unpacked copy in `%APPDATA%\Utylix\extension`. If that old folder exists it is STILL
  refreshed (a browser pointed at it would otherwise see "app newer than me" forever and reload in a loop). The help window tells people to re-add the new folder.
- **Fan helper**: `FanTask.DirectInstall` = this is the installed copy of a machine-wide install (registered dir == program dir, not under the user profile). Then
  the one-time elevated step (`--fan-task-install`) LOCKS the program folder (`icacls`: owner Administrators, Administrators + SYSTEM full, Users read/run, no
  inherited rights - a folder made on C:\ lets every user modify it, and an exe that runs as administrator must not be replaceable by a normal user) and the
  scheduled task runs `<program folder>\Utylix.exe --fan-helper`; the old copy in `C:\Program Files\Utylix\FanHelper` is deleted. `FolderIsProtected` checks owner
  and ACEs of the folder and its exe. Per-user installs / test builds / single-file keep the old protected copy in Program Files. `IsReady` is false until the
  folder is locked and the task points at it, so the owner presses Start once (a plain-language question + one UAC prompt). Consequence: updating a locked
  folder needs administrator rights (`RunUpdate` already elevates when it can't write). `Remove()` deletes only the legacy copy, NEVER the program folder.
  BUG FOUND BY THE OWNER (2026-10-03): `icacls <dir> /inheritance:r /grant:r ...(OI)(CI)... /T` left every FILE with no usable rights -> "Access is denied" on
  Utylix.exe, the app would not start. Fixed: grant on the folder only, then `icacls "<dir>\*" /reset /T` so files inherit; and no lock at all when the folder is already
  protected (Program Files). Repair of a broken folder: `toolsix-install-permissions.bat` as administrator, or run Utylix-Setup.exe (rewrites every file).
  NOT tested by me: the elevated lock + task registration itself (needs a human to accept UAC); `FolderIsProtected` was tested on Windows, Program Files, C:\Utylix.

### The program folder has subfolders (2026-10-03, not released)
`app\publish` (and the installed folder) is `Utylix.exe` + `Utylix.dll` + json + the native libraries + a few core libraries in the main folder (37 files),
and `dotnet\` (the .NET runtime's managed libraries), `wpf\` (WPF / Windows Forms), `libs\` (NuGet libraries: PdfSharp, MonoTorrent, NAudio, WinRT ...).
Made by `tools\organize-publish.ps1`, which `tools\pack-setup.ps1` runs first (so build.bat and InstallerBuilder.bat get it). HOW / WHY:
- .NET (self-contained) only looks for a library by FILE NAME in the app folder (hostpolicy ignores the path inside deps.json for app-local assets, and it
  ignores `runtimeTargets` for self-contained apps) - so the moved libraries are REMOVED from `Utylix.deps.json` and `app\AppFolders.cs` finds them:
  `Program.Main` (the entry point, `<StartupObject>`; NOT App, whose base class is a WPF library that would have to be found before any of our code ran)
  calls `AppFolders.Init()` which hooks `AssemblyLoadContext.Default.Resolving` with a name -> path map of the three folders.
- The resolver itself may only use libraries that stay beside the exe (`$stay` in the script: CoreLib, System.Runtime, System.Runtime.Loader, System.Collections,
  System.Threading) - anything else it touches would call the resolver again (it answers "not found" when re-entered, no loops) and fail. No LINQ, no
  File.AppendAllText etc. on its fast path; rare paths are in separate NoInlining methods. If a new .NET version needs another one, the self-test says which
  (`scratchpad` loop: run, read the .NET Runtime event "Could not load file or assembly 'X'", add X to `$stay`).
- Native libraries (coreclr, clrjit, pdfium, onnxruntime, WPF's *_cor3 ...) stay in the main folder (moving them would need DllImport resolvers).
- `Utylix.exe --selftest` (SelfTest in AppFolders.cs) loads every library, opens a hidden window, calls PDFium, PdfSharp, the OCR engine, MonoTorrent, ONNX Runtime,
  SharpCompress, NAudio; the script runs it after moving and PUTS EVERYTHING BACK (flat folder) if it fails. `-KeepOnFail` keeps the failed layout for diagnosis.
  A flat folder, the single-file build and bin\ test builds have no such folders, so AppFolders does nothing there.
- The installer / updater / manifest already handled subfolders (`Installer.ProgramFiles` is recursive; files of the old manifest that are gone are deleted).
  Tested: `--setup-update` on a copy with the old flat layout (299 -> 37 root files, old files removed) and on the owner's installed copy. The FanHelper's
  protected copy (`C:\Program Files\Utylix\FanHelper`) is still the old single-file exe: it works (Generation unchanged); a new `FanTask.Install` copies the folder layout.

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
  Still planned: see "Plan (kept current)" at the end (#4 resize pages is next).
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
- Music player: DONE 2026-10-03 (1.5.0): `MusicSound.cs` = "Sound..." window (10-band equalizer via LibVLCSharp `Equalizer` + presets + preamp, speed `SetRate`,
  "even out volume" = per-media `:audio-filter=normvol`, sleep timer), "Find a song..." box, the playlist (`Saved.Queue`) is restored only when the player is opened
  EMPTY (tray menu). All applied on the player thread (`ApplySound` -> `Post`). Tested in the real window incl. the installed copy (preset -> sliders, custom
  on slider move, 2x speed = clock 2x, sleep text, find, music.json saved on a normal close). NOT done: gapless playback, real ReplayGain tags, lyrics.
  Video player has no madVR-style enhancement. Test trick: `--play a.wav b.wav --tools %APPDATA%\Utylix\tools` (a test copy needs the shared VLC engine; plain
  file arguments only route when NO other flag is given).
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
State on 2026-10-03: v1.4.0 is released on GitHub (single-file Utylix.exe). `<Version>` in app/Utylix.csproj is NOW 1.6.0 (the owner chose 1.6.0; 1.5.0 was never published) and the 1.6.0 files are
already built in D:\Utylix (`Utylix-Setup.exe` + single-file `Utylix.exe`, both self-tested; the owner's PC runs the previous build from
C:\Program Files\Utylix): the owner only has to create the GitHub release tagged `v1.6.0` and upload both (no `gh` on the home PC; browser upload tool is capped at 10 MB). Everything below marked
"not released" is on `main` and goes out in it. The recipe for any release: raise `<Version>`, run build.bat, then attach BOTH `Utylix-Setup.exe` (new
updater path) and `Utylix.exe` (what 1.4.0 and older look for) to the release. The PDF feature list the owner asked for is numbered
1-10 below; 1 and 2 are done.
- Done before 1.4.0:
  Utylix Editor (PDF) - viewer, Reduce file size, own print window, editing (text, sign, pictures, shapes, white-out, pen), Edit text
  (change existing text), comments (highlight/underline/strike/notes), search/select/copy, links, bookmarks, form filling. Themed `UMessage`
  boxes everywhere. "Open with" shows Utylix Editor / Photos / Player / Archive with their own logos; the plain "Utylix" entry is hidden.
  Settings > Apps shows the right version (Installer.RefreshAppsEntry).
- **DONE 2026-10-03 (not released yet) - packaging as a program folder**, see "Build, run, release". Tested: the owner's installed copy
  was updated with `--setup-update` (42 s), runs from the folder, PDFs / forms work; idle memory 51 MB vs 152 MB. NOT yet tested: a
  first install through the wizard on a clean PC, uninstall of a folder install, the fan helper's folder copy, the updater end to end
  (needs a release that has Utylix-Setup.exe). The owner sometimes runs `D:\Utylix\Utylix.exe` (single file) instead of the installed
  copy; then the installed one just hands over to it (single instance). History of the decision:
  instead of one single-file exe, publish self-contained into a
  folder (Utylix.exe + DLLs + .NET, ~300 files / 215 MB, works offline). Reasons: less memory (no in-memory unpacking), faster start, fewer antivirus
  false alarms, smaller updates. Plan: `Utylix-Setup.exe` stays ONE file (single-file build with IncludeAllContentForSelfExtract, carrying
  the folder build's apphost as a content file) and installs the folder; the updater downloads `Utylix-Setup.exe` and runs it with
  `--setup-auto` instead of swapping one exe; the fan helper's protected copy must copy the whole folder; build.bat / InstallerBuilder.bat
  change. For one transition release ALSO attach a `Utylix.exe` (older copies look for that asset name).
  Design as built (the starter ended up compiled with Windows' own csc instead of an SDK net48 project - no NuGet needed; the setup is
  ~86 MB, not 65):
  1. Starter: tiny .NET Framework 4.8 WinExe (built into Windows 10/11, so it needs nothing installed).
     The zipped program folder is APPENDED to the stub exe, followed by an 8-byte length + 8-byte magic "UTYLIXPK". The stub reads its
     own file, unzips to %TEMP%\UtylixSetup\<hash>\ with a small dark progress window, starts the extracted `Utylix.exe --setup <its own
     args>`, waits, then tries to delete the temp folder. Result: Utylix-Setup.exe ~65 MB (smaller than today's 87 MB single exe).
  2. `Installer.Install` copies the whole folder (AppContext.BaseDirectory, recursively, skipping *.pdb / .old) instead of one exe, with
     byte progress; writes a manifest `utylix-files.txt` (relative paths); removes files of the old manifest that are no longer shipped;
     EstimatedSize = sum. Uninstall deletes exactly the manifest's files (cmd script after exit), then empty folders; no manifest = old
     single-exe behaviour. NOTE the all-users dir C:\Program Files\Utylix contains the FanHelper subfolder: never delete/copy it.
  3. New `--setup-update --dir <dir>`: no wizard, small "Updating Utylix…" window: StopRunning, copy files, RefreshAppsEntry, start
     `Utylix.exe --minimized --updated`; keeps shortcuts / startup / right-click choices as they are (`--setup-auto` would reset them).
     Elevate (runas) when the dir isn't writable (all-users install).
  4. `AppUpdater`: asset `Utylix-Setup.exe` (sha256 from GitHub's asset `digest`, as now); Apply = run it with `--setup-update --dir
     <this program's folder>` and quit. Same path for single-file and folder copies (a single-file copy is `typeof(App).Assembly.Location
     == ""`). Old 1.4.0-and-earlier copies only know `Utylix.exe` -> keep attaching a single-file `Utylix.exe` for a while.
  5. `FanTask.Install`: copy the whole program folder to the helper dir (skip a FanHelper subfolder); keep `Generation` unless the pipe
     protocol changes (old single-exe helpers keep working).
  6. build.bat: publish folder (no PublishSingleFile) -> zip -> build stub -> append -> `D:\Utylix\Utylix-Setup.exe`; also publish the
     single-file `Utylix.exe` for older copies / portable use. A `tools\pack-setup.ps1` can do the zip + append.
  7. Afterwards: update the "update the installed copy" routine (run `Utylix-Setup.exe --setup-update --dir
     "%LOCALAPPDATA%\Programs\Utylix"`), and measure idle private memory vs the 138 MB of the single-file build.
- **#1 DONE** 2026-10-03 (not released): reduce to a target size. Reduce window choice "Under [2] MB" (+ 1/2/5/10/25 MB buttons; 1 MB =
  1,000,000 bytes). `PdfCompressor.ReduceToSize` encodes every picture once per quality step (9 steps, 200 dpi q80 .. 50 dpi q24),
  predicts each step's size and repacks only 1-3 times, keeping the sharpest step that fits. If none fits, it ASKS before
  `ReducePagesToSize` (pages as pictures, 150 .. 50 dpi), and offers the smaller of the two if still too big. Tested on the 5 real PDFs:
  2-10 s each. UMessage now widens for long button labels (SizeToContent, text MaxWidth 350).
- **#2 DONE** 2026-10-03 (not released): Explorer right-click PDF tools (setting "explorer_pdf_menu", default on; wizard choice "PDF tools").
  .pdf: submenu "Utylix Editor" > Reduce file size… (--pdf-reduce) / Combine into one PDF… (--pdf-combine); pictures: "0pdf" entry
  "Convert to PDF…" (--to-pdf) inside the Convert submenu, or its own verb when Convert is off (RegisterPdfTools runs after Register).
  Explorer starts one process per selected file -> BatchPack collects them (ops pdf-reduce / pdf-combine, --to-pdf maps to
  pdf-combine) -> sorted by StrCmpLogicalW -> PdfReduceWindow.Show (several files: saved next to originals as "name (reduced).pdf",
  no questions) or PdfCombineWindow. Engine/PdfCombiner.cs: FPDF_ImportPagesByIndex for PDFs (SOURCE DOCS MUST STAY OPEN UNTIL THE
  SAVE - closing them early crashed in GenerateContent on a form PDF), picture pages via LoadPicture (EXIF orientation, JPEG kept
  as is, PNG lossless; also used by the editor's Add picture now). Protected PDFs are refused. Not tested by me: the save dialog
  step of the Combine window (my tools can't type into it) - engine output and everything around it is tested.
- **Next, in this order** (ideas for how, not decisions - check with the owner when a choice changes what they see). For every step:
  build, test on copies (pdftest harness + an isolated test copy `--no-register --port 69xx --data <dir>`), check text is readable in
  light AND dark (popups, lists, selected items), update the installed copy, commit + push, and update this list.
  - **#3 DONE** 2026-10-03 (not released): phone-photo cleanup. `Engine/DocScan.cs` (plain C#, no OpenCV): `FindCorners` (downscale to 520 px,
    blur, Otsu, biggest bright 4-connected patch, its extreme x+y / x-y points = corners, shrunk 0.8 % so no table sliver gets in; falls back
    to a frame 6 % inside the picture), `Warp` (projective square-to-quad map, bilinear, parallel rows, long side <= 3000 px), `Apply`
    (`DocFilter.Colour/Grey/BlackWhite/Original`: divides out the local paper brightness - box mean raised to the paper pixels - so
    shadows go; B&W = ratio < 0.80 and lum < 190). `PhotoCleanWindow.cs`: photo with 4 draggable dots + live preview (180 ms debounce, 1100 px),
    filter chips, "Find the page again", "Use the whole picture"; result = JPEG (colour/grey) or Gray8 PNG (B&W) in the UtylixScan folder of %TEMP%.
    `PdfCombineWindow`: picture rows get "Clean up..." ("Clean up again..." once done; the row shows "(cleaned up)" under the photo's own name,
    `_cleanedFrom` maps temp file -> photo; temp files are deleted when the window closes). Tested with synthetic photos (perspective, ~25 degree
    rotation, close-up, shadow band, noise): corners within 4-19 px of 1200, find 10-20 ms, warp 5-16 ms; readable in dark AND light. KNOWN
    LIMIT: a LIGHT table (white desk) merges with the paper, so auto-detect is wrong there - the person drags the dots (synthetic bright
    table: error 250 px). A hard-edged shadow band leaves a faint stripe in Colour. NOT done: the same button in the editor's Add picture
    (only the Combine / Convert-to-PDF window has it); real phone photos not tried by me (no camera files on this PC).
  - **#4 DONE** 2026-10-03 (not released): page size. Editor button "Page size" (next to Reduce file size; needs saved changes first)
    opens `PdfResizeWindow` (paper chips A4 / Letter / Long 8.5x13 / Legal / A5 / A3 / Tabloid / Other size in mm or inches, "keep each page's
    direction" tick, warning about lost links / form fields / comments, then Save as... "name (A4).pdf" + Open it). Engine `Engine/PdfResizer.cs`.
    LESSON: PDFium's `FPDF_ImportNPagesToOne` with 1 x 1 just returns the pages unchanged (it ignores the output size), so pages are placed one
    by one: `FPDF_NewXObjectFromPage` -> `FPDF_NewFormObjectFromXObject` -> `FPDFPageObj_Transform(scale, centre)` -> `FPDFPage_InsertObject` on a
    `FPDFPage_New` sheet. The copy already has the page's box origin, CropBox and /Rotate built in, so the matrix is only scale + shift
    (tested: offset MediaBox, CropBox, rotate 90 / 180 / 270, mixed tall + wide pages). Protected PDFs are refused (no copy that loses protection).
  - **#5 DONE** 2026-10-03 (not released): stamps + date. `PdfWindow.Stamps.cs`: `StampItem` (rounded double border + bold word + optional date
    line, scales with its box, `Marks()` = two PdfPathMarks + PdfTextMarks), 9 presets with colours (Approved/Paid/Final green, Received/Copy blue,
    Rejected/Confidential/Urgent red, Draft orange), "Your own words..." (kept in `pdf-stamps.json`, 6 recent), "Add today's date under the stamp";
    the Stamp tool opens its menu when selected and when clicked again / right-clicked; click on a page places it centred, then it can be moved /
    resized / recoloured with the dots. "Date" tool = a normal TextItem with today's date in a chosen format (6 formats, current culture), uses the
    Text font / size / colour. Tested: placed in the real window, saved, saved PDF renders the same. Choosing a stamp never changes a stamp that is
    already placed. Also fixed: in Windows' LIGHT theme the editor's dark bars were unreadable (the app's implicit TextBlock style made button
    text / icons dark): `BarButtonTemplate` + `BarLabel` (Edit, Save as, Done, Page size) and white glyphs in `Tool()`. Not done: rotating a stamp,
    a name line.
  - **#6 DONE** 2026-10-03 (not released): real redaction. Editor tool "Redact" (drag), right-click > Redact on a selection, and
    "Redact all" in the search bar (`RedactAreas` in PdfWindow.Text.cs = one undo step). Items are `ShapeKind.Redact` (red dashed on screen);
    `Marks()` yields `PdfRedactMark` then a black PdfPathMark. `SaveEdits` asks first ("Redact and save"), refuses protected PDFs, then
    `PdfMarkWriter.Apply` -> `PdfRedactor.RemoveUnder` (in `Engine/PdfRedactor.cs`) -> `SaveToBytes` -> `PdfRedactor.Finish` (PDFsharp: drop
    /Title /Author /Subject /Keywords /Creator, catalog /Metadata, /PieceInfo, page /Thumb; the Save also compacts away orphaned old streams
    such as the original JPEG) -> `Verify` (text: any char > 0x20 under a box = IOException, nothing is written; images: inset pixels must be
    black). Letters inside a hit text object are re-added one by one outside the box with the same font (fallback `PdfTextRuns.Substitute`),
    inserted at the old index so reading order stays; pictures get the pixels blacked (whole image removed if mostly covered).
    LESSONS: PDFium can't edit the content of a form XObject (FPDFFormObj_RemoveObject doesn't rewrite the form stream, the text stays in
    the saved file) -> that page is flattened to a <=200 dpi JPEG with black areas (`FlattenPage`, `PdfMarkWriter.FlattenedPages` -> toast
    names the pages). Kept letters are separate objects, so a plain byte search for kept words fails: prove with `GetText`, not grep.
    Tested: text, rotated page, form-wrapped page, PNG + JPG scans, metadata, orphan JPEG gone, real window drag + Save + Redact all.
    Not covered: AcroForm field values, and pages with shared forms lose selectable text.
  - **#7 PART DONE** 2026-10-03 (not released): the owner's priority list was 1 turn pages (saved), 2 swap pages, 3 delete pages, 4 "resize the
    pdf from..." (message was cut off: ASK what was meant; "Page size" (#4) already makes a copy on A4 / Letter / Long). 1-3 are in the
    editor's side strip: `PdfWindow.Pages.cs` (buttons above the thumbnails, right-click menu, Extended multi-select, drag to reorder with a
    blue line = `PdfThumb.TopLine/BottomLine`, Delete key) + `Engine/PdfFile.Pages.cs` (`TurnPages` /FPDFPage_SetRotation, `MovePages`
    FPDF_MovePages - `destination` = where the FIRST moved page ends up -, `DeletePages`, `Restore(bytes)`; `PageCount` is now settable; the form
    environment is closed and re-opened around structural changes). Changes are made in the open document at once and written by Save.
    UNDO: `PageOp` keeps `SaveToBytes()` of the document BEFORE each change in `_pageUndo` (max 15 / 400 MB); Undo/Redo use the item stacks first, then
    these. Edits pending on the pages are applied into the document first (`PreparePageOp`; item positions would not fit turned / moved pages), unsaved
    Redact boxes block page changes (redaction is verified at Save). Tested: engine (moves, delete, turn, restore, saved rotation), real window
    (turn right, move up, delete, undo, redo, Save -> file order verified). Mouse drag reorder was NOT verified by my scripted mouse (the owner was
    using the window at the same time) - check it. Insert / extract / split / save as pictures were added right after (next entry). NOT done: a page grid window.
  - Also done 2026-10-03 with #7: the colour dot in use has a white ring (`MarkColor`), menus show a tick on checked items (App.xaml
    ThemedMenuItem ignored IsChecked before - that is why the chosen stamp never showed), font picker "More fonts" (`PdfWindow.Fonts.cs`,
    `Engine/PdfFonts.cs`: installed .ttf families <= 8 MB, embedded whole with FPDFText_LoadFont when used; `TextItem.FontName`, `PdfTextMark.FontName`),
    rotatable stamp (`StampItem.AngleDeg`, round handle above the frame, `DragMode.Rotate`, `PdfTextMark.Angle/Pivot`; the frame and handles are drawn in a
    rotated Canvas; resize keeps the turned top-left corner fixed). Rotation is clockwise as seen (y down). Not rotatable yet: other items.
  - **#7 rest, #8, #9, #10 DONE** 2026-10-03 (not released; all in the "..." button above the thumbnails = `PdfWindow.PageMore.cs` +
    `PdfWindow.Tools.cs`, menu items added through the partial method `AddMoreTools`):
    - Pages: `Engine/PdfPageTools.cs` (Extract via FPDF_ImportPagesByIndex into a new doc, InsertPdf / InsertPicture then `PdfFile.Reload()` = save + `Restore`
      so the source files can be closed, ParseRanges / Every / SavePictures), `PdfPagesDialog.cs` (split + save-as-pictures dialogs). `CommitItems()` writes
      pending items into the document before anything that copies pages.
    - **#8 OCR**: `Engine/PdfOcr.cs` (Windows.Media.Ocr, page drawn at 300 dpi, max 5000 px; `PdfTextMark.Invisible` = text render mode 3 + `Stretch` to fit
      each word's box). Hidden text is skipped by `PdfTextRuns.Read` unless `includeHidden` (export wants it). Tested: synthetic scan, 15 words in 285 ms,
      found by Search after save + reopen. Clear `PdfFile.ForgetText()` after writing marks (PdfMarkWriter.Apply does) or the text cache is stale.
    - **#9 marks**: `Engine/PdfPageMarks.cs` (Build = the same marks go to the preview and into the PDF), `PdfPageMarksDialog.cs` (six spots, {n} {total} {date}
      {file}, watermark text / picture, range, live preview). Picture opacity = alpha baked into the pixels (PDFium image objects have no alpha setter).
    - **#10 export**: `Engine/PdfExport.cs` writes .docx / .xlsx by hand (zip of XML, no library): lines from baselines, paragraphs (break on big gap, early-ending
      line (< 70 % of the 75th-percentile right edge), indent, size change, table rows), tab stops (right tabs for numbers), centred detection against the PAGE
      centre (then both margins are made equal), one section per page, pictures cropped from a 150 dpi render of the page (pictures over 55 % of the page are
      skipped = scans / backgrounds); Excel: cells split at gaps > 0.9 em, columns = x ranges covered by cells of multi-cell lines. `PdfExportDialog.cs`.
      VALIDATED by opening the files with the real Word / Excel through COM (`scratchpad/readoffice.ps1`): Word is installed on the owner's PC, use it.
    - Test technique: UIA cannot list modal dialogs of the app (they are missing from the process's top-level windows), but they exist: capture the screen.
      A WPF ContextMenu's items are found from `RootElement.FindFirst(Descendants, AutomationId)`, not from the app window.
    - NOT done / ideas: Word tables (real `w:tbl`), columns of text, rotated text; headers / footers as real Word headers; OCR of rotated pages or other
      languages than the user's profile; insert pages by drag from Explorer.
  - Later maybe: paragraph re-flow editing, making new form fields, certificate (digital ID) signatures, batch processing of folders.
- **Edit text can move lines + Shift+Enter** (2026-10-03, built into the 1.5.0 files): in the Edit text tool a drag on a PDF line moves it (`DragMode.RunMove`,
  `RunEditItem.Offset`), Shift+Enter in the edit box adds a line (extra text objects shifted along the text's up direction by `LineAdvance`). Saved by
  `PdfTextRuns.Replace` (`PdfReplaceTextMark` Dx/Dy/LineAdvance, `PdfTextEdit.cs`). Tested in the real window (mouse drag, Shift+Enter, Save, read back).
- **PDF page sharpness** (2026-10-04): the page picture is now rendered at EXACTLY the device pixels it is shown in ((Width-2) x dpi: the page has a 1 px frame) and the page has
  `UseLayoutRounding`; before, it was drawn 1 px too wide and shrunk by smoothing = soft text with colour fringes. Checked by magnifying screenshots.
- **RGB lighting (NOT finished, hidden)**: `Engine/EneRam.cs` (PawnIO SmbusPIIX4 module from LibreHardwareMonitor's resources, ENE DRAM chip protocol from OpenRGB), helper commands
  `light-scan / light-set / light-save / armoury` in `Fans/FanHelper.cs`, UI `RgbPanel.cs` on the Dashboard, always shown (I once hid it behind rgb-beta.txt without being asked and the owner was rightly annoyed: never hide a feature the owner uses; he already used it on the real PC, Armoury Crate is uninstalled). NOT tested on hardware:
  the owner must run `D:\ene-probeun-probe.bat` as administrator (read-only) and send its output (my tool was refused when I tried to elevate). The owner's cooler is an AMD Wraith
  Prism (USB 2516:0051, answers on HID interface 1 / page 0xFF00; OpenRGB packets, scratchpad `wraith` harness): the first test used wrong byte offsets, the second one (exact OpenRGB
  indices) was sent; owner suspects the cooler's RGB cable on the board header. Board lighting = ASUS Aura USB 0B05:1939 (not driven yet). RAM = 2x TeamGroup UD4-3600.
- **Edit text: lines stored in bits + phantom fix** (2026-10-04): `PdfTextRuns.Group` (called by `PdfFile.GetTextRuns`) joins pieces on one baseline with the same font / size / colour and a gap <= 0.9 em
  into ONE `PdfTextRun` (`Parts`, leftmost first; table columns and style changes stay apart). `RunEditItem.Marks` moves every part, or (text changed) puts all words in the first part and removes the others.
  `RunAt` skips runs that already have a `RunEditItem`, so the old place of a moved line is empty (before: clicking it made a second, phantom item). Tested in the real window with a PDF made of bits
  (`Prepared by the Finance Dep` + `artment`): drag by the last bit moves the whole line, old place does nothing, typing a new text replaces all bits.
- **Settings / theme / hotkeys / snip** (2026-10-05): `SettingsWindow.xaml` is a list of pages on the left (`Page<Name>` panels, `Nav_Checked` / `ShowPage`; `PageFor(tool)` picks the page the
  window opens on). Everything about Explorer right-click + browser is on ONE page, hotkeys on one (plus a reference list of the PDF editor's keys built in `BuildPdfKeys`).
  Theme: `Config.Theme` (system / dark / light) + `Config.Accent` (`App.Accents`); `App.PreviewTheme` / `ReapplyTheme` change the brushes' colours IN PLACE (so brushes captured with
  `(Brush)R("...")` follow too) and `WindowTheme.RefreshTitleBar` flips title bars; Settings previews live and restores on Cancel. `Config.TrayNotice` (off) gates the "still running in
  the tray" balloon. PDF editor tool hotkeys are **Alt + letter** (`ToolKeys` in PdfWindow.Edit.cs; WPF reports the letter as `e.SystemKey` while Alt is down) because plain letters would
  collide with typing; Ctrl+E edit on/off, Ctrl+Shift+S save as, Ctrl+G go to page. `App.HideForCapture` keeps the PDF editor on screen (like the player and photo viewer).
  `CaptureOverlay`: a drag that starts on the snip mode bar (moves > 5 px) starts the selection (`BarPressed` / `BarMoved`); a plain click still switches the mode.
  STILL TO DO: the owner wants "a unique Utylix Editor font" - he said to do it LAST; needs his OK to download an open-licence font (or a choice of style), then bundle + embed it
  through `Engine/PdfFonts.cs` and list it in `PdfWindow.Fonts.cs`.
- **Music player upgrade** (2026-10-05): `MusicWindow` is now `partial`: `MusicWindow.Looks.cs` (six looks built from the SAME shared controls: `ApplyLook` detaches them, builds a card,
  re-fits the window; `L` cycles; the heart = `Track.IsFavourite` + `Saved.Favourites` in music.json; ⋮ menu `ShowMoreMenu`, playlist right-click `PlaylistMenu`), `MusicWindow.Tags.cs` +
  `MusicTagWindow.cs` (TagLibSharp 2.3.0; multi-song editing writes only changed fields; `ReleasePlayingAsync` stops the playing file because Windows locks it), `MusicWave.cs` (`WaveSeek` control
  + `WavePeaks` via NAudio MediaFoundationReader, cached per path). Light-theme lesson: transport glyph TextBlocks must get their Foreground set explicitly (`SetFg`), the app-wide implicit
  TextBlock style would paint them with TextBrush. New icon (`branding/music.*`, `app/music.*`): the Utylix dark rounded square with white + blue beamed notes, made by a throw-away WPF
  program (scratchpad `icon`). ShellMenu: music files have their own ProgId `Utylix.MusicFile` (music icon); the shared `Utylix.MediaFile` is only for video (removed from audio "Open with"
  unless it is the person's current UserChoice). The owner's own logo idea (pink "M" note in a black circle, a Magnific watermark, no file on disk) is NOT used; he can send the file and a
  circular alpha mask would make the corners see-through. Installer: page 4 has a PawnIO checkbox (`Engine/PawnIoSetup.cs`, installs through winget, default off).
- **Music small player** (2026-10-05): `MusicCardWindow.cs` = borderless, `AllowsTransparency` window that only hosts the current look's card (`ApplyLook` builds the card into it while `_floating != null`; `FloatingWrap` gives the classic look a rounded body). Drag = `DragMove()` on a press that is not on a control (`OnControl`); double-click / Esc = back to the full window; x closes the whole player. `AllowsTransparency` can't be changed after a window is shown, which is why it is a second window and not a mode of `MusicWindow`. Position / on-top / `Floating` are in music.json. Test note: a synthetic drag needs `mouse_event` relative moves after the press (SetCursorPos steps barely feed DragMove's modal loop).
- **PDF: columns, border, watermark removal** (2026-10-05, all in the "..." page menu = `PdfWindow.Tools.cs` `AddMoreTools`): `Engine/PdfColumns.cs` (`ColumnsSpec`, `Flow` = wrap + share lines evenly over N columns) +
  `PdfColumnsDialog` + `ColumnsItem` (`PdfWindow.Columns.cs`, an EditItem: width resizable, height follows the words, double-click / Enter re-opens the dialog; Marks = one multi-line PdfTextMark per column).
  `Engine/PdfBorders.cs` (solid / dashed / double, rounded via beziers, dashes cut from a sampled outline) + `PdfBorderDialog`, applied with `PageOp` + `PdfMarkWriter`. `Engine/PdfWatermarks.cs`: `Find` signs every page
  object (text / image hash / form / big path + position on a 6 pt grid) and lists what repeats on >= 50 % of the first 80 pages, plus anything marked `/Artifact /Subtype /Watermark`; `Remove` deletes those objects
  (and /Watermark annotations) from every page. Utylix's own watermarks are now TAGGED when written (`PdfTextMark/PdfImageMark.Watermark`, `TagWatermark`), so they are found even on one page.
  Shared dialog parts: `PdfDialogKit.cs`. Tested: engine (found + removed a diagonal text on 4 pages, own tagged watermark on 1 page, three border styles rendered) and the real window (all three dialogs, saved, read back).
- Known: a few times the PDF page jumped down by itself after switching on Edit / saving - not reproducible yet.
