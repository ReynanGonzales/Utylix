using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using IdmClone.Engine;
using LibreHardwareMonitor.Hardware;

namespace IdmClone;

/// <summary>
/// The part that touches the hardware. It runs as its own process with administrator rights (started by Utylix with
/// "--fan-helper <owner SID>"), reads the temperatures and fan speeds, and sets fan speeds when asked. The normal Utylix talks to it
/// over a named pipe that only the person who started it can use.
///
/// Safety first: fans it has not been told about stay under the PC's own control; a speed is never set below the floor; if a CPU or
/// graphics temperature reaches the emergency limit every controlled fan goes to 100 %; and when Utylix closes, stops answering for
/// 10 seconds, or this process ends for any reason, every fan is handed back to the PC's own control.
/// </summary>
internal static class FanHelper
{
    // The hardware library is not thread-safe: ONLY the loop thread reads or drives the hardware, and it holds Gate while it does.
    private static readonly object Gate = new();
    private static Computer? _computer;
    private static readonly Dictionary<string, (ISensor Sensor, double Last)> Driven = new();   // controls we have taken over
    private static FanConfig _config = new();
    private static volatile bool _emergency;
    private static long _lastContact = Environment.TickCount64;
    private static volatile bool _quit;

    // What Utylix is told is the latest reading the loop thread made, never a reading made on the spot. A sensor that stalls (a busy graphics card, a driver
    // that stops answering for 20 seconds) then only makes the numbers old; before, it made Utylix wait, give up and close the pipe, and the helper ended up
    // alive but listening to nobody (the "fan control always disconnects" of 2026-11).
    private static volatile List<FanSensor> _snapshot = new();
    private static long _snapshotAt;                              // Environment.TickCount64 of that reading (0 = none yet)
    private static FanConfig? _pendingConfig;                     // sent by Utylix, taken over by the loop thread
    private static volatile bool _revertRequested;
    private static readonly AutoResetEvent Wake = new(false);
    private static volatile string _reading = "";                 // the hardware the loop thread is waiting for right now ("" = none), for the log
    private static long _readingSince;
    private static long _reportedSince;
    private static string _lastFailure = "";

    public static int Run(string ownerSid)
    {
        try { return RunCore(ownerSid); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log("the helper stopped because of an error: " + e);
            return 3;
        }
    }

    private static int RunCore(string ownerSid)
    {
        Log("the helper started");
        try
        {
            _computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true, IsControllerEnabled = true };
            _computer.Open();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log("could not open the hardware: " + e.Message);
            return 2;
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RevertAll();
        var worker = new Thread(Loop) { IsBackground = true, Name = "Utylix fan loop" };
        worker.Start();
        using var watch = new Timer(_ => Watch(), null, 2000, 2000);       // says in the log which sensor a slow reading is waiting for
        try
        {
            while (!_quit)
            {
                try
                {
                    using var server = Create(ownerSid);
                    var wait = server.WaitForConnectionAsync();
                    while (!wait.Wait(1000))
                    {
                        if (_quit) return 0;
                        if (Driven.Count > 0 && Environment.TickCount64 - _lastContact > 10000) RequestRevert();
                        if (Environment.TickCount64 - _lastContact > 60000) return 0;       // nobody came: no reason to keep running
                    }
                    _lastContact = Environment.TickCount64;
                    Serve(server);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or UnauthorizedAccessException or AggregateException)
                {
                    // whatever goes wrong with one connection must never end the helper (it would stay alive without a pipe, and the scheduled task would not start another)
                    Log("the connection failed: " + e.GetType().Name + ": " + e.Message);
                    Thread.Sleep(500);
                }
                RequestRevert();                                            // Utylix went away: hand every fan back
                if (Environment.TickCount64 - _lastContact > 60000) break;
            }
        }
        finally
        {
            _quit = true;
            Wake.Set();
            RevertAll();
            try { _computer.Close(); } catch (Exception) { /* ending anyway */ }
            try { _smbus?.Dispose(); } catch (Exception) { /* ending anyway */ }
            Log("the helper ended");
        }
        return 0;
    }

    private static NamedPipeServerStream Create(string ownerSid)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(ownerSid), PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(FanJson.PipeFor(ownerSid), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, security);
    }

    private static void Serve(NamedPipeServerStream pipe)
    {
        var reader = new StreamReader(pipe);
        var writer = new StreamWriter(pipe) { AutoFlush = true };
        try
        {
            while (pipe.IsConnected && !_quit)
            {
                string? line;
                try { line = reader.ReadLine(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { Log("connection ended: " + e.Message); return; }
                if (line == null) { Log("Utylix closed the connection"); return; }
                _lastContact = Environment.TickCount64;
                FanReply reply;
                try
                {
                    var request = JsonSerializer.Deserialize<FanRequest>(line, FanJson.Options) ?? new FanRequest();
                    reply = Handle(request);
                }
                catch (JsonException) { reply = new FanReply { Ok = false, Error = "bad request" }; }
                catch (Exception e) when (e is not OutOfMemoryException) { Log("a request failed: " + e.GetType().Name + ": " + e.Message); reply = new FanReply { Ok = false, Error = e.Message }; }
                try { writer.WriteLine(JsonSerializer.Serialize(reply, FanJson.Options)); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { Log("could not answer: " + e.Message); return; }
                if (_quit) return;
            }
        }
        finally
        {
            // Closing a pipe whose other end has already gone throws "Pipe is broken" again. That used to leave Serve with an exception, which ended the helper's
            // main loop: the process stayed alive without a pipe, and the scheduled task ("run only one copy") could not start a new one.
            try { writer.Dispose(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            try { reader.Dispose(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
    }

    private static FanReply Handle(FanRequest request)
    {
        switch (request.Cmd)
        {
            case "snapshot": return Snapshot();
            case "config":
                Volatile.Write(ref _pendingConfig, Sanitize(request.Config ?? new FanConfig()));       // (the loop thread applies it within a moment)
                Wake.Set();
                return new FanReply { Ok = true, Emergency = _emergency };
            case "light-scan": return LightScan();
            case "light-set": return LightSet(request.Look ?? new LightLook());
            case "light-save": return LightSave();
            case "armoury": return Armoury(request.Flag);
            case "quit":
                _quit = true;
                Wake.Set();
                return new FanReply { Ok = true };
            default:
                return new FanReply { Ok = false, Error = "unknown command" };
        }
    }

    /// <summary>Keeps every number in a sane range, whatever was sent.</summary>
    private static FanConfig Sanitize(FanConfig c)
    {
        c.EmergencyCpu = Math.Clamp(c.EmergencyCpu, 60, 95);
        c.EmergencyGpu = Math.Clamp(c.EmergencyGpu, 60, 95);
        foreach (var r in c.Rules)
        {
            r.Floor = Math.Clamp(r.Floor, 15, 100);
            r.Percent = Math.Clamp(r.Percent, r.Floor, 100);
            r.Points = r.Points.Where(p => p.Length == 2).Select(p => new[] { Math.Clamp(p[0], 0, 110), Math.Clamp(p[1], 0, 100) }).OrderBy(p => p[0]).Take(12).ToList();
            if (r.Mode is not ("auto" or "manual" or "curve")) r.Mode = "auto";
        }
        return c;
    }

    // ------------------------------------------------------------------------------------------ reading

    private static IEnumerable<IHardware> AllHardware(IEnumerable<IHardware> list)
    {
        foreach (var h in list) { yield return h; foreach (var s in AllHardware(h.SubHardware)) yield return s; }
    }

    private static string KindOf(IHardware h)
    {
        for (var x = h; x != null; x = x.Parent)
            switch (x.HardwareType)
            {
                case HardwareType.Cpu: return "cpu";
                case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel: return "gpu";
                case HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController: return "board";
            }
        return "other";
    }

    /// <summary>The answer to "snapshot": the latest reading of the loop thread, at once (see the fields at the top).</summary>
    private static FanReply Snapshot()
    {
        long waited = Environment.TickCount64;
        while (Volatile.Read(ref _snapshotAt) == 0 && Environment.TickCount64 - waited < 8000 && !_quit) Thread.Sleep(50);       // the very first reading is still being made
        long at = Volatile.Read(ref _snapshotAt);
        if (at == 0) return new FanReply { Ok = false, Error = "still reading the hardware" };
        return new FanReply { Ok = true, Sensors = _snapshot, Emergency = _emergency, AgeSeconds = (int)((Environment.TickCount64 - at) / 1000) };
    }

    /// <summary>Reads every piece of hardware once (each can be slow on its own: the log says which one). Every other function here only looks at these values.</summary>
    private static readonly long Started = Environment.TickCount64;
    private static bool _testStalled;                              // (test only: UTYLIX_TEST_STALL_MS = one reading that hangs that long, 5 s after the start)

    private static void UpdateAll()
    {
        if (!_testStalled && int.TryParse(Environment.GetEnvironmentVariable("UTYLIX_TEST_STALL_MS"), out int stall) && stall > 0 && Environment.TickCount64 - Started > 5000)
        {
            _testStalled = true;
            _reading = "(test stall)"; Volatile.Write(ref _readingSince, Environment.TickCount64);
            Thread.Sleep(stall);
        }
        List<string>? slow = null;
        foreach (var h in AllHardware(_computer!.Hardware))
        {
            long started = Environment.TickCount64;
            Volatile.Write(ref _readingSince, started);
            _reading = h.Name;
            try { h.Update(); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                string why = h.Name + ": " + e.GetType().Name + ": " + e.Message;
                if (why != _lastFailure) { _lastFailure = why; Log("could not read " + why); }
            }
            long took = Environment.TickCount64 - started;
            if (took > 1500) (slow ??= new List<string>()).Add($"{h.Name} {took} ms");
        }
        _reading = "";
        if (slow != null) Log("slow reading: " + string.Join(", ", slow));
    }

    /// <summary>Runs on a timer: when a reading has been waiting for one sensor for a while, the log says which (the one-line "slow reading" only comes when it is over).</summary>
    private static void Watch()
    {
        string what = _reading;
        long since = Volatile.Read(ref _readingSince);
        if (what.Length == 0 || since == 0) return;
        long waited = Environment.TickCount64 - since;
        if (waited > 5000 && _reportedSince != since) { _reportedSince = since; Log($"still waiting for {what}: {waited / 1000} s so far"); }
    }

    private static List<FanSensor> Build()
    {
        var list = new List<FanSensor>();
        foreach (var h in AllHardware(_computer!.Hardware))
        {
            foreach (var s in h.Sensors)
            {
                string type = s.SensorType switch { SensorType.Temperature => "temp", SensorType.Fan => "fan", SensorType.Control => "control", _ => "" };
                if (type.Length == 0) continue;
                var dto = new FanSensor
                {
                    Id = s.Identifier.ToString(), Hardware = h.Name, HardwareId = h.Identifier.ToString(), Kind = KindOf(h), Name = s.Name, Type = type,
                    Value = s.Value is float v && !float.IsNaN(v) ? Math.Round(v, 1) : null,
                };
                if (type == "control")
                {
                    if (s.Control == null) continue;                       // a reading only: nothing to set
                    dto.Auto = !Driven.ContainsKey(dto.Id);
                    dto.Min = Math.Max(0, s.Control.MinSoftwareValue);
                    dto.Max = Math.Min(100, s.Control.MaxSoftwareValue);
                }
                list.Add(dto);
            }
        }
        return list;
    }

    // ------------------------------------------------------------------------------------------ driving

    /// <summary>The one thread that touches the hardware: once a second (or when Utylix sends something) it reads everything, drives the fans and keeps the snapshot.</summary>
    private static void Loop()
    {
        while (!_quit)
        {
            Wake.WaitOne(1000);
            if (_quit) break;
            try { lock (Gate) Pass(); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                string why = "a pass failed: " + e.GetType().Name + ": " + e.Message;
                if (why != _lastFailure) { _lastFailure = why; Log(why); }
            }
        }
    }

    private static void Pass()
    {
        if (_revertRequested) { _revertRequested = false; RevertNow(); }              // (before the new settings: a connection that ended must not leave a fan driven)
        var pending = Interlocked.Exchange(ref _pendingConfig, null);
        if (pending != null) _config = pending;
        UpdateAll();
        if (_config.Rules.Count > 0 || Driven.Count > 0) Apply(); else _emergency = false;
        _snapshot = Build();
        Volatile.Write(ref _snapshotAt, Environment.TickCount64);
    }

    private static ISensor? Find(string id) =>
        AllHardware(_computer!.Hardware).SelectMany(h => h.Sensors).FirstOrDefault(s => s.Identifier.ToString() == id);

    private static double? Temp(string id)
    {
        var s = Find(id);
        return s?.Value is float v && !float.IsNaN(v) ? v : null;                      // (UpdateAll has just read it)
    }

    private static bool Over(string kind, double limit)
    {
        foreach (var h in AllHardware(_computer!.Hardware))
        {
            if (KindOf(h) != kind) continue;
            foreach (var s in h.Sensors)
                if (s.SensorType == SensorType.Temperature && s.Value is float v && !float.IsNaN(v) && v >= limit && s.Name is not null &&
                    (kind == "gpu" ? s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) : s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase)))
                    return true;
        }
        return false;
    }

    private static void Apply()
    {
        bool emergency = Over("cpu", _config.EmergencyCpu) || Over("gpu", _config.EmergencyGpu);
        _emergency = emergency;
        foreach (var rule in _config.Rules)
        {
            var sensor = Find(rule.Control);
            if (sensor?.Control == null) continue;
            if (rule.Mode == "auto") { Release(rule.Control); continue; }
            double target;
            if (emergency) target = 100;
            else if (rule.Mode == "manual") target = rule.Percent;
            else
            {
                double? t = rule.Source.Length > 0 ? Temp(rule.Source) : null;
                if (t == null || rule.Points.Count == 0) { Release(rule.Control); continue; }        // no reading: better the PC's own control than a guess
                target = Interpolate(rule.Points, t.Value);
            }
            target = Math.Clamp(target, rule.Floor, 100);
            target = Math.Clamp(target, Math.Max(sensor.Control.MinSoftwareValue, 0), Math.Min(sensor.Control.MaxSoftwareValue, 100));
            if (Driven.TryGetValue(rule.Control, out var prev) && rule.Mode == "curve" && !emergency)
            {
                double step = target > prev.Last ? 15 : 4;                                      // up quickly, down slowly: no hunting
                target = target > prev.Last ? Math.Min(target, prev.Last + step) : Math.Max(target, prev.Last - step);
            }
            if (!Driven.TryGetValue(rule.Control, out var cur) || Math.Abs(cur.Last - target) >= 0.5)
            {
                sensor.Control.SetSoftware((float)target);
                Driven[rule.Control] = (sensor, target);
            }
        }
        // a control that has no rule any more goes back to the PC
        foreach (var id in Driven.Keys.ToList())
            if (!_config.Rules.Any(r => r.Control == id && r.Mode != "auto")) Release(id);
    }

    private static double Interpolate(List<double[]> points, double t)
    {
        if (t <= points[0][0]) return points[0][1];
        for (int i = 1; i < points.Count; i++)
            if (t <= points[i][0])
            {
                double x0 = points[i - 1][0], x1 = points[i][0], y0 = points[i - 1][1], y1 = points[i][1];
                return x1 <= x0 ? y1 : y0 + (y1 - y0) * (t - x0) / (x1 - x0);
            }
        return points[^1][1];
    }

    private static void Release(string id)
    {
        if (!Driven.TryGetValue(id, out var d)) return;
        try { d.Sensor.Control?.SetDefault(); } catch (Exception e) when (e is not OutOfMemoryException) { Log("could not hand back " + id + ": " + e.Message); }
        Driven.Remove(id);
    }

    /// <summary>Utylix is gone (or silent): the loop thread hands every fan back on its next turn, and any settings that were still on their way are dropped.</summary>
    private static void RequestRevert()
    {
        Interlocked.Exchange(ref _pendingConfig, null);
        _revertRequested = true;
        Wake.Set();
    }

    /// <summary>Hands every fan back now (the process is ending). It waits at most 3 s for the hardware: if the loop thread is stuck inside a slow sensor, ending must not hang with it.</summary>
    private static void RevertAll()
    {
        if (!Monitor.TryEnter(Gate, 3000)) { Log("could not hand the fans back: the sensors did not answer"); return; }
        try { RevertNow(); }
        finally { Monitor.Exit(Gate); }
    }

    private static void RevertNow()
    {
        foreach (var id in Driven.Keys.ToList()) Release(id);
        _config = new FanConfig();
    }

    // ------------------------------------------------------------------------------------------ lights of the RAM

    private static readonly object RgbGate = new();
    private static PawnSmbus? _smbus;
    private static List<EneModule> _sticks = new();

    private static FanReply Fail(string why) => new() { Ok = false, Error = why };

    private static bool Light(out PawnSmbus bus, out IDisposable? hold, out FanReply? failure)
    {
        _smbus ??= PawnSmbus.Open();
        bus = _smbus!; hold = null; failure = null;
        if (_smbus == null) { failure = Fail(PawnSmbus.Problem); return false; }
        hold = _smbus.Lock();
        if (hold == null) { failure = Fail("Another program is using the motherboard's SMBus right now (Armoury Crate or a monitoring tool). Try again in a moment."); return false; }
        return true;
    }

    private static LightStick Describe(PawnSmbus bus, EneModule m)
    {
        var s = m.Read(bus);
        return new LightStick
        {
            Name = m.Name, Address = m.Address, Leds = m.Leds,
            Look = s == null ? null : new LightLook { Mode = s.Value.Mode, R = s.Value.R, G = s.Value.G, B = s.Value.B, Speed = s.Value.Speed, Reverse = s.Value.Reverse },
        };
    }

    private static FanReply LightScan()
    {
        lock (RgbGate)
        {
            if (!Light(out var bus, out var hold, out var failure)) return failure!;
            using (hold)
            {
                _sticks = EneModule.Scan(bus);
                return new FanReply { Ok = true, Sticks = _sticks.Select(m => Describe(bus, m)).ToList(), Armoury = ArmouryState() };
            }
        }
    }

    private static FanReply LightSet(LightLook look)
    {
        lock (RgbGate)
        {
            if (_sticks.Count == 0) return Fail("No lit RAM was found yet.");
            if (!Light(out var bus, out var hold, out var failure)) return failure!;
            using (hold)
            {
                int bad = 0;
                foreach (var m in _sticks)
                    if (!m.Apply(bus, Math.Clamp(look.Mode, 0, EneModule.ModeNames.Length - 1), (byte)Math.Clamp(look.R, 0, 255), (byte)Math.Clamp(look.G, 0, 255), (byte)Math.Clamp(look.B, 0, 255), Math.Clamp(look.Speed, 0, 4), look.Reverse)) bad++;
                return bad == 0 ? new FanReply { Ok = true } : Fail(bad == _sticks.Count ? "The RAM did not answer." : "Some of the RAM did not answer.");
            }
        }
    }

    private static FanReply LightSave()
    {
        lock (RgbGate)
        {
            if (_sticks.Count == 0) return Fail("No lit RAM was found yet.");
            if (!Light(out var bus, out var hold, out var failure)) return failure!;
            using (hold)
            {
                int bad = 0;
                foreach (var m in _sticks) if (!m.Save(bus)) bad++;
                return bad == 0 ? new FanReply { Ok = true } : Fail("The RAM did not accept the saving.");
            }
        }
    }

    // ---- Armoury Crate's lighting service writes to the same chips, so it has to be out of the way for Utylix's choice to stay

    private const string LightingService = "LightingService";
    private static string RgbStateFile => Path.Combine(App.DataDir, "rgb-armoury.txt");

    private static string Sc(string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", arguments) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            return text;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return ""; }
    }

    private static string ArmouryState()
    {
        string q = Sc("query " + LightingService);
        if (!q.Contains("STATE", StringComparison.OrdinalIgnoreCase)) return "none";
        if (q.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) return "running";
        return Sc("qc " + LightingService).Contains("DISABLED", StringComparison.OrdinalIgnoreCase) ? "disabled" : "stopped";
    }

    private static FanReply Armoury(bool back)
    {
        string state = ArmouryState();
        if (state == "none") return new FanReply { Ok = true, Armoury = state };
        if (!back)
        {
            if (state == "running" || state == "stopped")
            {
                string qc = Sc("qc " + LightingService);
                string kind = qc.Contains("DELAYED", StringComparison.OrdinalIgnoreCase) ? "delayed-auto" : qc.Contains("DEMAND", StringComparison.OrdinalIgnoreCase) ? "demand" : "auto";
                try { File.WriteAllText(RgbStateFile, kind); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                Sc("config " + LightingService + " start= disabled");
                Sc("stop " + LightingService);
                for (int i = 0; i < 16 && ArmouryState() == "running"; i++) Thread.Sleep(250);
            }
        }
        else
        {
            string kind = "auto";
            try { if (File.Exists(RgbStateFile)) kind = File.ReadAllText(RgbStateFile).Trim(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (kind is not ("auto" or "demand" or "delayed-auto")) kind = "auto";
            Sc("config " + LightingService + " start= " + kind);
            Sc("start " + LightingService);
            try { File.Delete(RgbStateFile); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return new FanReply { Ok = true, Armoury = ArmouryState() };
    }

    private static void Log(string text)
    {
        try { File.AppendAllText(Path.Combine(App.DataDir, "fan-helper.log"), $"{DateTime.Now:s} {text}{Environment.NewLine}"); } catch (Exception) { }
    }
}
