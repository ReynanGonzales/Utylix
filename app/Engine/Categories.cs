using System;
using System.Collections.Generic;
using System.IO;

namespace IdmClone.Engine;

/// <summary>Which type folder (Compressed, Picture, ...) a file belongs in, decided by its extension.</summary>
public static class Categories
{
    public const string Other = "Other";

    /// <summary>The type folders, in the order they are shown.</summary>
    public static readonly string[] All = { "Compressed", "Picture", "Video", "Music", "Document", "Application" };

    private static readonly Dictionary<string, string> Map = Build();

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string category, string extensions)
        {
            foreach (var e in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries)) map[e] = category;
        }
        Add("Compressed", "zip rar 7z tar gz tgz bz2 xz zst cab");
        Add("Picture", "jpg jpeg png gif webp bmp svg tif tiff avif heic ico");
        Add("Video", "mp4 mkv avi mov wmv flv webm m4v mpg mpeg 3gp");
        Add("Music", "mp3 flac wav aac ogg m4a opus wma");
        Add("Document", "pdf doc docx xls xlsx ppt pptx txt rtf odt ods odp epub csv md");
        Add("Application", "exe msi msu dmg pkg deb rpm apk bin iso img appx jar");
        return map;
    }

    public static string Of(string? fileName)
    {
        string ext = Path.GetExtension(fileName ?? "").TrimStart('.');
        return ext.Length > 0 && Map.TryGetValue(ext, out var category) ? category : Other;
    }

    public static string Glyph(string category) => category switch
    {
        "Compressed" => "🗜",
        "Picture" => "🖼",
        "Video" => "🎬",
        "Music" => "🎵",
        "Document" => "📄",
        "Application" => "⚙",
        _ => "📦",
    };
}
