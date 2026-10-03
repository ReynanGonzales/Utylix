using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IdmClone;

/// <summary>
/// Starting the fan helper needs administrator rights, which Windows asks for each time. To avoid a prompt on every start, a one-time
/// setup (that DOES ask) puts a copy of the program into the protected Program Files folder and registers a Windows scheduled task for
/// the current user, "run with highest privileges, only when asked". Afterwards Utylix starts that task without any prompt.
///
/// The task runs the copy in Program Files, never the program in the user's own folder: a file anyone can change must not be run with
/// administrator rights. After an update of Utylix the copy that was approved before keeps working (nothing is asked again) as long as
/// it speaks the same language as the program: <see cref="Generation"/> is raised only when the two stop understanding each other.
/// </summary>
internal static class FanTask
{
    public const string Name = "Utylix Fan Helper";

    /// <summary>The old place of the helper's own protected copy (Program Files\Utylix\FanHelper). Still used by installs that can't be locked (a per-user install).</summary>
    private static string LegacyDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Utylix", "FanHelper");
    private static string LegacyExe => Path.Combine(LegacyDir, "Utylix.exe");
    private static string GenerationFile => Path.Combine(LegacyDir, "generation.txt");

    /// <summary>The folder this program runs from, and its exe.</summary>
    private static string ProgramDir => Path.GetDirectoryName(Environment.ProcessPath) ?? "";
    private static string ProgramExe => Environment.ProcessPath ?? "";

    /// <summary>
    /// The installed copy of a machine-wide install (not inside anyone's profile): its folder can be locked so only administrators can change it,
    /// and then the helper runs from that folder itself, with no second copy. Anything else (a per-user install, a test build, the single-file build)
    /// keeps using the protected copy in Program Files.
    /// </summary>
    public static bool DirectInstall
    {
        get
        {
            try
            {
                if (Installer.IsSingleFile || ProgramExe.Length == 0) return false;
                if (Installer.Installed() is not { } installed) return false;
                string dir = Path.GetFullPath(ProgramDir).TrimEnd('\\'), profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
                return string.Equals(Path.GetFullPath(installed.Dir).TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase)
                       && !dir.StartsWith(profile + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>The exe the task runs.</summary>
    public static string HelperExe => DirectInstall ? ProgramExe : LegacyExe;

    /// <summary>Raise this when the helper and the program can no longer work with an older copy of each other (then one prompt sets it up again).</summary>
    private const string Generation = "1";

    // ---------- is a folder one that only administrators can change? ----------
    private static readonly System.Security.Principal.SecurityIdentifier[] Trusted =
    {
        new("S-1-5-32-544"), new("S-1-5-18"), new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"),      // Administrators, SYSTEM, TrustedInstaller
    };

    /// <summary>Nobody but an administrator can add, change or delete files in this folder (or take it over): then it is safe to run what is in it with administrator rights.</summary>
    public static bool FolderIsProtected(string dir)
    {
        try
        {
            var rights = System.Security.AccessControl.FileSystemRights.WriteData | System.Security.AccessControl.FileSystemRights.AppendData | System.Security.AccessControl.FileSystemRights.Delete
                         | System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles | System.Security.AccessControl.FileSystemRights.ChangePermissions | System.Security.AccessControl.FileSystemRights.TakeOwnership;
            var checks = new List<System.Security.AccessControl.FileSystemSecurity> { new DirectoryInfo(dir).GetAccessControl() };
            string exe = Path.Combine(dir, "Utylix.exe");
            if (File.Exists(exe)) checks.Add(new FileInfo(exe).GetAccessControl());
            foreach (var security in checks)
            {
                if (security.GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) is System.Security.Principal.SecurityIdentifier owner && !Trusted.Contains(owner)) return false;
                foreach (System.Security.AccessControl.FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
                    if (rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow && (rule.FileSystemRights & rights) != 0
                        && rule.IdentityReference is System.Security.Principal.SecurityIdentifier who && !Trusted.Contains(who) && who.Value != "S-1-3-0") return false;      // (S-1-3-0 = creator owner: only matters through the owner)
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Principal.IdentityNotMappedException) { return false; }
    }

    /// <summary>The program's folder will be locked by the one-time setup (a machine-wide install that isn't locked yet).</summary>
    public static bool WillLockFolder => DirectInstall && !FolderIsProtected(ProgramDir);

    private static int Schtasks(string arguments, out string output)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(20000);
        return p.ExitCode;
    }

    public static bool Exists() => Schtasks($"/query /tn \"{Name}\"", out _) == 0;

    /// <summary>The task exists and the copy it runs, though perhaps from an older version, still works with this program.</summary>
    public static async Task<bool> IsReadyAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                string? self = Environment.ProcessPath;
                if (self == null || !Exists()) return false;
                if (DirectInstall)
                {
                    // the task must run THIS folder's exe, and the folder must be locked (else anyone could swap the exe that runs as administrator)
                    Schtasks($"/query /tn \"{Name}\" /xml", out string xml);
                    return xml.Contains(ProgramExe, StringComparison.OrdinalIgnoreCase) && FolderIsProtected(ProgramDir);
                }
                if (!File.Exists(LegacyExe)) return false;
                string have = File.Exists(GenerationFile) ? File.ReadAllText(GenerationFile).Trim() : "1";       // copies made before this file existed are generation 1
                return have == Generation;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        });
    }

    /// <summary>Starts the helper through the task: no prompt.</summary>
    public static bool RunNow() => Schtasks($"/run /tn \"{Name}\"", out _) == 0;

    /// <summary>
    /// Ends the helper the task started (no prompt: it is the person's own task). For a helper that is running but doesn't answer: one left
    /// over from before an update can keep serving a program that is gone, and the task runs only one copy, so a new start would do nothing.
    /// </summary>
    public static void EndStale() => Schtasks($"/end /tn \"{Name}\"", out _);

    /// <summary>Asks Windows for administrator rights once and sets the task up. Returns false if it was refused or failed.</summary>
    public static async Task<bool> InstallWithPromptAsync()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return false;
        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        psi.ArgumentList.Add("--fan-task-install"); psi.ArgumentList.Add(sid);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }                       // "No" on the Windows prompt
    }

    public static async Task<bool> RemoveWithPromptAsync()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return false;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        psi.ArgumentList.Add("--fan-task-remove");
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    // ----------------------------------------------------------------------------------------- the elevated side

    /// <summary>Ends the programs running from this exe (not this process: the setup itself may be that exe).</summary>
    private static void KillAt(string exe)
    {
        foreach (var p in Process.GetProcessesByName("Utylix"))
        {
            try { if (p.Id != Environment.ProcessId && string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(5000); } }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            finally { p.Dispose(); }
        }
    }

    /// <summary>
    /// Makes the program's folder one only administrators can change: they own it and can do everything, other users can read and run what is in it.
    /// (A folder made on C:\ lets every user change its files, which would let any program put its own exe where the administrator helper runs.)
    /// </summary>
    private static bool LockFolder(string dir)
    {
        int Icacls(string arguments)
        {
            var psi = new ProcessStartInfo("icacls.exe", arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            p.WaitForExit(120000);
            return p.ExitCode;
        }
        return Icacls($"\"{dir}\" /setowner *S-1-5-32-544 /T /C /Q") == 0
            && Icacls($"\"{dir}\" /inheritance:r /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX /T /C /Q") == 0
            && FolderIsProtected(dir);
    }

    /// <summary>Takes away the old separate copy in Program Files (the helper runs from the program's own folder now).</summary>
    private static void RemoveLegacyCopy()
    {
        KillAt(LegacyExe);
        try { if (Directory.Exists(LegacyDir)) Directory.Delete(LegacyDir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        try { string parent = Path.GetDirectoryName(LegacyDir)!; if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Runs with administrator rights ("--fan-task-install SID"): lock the program's folder (or copy into Program Files), register the task.</summary>
    public static int Install(string sid)
    {
        try
        {
            Schtasks($"/end /tn \"{Name}\"", out _);
            KillAt(LegacyExe);
            string exe;
            if (DirectInstall)
            {
                // the helper runs from the program's own folder, which is locked so only administrators can change it
                if (!LockFolder(ProgramDir)) return 3;
                exe = ProgramExe;
                RemoveLegacyCopy();
            }
            else
            {
                Directory.CreateDirectory(LegacyDir);
                // the protected copy: the whole program folder (or the one exe of a single-file build), as Utylix.exe + its files
                foreach (string rel in Installer.ProgramFiles(out string src))
                {
                    string target = Path.Combine(LegacyDir, Installer.IsSingleFile ? "Utylix.exe" : rel), tmp = target + ".new";
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Path.Combine(src, rel), tmp, true);
                    File.Move(tmp, target, true);
                }
                File.WriteAllText(GenerationFile, Generation);
                exe = LegacyExe;
            }

            string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Utylix fan control helper. It runs only when Utylix asks for it.</Description></RegistrationInfo>
  <Triggers />
  <Principals><Principal id=""Author""><UserId>{Escape(sid)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{Escape(exe)}</Command><Arguments>--fan-helper {Escape(sid)}</Arguments></Exec></Actions>
</Task>";
            string file = Path.Combine(Path.GetTempPath(), "utylix_fan_task_" + Guid.NewGuid().ToString("N")[..8] + ".xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            try { return Schtasks($"/create /tn \"{Name}\" /xml \"{file}\" /f", out _); }
            finally { try { File.Delete(file); } catch (IOException) { } }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 1; }
    }

    /// <summary>Runs with administrator rights ("--fan-task-remove"): delete the task and the copy.</summary>
    public static int Remove()
    {
        Schtasks($"/end /tn \"{Name}\"", out _);
        int code = Schtasks($"/delete /tn \"{Name}\" /f", out _);
        // only the old separate copy is deleted: when the helper runs from the program's own folder, that folder is the program itself
        RemoveLegacyCopy();
        if (Directory.Exists(LegacyDir)) code = code == 0 ? 2 : code;
        return code;
    }

    private static string Escape(string s) => System.Security.SecurityElement.Escape(s) ?? "";
}
