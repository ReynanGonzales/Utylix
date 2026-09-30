using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
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
    private static readonly object Gate = new();          // the hardware library is not thread-safe
    private static Computer? _computer;
    private static readonly Dictionary<string, (ISensor Sensor, double Last)> Driven = new();   // controls we have taken over
    private static FanConfig _config = new();
    private static bool _emergency;
    private static long _lastContact = Environment.TickCount64;
    private static volatile bool _quit;

    public static int Run(string ownerSid)
    {
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
        try
        {
            while (!_quit)
            {
                using var server = Create(ownerSid);
                var wait = server.WaitForConnectionAsync();
                while (!wait.Wait(1000))
                {
                    if (_quit) return 0;
                    if (Driven.Count > 0 && Environment.TickCount64 - _lastContact > 10000) { RevertAll(); }
                    if (Environment.TickCount64 - _lastContact > 60000) return 0;           // nobody came: no reason to keep running
                }
                _lastContact = Environment.TickCount64;
                Serve(server);
                RevertAll();                                                // Utylix went away: hand every fan back
                if (Environment.TickCount64 - _lastContact > 60000) break;
            }
        }
        finally
        {
            _quit = true;
            RevertAll();
            try { _computer.Close(); } catch (Exception) { /* ending anyway */ }
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
        using var reader = new StreamReader(pipe);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        while (pipe.IsConnected && !_quit)
        {
            string? line;
            try { line = reader.ReadLine(); } catch (IOException) { return; }
            if (line == null) return;
            _lastContact = Environment.TickCount64;
            FanReply reply;
            try
            {
                var request = JsonSerializer.Deserialize<FanRequest>(line, FanJson.Options) ?? new FanRequest();
                reply = Handle(request);
            }
            catch (JsonException) { reply = new FanReply { Ok = false, Error = "bad request" }; }
            try { writer.WriteLine(JsonSerializer.Serialize(reply, FanJson.Options)); } catch (IOException) { return; }
            if (_quit) return;
        }
    }

    private static FanReply Handle(FanRequest request)
    {
        switch (request.Cmd)
        {
            case "snapshot":
                lock (Gate) return new FanReply { Ok = true, Sensors = Read(), Emergency = _emergency };
            case "config":
                lock (Gate)
                {
                    _config = Sanitize(request.Config ?? new FanConfig());
                    Apply();
                    return new FanReply { Ok = true, Emergency = _emergency };
                }
            case "quit":
                _quit = true;
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

    private static List<FanSensor> Read()
    {
        var list = new List<FanSensor>();
        foreach (var h in AllHardware(_computer!.Hardware))
        {
            h.Update();
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

    private static void Loop()
    {
        while (!_quit)
        {
            Thread.Sleep(1000);
            lock (Gate) { if (_config.Rules.Count > 0) Apply(); }
        }
    }

    private static ISensor? Find(string id) =>
        AllHardware(_computer!.Hardware).SelectMany(h => h.Sensors).FirstOrDefault(s => s.Identifier.ToString() == id);

    private static double? Temp(string id)
    {
        var s = Find(id);
        s?.Hardware.Update();
        return s?.Value is float v && !float.IsNaN(v) ? v : null;
    }

    private static bool Over(string kind, double limit)
    {
        foreach (var h in AllHardware(_computer!.Hardware))
        {
            if (KindOf(h) != kind) continue;
            h.Update();
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

    private static void RevertAll()
    {
        lock (Gate)
        {
            foreach (var id in Driven.Keys.ToList()) Release(id);
            _config = new FanConfig();
        }
    }

    private static void Log(string text)
    {
        try { File.AppendAllText(Path.Combine(App.DataDir, "fan-helper.log"), $"{DateTime.Now:s} {text}{Environment.NewLine}"); } catch (Exception) { }
    }
}
