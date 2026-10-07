# Utylix

Made by **Reynan Gonzales** - <https://github.com/ReynanGonzales/Utylix>. A personal all-in-one toolbox for Windows. (Inside the app: tray menu or Settings > *About Utylix…*.)

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
  **Check for updates…**, or Settings → *General*).
- **Settings with a page for each thing** – a list on the left: *General* (start with Windows, the "still running in the tray" notice
  - off by default -, updates, backup), *Appearance*, *Right-click menus and browser* (every Explorer entry, the magnet-link handler and
  all the browser-extension options together), *Hotkeys* (Win + S, Ctrl + Alt + S, Ctrl + Alt + R / P, Win + F and the whole list of PDF
  editor keys), *Downloads*, *Video sites*, *Torrents*, *Screen Capture*, *Screen Recorder*, *Video Player*. *Settings* opens on the page of
  the tool you are in.
- **Theme** – Settings → *Appearance*: match Windows, Dark or Light, and an accent colour (blue, purple, teal, green, orange, pink, red).
  The change shows at once; Save keeps it, Cancel puts it back.
- **Backup** – Settings → *General* exports your settings to a file and imports them again after a reinstall.

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
updates), what the downloads are doing, and the fans and temperatures.

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
  you tick *Show every sensor*. Each temperature is a tile: what it is, the big number in the colour of its heat (cool green, warm amber, hot orange, very hot red, from 55 / 70 / 82 degrees), a 0-100 degree gauge, and a small line of the last minute and a half. A **Memory** tile (RAM in use as a percentage and "x / y GB", the kind and speed of the memory sticks such as "DDR4 · 3600 MHz", with the same gauge and line) sits beside them; it needs no administrator permission, so it shows even when fan control is off.
- **Start fan control** asks Windows for administrator permission: reading the motherboard and setting fans needs it, so it is
  done by a separate helper (`Utylix.exe --fan-helper`, started from the button); the rest of Utylix stays a normal program.
  It uses the open-source LibreHardwareMonitor library. The processor and motherboard sensors need the free **PawnIO** driver
  (`winget install namazso.PawnIO`), which can be installed with one button in Settings > Video sites, next to yt-dlp and ffmpeg (or with a checkbox in the installer); the page says so if it is missing.
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
  it in `chrome://extensions/shortcuts`) floats the video that is playing on any page, no right click needed. While a video floats, Utylix follows the feed: swipe to the next Reel (or the next video of a playlist starts) and the floating window switches to it by itself. Scroll the mouse wheel over the floating window to go to the next (wheel down) or previous (wheel up) Reel or video: the browser never passes the wheel to the page, so the Utylix app watches for it while a video floats (only over a window titled "Picture in picture"; it needs Utylix running). **Subtitles** (extension 1.9.9): Chrome and Brave's own floating window never draws subtitles (a known browser gap), so subtitles a site imports (OpenSubtitles ...) were always left behind. Now, when the video has subtitles (a subtitle track of its own, even a hidden one, or a subtitle layer the page draws over it), Alt+P / the right-click entry floats it in a small **Utylix window** instead: the same video, with play / pause, a seek bar, sound and subtitle size buttons, and the subtitles drawn over it by Utylix (they follow the page's subtitles, also when you pick another language there). Closing it puts the video back in the page. It uses the browser's "document Picture in Picture", so it needs a recent Chrome / Brave; without that the video floats as before and a note says there are no subtitles. A video without subtitles still floats the normal way. If the browser wants a click first, a note asks you to click the video once. **Your own subtitle file**: the **CC** button in the window (or dropping a .srt / .vtt / .ass file on it) loads a subtitle file from your PC, which then replaces what the page offers; **−.5s / +.5s** move them earlier / later until they match, and the file is remembered for that video while the page stays open. To open the window on a video where no subtitles were found (or the page has none), use **Alt + Shift + P** or right-click > *Picture in Picture with a subtitle file...*. Not shown: picture subtitles (burnt into the video). After
  updating, reload the extension in `brave://extensions` (and refresh open pages once).
- **Sites that refuse Utylix** – some sites only let the browser itself download (login, anti-bot checks: HTTP 401 / 403). Utylix looks again without the Range request and with the headers a browser sends; if the site still says no, the "New download" window closes by itself and the browser takes the download back, instead of leaving you with a window that can only fail. (If the link works only once, the browser's retry can fail too.)
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
screen and dims it: drag over the part you want, draw around it, or click a window; Esc or right-click cancels. While you
are choosing, the bar at the top also has **Snip again in 3 s / 5 s / 10 s**: it closes the dimmed screen, counts down
(a small "Snipping in 3…" at the top of the screen; click it to cancel) and then starts a fresh snip, so you can set up a menu
or a tooltip first. That timer is used once; the **Delay** button keeps its own setting. The snip
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
- **Pages** (the small buttons above the small pages, or right-click a small page): **turn left / right** (a quarter turn, saved in the PDF), **move up / down**, **delete**. Pick several with Ctrl or Shift + click, and **drag** pages to a new place (a blue line shows where they will land). The changes are made at once and written when you Save; **Undo / Redo** bring a turned, moved or deleted page back until you save. Edits you made on the pages before are put into them first (their places would no longer fit), so Undo then starts from there. The last page can't be deleted; black boxes (Redact) that are not saved yet must be saved first.
- **The … button** (last of those buttons) has the bigger page tools:
  - **Add pages from a PDF or pictures** (after the chosen page; a picture gets a page the size of its neighbour, turned like the picture).
  - **Take the chosen page(s) out as a new PDF**, and **Split into several PDFs** (every N pages, by ranges like `1-3, 4-6, 9`, or one per page; files are named `name (pages 1-3).pdf`).
  - **Save pages as pictures** (PNG or JPG, 72 / 150 / 300 / 600 dpi, chosen pages or all).
  - **Page numbers, header, footer, watermark**: a line of text at one of six places on every page (or a range), with `{n}` (page number), `{total}`, `{date}`, `{file}`, a start number and "no number on the cover"; and/or a watermark across the pages (words, slanted or straight, or a picture; size, see-through, colour). A preview of a page follows every change. Undo takes it away until you save.
  - **Make scanned pages searchable (OCR)**: Windows' own text reader (offline) reads pages that have no text and puts the words under the picture as invisible text, so Search, select and copy work. Needs a Windows language with text recognition (English is normally there).
  - **Save as Word or Excel**: Word (.docx) gets paragraphs, **real tables** (neighbouring lines whose cells line up in columns become a Word table with its rows and columns, numbers against the right edge, light grey borders you can remove; a single line with a gap stays a paragraph with a tab stop), tab stops for other columns, fonts, sizes, bold, italic, colours, centred / right-aligned lines and pictures, one section per page; Excel (.xlsx) gets each line as a row with the pieces lined up in columns and numbers as numbers (one sheet per page, or all on one). Scans can be read with OCR first. Complicated layouts (columns of text, text round pictures, rotated text) come out simpler.
  - **To Word / To Excel / To Pictures** (the last group of the Edit bar): big icon-over-label buttons. To Word and To Excel open the dialog below with that format already chosen; To Pictures saves PNG / JPG pictures, one per page. PowerPoint and PDF/A are not offered: Utylix can't make them.
  - **Fillable form fields** (Edit bar, *Text box* and *Check box*, Alt+F / Alt+B): drag a box on the page for a text box (a click gives the usual size; a tall box takes several lines), click for a check box. Pick their colour with the Colour dots (the edge of the field, no background; default black) and, for a text box, Sans / Serif / Mono, bold and size. Select moves and resizes them, and **Save** writes them into the PDF as real form fields: they fill in here (the blue "This PDF has fields to fill in" bar), print, and open as fields in other PDF readers. The blue "Tint the fields" tick in the form bar colours every fillable field lightly on screen (off by default). Not for password-protected PDFs. Dropdowns, radio buttons and renaming fields are not there yet.
  - **Undo goes back through saves**: Ctrl+Z (or the undo arrow) works after Save too, one change at a time and in order, even across several saves (and Ctrl+Y redoes). The file on disk only changes when you save again. After a redaction the history starts again (a redaction can't be undone).
  - **Choose several things**: with *Select*, drag a box on an empty spot of the page; everything it touches is chosen, and dragging any one moves them all. Delete removes them, the arrow keys nudge them, Esc lets go.
  - **The tool bar is sorted into tabs** (when Edit is on): *Select* is always there, then **Add** (text, edit text, date, columns, picture, sign, stamp), **Mark up** (highlight, underline, strike, note, pen, shapes, table, check, cross), **Forms** (text box, check box, option, sign box), **Page** (white-out, redact, border, watermark, page numbers) and **Convert** (To Word, To Excel, To Pictures). Choosing a tool with its Alt key shows its tab. The Tools menu keeps the bigger jobs: pages, protect, clean up, compare and convert.
  - **More form fields and a table**: *Option* (round buttons: place several one after the other = one group where only one can be chosen; choose the tool again for another group), *Sign box* (an empty signature field: click it later to sign, or sign with a certificate) next to *Text box* and *Check box*, and a *Table* tool (drag its size, choose the rows and columns: a grid of lines; type in the cells with the Text tool). Saved ones come back as editable things when you click them with Select.
  - **Field options**: double-click a field with Select (or Enter, or right-click > *Field options…*) to set its name, *must be filled in*, and, for a text box, several lines / longest text / starting text; for a *Dropdown* the choices (the window opens when you place one); for a check box *ticked at the start*; for an option its choice name. Tab goes along the rows of the page. **Tools > Forms > Make the fields permanent** turns the fields into ordinary page content (what is typed stays; comments stay comments; Undo takes it back until you save).
  - **Copy things already in the PDF**: pick them up with Select (click, or a box round them), then Ctrl+C / Ctrl+X / Ctrl+V / Ctrl+D or the right-click menu: they paste as one thing you can move, resize, arrange and delete.
  - **Arrange**: right-click something (or Ctrl+] / Ctrl+[, add Shift for the very front / back): *Bring to front*, *Bring forward*, *Send backward*, *Send to back*. It works for what you added and for things already in the PDF.
  - **Pages from a scanner**: *+ Add page* (or right-click in the page strip) > *Pages from a scanner…* opens Windows' own scan window; every scan becomes a page after the chosen one, and it asks if you want another.
  - **Compare two PDFs** (Tools > *Compare with another PDF…*): pick the other file; the page list shows which pages differ, and you can look at *Differences* (red = only in the first, green = only in the second), *Side by side*, or *Words changed* (what was taken out and put in).
  - **Edit what you saved earlier**: with *Select*, click anything already in the PDF (text, a check mark, a picture, a stamp, a drawing) or drag a box round several: move them, resize them by the corner, or press Delete (Undo brings them back). A text box or check box you made here comes back as a text box / check box when you click it, so you can also recolour, copy and delete it, and Save makes it a field again. (Fields from other people's forms are left alone; things already in the PDF can't be copied.)
  - **Copy and paste**: choose something you added (or several), then Ctrl+C / Ctrl+X / Ctrl+V (Ctrl+D = duplicate); right-click gives Copy, Cut and *Paste here*. It also pastes onto another page.
  - **Comments**: *Note* tool or right-click > *Add a note (comment) here* puts a sticky note (in the colour you pick); select words, right-click > *Add a comment on the selected words* highlights them with a comment. Point at a comment to read it; click a sticky note (or right-click > *Read or change the comment here*) to change or delete it. Other PDF readers show them as comments.
  - **Links** (Edit bar > Add > *Link*, Alt+J; or select words > right-click > *Make the selected words a link…*): drag a box and say where it goes: a **web address** (a bare address gets https://, `name@site` becomes an e-mail link) or a **page of this PDF**. It works in any PDF reader (a click opens the address or jumps to the page). While Select or Link is the tool, the links of the PDF are outlined in blue: click one to choose it, **Enter** (or double-click) changes where it goes, **Delete** removes it. Links are put into the open document at once; Undo takes them back until you save. Not for password-protected PDFs.
  - **Bookmarks editor** (side panel > *Bookmarks*, now always available): **+ Add** makes a bookmark for the page in view (named after the selected words if there are some, else "Page n"; you can change the name and the page), **Edit** (or double-click / F2) changes name and page, **Delete** (or the Delete key) removes it with the ones inside, **▲ ▼** move it, **→** puts it inside the one above and **←** takes it out again. Changes are made at once; Undo takes them back until you save.
  - **The tab and the settings row remember themselves**: the tab of the tool bar you were on is still there next time. The row with colours / size / font only shows what the chosen tool can use; otherwise it shows a short hint for the tool.
  - **Group / Ungroup** (Select tool; right-click, or Ctrl+Shift+G / Ctrl+Shift+U): choose several things with a box, or **Shift + click** (Ctrl + click works too) one after the other (Shift + click on a chosen one takes it out again), then *Group*: from then on a click on any one of them chooses them all, and they move, copy and delete together (a pasted group is a new group). A group of things you added stays a group until you save. For things already saved in the PDF (a box round them), *Group* makes them ONE thing on the page, which stays grouped in the file; that one can't be taken apart again (Undo does, until you save).
  - **Text style** (the *Style* button next to the fonts, when text is chosen or the Text tool is on): italic, underline, align left / middle / right, and **Box, width and background…**: a width the words wrap in (the corner then changes the width instead of the size), a background colour and a frame in the text's colour. What you see is what is saved.
  - **Fill** for boxes and circles (Mark up > Shapes): *Fill ▾* gives the inside a colour (or none).
  - **Picture stamps**: Add > Stamp > *Your own picture (logo, seal, signature)…* keeps up to six pictures in the Stamp menu; one click puts the picture on the page (about 4 cm wide, then move and resize it like any picture).
  - **Pictures already in the PDF**: pick one with Select, then right-click: *Replace this picture…* (same place, never stretched, fitted inside the old box), *Crop this picture…* (sliders with a shaded preview; the cut pixels are removed, a JPEG stays a JPEG) and *Save this picture…*. Tools > PICTURES > *Save all the pictures of this PDF…* (JPEGs as they are stored, the rest as PNG; tiny icons are skipped).
  - **Comments tab** (side panel, next to Pages and Bookmarks): every comment of the PDF (sticky notes, and the comments on highlighted / underlined / struck-out words) with its page and the words it is about; click one to go there (a ring shows the place), double-click to read or change it, *Save a summary…* / *Copy* for a text summary.
  - **Form data**: Tools > FORMS > *Save what is filled in as a CSV file…* (Field, Value: opens in Excel) and *Fill the form from a CSV file…* (text boxes, lists and check boxes; round options are only saved; values are checked against each box's number / date / time format).
  - **Number, date and time boxes, and calculated boxes** (Forms > Text box > *Field options*): *Shows what is typed as* a number (decimal places, decimal point or comma), a date (dd/mm/yyyy, mm/dd/yyyy, yyyy-mm-dd, d mmm yyyy, mmmm d, yyyy) or a time; *Worked out* as the sum, product, average, smallest or largest of other boxes (by name). They are written as the standard Acrobat form scripts, so Acrobat, Edge and Chrome run them, and Utylix reads those scripts back (also from other makers' forms, when they use the standard ones) and does the same while you fill the form here: a typed value that doesn't fit is refused with a message, and calculated boxes work themselves out after every change (also after *Fill the form from a CSV*). No thousands separator on purpose (readers parse the stored text again to calculate). Not done: currency signs, percent, custom scripts.
  - **Find and replace in the whole PDF** (Tools > TEXT, or Ctrl+H): counts the matches as you type (match case, whole words), then changes every line in one step with the line's own font, size and colour; Undo takes it back, Save writes it. A phrase the PDF has split over two lines, and scanned pages (use OCR first), are not found.
  - **Edit the whole paragraph** (Edit text tool: Alt+click on a line, or right-click > *Edit the whole paragraph*): the lines of the paragraph (same font, size, colour and line spacing; a line that had room for the next word ends it) come up as one text; after the change the words are laid out again to the same width and spacing, an indented first line stays indented, justified text comes back left-aligned. Longer text grows downwards.
  - **Opens the way you left it**: if the editor was maximized when you closed it, the next PDF opens maximized too. Pressing Edit always starts on the Select tool (no tool is armed until you pick one).
  - **Placing guide**: with any tool that puts something on the page, a dashed outline follows the pointer and shows where it will land and how big it will be (text and date: the caret, "Abc" and the line the letters stand on; check / cross / note / stamp / form boxes: their usual size). It lines up with the other things on the page (the pink lines show) and the click lands exactly where the guide is; hold Alt to place freely. Tools you drag (pen, shapes, highlight, underline, strike, white-out, redact, link, table) show a small cross.
  - **Alignment guides** also work while resizing (the right and bottom edge snap), and saved form fields count as things to line up with.
  - **Find personal details to black out** (Tools > PRIVACY): looks through the text of every page for e-mail addresses, phone numbers, card numbers (only numbers that pass the card checksum), ID numbers (US Social Security, Philippine TIN / SSS style, IBAN, passport style), dates, web addresses and words or patterns of your own (one per line; start a line with `re:` for a pattern). Every match is listed with its page; tick what must go and press *Black out the ticked*: Redact boxes go on them (one undo step), and Save removes what is under them from the file for good and checks it. Only finds text, not text inside pictures: run OCR first on scans.
  - **Document** (Tools > DOCUMENT): **Properties** (title, author, subject, keywords; what Explorer and other programs show), **Page labels** (number the pages like a book: i, ii, iii for the front, then 1, 2, 3, or with a prefix such as A-1; shown in other readers' page box and next to the small pages) and **Attached files** (see, add, save out and remove the files carried inside a PDF). **Bates-style numbers**: in the page-number text use `{n:000000}` (for example `CASE-{n:000000}` gives CASE-000001, CASE-000002 ...).
  - **Alignment guides**: when you move something (one thing, several chosen things, or things already in the PDF) it snaps to the edges and middles of the other things on the page and to the page's own edges and middle, and thin pink dashed lines show what it lined up with. Hold **Alt** to move freely. The lines go when you let go. (Resizing does not snap yet.)
  - **Dark reading mode** (the moon button in the top bar): the pages are shown dark (white paper becomes dark grey, black text light grey; pictures are inverted too). Only for looking: the file is not changed, it is off while you edit, and it is remembered.
  - **Read aloud** (the speaker button): Windows' own voice reads from the page in view, page after page (or just the chosen words); Pause / Stop in the same menu, speed Slow / Normal / Fast / Very fast. A page with no text (a scan) is skipped: use OCR first.
  - **Back where you stopped**: a PDF you opened before comes back at the page and zoom where you left it (the 300 latest files are remembered, in `pdf-places.json`); a small note says so.
  - **Many PDFs at once** (Tools > *Do one job to many PDFs…*): put PDFs in the list (add files, add a folder, or drop them), choose ONE job (*Make smaller*, *Page numbers*, *Watermark*, *Add a password*, *Remove a password*, *Page size*, *Save as Word*) and where the new files go (next to each original with what was done in the name, or one folder). Each file is marked *Done*, *Left out* (already small, already has a password, protected ...) or *Couldn't*, and the others carry on; the originals are never changed and nothing is overwritten (`name (2).pdf`).
  - **Word export: merged and wrapped cells**: a cell that wraps onto several lines (the lines under it, tight together, in the same column) stays ONE cell with one paragraph, and a line centred above a table becomes its heading row across all the columns (a merged cell). Not done: merged cells that go downwards, cells you can only see through the lines of the table (the text decides), and a wrapped cell in the very last row (its extra lines come out under the table).
- **Text**: drag to select (double-click a word, triple-click a line), Ctrl+C copies, Ctrl+A selects the page; Ctrl+F (or the magnifier) searches the whole PDF, Enter / F3 for the next. Links work (web links ask first); pointing at a note shows its text. Right-click: copy, highlight / underline / strike out the selection, add a note, search.
- **Forms**: a PDF with fields says so in a blue bar. Click a field to type (Tab: the next one), tick boxes and round options, pick from lists; a signature field opens "Your signature". Ctrl+S or Save keeps the values in the PDF (drawn into it, so every reader shows them). XFA forms can't be filled.
- **Edit**: Edit text (click a line of the PDF's own text and change it, or drag it to move it; Shift+Enter makes a new line under it: the same font when the PDF has the letters, else the same Windows font, else one like it. While a line is open the bar below shows its **font** (Sans / Serif / Mono, or **More fonts**), **Bold**, **size** and **colour**: change any of them and the line is rewritten that way when you save), **Columns** and **Border** (buttons in the bar that open the same dialogs as the Tools menu), Text (type anywhere; Sans / Serif / Mono, Bold, or **More fonts**: any TrueType font installed on the PC, searchable, each shown in its own look, and put into the PDF so it looks the same on every computer; the colour in use has a ring round its dot), Sign (draw, type or a photo of your signature; remembered), Picture, Check / Cross, Highlight / Underline / Strike (on text; Highlight also as a box), Note (a real PDF comment), Pen, Shapes (box, circle, line, arrow), White-out (covers; it doesn't erase what is under it), **Redact** (drag over what must go: on Save it is removed from the file for good, see below), then **Watermark** and **Page no.** (buttons that open the page-marks dialog on the watermark / the page-number half). **Stamp** (Approved, Received, Paid, Final, Rejected, Confidential, Urgent, Draft, Copy or your own words, with today's date under it if you like: a stamp-style box you can move, resize and **turn** with the round handle above it; Shift = steps of 15°, and the next stamp starts with the same tilt; the chosen stamp is ticked in its menu) and **Date** (today's date as text, in the look you pick). Select moves / resizes, Delete removes. **Turning**: texts, dates, pictures, signatures, pen drawings, text in columns, boxes, circles, check marks and crosses (and stamps) have a round handle above them: drag it to turn the item (it clicks into place near 0 / 45 / 90 degrees; Shift = steps of 15 degrees); the square corner resizes it, also when it is turned. Lines, arrows, highlights, white-out and redaction boxes stay straight. A turned item is saved turned. Ctrl+Z / Ctrl+Y undo / redo. Save writes into the PDF, Save as… into a new one; a protected PDF stays protected.
- **Redact** (really removing, not just covering): choose Redact and drag over what must go, or select words > right-click > *Redact*, or search (Ctrl+F) and press *Redact all* to box every match. Nothing is removed until you Save; the window then asks first. Saving takes out, for good, the words under each box (the letters next to them stay as text), the parts of pictures under it (the pixels are blacked), small drawings, and links, comments and form fields there; then it draws the black boxes, clears the document's properties (title, author…) and the pages' preview pictures, and **checks the saved file**: if anything readable is still under a box, it refuses to save. A page whose content PDFium can't edit (text inside shared "forms") is turned into a picture instead and the window tells you which pages (their text can't be selected afterwards). Use Save as… to keep the original. Password-protected PDFs are refused. Not covered: the content of an AcroForm field's value elsewhere in the file, and earlier versions kept by other programs' "incremental saves" (Utylix rewrites the whole file, so those are dropped too).
- **Print**: Utylix's own print window with a preview of every sheet: printer and its own settings, copies, pages ("1-3, 5", odd / even, reverse), fit / actual size / shrink big pages, 1 to 16 pages per sheet, orientation, paper (with Long 8.5 × 13 when the printer has it), colour or black and white, two-sided, quality.
- Ctrl+O opens another PDF; PDFs with an open password ask for it.
- **Tools menu**: the **Tools ▾** button in the top bar (and the **…** button above the thumbnails) opens every page tool in groups: *Pages* (crop, take pages out, split, save as pictures), *Protect* (password, remove the password, sign, check signatures), *Clean up* (remove a watermark, OCR), *Convert* (Word / Excel). Adding a watermark, page numbers, a border or text in columns is done with the buttons of the Edit bar, and adding / moving pages with the page strip and the grid button, so they are not repeated in this menu.
- **Page grid** (the round grid button in the bottom-right corner of the pages, or right-click in the page strip): every page as a big picture in a grid. Click to choose (Ctrl / Shift for several), drag to move them (a blue line shows where they land), *Turn left / right*, *Delete*, *Add a blank page*, *Choose all*, a size slider; double-click a page to go to it. Nothing changes until *Apply*, and Apply is one Undo step.
- **Crop pages** (Tools > *Crop pages…*): *Cut from the edges* (top / bottom / left / right in mm, optionally moving together) or *Trim the empty edges* (each page is cut down to what is on it, with a little space around); all pages, this page, odd, even or a range; with a live preview (the darker part is cut off). It sets the PDF's crop box, so every reader shows the cropped page, but what was cut off stays hidden inside the file (use Redact to remove something for good). Undo takes it back until you save.
- **Password and limits** (Tools > *PROTECT*): *Add a password or limits…* makes a COPY with a password to open it (AES-256), and / or limits (no printing, no copying, no changing) that need a second password to lift; *Remove the password…* makes a copy with no password and no limits (a PDF that limits changes needs its owner password first). Your open PDF is never changed. A lost password can't be recovered.
- **Digital signature** (Tools > *PROTECT* > *Sign with a certificate…*): a real certificate signature (the kind Acrobat and every PDF reader check), saved in a new copy. Choose who signs: a certificate that is already in Windows (from your work, school, a government or a certificate authority), a certificate file (.pfx / .p12) with its password, or *Make my own certificate* (kept in your Windows certificate store for 5 years). Optional reason, place, and a visible box ("Digitally signed by …, date, reason") in a corner of the first / this / last page. Sign last: any change afterwards makes readers say "changed since signing". A certificate you made yourself proves the file was not changed, but readers say "signer not verified" until the other person trusts it. *Check the signatures…* tells who signed an open PDF, when, whether it was changed since (or added to), and whether Windows trusts the certificate. A password-protected PDF has to lose its password first.
- **Add page** (the *+ Add page* button under the small pages, or right-click a page): a blank page after the chosen page, or pages from a PDF or pictures. A blank page is as big as the chosen page and you can write on it with Text, Sign and the other tools.
- **(older note) Blank page**: an empty page the size of the chosen page, which you can write on with Text, Sign and the other tools.
- **Reduce to KB or MB**: *Under a size* has KB / MB choices next to the box and quick sizes (100 KB, 200 KB, 500 KB, 1 / 2 / 5 / 10 / 25 MB). A PDF that is mostly text can't go below what its text and fonts need.
- **Text in columns** (Edit bar > *Columns*): type the words, pick 1 to 4 columns, the space between them, how wide the block is, the font, size and colour; the text flows from one column to the next, shared out evenly, and is written into the page as real text. It stays an object you can move, resize (width) and change (double-click, or Enter).
- **Border around the pages** (Edit bar > *Border*): colour, thickness, distance from the edge, solid / dashed / double, square or rounded corners; all pages, this page, odd, even or a range; with a live preview. It is written as real lines; Undo takes it away until you save.
- **Remove a watermark** (**…** > *Remove a watermark…*): looks through the pages for pieces that repeat in the same place (a big diagonal text, a logo, an overlay every page draws) and for pieces marked as watermarks, shows where each is, and takes the ticked ones out of every page. Watermarks Utylix adds itself are marked, so they are always found, even on a single page. A watermark baked into a scanned picture can't be removed cleanly. Only for documents that are yours or that you may change.
- **Page size**: a copy of the PDF with every page on A4, Letter, Long (8.5 × 13), Legal, A5, A3, Tabloid or your own size (mm or inches), scaled to fit and centred, never stretched; wide pages stay wide unless you untick that. Text stays text. Links, form fields and comments are not kept in the copy (the window says so); a password-protected PDF is refused.
- **Reduce file size**, like Acrobat's: Recommended (pictures and scans at 150 dpi, text and links untouched), Smaller (110 dpi, lower quality), or Smallest (each page becomes one picture; for scans that are still too big, text can't be selected afterwards). Pictures that are really grey are stored grey, and fonts or pictures stored many times over are kept once. The original is never changed: the smaller copy is saved where you choose. A PDF protected against changes needs its owner (permissions) password, and the smaller copy keeps the same passwords and permissions ("Smallest" isn't offered for protected PDFs).
  **Under a size** ("Under 2 MB", with 1 / 2 / 5 / 10 / 25 MB buttons) makes pictures only as light as needed to fit, keeping text, links and fields; if it can't fit that way it asks before saving the pages as pictures.
- **Right-click in Explorer** (Settings → Tools, "PDF tools"): on PDFs, *Utylix Editor → Reduce file size…* (several selected PDFs: each smaller copy is saved next to its original as `name (reduced).pdf`) and *Combine into one PDF…*; on pictures, *Convert → Convert to PDF…*. Combining puts PDFs (all their pages, text stays text) and pictures (one page each: the picture's own shape, A4, Letter or Long 8.5 × 13) into one new PDF, in the order you set with the arrows; phone photos are turned the right way up and JPEG photos go in without losing quality. Password-protected PDFs can't be combined. A **photo of a page** can be cleaned up first: the *Clean up…* button on a picture row finds the paper, lets you drag its four corners, straightens it and takes the shadows out (colour, grey or black and white); the original photo is not changed. Also **New → Blank PDF** (right-click an empty spot in a folder or on the desktop): makes an empty one-page A4 PDF there, ready to open in the Utylix Editor and fill with text, pictures, fields...
  - **Word, Excel and PowerPoint files → PDF**: right-click a `.docx` / `.doc` / `.rtf` / `.odt`, `.xlsx` / `.xls` / `.ods` or `.pptx` / `.ppt` / `.odp` file → *Convert to PDF*. Utylix asks the Microsoft Office on this PC to do it (it has no Office engine of its own, so the entry only appears for the programs that are installed); the PDF is saved next to the original as `name.pdf` (`name (2).pdf` if that exists), several selected files are done one after the other, and a message by the clock says when each is ready.

## Music player

- **Six looks** (press **L**, or the ⋮ button > Look): *Classic* (big cover, playlist beside it), *Wide card* (one slim card with the playlist dropping under it), *Dark card*, *Frosted card*,
  *Light card* and *Waveform card* (draws the song's loudness as bars; click or drag to jump). The heart marks **favourites** (key **F**; the ♥ button plays only your favourites).
- **Small player**: the *Small player* button (or ⋮ > Small player) turns the player into just the card: no title bar, rounded, see-through around it, with its shadow. Press on any empty part of it and drag to move it anywhere (it remembers the place, and can stay on top of other windows). Double-click it, or press Esc, to get the full player and the playlist back. It stays this way if you close Utylix like that.
- **Song info editor** (**F2**, right-click a song > *Edit song info…*, or ⋮ menu): title, artist, album artist, album, year, track and disc numbers, genre, composer, comment, lyrics and the cover
  picture, written into the file itself (MP3, FLAC, M4A, OGG, WAV ...). Choose several songs to set the same album, artist or cover on all of them (only what you change is written). A song that
  is playing is stopped for a moment and carries on where it was. Uses the open-source TagLib# library.
- **Explorer logo**: music files show the Utylix Music logo once Utylix Music is the default for that type (Windows' *Default apps*; Utylix never takes over a choice you made, e.g. VLC).

Songs open in their own player (videos still open in the Video Player): a playlist with cover art (from the file, or a `cover.jpg` / `folder.jpg` next to it), shuffle, repeat (off / all / one), and the keyboard's media keys (play-pause, next, previous) while it is open. Open one song from Explorer and the rest of its folder follows it. "Add folder…" adds a folder and its folders; "Show: …" switches the list and the player between song + artist, artist only and song only; "Small player" hides the list. The tray menu has "Music: play / pause" and "Music: next song".

**Sound…** (under the volume): a 10-band **equalizer** with a preamp and presets (Flat, Bass boost, Treble boost, Vocal, Rock, Pop, Jazz, Classical, Dance; move a slider and the preset becomes your own), **speed** (0.75× to 2×), **even out the volume** (loud and quiet songs sound about the same, from the next song on), and a **sleep timer** (pause after 15 / 30 / 60 minutes, or stop when this song ends). Your choices are remembered. Above the playlist, **Find a song…** jumps to the first match by title, artist, album or file name (Enter: the next one). When the player is opened empty (tray menu > Music player), the playlist you left is back (not playing yet).

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

## Drivers

Dashboard → **Drivers**. Made for the day you format a PC and have no Wi-Fi driver to get online with. Nothing is downloaded and no driver is shipped inside Utylix: it saves and puts back the drivers *you already have*.

- **This PC now** shows the network cards (Wi-Fi / Ethernet) and how many drivers that aren't part of Windows are installed.
- **Save my drivers**: choose *Network and Bluetooth only* (small: Wi-Fi, Ethernet, Bluetooth; virtual adapters of VMware and the like are left out) or *Every driver that isn't part of Windows* (graphics, audio, chipset ...), and a folder. A USB drive is the default when one is plugged in. It uses Windows' own export, so no administrator rights are needed; every driver goes in its own subfolder and a `Utylix-drivers.txt` lists what is inside. Drivers that are part of Windows itself can't be saved (Windows already has them).
- **Install drivers from a folder**: on the PC that needs them, run `Utylix-Setup.exe` from the USB drive, open Drivers, choose the folder. Windows asks for permission once, then Windows' own installer puts in every driver of the folder (all, or only network + Bluetooth). Windows still refuses a driver that isn't signed. Restart afterwards if a device still doesn't work.
- **What this PC is missing**: devices Windows lists with a problem (mostly "no driver installed"), with their hardware IDs and a *Copy the hardware IDs* button, to search for on another PC.
- Limits: it can't help during Windows' own setup screens (Utylix has to be installed first), a driver for a different card than the PC has simply won't attach, and drivers for hardware you never had on a PC can't be saved from it.

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
