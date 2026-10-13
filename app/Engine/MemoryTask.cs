using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace IdmClone.Engine;

/// <summary>
/// The scheduled task "Utylix Free Memory (account)": after the person said yes ONCE to Windows' permission question, the desktop entry runs the
/// deep clean with administrator rights without asking again (the same technique as the fan helper's task). The task only ever runs
/// "Utylix.exe --free-memory-deep". It is made only when Utylix's own folder can be changed by administrators only: a program that anyone could
/// replace must never be started with administrator rights without a question.
/// </summary>
public static class MemoryTask
{
    private const string BaseName = "Utylix Free Memory";
    private const string GuardBaseName = "Utylix Memory Guard";

    public static string NameFor(string sid) => NameFor(BaseName, sid);

    private static string NameFor(string baseName, string sid)
    {
        string who;
        try
        {
            who = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
            who = who[(who.LastIndexOf('\\') + 1)..];
        }
        catch (Exception e) when (e is ArgumentException or IdentityNotMappedException or SystemException) { who = sid; }
        foreach (char c in Path.GetInvalidFileNameChars()) who = who.Replace(c, '_');
        return baseName + " (" + who + ")";
    }

    /// <summary>The task of the memory guard (limits for programs, see MemoryGuard): "Utylix.exe --memory-guard" with administrator rights.</summary>
    public static bool GuardExists() => Schtasks($"/query /tn \"{NameFor(GuardBaseName, CurrentSid)}\"", out _) == 0;
    public static bool RunGuard() => Schtasks($"/run /tn \"{NameFor(GuardBaseName, CurrentSid)}\"", out _) == 0;
    public static bool InstallGuard(string sid) => InstallTask(GuardBaseName, sid, "--memory-guard",
        "Utylix: watches the programs you gave a memory limit and trims them with administrator rights, without asking each time. It runs only while Utylix runs.");

    private static string CurrentSid => WindowsIdentity.GetCurrent().User?.Value ?? "";

    private static int Schtasks(string arguments, out string output)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(20000);
        return p.ExitCode;
    }

    public static bool Exists() => Schtasks($"/query /tn \"{NameFor(CurrentSid)}\"", out _) == 0;

    /// <summary>Starts the task (it shows its own notice and does the deep clean). False when it could not be started.</summary>
    public static bool RunNow() => Schtasks($"/run /tn \"{NameFor(CurrentSid)}\"", out _) == 0;

    /// <summary>The task can be made here: an account that may use administrator rights, and a program folder only administrators can change.</summary>
    public static bool CanBeSilent
    {
        get
        {
            try
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length == 0 || IdmClone.Installer.IsSingleFile) return false;
                return IdmClone.FanTask.AccountCanElevate && IdmClone.FanTask.FolderIsProtected(Path.GetDirectoryName(exe)!);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        }
    }

    /// <summary>Makes (or replaces) the task for the account <paramref name="sid"/>. Needs administrator rights.</summary>
    public static bool Install(string sid) => InstallTask(BaseName, sid, "--free-memory-deep",
        "Utylix: free up memory with administrator rights, without asking each time. It runs only when you use Free up memory.");

    private static bool InstallTask(string baseName, string sid, string arguments, string description)
    {
        try
        {
            string exe = Environment.ProcessPath!;
            string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>{Esc(description)}</Description></RegistrationInfo>
  <Triggers />
  <Principals><Principal id=""Author""><UserId>{Esc(sid)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
    <Priority>5</Priority>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{Esc(exe)}</Command><Arguments>{Esc(arguments)}</Arguments></Exec></Actions>
</Task>";
            string file = Path.Combine(Path.GetTempPath(), "utylix_mem_task_" + Guid.NewGuid().ToString("N")[..8] + ".xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            try { return Schtasks($"/create /tn \"{NameFor(baseName, sid)}\" /xml \"{file}\" /f", out _) == 0; }
            finally { try { File.Delete(file); } catch (IOException) { } }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? "";
}
