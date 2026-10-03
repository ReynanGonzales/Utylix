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
  Still planned: see "Plan (kept current)" at the end (#3 phone-photo cleanup is next).
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
State on 2026-10-03: v1.4.0 is released on GitHub (single-file Utylix.exe). Everything below marked "not released" is on `main`
and will go out as **1.5.0**: raise `<Version>` in app/Utylix.csproj, run build.bat, then attach BOTH `Utylix-Setup.exe` (new
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
  - **#3 Phone-photo cleanup** (photos of documents): a "Clean up" button on picture rows of PdfCombineWindow (and in the editor's
    Picture) opens a small window: find the paper's 4 corners automatically (edge detection in plain C# - no OpenCV, the app must stay
    light), let the person drag the corners, straighten with a perspective warp, then "Document" filters: Original / Grey / Black &
    white (adaptive threshold on local means, so shadows don't go black) / brighter. Result feeds PdfCombiner as pixels or a JPEG.
  - **#4 Resize pages** (A4 / Letter / Long 8.5x13 / custom): an editor command "Page size…". PDFium `FPDF_ImportNPagesToOne(src,
    w, h, 1, 1)` makes a new document with every page scaled to fit the new paper (add the P/Invoke); keep links/forms in mind (that
    call flattens pages into XObjects - warn or keep the old size for form PDFs).
  - **#5 Stamps + date**: editor tools "Stamp" (Approved, Received, Paid, Rejected, Confidential, Draft, Copy, custom text; coloured
    rounded box + text + optional date/name line; remembered recent stamps) and "Date" (today's date text, choice of format). Draw as
    PdfMarks (path + text), flattened like the other edits.
  - **#6 Real redaction**: mark areas (and "redact all matches" of a search), then Apply = remove what is under them for real: text
    objects/characters inside (PDFium can't delete single characters - remove the object and re-add the characters outside the box as
    new text objects), picture pixels inside (GetBitmap -> paint black -> SetBitmap), paths inside, annotations, plus metadata.
    Black boxes are drawn after. Verify with the text reader that nothing under a box can be found or copied.
  - **#7 Organize pages / split / pictures <-> PDF**: a page-grid window (thumbnails): drag to reorder, rotate, delete, insert pages
    from another PDF or pictures, extract selected pages to a new PDF, split every N pages / by ranges, save pages as PNG/JPG (render at a
    chosen dpi). Pdfium.cs already has FPDF_MovePages, FPDFPage_Delete, FPDF_ImportPagesByIndex, FPDFPage_SetRotation. Merging = #2's
    Combine window (reachable from the editor too).
  - **#8 OCR (scans -> searchable)**: Windows.Media.Ocr (built into Windows, offline; the TFM already targets 10.0.19041). Render each
    page at ~300 dpi, OcrEngine.TryCreateFromUserProfileLanguages, then put each word as INVISIBLE text (text render mode 3; add
    FPDFTextObj_SetTextRenderMode) at its box, sized to the box. Afterwards search/select/copy work. Offer it when a page has no text.
  - **#9 Page numbers / watermark / header-footer**: one dialog: text with {n} / {total} / {date} / {file}, position (6 spots), font,
    size, colour, start number, page range, skip first page; watermark = big diagonal text or a picture with opacity (fill alpha).
    Written as PdfMarks, with a live preview on the page.
  - **#10 PDF -> Word and -> Excel**: Word: group text runs (PdfTextRuns gives position, font, size) into lines and paragraphs, keep
    bold/italic/size, pictures inline, page breaks; write .docx as plain OOXML in a zip (no big library). Excel: find tables from text
    positions (columns by x clusters, rows by y) -> .xlsx (plain OOXML). Scanned pages need #8 first.
  - Later maybe: paragraph re-flow editing, making new form fields, certificate (digital ID) signatures, batch processing of folders.
- Known: a few times the PDF page jumped down by itself after switching on Edit / saving - not reproducible yet.
