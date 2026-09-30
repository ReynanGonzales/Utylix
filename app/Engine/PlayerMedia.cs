using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IdmClone.Engine;

/// <summary>Which files the Video Player opens.</summary>
public static class PlayerMedia
{
    public static readonly string[] VideoExtensions =
    {
        "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg", "3gp", "3g2", "mts", "m2ts", "ts", "ogv", "vob",
        "divx", "asf", "rm", "rmvb", "f4v", "mxf", "wtv", "dvr-ms",
    };

    public static readonly string[] AudioExtensions =
    {
        "mp3", "flac", "wav", "aac", "ogg", "oga", "m4a", "opus", "wma", "aiff", "aif", "amr", "mka", "ape", "wv", "mpc", "ac3", "dts", "mid",
    };

    public static readonly string[] SubtitleExtensions = { "srt", "ass", "ssa", "sub", "vtt", "smi", "idx" };

    /// <summary>
    /// Subtitle files that belong to a video, best first, each with the priority libvlc should give it (the highest is switched on):
    /// "Movie.srt" next to "Movie.mkv"; then "Movie.en.srt" / "Movie.English.srt" (English first); also inside a "Subs" or "Subtitles" folder.
    /// </summary>
    public static List<(string File, uint Priority)> FindSubtitles(string video)
    {
        var found = new List<(string File, uint Priority)>();
        try
        {
            string dir = Path.GetDirectoryName(video) ?? "";
            string name = Path.GetFileNameWithoutExtension(video);
            var folders = new List<string> { dir };
            foreach (var sub in new[] { "Subs", "Subtitles", "subs", "subtitles", "Sub" })
                if (Directory.Exists(Path.Combine(dir, sub))) folders.Add(Path.Combine(dir, sub));

            var candidates = new List<(string File, int Rank)>();
            foreach (var folder in folders)
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    if (!SubtitleExtensions.Contains(Ext(file))) continue;
                    string subName = Path.GetFileNameWithoutExtension(file);
                    if (string.Equals(subName, name, StringComparison.OrdinalIgnoreCase)) candidates.Add((file, 0));                    // Movie.srt
                    else if (subName.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) || subName.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase) ||
                             subName.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase))
                    {
                        string tag = subName[(name.Length + 1)..].ToLowerInvariant();                                                    // en, eng, English, forced ...
                        candidates.Add((file, tag is "en" or "eng" or "english" or "en-us" or "en-gb" ? 1 : 2));
                    }
                }
            uint priority = 4;
            foreach (var c in candidates.OrderBy(c => c.Rank).ThenBy(c => c.File, StringComparer.OrdinalIgnoreCase).Take(6))
            {
                found.Add((c.File, priority));
                if (priority > 1) priority--;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* no subtitles then */ }
        return found;
    }

    public static string Ext(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
    public static bool IsVideo(string path) => VideoExtensions.Contains(Ext(path));
    public static bool IsAudio(string path) => AudioExtensions.Contains(Ext(path));
    public static bool IsPlayable(string path) => IsVideo(path) || IsAudio(path);
}
