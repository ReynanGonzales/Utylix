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

public sealed class FanRequest
{
    [JsonPropertyName("cmd")] public string Cmd { get; set; } = "";
    [JsonPropertyName("config")] public FanConfig? Config { get; set; }
}

public sealed class FanReply
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("sensors")] public List<FanSensor>? Sensors { get; set; }
    [JsonPropertyName("emergency")] public bool Emergency { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}

internal static class FanJson
{
    public static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public static string PipeFor(string ownerSid) => "Utylix.Fans." + ApiPort.UserIdOf(ownerSid);
}
