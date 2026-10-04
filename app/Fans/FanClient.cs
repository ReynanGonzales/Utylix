using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IdmClone;

/// <summary>The normal Utylix's side: starts the administrator helper (Windows asks first) and talks to it.</summary>
internal sealed class FanClient : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _one = new(1, 1);

    public bool Connected => _pipe is { IsConnected: true };

    /// <summary>Starts the helper with administrator rights. Returns false if the person said no to the Windows prompt.</summary>
    public static bool StartHelper()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return false;
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        psi.ArgumentList.Add("--fan-helper");
        psi.ArgumentList.Add(sid);
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] is "--data" or "--fan-suffix") {           // a test copy: both use the same data folder / pipe name
                psi.ArgumentList.Add(args[i]); psi.ArgumentList.Add(args[i + 1]); }
        try { Process.Start(psi); return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }            // "No" on the Windows prompt
    }

    public async Task<bool> ConnectAsync(int timeoutMs = 8000)
    {
        await _one.WaitAsync();
        try
        {
            Close();
            var pipe = new NamedPipeClientStream(".", FanJson.PipeFor(WindowsIdentity.GetCurrent().User?.Value ?? ""), PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(timeoutMs); }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { pipe.Dispose(); return false; }
            _pipe = pipe;
            _reader = new StreamReader(pipe);
            _writer = new StreamWriter(pipe) { AutoFlush = true };
            return true;
        }
        finally { _one.Release(); }
    }

    public async Task<FanReply?> AskAsync(FanRequest request, int timeoutSeconds = 6)
    {
        await _one.WaitAsync();
        try
        {
            if (_pipe is not { IsConnected: true } || _writer == null || _reader == null) return null;
            await _writer.WriteLineAsync(JsonSerializer.Serialize(request, FanJson.Options));
            var line = await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
            return line == null ? null : JsonSerializer.Deserialize<FanReply>(line, FanJson.Options);
        }
        catch (Exception e) when (e is IOException or TimeoutException or JsonException or ObjectDisposedException or InvalidOperationException)
        {
            Close();
            return null;
        }
        finally { _one.Release(); }
    }

    /// <summary>Tells the helper to hand every fan back and end.</summary>
    public async Task StopAsync()
    {
        try { await AskAsync(new FanRequest { Cmd = "quit" }); } catch (Exception) { /* closing anyway */ }
        Close();
    }

    private void Close()
    {
        try { _pipe?.Dispose(); } catch (Exception) { }
        _pipe = null; _reader = null; _writer = null;
    }

    public void Dispose() => Close();
}
