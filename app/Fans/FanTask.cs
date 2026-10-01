using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace IdmClone;

/// <summary>
/// Starting the fan helper needs administrator rights, which Windows asks for each time. To avoid a prompt on every start, a one-time
/// setup (that DOES ask) puts a copy of the program into the protected Program Files folder and registers a Windows scheduled task for
/// the current user, "run with highest privileges, only when asked". Afterwards Utylix starts that task without any prompt.
///
/// The task runs the copy in Program Files, never the program in the user's own folder: a file anyone can change must not be run with
/// administrator rights. The copy is checked against the running program; after an update it has to be set up again (one prompt).
/// </summary>
internal static class FanTask
{
    public const string Name = "Utylix Fan Helper";
    public static string HelperDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Utylix", "FanHelper");
    public static string HelperExe => Path.Combine(HelperDir, "Utylix.exe");

    private static int Schtasks(string arguments, out string output)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(20000);
        return p.ExitCode;
    }

    public static bool Exists() => Schtasks($"/query /tn \"{Name}\"", out _) == 0;

    /// <summary>The task exists and the copy it runs is exactly this program.</summary>
    public static async Task<bool> IsReadyAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                string? self = Environment.ProcessPath;
                if (self == null || !File.Exists(HelperExe) || !Exists()) return false;
                if (new FileInfo(self).Length != new FileInfo(HelperExe).Length) return false;
                return Hash(self) == Hash(HelperExe);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        });
    }

    private static string Hash(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(f));
    }

    /// <summary>Starts the helper through the task: no prompt.</summary>
    public static bool RunNow() => Schtasks($"/run /tn \"{Name}\"", out _) == 0;

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

    /// <summary>Runs with administrator rights ("--fan-task-install SID"): copy into Program Files, register the task.</summary>
    public static int Install(string sid)
    {
        try
        {
            string self = Environment.ProcessPath ?? throw new IOException("no path");
            Schtasks($"/end /tn \"{Name}\"", out _);
            foreach (var p in Process.GetProcessesByName("Utylix"))
            {
                try { if (string.Equals(p.MainModule?.FileName, HelperExe, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(5000); } }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                finally { p.Dispose(); }
            }
            Directory.CreateDirectory(HelperDir);
            string tmp = HelperExe + ".new";
            File.Copy(self, tmp, true);
            File.Move(tmp, HelperExe, true);

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
  <Actions Context=""Author""><Exec><Command>{Escape(HelperExe)}</Command><Arguments>--fan-helper {Escape(sid)}</Arguments></Exec></Actions>
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
        foreach (var p in Process.GetProcessesByName("Utylix"))
        {
            try { if (string.Equals(p.MainModule?.FileName, HelperExe, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(5000); } }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            finally { p.Dispose(); }
        }
        try { if (Directory.Exists(HelperDir)) Directory.Delete(HelperDir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { code = code == 0 ? 2 : code; }
        return code;
    }

    private static string Escape(string s) => System.Security.SecurityElement.Escape(s) ?? "";
}
