using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IdmClone;

/// <summary>One reading or control of the PC's hardware, as the fan helper reports it.</summary>
public sealed class FanSensor
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("hw")] public string Hardware { get; set; } = "";        // "Nuvoton NCT6798D", "AMD Ryzen 7 5700X" ...
    [JsonPropertyName("hwid")] public string HardwareId { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";          // "cpu" | "gpu" | "board" | "other"
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";          // "temp" | "fan" | "control"
    [JsonPropertyName("value")] public double? Value { get; set; }             // °C, RPM, or % for a control
    [JsonPropertyName("auto")] public bool Auto { get; set; } = true;          // control: still under the PC's own (BIOS / driver) control
    [JsonPropertyName("min")] public double Min { get; set; }                  // control: lowest allowed %
    [JsonPropertyName("max")] public double Max { get; set; } = 100;
}

/// <summary>What a fan should do.</summary>
public sealed class FanRule
{
    [JsonPropertyName("control")] public string Control { get; set; } = "";    // sensor id of the control
    [JsonPropertyName("mode")] public string Mode { get; set; } = "auto";      // "auto" | "manual" | "curve"
    [JsonPropertyName("percent")] public double Percent { get; set; } = 50;    // manual
    [JsonPropertyName("source")] public string Source { get; set; } = "";      // curve: sensor id of the temperature
    [JsonPropertyName("points")] public List<double[]> Points { get; set; } = new();   // curve: [°C, %] pairs, rising
    [JsonPropertyName("floor")] public double Floor { get; set; } = 25;        // never below this %
}

public sealed class FanConfig
{
    [JsonPropertyName("rules")] public List<FanRule> Rules { get; set; } = new();
    [JsonPropertyName("emergency_cpu")] public double EmergencyCpu { get; set; } = 85;     // °C: every controlled fan goes to 100 %
    [JsonPropertyName("emergency_gpu")] public double EmergencyGpu { get; set; } = 85;
}

/// <summary>How the lights of the RAM should look (the helper keeps every value in range).</summary>
public sealed class LightLook
{
    [JsonPropertyName("mode")] public int Mode { get; set; } = 1;              // see EneModule.ModeNames: 0 off, 1 static ...
    [JsonPropertyName("r")] public int R { get; set; } = 255;
    [JsonPropertyName("g")] public int G { get; set; }
    [JsonPropertyName("b")] public int B { get; set; }
    [JsonPropertyName("speed")] public int Speed { get; set; } = 2;            // 0 fastest .. 4 slowest
    [JsonPropertyName("reverse")] public bool Reverse { get; set; }
}

/// <summary>One RAM stick with lights, as the helper found it.</summary>
public sealed class LightStick
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("address")] public int Address { get; set; }
    [JsonPropertyName("leds")] public int Leds { get; set; }
    [JsonPropertyName("look")] public LightLook? Look { get; set; }           // what it shows now
}

public sealed class FanRequest
{
    [JsonPropertyName("cmd")] public string Cmd { get; set; } = "";
    [JsonPropertyName("config")] public FanConfig? Config { get; set; }
    [JsonPropertyName("look")] public LightLook? Look { get; set; }           // "light-set"
    [JsonPropertyName("flag")] public bool Flag { get; set; }                 // "armoury": true = switch Armoury Crate's lighting service back on
}

public sealed class FanReply
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("sticks")] public List<LightStick>? Sticks { get; set; }
    [JsonPropertyName("armoury")] public string? Armoury { get; set; }         // state of Armoury Crate's lighting service: "running" | "stopped" | "disabled" | "none"
    [JsonPropertyName("sensors")] public List<FanSensor>? Sensors { get; set; }
    [JsonPropertyName("emergency")] public bool Emergency { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}

internal static class FanJson
{
    public static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    /// <summary>A test copy of Utylix gives its own name to the pipe ("--fan-suffix x") so it never talks to the real helper.</summary>
    public static string Suffix { get; set; } = "";
    public static string PipeFor(string ownerSid) => "Utylix.Fans." + ApiPort.UserIdOf(ownerSid) + Suffix;
}
