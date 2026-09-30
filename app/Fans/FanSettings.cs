using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IdmClone;

/// <summary>What the person chose for their fans (kept in fans.json in the data folder), and the one client to the helper.</summary>
internal sealed class FanSettings
{
    [JsonPropertyName("names")] public Dictionary<string, string> Names { get; set; } = new();         // control id -> the person's name for that fan
    [JsonPropertyName("config")] public FanConfig Config { get; set; } = new();
    [JsonPropertyName("show_unused")] public bool ShowUnused { get; set; }
    [JsonPropertyName("floor")] public double Floor { get; set; } = 25;

    public static FanClient Client { get; } = new();
    private static string PathOnDisk => System.IO.Path.Combine(App.DataDir, "fans.json");

    public static FanSettings Load()
    {
        try { if (File.Exists(PathOnDisk)) return JsonSerializer.Deserialize<FanSettings>(File.ReadAllText(PathOnDisk)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public FanRule RuleFor(string controlId)
    {
        var rule = Config.Rules.Find(r => r.Control == controlId);
        if (rule == null) { rule = new FanRule { Control = controlId, Floor = Floor }; Config.Rules.Add(rule); }
        return rule;
    }

    /// <summary>Curves to start from: [°C, %] pairs.</summary>
    public static readonly (string Name, double[][] Points)[] Presets =
    {
        ("Quiet",       new[] { new[] { 40d, 25d }, new[] { 55d, 35d }, new[] { 70d, 55d }, new[] { 80d, 85d }, new[] { 88d, 100d } }),
        ("Balanced",    new[] { new[] { 35d, 30d }, new[] { 50d, 45d }, new[] { 65d, 65d }, new[] { 75d, 85d }, new[] { 85d, 100d } }),
        ("Performance", new[] { new[] { 30d, 40d }, new[] { 45d, 60d }, new[] { 60d, 80d }, new[] { 70d, 100d } }),
    };
}
