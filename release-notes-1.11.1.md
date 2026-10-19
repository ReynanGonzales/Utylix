Free up memory is gentler on the disk

## Fixes
- **Free up memory** (desktop right-click, and the "Deep clean" choice in Memory limits) no longer makes the disk busy afterwards. It now trims only programs that hold 100 MB or more, one after the other, and the deep clean no longer flushes Windows' modified page list or throws away its file cache (those were the steps that kept the disk at 100 % for a while).
- The wording in the toast and in the Memory limits tooltip says what it really does.

## Also
- An **offline setup file** can now be made with the tools folder inside it (for a PC without internet): the wizard copies yt-dlp, ffmpeg, the player engine, the background remover's model and the JavaScript helper by itself. It is made with `tools\pack-setup.ps1 -Tools <folder>` and is not part of this download.
