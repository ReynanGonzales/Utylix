Free up memory is gentler on the disk, with Trim and Deep clean in the menu

## Free up memory
- The desktop right-click entry is now **Free up memory > Trim / Deep clean**.
- **No more disk spikes.** It only trims until about 65 % of the memory is free (Deep clean: 80 %), does nothing at all when that is already so, takes the biggest programs first, one after the other, and waits for Windows' page file to calm down in between. The system-wide "empty every program's memory" step that kept the disk at 100 % is gone.
- **Deep clean asks for permission only once.** On an account that is not an administrator, Windows asks for an administrator's password the first time; a small hidden helper then stays and does the next Deep cleans without asking (it ends by itself after six hours without use, and when Utylix is updated).
- The Memory limits "Deep clean" choice uses the same gentler method.

## Also
- An **offline setup file** can now be made with the tools folder inside it (for a PC without internet): the wizard copies yt-dlp, ffmpeg, the player engine, the background remover's model and the JavaScript helper by itself. It is made with `tools\pack-setup.ps1 -Tools <folder>` and is not part of this download.
