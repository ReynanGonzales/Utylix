using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using IdmClone.Engine;
using Microsoft.Win32;

namespace IdmClone;

/// <summary>
/// The "Convert" entry in Explorer's right-click menu for pictures, videos and music. Registered for the current user only
/// (HKCU, no admin rights) as a submenu: Convert &gt; to JPG / PNG / ... / More options.
/// Each entry starts Utylix.exe with --convert-to &lt;format&gt; "file" (or --convert "file" for the window).
/// </summary>
public static class ShellMenu
{
    private const string VerbKey = "Utylix.Convert";
    private const string LegacyVerbKey = "IdmClone.Convert";     // from before the rename
    private static string KeyFor(string ext) => $@"Software\Classes\SystemFileAssociations\.{ext}\shell\{VerbKey}";

    private static IEnumerable<string> AllExtensions() =>
        ImageConverter.InputExtensions.Concat(MediaConverter.VideoExtensions).Concat(MediaConverter.AudioExtensions).Distinct();

    private static string Normalize(string ext) => ext switch { "jpeg" or "jfif" => "jpg", "tif" => "tiff", _ => ext };

    /// <summary>The "Convert to ..." entries for one file type (never "convert a PNG to PNG").</summary>
    private static List<(string Target, string Label)> EntriesFor(string ext)
    {
        var list = new List<(string, string)>();
        switch (MediaConverter.KindOf("x." + ext))
        {
            case MediaKind.Image:
                foreach (var t in new[] { "jpg", "png", "webp", "bmp", "gif", "ico" })
                    if (Normalize(ext) != t) list.Add((t, "Convert to " + MediaConverter.Label(t)));
                break;
            case MediaKind.Video:
                list.Add(("mp4", "Convert to MP4"));
                list.Add(("mp3", "Convert to MP3 (sound only)"));
                list.Add(("gif", "Convert to GIF (animated)"));
                break;
            case MediaKind.Audio:
                foreach (var t in new[] { "mp3", "m4a", "wav", "flac" })
                    if (Normalize(ext) != t) list.Add((t, "Convert to " + MediaConverter.Label(t)));
                break;
        }
        return list;
    }

    /// <summary>Writes (enabled) or removes (disabled) the menu. Safe to call at every start.</summary>
    public static void Register(string dataDir, bool enabled)
    {
        try
        {
            string exe = Environment.ProcessPath!;
            string icon = enabled ? IconOf(exe) : "";
            foreach (string ext in AllExtensions())
            {
                Registry.CurrentUser.DeleteSubKeyTree(KeyFor(ext), throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(KeyFor(ext).Replace(VerbKey, LegacyVerbKey), throwOnMissingSubKey: false);
                if (!enabled) continue;

                using var menu = Registry.CurrentUser.CreateSubKey(KeyFor(ext));
                menu.SetValue("MUIVerb", "Convert");
                menu.SetValue("SubCommands", "");                       // empty = the entries are in the "shell" sub-key below
                if (icon.Length > 0) menu.SetValue("Icon", icon);

                int n = 1;
                foreach (var (target, label) in EntriesFor(ext))
                    Add(menu, $"{n++}{target}", label, $"\"{exe}\" --convert-to {target} \"%1\"");
                Add(menu, $"{n}more", "More options…", $"\"{exe}\" --convert \"%1\"");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* not fatal */ }
    }

    // ---------- background remover ----------
    private const string BgVerb = "Utylix.RemoveBg";
    private static readonly string[] BgExtensions = { "png", "jpg", "jpeg", "jfif", "bmp", "webp", "tif", "tiff" };

    /// <summary>"Remove background" on pictures (one plain entry, not hidden in a submenu). Safe to call at every start.</summary>
    public static void RegisterBackground(string dataDir, bool enabled)
    {
        try
        {
            string exe = Environment.ProcessPath!;
            foreach (string ext in BgExtensions)
            {
                string key = $@"Software\Classes\SystemFileAssociations\.{ext}\shell\{BgVerb}";
                Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
                if (!enabled) continue;
                using var verb = Registry.CurrentUser.CreateSubKey(key);
                verb.SetValue("MUIVerb", "Remove background");
                verb.SetValue("Icon", IconOf(exe));
                using var command = verb.CreateSubKey("command");
                command.SetValue("", $"\"{exe}\" --remove-bg \"%1\"");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* not fatal */ }
    }

    // ---------- video player ----------
    private const string PlayVerb = "Utylix.Play", PlayProgId = "Utylix.MediaFile";

    /// <summary>"Play with Utylix" on video and music files, and Utylix in their "Open with" list (so it can be made the default).</summary>
    public static void RegisterPlayer(string dataDir, bool enabled)
    {
        try
        {
            string exe = Environment.ProcessPath!;
            string icon = enabled ? OwnIcon(dataDir, "player", IconOf(exe)) : "";
            if (enabled)
            {
                // Only what is different is written: Windows watches these keys, and re-writing the same values at every start
                // can make it distrust the person's choice of default app ("How do you want to open this file?").
                using var progId = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + PlayProgId);
                SetIfDifferent(progId, "", "Video or music (played with Utylix)");
                using (var di = progId.CreateSubKey("DefaultIcon")) SetIfDifferent(di, "", icon);
                AppIdentity(progId, "Utylix Player", icon, "Plays videos and music");
                using var cmd = progId.CreateSubKey(@"shell\open\command");
                SetIfDifferent(cmd, "", $"\"{exe}\" --play \"%1\"");
            }
            else Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + PlayProgId, throwOnMissingSubKey: false);

            foreach (string ext in PlayerMedia.VideoExtensions.Concat(PlayerMedia.AudioExtensions).Distinct())
            {
                string verbKey = $@"Software\Classes\SystemFileAssociations\.{ext}\shell\{PlayVerb}";
                string wantCommand = $"\"{exe}\" --play \"%1\"";
                if (enabled)
                {
                    using (var existing = Registry.CurrentUser.OpenSubKey(verbKey))
                    {
                        bool same = existing != null && existing.GetValue("MUIVerb") as string == "Play with Utylix" && existing.GetValue("Icon") as string == icon;
                        if (same) { using var c = existing!.OpenSubKey("command"); same = c?.GetValue("") as string == wantCommand; }
                        if (!same)
                        {
                            existing?.Close();
                            Registry.CurrentUser.DeleteSubKeyTree(verbKey, throwOnMissingSubKey: false);
                            using var verb = Registry.CurrentUser.CreateSubKey(verbKey);
                            verb.SetValue("MUIVerb", "Play with Utylix");
                            verb.SetValue("Icon", icon);
                            using var command = verb.CreateSubKey("command");
                            command.SetValue("", wantCommand);
                        }
                    }
                    using var owp = Registry.CurrentUser.CreateSubKey($@"Software\Classes\.{ext}\OpenWithProgids");
                    if (!owp.GetValueNames().Contains(PlayProgId)) owp.SetValue(PlayProgId, new byte[0], RegistryValueKind.None);
                }
                else
                {
                    Registry.CurrentUser.DeleteSubKeyTree(verbKey, throwOnMissingSubKey: false);
                    using var owp = Registry.CurrentUser.OpenSubKey($@"Software\Classes\.{ext}\OpenWithProgids", writable: true);
                    owp?.DeleteValue(PlayProgId, throwOnMissingValue: false);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* not fatal */ }
    }

    // ---------- pictures ----------
    private const string PictureProgId = "Utylix.PictureFile";

    /// <summary>Utylix in the "Open with" list of pictures (so it can be made the default), starting the photo viewer. Pass null to remove it.</summary>
    public static void RegisterViewer(string? dataDir)
    {
        try
        {
            string exe = Environment.ProcessPath!;
            bool enabled = dataDir != null;
            if (enabled)
            {
                string icon = OwnIcon(dataDir!, "photos", IconOf(exe));
                using var progId = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + PictureProgId);
                SetIfDifferent(progId, "", "Picture (opened with Utylix)");
                using (var di = progId.CreateSubKey("DefaultIcon")) SetIfDifferent(di, "", icon);
                AppIdentity(progId, "Utylix Photos", icon, "Shows pictures");
                using var cmd = progId.CreateSubKey(@"shell\open\command");
                SetIfDifferent(cmd, "", $"\"{exe}\" --view \"%1\"");
            }
            else Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + PictureProgId, throwOnMissingSubKey: false);

            foreach (string ext in ViewerWindow.Extensions)
            {
                if (enabled)
                {
                    using var owp = Registry.CurrentUser.CreateSubKey($@"Software\Classes\.{ext}\OpenWithProgids");
                    if (!owp.GetValueNames().Contains(PictureProgId)) owp.SetValue(PictureProgId, new byte[0], RegistryValueKind.None);
                }
                else
                {
                    using var owp = Registry.CurrentUser.OpenSubKey($@"Software\Classes\.{ext}\OpenWithProgids", writable: true);
                    owp?.DeleteValue(PictureProgId, throwOnMissingValue: false);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* not fatal */ }
    }

    // ---------- PDFs ----------
    private const string PdfProgId = "Utylix.PdfFile";

    /// <summary>
    /// Utylix PDF in the "Open with" list of .pdf files (so it can be chosen as the default in Windows), never taking over the PDF
    /// program already chosen. Pass null to remove it.
    /// </summary>
    public static void RegisterPdf(string? dataDir)
    {
        try
        {
            string exe = Environment.ProcessPath!;
            bool enabled = dataDir != null;
            if (enabled)
            {
                string icon = OwnIcon(dataDir!, "pdf", IconOf(exe));
                using var progId = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + PdfProgId);
                SetIfDifferent(progId, "", "PDF document (opened with Utylix)");
                using (var di = progId.CreateSubKey("DefaultIcon")) SetIfDifferent(di, "", icon);
                AppIdentity(progId, "Utylix Editor", icon, "Reads, edits, signs and shrinks PDFs");
                using var cmd = progId.CreateSubKey(@"shell\open\command");
                SetIfDifferent(cmd, "", $"\"{exe}\" --pdf \"%1\"");
                using var owp = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids");
                if (!owp.GetValueNames().Contains(PdfProgId)) owp.SetValue(PdfProgId, new byte[0], RegistryValueKind.None);
            }
            else
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + PdfProgId, throwOnMissingSubKey: false);
                using var owp = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids", writable: true);
                owp?.DeleteValue(PdfProgId, throwOnMissingValue: false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* not fatal */ }
    }

    // ---------- PDF tools ----------
    private const string PdfToolsVerb = "Utylix.PdfTools", ToPdfVerb = "Utylix.ToPdf";

    /// <summary>
    /// Right-click a PDF: "Utylix Editor" &gt; Reduce file size… / Combine into one PDF…. Right-click a picture: "Convert to PDF" (in the
    /// Convert submenu when that is on, otherwise on its own). Call after <see cref="Register"/>, which rewrites the Convert submenu.
    /// </summary>
    public static void RegisterPdfTools(string dataDir, bool enabled)
    {
        try
        {
            string exe = Environment.ProcessPath!;
            string key = $@"Software\Classes\SystemFileAssociations\.pdf\shell\{PdfToolsVerb}";
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            if (enabled)
            {
                using var menu = Registry.CurrentUser.CreateSubKey(key);
                menu.SetValue("MUIVerb", "Utylix Editor");
                menu.SetValue("SubCommands", "");
                menu.SetValue("Icon", OwnIcon(dataDir, "pdf", IconOf(exe)));
                Add(menu, "1reduce", "Reduce file size…", $"\"{exe}\" --pdf-reduce \"%1\"");
                Add(menu, "2combine", "Combine into one PDF…", $"\"{exe}\" --pdf-combine \"%1\"");
            }
            foreach (string ext in PdfCombiner.PictureExtensions)
            {
                string own = $@"Software\Classes\SystemFileAssociations\.{ext}\shell\{ToPdfVerb}";
                Registry.CurrentUser.DeleteSubKeyTree(own, throwOnMissingSubKey: false);
                if (!enabled) continue;
                string command = $"\"{exe}\" --to-pdf \"%1\"";
                using var convert = Registry.CurrentUser.OpenSubKey(KeyFor(ext), writable: true);
                if (convert != null) Add(convert, "0pdf", "Convert to PDF…", command);
                else
                {
                    using var verb = Registry.CurrentUser.CreateSubKey(own);
                    verb.SetValue("MUIVerb", "Convert to PDF");
                    verb.SetValue("Icon", OwnIcon(dataDir, "pdf", IconOf(exe)));
                    using var c = verb.CreateSubKey("command");
                    c.SetValue("", command);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* not fatal */ }
    }

    // ---------- torrents ----------
    private const string TorrentProgId = "Utylix.Torrent", MagnetProgId = "Utylix.Magnet";

    /// <summary>
    /// Utylix as a program for magnet links and .torrent files: it shows up in Windows' "Default apps" (Choose defaults by file type / link
    /// type) and in "Open with". Where nothing is set for them yet, it is also made the program that opens them. A choice already made in
    /// Windows (for example uTorrent) is never overridden: Windows keeps that in a place only the person can change.
    /// </summary>
    public static void RegisterTorrent(bool enabled)
    {
        try
        {
            string exe = Environment.ProcessPath!;
            string command = $"\"{exe}\" --torrent \"%1\"";
            if (enabled)
            {
                foreach (var (id, text, url) in new[] { (TorrentProgId, "Torrent file (opened with Utylix)", false), (MagnetProgId, "URL:Magnet link", true) })
                {
                    using var progId = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + id);
                    SetIfDifferent(progId, "", text);
                    if (url) SetIfDifferent(progId, "URL Protocol", "");
                    using (var di = progId.CreateSubKey("DefaultIcon")) SetIfDifferent(di, "", IconOf(exe));
                    using var cmd = progId.CreateSubKey(@"shell\open\command");
                    SetIfDifferent(cmd, "", command);
                }
                using (var owp = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.torrent\OpenWithProgids"))
                    if (!owp.GetValueNames().Contains(TorrentProgId)) owp.SetValue(TorrentProgId, new byte[0], RegistryValueKind.None);

                // listed in Settings > Default apps
                using (var cap = Registry.CurrentUser.CreateSubKey(@"Software\Utylix\Capabilities"))
                {
                    SetIfDifferent(cap, "ApplicationName", "Utylix");
                    SetIfDifferent(cap, "ApplicationDescription", "Downloads, torrents, converting, archives, player and more.");
                    using var files = cap.CreateSubKey("FileAssociations"); SetIfDifferent(files, ".torrent", TorrentProgId);
                    using var urls = cap.CreateSubKey("URLAssociations"); SetIfDifferent(urls, "magnet", MagnetProgId);
                }
                using (var reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications")) SetIfDifferent(reg, "Utylix", @"Software\Utylix\Capabilities");

                // nothing opens magnet links / .torrent files on this PC yet: Utylix does
                using (var existing = Registry.ClassesRoot.OpenSubKey(@"magnet\shell\open\command"))
                    if (existing == null || (existing.GetValue("") as string ?? "").Contains(exe, StringComparison.OrdinalIgnoreCase))
                    {
                        using var m = Registry.CurrentUser.CreateSubKey(@"Software\Classes\magnet");
                        SetIfDifferent(m, "", "URL:Magnet link"); SetIfDifferent(m, "URL Protocol", "");
                        using var mc = m.CreateSubKey(@"shell\open\command"); SetIfDifferent(mc, "", command);
                    }
                using (var t = Registry.ClassesRoot.OpenSubKey(".torrent"))
                {
                    string current = t?.GetValue("") as string ?? "";
                    if (current.Length == 0)
                    {
                        using var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.torrent");
                        SetIfDifferent(k, "", TorrentProgId);
                    }
                }
            }
            else
            {
                foreach (var id in new[] { TorrentProgId, MagnetProgId }) Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + id, false);
                using (var owp = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.torrent\OpenWithProgids", true)) owp?.DeleteValue(TorrentProgId, false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Utylix\Capabilities", false);
                using (var reg = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true)) reg?.DeleteValue("Utylix", false);
                using (var m = Registry.CurrentUser.OpenSubKey(@"Software\Classes\magnet\shell\open\command"))
                    if (m?.GetValue("") is string c && c.Contains(exe, StringComparison.OrdinalIgnoreCase)) { m.Close(); Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\magnet", false); }
                using (var t = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.torrent", true))
                    if (t?.GetValue("") as string == TorrentProgId) t.DeleteValue("", false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* not fatal */ }
    }

    private static void SetIfDifferent(RegistryKey key, string name, string value)
    {
        if (key.GetValue(name) as string != value) key.SetValue(name, value);
    }

    /// <summary>
    /// The name and logo "Open with" shows for one kind of Utylix file (like Microsoft Edge does for its PDFs): "Utylix Editor" with the
    /// PDF logo, "Utylix Photos", ... instead of a plain "Utylix" with the main logo for everything.
    /// </summary>
    private static void AppIdentity(RegistryKey progId, string name, string icon, string description)
    {
        using var app = progId.CreateSubKey("Application");
        SetIfDifferent(app, "ApplicationName", name);
        if (icon.Length > 0) SetIfDifferent(app, "ApplicationIcon", icon);
        SetIfDifferent(app, "ApplicationDescription", description);
        SetIfDifferent(app, "ApplicationCompany", "Utylix");
    }

    /// <summary>
    /// Windows adds a plain "Utylix" to "Open with" by itself when Utylix.exe is ever picked through "Choose another app". That entry
    /// duplicated the proper ones (Utylix Editor, Utylix Photos, ...), so it is hidden. (Utylix still understands a plain file name.)
    /// </summary>
    public static void HidePlainExeFromOpenWith()
    {
        try
        {
            string name = Path.GetFileName(Environment.ProcessPath!);
            using var app = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{name}");
            if (app.GetValue("NoOpenWith") == null) app.SetValue("NoOpenWith", "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* not fatal */ }
    }

    // ---------- archives ----------
    private const string ArchiveVerb = "Utylix.Archive", ProgId = "Utylix.ArchiveFile";

    /// <summary>
    /// Archive commands in Explorer's menu (current user only), in one "Utylix" submenu like WinRAR's: "Open with Utylix / Extract files... /
    /// Extract here / Extract to folder" on ZIP, RAR, 7z ... files, "Add to archive... / Add to ZIP" on every other file and folder,
    /// and Utylix in the "Open with" list.
    /// </summary>
    public static void RegisterArchive(string dataDir, bool enabled)
    {
        try
        {
            string exe = Environment.ProcessPath!;
            string icon = enabled ? OwnIcon(dataDir, "archive", IconOf(exe)) : "";

            // like WinRAR: ONE submenu called "Utylix". On other files and folders it offers to pack them, on archives to unpack.
            // (An archive's own entry has the same name as the general one, so Explorer shows just that one.)
            foreach (string place in new[] { @"Software\Classes\*\shell\" + ArchiveVerb, @"Software\Classes\Directory\shell\" + ArchiveVerb })
            {
                Registry.CurrentUser.DeleteSubKeyTree(place, throwOnMissingSubKey: false);
                if (!enabled) continue;
                using var menu = Registry.CurrentUser.CreateSubKey(place);
                menu.SetValue("MUIVerb", "Utylix Archive");
                menu.SetValue("SubCommands", "");
                if (icon.Length > 0) menu.SetValue("Icon", icon);
                Add(menu, "1new", "Add to archive…", $"\"{exe}\" --archive-add \"%1\"");
                Add(menu, "2zip", "Add to ZIP", $"\"{exe}\" --zip-add \"%1\"");
            }

            if (enabled)
            {
                using var progId = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId);
                progId.SetValue("", "Archive (opened with Utylix)");
                if (icon.Length > 0) { using var di = progId.CreateSubKey("DefaultIcon"); di.SetValue("", icon); }
                AppIdentity(progId, "Utylix Archive", icon, "Opens and makes ZIP, RAR, 7z and other archives");
                using var cmd = progId.CreateSubKey(@"shell\open\command");
                cmd.SetValue("", $"\"{exe}\" --archive-open \"%1\"");
            }
            else Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, throwOnMissingSubKey: false);

            foreach (string ext in ArchiveService.Extensions)
            {
                string verbKey = $@"Software\Classes\SystemFileAssociations\.{ext}\shell\{ArchiveVerb}";
                Registry.CurrentUser.DeleteSubKeyTree(verbKey, throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\.{ext}\shell\Utylix.Extract", throwOnMissingSubKey: false);   // an earlier name
                if (enabled)
                {
                    using var menu = Registry.CurrentUser.CreateSubKey(verbKey);
                    menu.SetValue("MUIVerb", "Utylix Archive");
                    menu.SetValue("SubCommands", "");
                    if (icon.Length > 0) menu.SetValue("Icon", icon);
                    Add(menu, "1open", "Open with Utylix", $"\"{exe}\" --archive-open \"%1\"");
                    Add(menu, "2files", "Extract files…", $"\"{exe}\" --extract-files \"%1\"");
                    Add(menu, "3here", "Extract here", $"\"{exe}\" --extract-here \"%1\"");
                    Add(menu, "4to", "Extract to folder", $"\"{exe}\" --extract-to \"%1\"");
                    Add(menu, "5new", "Add to archive…", $"\"{exe}\" --archive-add \"%1\"");          // like WinRAR: packing is offered on an archive too
                    Add(menu, "6zip", "Add to ZIP", $"\"{exe}\" --zip-add \"%1\"");
                }

                // "Open with": list Utylix; only OUR entry in the list is ever added or removed
                if (enabled)
                {
                    using var owp = Registry.CurrentUser.CreateSubKey($@"Software\Classes\.{ext}\OpenWithProgids");
                    owp.SetValue(ProgId, new byte[0], RegistryValueKind.None);
                }
                else
                {
                    using var owp = Registry.CurrentUser.OpenSubKey($@"Software\Classes\.{ext}\OpenWithProgids", writable: true);
                    owp?.DeleteValue(ProgId, throwOnMissingValue: false);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* not fatal */ }
    }

    private static void Add(RegistryKey menu, string name, string label, string command)
    {
        using var item = menu.CreateSubKey(@"shell\" + name);
        item.SetValue("", label);
        using var cmd = item.CreateSubKey("command");
        cmd.SetValue("", command);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")] private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>
    /// The player's and the archive's own logo for Explorer: copied out of the program into the data folder (Explorer needs a real
    /// .ico file for anything other than the program's main icon). Falls back to the main icon if that fails.
    /// </summary>
    private static string OwnIcon(string dataDir, string name, string fallback)
    {
        try
        {
            string dir = Path.Combine(dataDir, "icons");
            Directory.CreateDirectory(dir);
            var res = Application.GetResourceStream(new Uri($"pack://application:,,,/{name}.ico"));
            using var src = res.Stream;
            using var ms = new MemoryStream();
            src.CopyTo(ms);
            // The file name carries a fingerprint of the picture: a new logo gets a NEW path, so Explorer (which remembers icons by
            // path, also in open menus) cannot keep showing the old one.
            string tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ms.ToArray()))[..8].ToLowerInvariant();
            string path = Path.Combine(dir, $"{name}-{tag}.ico");
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, ms.ToArray());
                foreach (var old in Directory.GetFiles(dir, name + "*.ico")) if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) { try { File.Delete(old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
                // SHCNE_ASSOCCHANGED, a moment later (after the registry entries that use this file are written)
                _ = System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero));
            }
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return fallback; }
    }

    /// <summary>
    /// The menu icon: the logo built into Utylix.exe itself ("path,0"). Nothing is copied anywhere, so there is no separate file
    /// that can go missing (an earlier version copied it into the data folder).
    /// </summary>
    private static string IconOf(string exe) => $"\"{exe}\",0";

    /// <summary>(no longer used for the menus) Explorer needs the icon as a real file: copy it out of the program once.</summary>
    private static string EnsureIcon(string dataDir)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            string path = Path.Combine(dataDir, "convert.ico");
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/convert.ico"));
            using var src = res.Stream;
            using var ms = new MemoryStream();
            src.CopyTo(ms);
            if (!File.Exists(path) || new FileInfo(path).Length != ms.Length)
            {
                File.WriteAllBytes(path, ms.ToArray());
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);            // SHCNE_ASSOCCHANGED: Explorer shows the new icon now, not after a restart
            }
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }
}
