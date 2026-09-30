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
                using var progId = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + PlayProgId);
                progId.SetValue("", "Video or music (played with Utylix)");
                using (var di = progId.CreateSubKey("DefaultIcon")) di.SetValue("", icon);
                using var cmd = progId.CreateSubKey(@"shell\open\command");
                cmd.SetValue("", $"\"{exe}\" --play \"%1\"");
            }
            else Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + PlayProgId, throwOnMissingSubKey: false);

            foreach (string ext in PlayerMedia.VideoExtensions.Concat(PlayerMedia.AudioExtensions).Distinct())
            {
                string verbKey = $@"Software\Classes\SystemFileAssociations\.{ext}\shell\{PlayVerb}";
                Registry.CurrentUser.DeleteSubKeyTree(verbKey, throwOnMissingSubKey: false);
                if (enabled)
                {
                    using var verb = Registry.CurrentUser.CreateSubKey(verbKey);
                    verb.SetValue("MUIVerb", "Play with Utylix");
                    verb.SetValue("Icon", icon);
                    using var command = verb.CreateSubKey("command");
                    command.SetValue("", $"\"{exe}\" --play \"%1\"");
                    using var owp = Registry.CurrentUser.CreateSubKey($@"Software\Classes\.{ext}\OpenWithProgids");
                    owp.SetValue(PlayProgId, new byte[0], RegistryValueKind.None);
                }
                else
                {
                    using var owp = Registry.CurrentUser.OpenSubKey($@"Software\Classes\.{ext}\OpenWithProgids", writable: true);
                    owp?.DeleteValue(PlayProgId, throwOnMissingValue: false);
                }
            }
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
            string path = Path.Combine(dir, name + ".ico");
            var res = Application.GetResourceStream(new Uri($"pack://application:,,,/{name}.ico"));
            using var src = res.Stream;
            using var ms = new MemoryStream();
            src.CopyTo(ms);
            if (!File.Exists(path) || new FileInfo(path).Length != ms.Length) File.WriteAllBytes(path, ms.ToArray());
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
            if (!File.Exists(path) || new FileInfo(path).Length != ms.Length) File.WriteAllBytes(path, ms.ToArray());
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }
}
