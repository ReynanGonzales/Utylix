using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// "Deep clean" asks for administrator rights (it reaches programs that run as administrator). On an account that is not an administrator Windows asks
/// for an administrator's name and password, and a scheduled task cannot help there (it gets no more rights than the account itself). So the password is
/// asked ONCE: this elevated copy ("--memory-helper") stays running, hidden, and does every further deep clean on request over a named pipe that only that
/// account may use - the next Deep clean from the desktop menu finds it and does not ask again. It does one thing only (the deep clean), shows the same
/// small notice, and ends after six hours without a request (or when the account logs off).
/// </summary>
internal static class MemoryHelper
{
    private static string PipeFor(string sid) => "Utylix.MemoryHelper." + sid;
    private static string CurrentSid => WindowsIdentity.GetCurrent().User?.Value ?? "";
    private static readonly TimeSpan IdleLimit = TimeSpan.FromHours(6);

    /// <summary>Asks a helper that is already running to do the deep clean. True when it did (nothing has to be asked then).</summary>
    public static bool TryAsk(string? sid = null)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeFor(sid ?? CurrentSid), PipeDirection.InOut);
            pipe.Connect(500);
            var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine("deep");
            return new StreamReader(pipe).ReadLine() == "ok";
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Asks a running helper to end (before Utylix is updated: its files must not be in use). Nothing happens when there is none.</summary>
    public static void TryQuit()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeFor(CurrentSid), PipeDirection.InOut);
            pipe.Connect(300);
            var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine("quit");
            new StreamReader(pipe).ReadLine();
            Thread.Sleep(800);                                       // (a moment to let it end)
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Windows asks for permission (an administrator's password on an account that is not one) and the helper starts; it does the first deep clean at once.</summary>
    public static bool StartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--memory-helper --for " + CurrentSid + " --now") { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception) { return false; }                      // (permission refused)
    }

    private static NamedPipeServerStream Create(string ownerSid)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(ownerSid), PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeFor(ownerSid), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
    }

    /// <summary>The elevated copy: serves deep-clean requests from the account <paramref name="ownerSid"/> until it has been idle for six hours.</summary>
    public static void RunHelper(string ownerSid, bool cleanNow)
    {
        var app = Application.Current;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;           // (the notice closing must not end the helper)
        MemoryToast.KeepAlive = true;
        var mutex = new Mutex(true, "Local\\Utylix.MemoryHelper." + ownerSid, out bool first);
        if (!first)                                                  // another helper is already there: it does the work
        {
            if (cleanNow) TryAsk(ownerSid);
            app.Shutdown();
            return;
        }
        var lastUse = DateTime.UtcNow;
        if (cleanNow) MemoryToast.Run(true, null);

        Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = Create(ownerSid);
                    pipe.WaitForConnection();
                    var reader = new StreamReader(pipe);
                    string? request = reader.ReadLine();
                    var writer = new StreamWriter(pipe) { AutoFlush = true };
                    if (request == "deep")
                    {
                        lastUse = DateTime.UtcNow;
                        app.Dispatcher.Invoke(() => MemoryToast.Run(true, null));
                        writer.WriteLine("ok");
                    }
                    else if (request == "quit")
                    {
                        writer.WriteLine("ok");
                        app.Dispatcher.BeginInvoke(new Action(() => app.Shutdown()));
                        return;
                    }
                    else writer.WriteLine("no");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) { Thread.Sleep(500); }
            }
        });

        var idle = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        idle.Tick += (_, _) => { if (DateTime.UtcNow - lastUse > IdleLimit) { idle.Stop(); GC.KeepAlive(mutex); app.Shutdown(); } };
        idle.Start();
    }
}
