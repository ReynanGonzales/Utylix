using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace IdmClone.Engine;

/// <summary>
/// The free PawnIO driver (pawnio.eu) that fan control and RAM lighting need. It is not carried inside Utylix (it is a kernel driver made by someone
/// else and it has its own updates): the installer, when asked, has Windows' own winget fetch and install it, and Windows asks for permission.
/// </summary>
internal static class PawnIoSetup
{
    public static bool Installed =>
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll")) ||
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PawnIO", "PawnIOLib.dll"));

    private static string? Winget()
    {
        string alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(alias) ? alias : null;
    }

    public static async Task InstallAsync(Action<string> status, CancellationToken cancel)
    {
        if (Installed) { status("PawnIO is already installed"); return; }
        string? winget = Winget();
        if (winget == null) throw new IOException("winget (Windows' App Installer) isn't on this PC. Install PawnIO from https://pawnio.eu instead (Settings > Fans explains how).");
        status("Installing PawnIO (Windows may ask for permission)…");
        var psi = new ProcessStartInfo(winget, "install --id namazso.PawnIO --exact --silent --accept-package-agreements --accept-source-agreements")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(psi) ?? throw new IOException("winget did not start.");
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw new IOException("Installing PawnIO took too long, or was cancelled.");
        }
        if (!Installed && process.ExitCode != 0) throw new IOException($"winget could not install PawnIO (code {process.ExitCode}). You can install it from https://pawnio.eu.");
    }
}
