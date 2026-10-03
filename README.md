# Utylix

Your PC tools in one small Windows app: a fast download manager with browser capture, Multi Convert (pictures, videos and music), an archive manager (ZIP/RAR/7z), screen
capture with a text detector, a screen recorder, and more tools on the way (QR maker, background remover).

`Utylix.exe` is a single file. **Nothing else needs to be installed** – the .NET runtime is inside it – so after a
fresh Windows install you just copy it back and run it. No admin rights needed.

- **One window, one tab per tool** – Downloads, Multi Convert, Screen Capture, Screen Recorder. The window changes size to suit the
  tool you pick (and remembers a size you dragged it to). Closing it keeps Utylix running in the tray (so the
  browser can still hand it downloads and the capture shortcuts keep working); *Exit* in the tray menu quits it.
  Light/dark theme follows Windows.
- **Downloads** – up to 8 parallel connections per file (1–32 in Settings), pause/resume, survives restarts,
  auto-retries; captures downloads from your browser; YouTube/Facebook and other video sites.
- **Multi Convert** – pictures (PNG, JPG, WebP, BMP, GIF, TIFF, ICO), videos (MP4, MKV, WebM, MOV, AVI, animated GIF)
  and music (MP3, M4A, WAV, FLAC, OGG); also from the Explorer right-click menu and while downloading a picture.
- **Screen Capture** – like the Snipping Tool: rectangle, window or full screen, with a delay, pen/highlighter marks,
  copy and save. Plus a **Text Detector** that reads text off the screen or out of a picture.
- **Screen Recorder** – records the screen (all of it, one monitor, an area or one window) to MP4 or a GIF, with system
  sound and/or microphone, pause and resume, a countdown, click highlights and hotkeys (**Ctrl + Alt + R** start / stop,
  **Ctrl + Alt + P** pause).
- **Video Player** – a VLC-style player in its own window that plays practically every video and music format, with
  subtitles (a `.srt` with the same name next to a video turns on by itself), audio tracks, speed, full screen, a playlist
  and "continue where you stopped". Right-click a video → **Play with Utylix**.
- **Background Remover** – right-click a picture → **Remove background**: an AI model running on your PC saves a PNG with
  a transparent background next to the picture.
- **Brightness** – tray icon → **Brightness**: a small panel with a slider for every screen, like *Monitorian* (external
  monitors over the cable, a laptop's own screen through Windows).
- **Updates** – Utylix checks GitHub now and then for a newer version of itself and asks before installing it (tray icon →
  **Check for updates…**, or Settings → *Updates*).
- **Settings match the tab** – *Settings* opens with just the settings of the tool you are in (Downloads, Image
  Converter or Screen Capture), with *Show all settings* to see the rest.
- **Backup** – Settings → *Backup* exports your settings to a file and imports them again after a reinstall.

## Use it

1. Run `Utylix.exe`. Press **Win + F** anywhere to bring Utylix up (again to put it back in the tray; it replaces Windows'
   Feedback Hub shortcut; switch it off in Settings → *Shortcut*, where you can also turn off Windows' own Win + F and
   Win + S for your account so Windows never opens the Feedback Hub or Search on them (takes effect after signing out and in).
2. For the browser features, add the extension once: the setup's last page offers it, or use the tray icon → **Browser
   extension…** (also Settings → Downloads). The extension is built into Utylix.exe and unpacked into
   `%APPDATA%\Utylix\extension` (kept current); that window has buttons that open your browser's extensions page and the
   folder, and shows the three clicks: turn on **Developer mode**, **Load unpacked**, choose that folder. (By hand:
   `chrome://extensions` / `brave://extensions` and the `extension` folder of this repository.)
3. Download something in the browser – it shows up in Utylix. If Utylix isn't running, the extension starts it. Utylix comes up on its Downloads tab when a download starts (Settings → Downloads switches this off).

## Dashboard and fans

Click the **Utylix logo** at the top left to open the **dashboard** (click it again to go back to the tool you were in): quick
actions (take a snip, record the screen, video player, convert files, downloads, brightness, browser extension, check for
updates), what the downloads are doing, and under *This PC* the fans.

The fan part shows the temperatures (processor, graphics card, one board reading) and, two to a row, every fan that spins with
a little fan icon that turns as fast as the real one, and lets you set each fan: **Automatic** (the PC decides, as always), **Fixed speed** (a slider), or **Curve** (the fan speed follows
the processor's or the graphics card's temperature: start from *Quiet*, *Balanced* or *Performance* and edit the points; a
point is `temperature in °C : speed in %`). Double-click a fan's name to call it "CPU fan" or "front fan".

- **No clicking after the first time.** The first **Start fan control** asks Windows for permission once and sets up a Windows
  task that starts the helper without a prompt; from then on Utylix starts fan control by itself whenever it starts. For safety
  that task runs a copy of the program in the protected Program Files folder (`C:\Program Files\Utylix\FanHelper`), never the
  one in your own folder, and the copy is checked against Utylix; after an update, press Start once more (one prompt). Untick
  *Start fan control by itself* to go back to a prompt each time, or *Remove this from the PC…* to delete the copy and the task
  (uninstalling Utylix does that too). The Fans page lists only the processor, graphics card and one board temperature unless
  you tick *Show every sensor*.
- **Start fan control** asks Windows for administrator permission: reading the motherboard and setting fans needs it, so it is
  done by a separate helper (`Utylix.exe --fan-helper`, started from the button); the rest of Utylix stays a normal program.
  It uses the open-source LibreHardwareMonitor library. The processor and motherboard sensors need the free **PawnIO** driver
  (`winget install namazso.PawnIO`); the page says so if it is missing.
- **Safety:** every fan stays under the PC's own control until you change it. A fan never goes below the lowest speed you set
  (25 % by default); if the processor or the graphics card reaches its limit (85 °C by default) every controlled fan goes to
  100 %; a curve with no temperature reading hands the fan back. When you press **Stop**, close Utylix, or Utylix crashes, every
  fan is handed back at once. (If the helper process itself is killed in Task Manager the fans keep the last speed until
  you restart the PC or start fan control again and press Stop.)
- Settings are kept in `fans.json` in Utylix's data folder. Which fans can be set depends on the motherboard and the graphics
  card; laptops usually do not allow it.

## Several Windows users at once

Every Windows user who is signed in runs their own copy of Utylix with their own settings and downloads. The first one
listens on port 6800; the next user's copy takes the next free port (6801, 6802 ...) and notes it in `port.txt` in their own
data folder, so nobody gets an error and nobody's downloads end up in someone else's Utylix. Explorer's right-click menus and
the browser link find the right copy by themselves (the extension asks its helper, which runs as the browser's user, for the
port; after updating Utylix, reload the extension once in `chrome://extensions`).

## Installing and uninstalling

Run **`InstallerBuilder.bat`** to build the installer: `installer\Utylix-Setup.exe`, one file you can give to anyone (it is
the same program as `Utylix.exe`, started under that name). Double-click it to run the wizard. On a PC
where Utylix has never run, a plain `Utylix.exe` offers the same wizard. It has five pages:

1. **Welcome** – what Utylix is. (*Just run it, don't install* keeps it portable.)
2. **Where to install** – *Just for me* (no administrator rights; `%LOCALAPPDATA%\Programs\Utylix`) or *For all users of this
   PC* (Windows asks for administrator permission; `C:\Program Files\Utylix`, shortcuts and startup for everyone), and the folder.
3. **Right-click menus and startup** – Convert, Utylix Archive, Remove background, Play with Utylix, run Utylix when Windows
   starts, desktop shortcut.
4. **Extra parts to download** – yt-dlp, ffmpeg, the player engine, the background remover's AI model (each from its official
   source, checked against its fingerprint; skipped ones are offered later when needed).
5. **Installing** – a progress bar, then *Start Utylix now*.

It adds Utylix to *Settings > Apps* and the Start menu. Uninstall from there, or with **`Uninstall.bat`**: it removes the
program, its shortcuts, Start with Windows, the apps entry and **every right-click menu and file type Utylix added**, and asks
whether to delete Utylix's own settings and downloaded tools too (your downloads, recordings and screenshots are never touched).
For an "all users" install, updating or removing asks for administrator permission again.

## Downloads

Works in every Chromium browser: Brave, Chrome, Edge, Opera, Vivaldi. The extension cancels the browser's own
download before the browser saves anything and hands the link (plus the cookies, referrer and user-agent the
browser used) to Utylix. If the app can't fetch the file, the browser gets it back, so nothing is lost.

- **"New download" window** – shows the size, whether fast multi-connection download is possible, the file name,
  a type (Compressed, Picture, Video, Music, Document, Application, Other) and where it will be saved; choose
  *Start download*, *Download later* or *Cancel*. Turn the window off in Settings → *Browser capture*.
- **When done** – the "New download" window has a *When done* row: *Just notify me*, *Open it* or *Show in folder*
  (your last choice is remembered). Programs and scripts (.exe, .bat, .ps1 ...) are never opened by themselves; they
  are shown in their folder instead.
- **Pictures with the wrong name** – a photo that a site calls `photo.img` (which Windows shows as a "Disk Image")
  is saved as a picture: Utylix trusts the picture type the server reports, and for files with no type it checks the
  file's first bytes when the download finishes. Real disk images are left alone.
- **Click the "Download complete" notice** – it opens the folder with the file selected.
- **File icons** – the window and the list show the icon Windows uses for that file type.
- **Folders by type** – downloads go to `Compressed`, `Picture`, `Video`, `Music`, `Document` and `Application`
  inside your download folder (change or switch off in Settings). Pick a different folder for a type in the
  window and it becomes the new default for that type.
- **What gets captured** – every download, or only chosen file types, a minimum size, and sites to skip.
- **Video download button** – hover a video and *Download this video* appears. It lists the video/audio files the
  page loaded and, for YouTube/Facebook and similar sites, the qualities (2160p … 144p, or audio only). The
  quality menu has **Video + sound / Video only / Sound only**; the default is always one file with both. The
  button hides while a video is fullscreen and can be switched off in Settings (right-click → *Download with
  Utylix* keeps working).
- **YouTube, Facebook and other video sites** – uses two free open-source helpers, *yt-dlp* (finds the video) and
  *ffmpeg* (joins picture and sound into ONE MP4). Install them once in Settings → *Video sites* (official GitHub
  releases, checked against their published checksums; they live in `%APPDATA%\Utylix\tools`, so after a
  reinstall just click Install again). Protected (DRM) video is not supported; only save videos you're allowed to keep.
- **Vertical videos and good names** – reels, Shorts and other portrait videos are measured by their shorter side
  (a 1080x1920 reel is "1080p") so the quality you pick is really what you get, instead of "Requested format is not
  available". Facebook titles like "1.1M views · 8.1K reactions | caption | Page" are cleaned to just the caption
  (or the post text when a site only says "Facebook"), and emoji are dropped, so the file is named after the video.
- **Subtitles** – save as a separate `.srt` (default), inside the MP4, or skip; choose languages and whether
  machine-made captions count.
- **Keeping YouTube working** – Utylix checks for a newer yt-dlp every few days and *asks* before installing it;
  a failed video download has an **Update yt-dlp & retry** button.
- **Copied-link popup** – copy a link to a file or a video page and a small popup offers to download it.
- **Starts itself when the browser needs it** – through the browser's "native messaging" (registered for the
  current user only; it only answers the Utylix extension and can only start the app). Switch off in Settings.
- **Picture in Picture** – right-click a video on any site except YouTube (which has its own button) → **Picture in
  Picture** (under "Utylix Integration"): the video floats in a small window that stays on top; choose it again to put the
  video back. There is just one entry, and it only appears while the pointer is over a video. It also finds videos that a
  player covers with its own layer and works on sites that switch Picture in Picture off (where the browser's own entry
  is missing or greyed out). If the browser insists on a click first, a note asks you to click the video once. Facebook and
  Instagram hide the browser's right-click menu on videos, so there Utylix lets the normal menu through; and **Alt + P** (change
  it in `chrome://extensions/shortcuts`) floats the video that is playing on any page, no right click needed. While a video floats, Utylix follows the feed: swipe to the next Reel (or the next video of a playlist starts) and the floating window switches to it by itself. Scroll the mouse wheel over the floating window to go to the next (wheel down) or previous (wheel up) Reel or video: the browser never passes the wheel to the page, so the Utylix app watches for it while a video floats (only over a window titled "Picture in picture"; it needs Utylix running). After
  updating, reload the extension in `brave://extensions` (and refresh open pages once).
- **Pictures** – "Save image as" is captured like any file; for pictures that are only displayed, use right-click →
  *Download with Utylix* or the extension popup's *Images on this page*.

## Torrents and magnet links

Utylix downloads torrents itself (the [MonoTorrent](https://github.com/alanmcgovern/monotorrent) library); uTorrent is not needed.

- **Add one:** paste a `magnet:` link or the address of a `.torrent` file in the Downloads tab, copy one (the copied-link popup offers it), or click a magnet link / open a `.torrent` file in Windows. Utylix is offered to Windows for both (Settings > Torrents; where nothing is set yet it becomes the default, a choice such as uTorrent is never overridden - change it in Windows' "Default apps").
- It shows in the list like any download: peers and seeds, speed, time left, Pause / Resume, and "Delete" to remove the files too. A multi-file torrent is a folder named after the torrent, in `Downloads\Torrents`.
- A finished torrent stops uploading by default; Settings can keep it sharing while Utylix is open, and set speed limits.
- The browser extension also hands over `.torrent` files it catches.
- Windows may ask once whether Utylix may use the network: allow private networks.

## Screen Capture

There is no tab for it in the main window: press **Win + S** and a snip starts at once (the tray icon → *Screen Capture*
opens the window instead). The small **Utylix Snip** window, in Utylix's own look, has its buttons where the classic Windows
Snipping Tool has them:
**New**, **Mode** (*Free-form*, *Rectangular*, *Window*, *Full-screen* snip, and *Text Detector*; choosing one starts the
snip), **Delay** (none, 1 to 5 seconds), **Cancel** and **Options** (the capture settings). Utylix hides itself, freezes the
screen and dims it: drag over the part you want, draw around it, or click a window; Esc or right-click cancels. The snip
shows in the window, where you can draw on it with the **Pen**, **Highlighter** (six colors, three thicknesses),
**Eraser** and **Undo**, then **Copy** (Ctrl + C), **Save** or **Save as…** (Ctrl + S), or **Detect text**. Ctrl + N takes a
new snip, Ctrl + O opens a picture, Ctrl + V pastes one, and you can drop a picture on the window. The window opens where
the snip was taken (the picture lands on the spot it was cut from), the snip lies on a white sheet like in the Snipping Tool,
and the pen also draws on that sheet; such marks are kept when you copy or save (the picture then comes with white around it).

- **Shortcuts, from anywhere:** **Win + S** and **Ctrl + Alt + S**. Windows keeps Win + S for Search, so while it is on
  Utylix takes that shortcut for itself (it swallows only that one combination, never records keys, and does not work
  over programs running as administrator). Turn either off in Settings → *Screen Capture* to get Windows Search back.
- **Settings → Screen Capture:** copy every capture to the clipboard (on by default), save every capture as a file
  (off by default), the screenshot folder (default `Pictures\Screenshots`).
- **Text Detector** – choose the mode and drag over any text on the screen: Utylix reads it with the text recognition
  built into Windows (nothing to download), shows it in a box and copies it to the clipboard. After any capture,
  **Detect text** reads that picture. You can also *Open image…*, *Paste image* or drop a picture on the tab. If Windows
  has several recognition languages installed you can pick one; add more under Settings → Time & Language → Language.

## Screen Recorder

Choose what to record, then **Start recording** (or press **Ctrl + Alt + R** anywhere; press it again to stop;
**Ctrl + Alt + P** pauses and continues):

- **Record** – *Full screen* (with several monitors: all of them or one), *Area* (drag over the part you want) or
  *Window*: a list of your open windows (with their program icons) appears, you click the one to record, and Utylix
  brings it to the front (un-minimizing it) and records the area it covers. It does not follow if you move the window
  afterwards. (Recording the screen area, not the window itself, is what makes browsers and games work: a window's own
  picture comes out blank for programs that draw with the graphics card.)
- **Sound** – *System sound* (everything you hear) and/or *Microphone* (the default one, or pick another). They are mixed
  into one track. Windows sends nothing while the speakers are silent, so Utylix fills those gaps: sound and picture stay
  in step. (A GIF has no sound.)
- **Format, quality, frame rate** – MP4 video (Low / Normal / High, 15 / 30 / 60 fps) or an animated GIF (at most 15
  pictures a second and 800 pixels wide, for short clips).
- **Countdown** of 3, 5 or 10 seconds, and options to show the **mouse pointer** and to **highlight clicks** with a ring
  (left click yellow, right click blue) that shows in the video.
- **While recording** Utylix hides itself and shows a small bar with the Utylix logo, the time, **Pause / Resume** and
  **Stop**, and a **red frame with a "REC" tag** around what is being recorded (amber and "PAUSED" while paused; switch
  the frame off with *On screen* on the tab). The frame and tag sit just *outside* the recorded area, so they are never
  in the video; where there is no room outside (full screen, an area at the screen edge) and for the bar and countdown,
  Windows keeps them out of the video (Windows 10 version 2004 or later). The tray icon says it is recording and
  its menu has *Stop recording*. Pausing leaves the pause out of the video.
- **Saved** as `Utylix Record <date> <time>.mp4` (or `.gif`) in `Videos\Utylix` (change it in Settings → *Screen Recorder*); a notice appears, and clicking it opens the
  folder. The tab shows the last recording with **Play**, **Show in folder** and **Delete** (moves it to the Recycle Bin, so
  you can still get it back). If Utylix is closed while recording,
  the recording is finished and saved first; if it is killed, Windows ends the recording program too (that half file
  can't be played and is removed at the next start).
- Uses **ffmpeg** (the same helper as Multi Convert; the tab offers to install it) for filming and encoding, and the NAudio
  library (MIT, inside the exe) to listen to the sound. Tested at 100% display scaling. Protected video (Netflix and the
  like) is shown black by Windows in every screen recorder.

## Video Player

The **Video Player** tab opens files and lists what you played recently (with "continue from 12:34"); the player itself
is its own window, one for everything you open. Open it from the tab, by dropping files on it, or from Explorer:
right-click a video or music file → **Play with Utylix**, or *Open with → Utylix* (tick *Always* to make it the default;
the installer will do that for you later). If Utylix was started only to play a file, it quits when the player closes.

- **Engine** – the player uses libvlc, the engine inside VLC (videolan.org), so it plays MP4, MKV, AVI, FLV, WMV, WebM,
  MOV, TS, MP3, FLAC, OGG, M4A, Opus, WMA and many more. If VLC is installed on the PC Utylix uses that; otherwise it asks
  once before downloading the engine (about 80 MB from videolan.org, checked against the checksum VideoLAN publishes) and
  keeps its own copy in `%APPDATA%\Utylix\tools\vlc`. Settings → *Video Player* shows which one is in use.
- **Subtitles** – a subtitle file next to the video with the same name (`Movie.srt` for `Movie.mkv`; also `.ass`, `.ssa`,
  `.sub`, `.vtt`, `.smi`) is switched on automatically, like in VLC. Also found: `Movie.en.srt` / `Movie.English.srt`
  (English first) and files in a `Subs` or `Subtitles` folder. *Subtitle → Add Subtitle File…*, track choice and delay
  (G / H) are there too.
- **Playing** – Space pause, double-click or **F** full screen (the controls appear on top of the picture while the mouse
  moves), arrows seek 10 s (Shift 3 s, Ctrl 1 min), Up / Down or the wheel change the volume, **M** mute, **[** **]** speed
  (0.25× – 4×), **E** next frame, **V** / **B** next subtitle / audio track, **A** aspect ratio, **Shift + S** snapshot
  (`Pictures\Utylix Snapshots`). All of it is in the menu bar too (*Help → Keyboard Shortcuts*).
- **Playlist** – open one file and the other videos of its folder follow it; *Open Folder…*, drag files in (hold Ctrl to add
  instead of replace), repeat (playlist / one file), random, *Open Network Stream…*. **Ctrl + L** shows the list.
- **Remembers** where you stopped in each file (not for files under a minute), the volume, the window size and a recent list.
  The screen stays awake while a video plays.

## Photo viewer

A quick, dark window for pictures. Open one from Explorer (right-click > Open with > Utylix, or make it the default for pictures in Windows' Default apps), from the Dashboard / tray ("Photo viewer…"), or drop pictures on it.

- Left / Right (or the arrows on the sides, or the mouse's back / forward buttons) go through the folder in Explorer's order; Home / End jump to the first / last.
- The wheel zooms around the pointer, drag moves, double-click toggles fit / 100 %. `+` `-` zoom, `0` fits, `1` is actual size.
- `R` / Shift+R rotate (photos are turned upright by their own orientation tag), Space plays a slideshow (3 s), `T` shows a strip of small pictures, F11 (or F) is full screen.
- Ctrl+C copies the picture, Delete moves it to the Recycle Bin, Esc closes.
- It reads what Windows can decode: JPEG, PNG, GIF (first frame), BMP, TIFF, ICO; WebP, HEIC and AVIF need Microsoft's free codec extensions from the Store (the window says so).

## Utylix Editor (PDF)

Utylix Editor reads, fills in, edits, signs, prints and shrinks PDFs with PDFium (the PDF engine inside Chrome), offline. Open one from Explorer (right-click > Open with > Utylix Editor, or make it the default for .pdf in Windows' Default apps), from the Dashboard / tray ("PDF editor…"), or drop PDFs on the window.

- The pages sit in one column; only the pages on screen are drawn, sharp at any zoom. Ctrl + wheel or Ctrl+`=` / Ctrl+`-` zoom, Ctrl+2 fits the width, Ctrl+0 shows the whole page, Ctrl+1 is 100 %.
- The page box jumps to a page; F4 shows the small pages at the side, or the **Bookmarks** (the PDF's table of contents). Ctrl+R turns the pages (for looking only).
- **Text**: drag to select (double-click a word, triple-click a line), Ctrl+C copies, Ctrl+A selects the page; Ctrl+F (or the magnifier) searches the whole PDF, Enter / F3 for the next. Links work (web links ask first); pointing at a note shows its text. Right-click: copy, highlight / underline / strike out the selection, add a note, search.
- **Forms**: a PDF with fields says so in a blue bar. Click a field to type (Tab: the next one), tick boxes and round options, pick from lists; a signature field opens "Your signature". Ctrl+S or Save keeps the values in the PDF (drawn into it, so every reader shows them). XFA forms can't be filled.
- **Edit**: Edit text (click a line of the PDF's own text and change it: the same font when the PDF has the letters, else the same Windows font, else one like it), Text (type anywhere), Sign (draw, type or a photo of your signature; remembered), Picture, Check / Cross, Highlight / Underline / Strike (on text; Highlight also as a box), Note (a real PDF comment), Pen, Shapes (box, circle, line, arrow), White-out (covers; it doesn't erase what is under it). Select moves / resizes, Delete removes, Ctrl+Z / Ctrl+Y undo / redo. Save writes into the PDF, Save as… into a new one; a protected PDF stays protected.
- **Print**: Utylix's own print window with a preview of every sheet: printer and its own settings, copies, pages ("1-3, 5", odd / even, reverse), fit / actual size / shrink big pages, 1 to 16 pages per sheet, orientation, paper (with Long 8.5 × 13 when the printer has it), colour or black and white, two-sided, quality.
- Ctrl+O opens another PDF; PDFs with an open password ask for it.
- **Reduce file size**, like Acrobat's: Recommended (pictures and scans at 150 dpi, text and links untouched), Smaller (110 dpi, lower quality), or Smallest (each page becomes one picture; for scans that are still too big, text can't be selected afterwards). Pictures that are really grey are stored grey, and fonts or pictures stored many times over are kept once. The original is never changed: the smaller copy is saved where you choose. A PDF protected against changes needs its owner (permissions) password, and the smaller copy keeps the same passwords and permissions ("Smallest" isn't offered for protected PDFs).

## Music player

Songs open in their own player (videos still open in the Video Player): a playlist with cover art (from the file, or a `cover.jpg` / `folder.jpg` next to it), shuffle, repeat (off / all / one), and the keyboard's media keys (play-pause, next, previous) while it is open. Open one song from Explorer and the rest of its folder follows it. "Add folder…" adds a folder and its folders; "Show: …" switches the list and the player between song + artist, artist only and song only; "Small player" hides the list. The tray menu has "Music: play / pause" and "Music: next song".

## Background Remover

Right-click a picture (PNG, JPG, WebP, BMP, TIFF, HEIC, AVIF) → **Remove background**. A few seconds later a notice says
it is done, and `name (no background).png` sits next to the original, with a transparent background; the original is never
touched. Several pictures at once work too.

- The AI model is **isnet-general-use** (Apache-2.0, from the rembg project), run on your PC with ONNX Runtime: nothing is
  uploaded. The first time, Utylix asks before downloading it (about 170 MB from GitHub; checked against its fingerprint).
- Switch the menu entry off, or download the model ahead of time, in Settings → *Multi Convert* (Tools).

## Brightness

Right-click the Utylix tray icon → **Brightness**. A panel opens by the taskbar with one slider per screen (drag it, click
on the bar, or use the mouse wheel over it); it closes when you click somewhere else. With two or more screens, *Move all
screens together* adjusts them at the same time.

- External monitors are controlled with **DDC/CI**, the same way Windows-side tools like Monitorian do it. If a monitor is
  listed as "does not answer brightness commands", switch *DDC/CI* on in the monitor's own menu (some TVs, docks and
  adapters do not pass it on). A laptop's built-in screen is controlled through Windows itself.
- Nothing is saved or changed at startup: the monitor keeps the brightness you set, and the panel always shows what the
  monitor reports.

## Updates

Utylix updates itself from the **Releases** of its GitHub repository (`ReynanGonzales/Utylix`).

- About once a day (and whenever you choose tray icon → **Check for updates…**) it asks GitHub for the newest release. If it
  is newer than the version you run (Settings → *Updates* shows it), a notice appears; clicking it shows what is new.
- **Update now** downloads `Utylix.exe` from that release, checks it against the SHA-256 checksum GitHub publishes for the
  file (a file that does not match is thrown away), puts it in place of the running program, and restarts Utylix.
  Downloads in progress are paused; a screen recording in progress has to be stopped first. Nothing is ever installed
  without your click.
- **Private repository:** GitHub shows nothing to a program that is not logged in. Create a *fine-grained personal access
  token* on github.com for this repository with read-only permission for **Contents**, and paste it under Settings →
  *Updates* → *GitHub access token…* (or in the window that explains it). It is stored encrypted for your Windows user.
  If the repository is made public, no token is needed.

### Publishing a new version (for the developer)

1. Raise `<Version>` in `app/Utylix.csproj` (for example `1.2.0`) and run `build.bat`.
2. On GitHub: *Releases → Draft a new release*, tag `v1.2.0`, write what is new, attach the built **`Utylix.exe`** (the file
   must be called exactly that), and publish. The notes you write are what the update window shows.

## Multi Convert

Open the Multi Convert tab. Add files with the button or by dropping files/folders (mixing kinds is fine), choose
**Images**, **Video** or **Audio** and a format, and click Convert. The window shows progress for each file and has a
**Cancel** button (a cancelled file leaves nothing half-written). Originals are never changed; a taken name becomes
`clip (1).mp4`. What can become what: pictures into pictures; videos into other video formats, an animated GIF, or just
their sound (MP3, M4A, WAV, FLAC, OGG); music into other music formats. Anything that doesn't fit (a song into a video)
is skipped with the reason shown.

- **Pictures** – optional JPG/WebP quality and a maximum size for the longest side (never enlarged). Transparent areas
  become white for JPG and BMP; ICO gets the sizes 16–256 px; phone photos keep their right way up.
- **Video** – quality *High / Balanced / Small file* and a size limit (*Original / 1080p / 720p / 480p*, never enlarged).
  MP4, MKV and MOV use H.264 + AAC (plays everywhere), WebM uses VP9 + Opus, AVI is for old players, GIF makes a short
  silent animation.
- **Audio** – a bit rate for MP3 / M4A / OGG (128–320 kbps); WAV and FLAC are lossless. Tags are kept.
- **ffmpeg** – videos and music (and WebP) are converted with ffmpeg, the same free helper the video downloader uses.
  If it isn't installed, the tab shows an **Install ffmpeg** button (about 100 MB, checked against its checksum).

- **Right-click → Convert** – in Explorer, right-click a picture, video or music file → **Convert** → pick a format
  (pictures: JPG / PNG / WebP / BMP / GIF / ICO; videos: MP4 / MP3 sound only / animated GIF; music: MP3 / M4A / WAV /
  FLAC) and it converts in the background next to the original; a notice says when it is done (click it to open the
  folder). *More options…* opens Multi Convert with the file in its list. Added for your Windows account only; **switch it
  off in Settings → Tools**. (On Windows 11 it is under *Show more options*.)
- **Convert while downloading** – for a picture in the "New download" window, *Convert to* downloads it and then
  really converts it (not just renames). The original is removed unless you tick *Keep the original picture too*.

## Archives (a small WinRAR)

Archives are **not** a tab of the main window. Like WinRAR, they open in a window of their own: double-click a
`.zip`, `.rar`, `.7z`, `.tar.gz` ... (after choosing Utylix under *Open with*), or right-click it. Only that window
appears, one per archive, and if Utylix wasn't running it quits again when you close it.

- **Open and browse** – folders, sizes, packed sizes, dates; double-click a folder to go in, *Up* to go back. Double-click a
  file to open it (a program or script asks first). Formats it opens: ZIP, RAR, 7z, TAR, GZ, BZ2, XZ, TAR.GZ / TAR.BZ2 /
  TAR.XZ, CBZ, CBR, JAR.
- **Select and drag out** – click **Select all** (or Ctrl + A), Ctrl / Shift-click, or draw a box around files with the
  mouse (start on empty space or beside a name; hold Ctrl to add). Then drag them onto any folder in Explorer, or the
  desktop: they are extracted right there, with their subfolders. Nothing is unpacked until you let go. A file
  dragged out of a subfolder arrives on its own, not inside its parent folders. If the archive has a password, it is
  asked for when you start the drag.
- **Extract** – everything or only what you select, to a new folder named after the archive (default), next to it, or
  anywhere. If a file already exists: rename the new one (default), overwrite, or skip. Progress and Cancel; **Test
  archive** checks it is not damaged. Passwords are asked for (a wrong one is refused and asked again) for encrypted ZIP,
  RAR and 7z. Files can never be written outside the chosen folder, even by a booby-trapped archive.
- **Create** – *Add to archive…* opens a window to build a **ZIP**, **TAR.GZ** or **TAR** (Store / Fast / Normal / Best),
  with a name and folder of your choice. For a **ZIP** you can type an optional **password** (twice): the files are then
  locked with AES-256 encryption and open in 7-Zip, WinRAR, WinZip and Utylix. File names stay visible in a ZIP, a password
  can't be recovered, and a password-protected ZIP can't hold files over 4 GB. RAR is a closed format that only WinRAR can
  write, and 7z can be opened but not yet created.
- **Right-click, like WinRAR** – one **Utylix Archive** submenu. On an archive: *Open with Utylix*, *Extract files…* (asks for a folder), *Extract here*, *Extract to folder*, and also *Add to archive…* / *Add to ZIP*. On any other file or folder: *Add to archive…* and *Add to ZIP* (several
  selected files become one ZIP named after their folder). Utylix is also listed under *Open with* for archives. Switch off
  in Settings (with the other Explorer options); nothing is changed for other users, and no admin rights are needed. The logo in the menu is read from Utylix.exe itself.
  (WinRAR writes the archive's own name into each entry; that needs a native Windows extension, so these say "folder".)
- Folders in an archive are shown as yellow folders like in Explorer.
- The installer (when there is one) can make Utylix the default program for these types; until then choose *Open with →
  Utylix → Always* once for each type.

## Build from source

`build.bat` (needs the .NET 10 SDK) publishes a fresh single-file, self-contained `Utylix.exe` into this folder
(about 70 MB, because the runtime is inside). Source is in `app/`. Branding files are in `branding/`.
Settings and history live in `%APPDATA%\Utylix`.

## Limits

- Downloads: only direct HTTP(S) links and the video sites yt-dlp supports; one-time or POST-only links can't be replayed.
- The speed-up only happens when the server allows Range requests.
- Only download things you're allowed to download.

## Security notes

The extension talks to the app over `127.0.0.1:6800` only. The API rejects requests from web pages (Origin/Host
checks), accepts only `http`/`https` URLs and existing local picture files, and cannot open or delete files.
Downloaded files get the Windows "from the internet" mark so SmartScreen still checks them, and cookies are wiped
from the saved state once a download finishes.
