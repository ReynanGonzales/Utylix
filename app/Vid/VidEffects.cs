using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace IdmClone.Vid;

public sealed record EffectParam(string Label, double Min, double Max, double Default, string Unit = "");

/// <summary>What the Effects panel shows for one effect: where it is listed, its name and its numbers.</summary>
public sealed record EffectInfo(string Type, string Name, string Group, bool Audio, params EffectParam[] Params);

/// <summary>The effects a clip can have, and how each becomes an ffmpeg filter.</summary>
public static class VidEffects
{
    public static readonly EffectInfo[] All =
    {
        // ---- video: colour ----
        new("color", "Brightness and contrast", "Video effects · Colour", false,
            new EffectParam("Brightness", -1, 1, 0), new EffectParam("Contrast", 0, 3, 1), new EffectParam("Saturation", 0, 3, 1), new EffectParam("Gamma", 0.2, 3, 1)),
        new("balance", "Warm / cool", "Video effects · Colour", false, new EffectParam("Cooler  ←  →  Warmer", -1, 1, 0.3)),
        new("bw", "Black and white", "Video effects · Colour", false),
        new("sepia", "Sepia", "Video effects · Colour", false),
        new("invert", "Invert", "Video effects · Colour", false),
        // ---- video: blur and sharpen ----
        new("blur", "Gaussian blur", "Video effects · Blur and sharpen", false, new EffectParam("Amount", 0, 40, 6)),
        new("sharpen", "Sharpen", "Video effects · Blur and sharpen", false, new EffectParam("Amount", 0, 5, 1.5)),
        new("noise", "Film grain", "Video effects · Blur and sharpen", false, new EffectParam("Amount", 0, 60, 18)),
        // ---- video: distort / stylize ----
        new("crop", "Crop", "Video effects · Transform", false,
            new EffectParam("Left", 0, 49, 0, "%"), new EffectParam("Top", 0, 49, 0, "%"), new EffectParam("Right", 0, 49, 0, "%"), new EffectParam("Bottom", 0, 49, 0, "%")),
        new("mirror", "Mirror", "Video effects · Transform", false, new EffectParam("0 across, 1 up-down, 2 both", 0, 2, 0)),
        new("vignette", "Vignette", "Video effects · Stylize", false, new EffectParam("Size", 0.1, 1.5, 0.6)),
        new("pixelate", "Mosaic", "Video effects · Stylize", false, new EffectParam("Block size", 2, 80, 16)),
        new("edges", "Find edges", "Video effects · Stylize", false),
        new("greenscreen", "Green screen key", "Video effects · Keying", false, new EffectParam("Similarity", 0.01, 0.5, 0.15), new EffectParam("Edge softness", 0, 0.5, 0.05)),
        // ---- audio ----
        new("gain", "Gain", "Audio effects", true, new EffectParam("Gain", -30, 30, 0, "dB")),
        new("highpass", "High-pass (cut the rumble)", "Audio effects", true, new EffectParam("Cut below", 20, 2000, 120, "Hz")),
        new("lowpass", "Low-pass (cut the hiss)", "Audio effects", true, new EffectParam("Cut above", 500, 20000, 8000, "Hz")),
        new("denoise", "Noise reduction", "Audio effects", true, new EffectParam("Strength", 5, 40, 14, "dB")),
        new("normalize", "Even out the volume", "Audio effects", true),
        new("echo", "Echo", "Audio effects", true, new EffectParam("Delay", 50, 1000, 300, "ms"), new EffectParam("Decay", 0, 0.9, 0.5)),
        new("bass", "Bass", "Audio effects", true, new EffectParam("Boost", -12, 12, 6, "dB")),
        new("treble", "Treble", "Audio effects", true, new EffectParam("Boost", -12, 12, 6, "dB")),
    };

    public static readonly (TransitionKind Kind, string Name)[] Transitions =
    {
        (TransitionKind.Dissolve, "Cross dissolve"), (TransitionKind.DipToBlack, "Dip to black"),
        (TransitionKind.WipeRight, "Wipe right"), (TransitionKind.WipeLeft, "Wipe left"), (TransitionKind.WipeDown, "Wipe down"), (TransitionKind.WipeUp, "Wipe up"),
    };

    public static EffectInfo? Info(string type) => All.FirstOrDefault(e => e.Type == type);

    public static ClipEffect Create(string type)
    {
        var info = Info(type) ?? throw new ArgumentException("unknown effect " + type);
        var e = new ClipEffect { Type = type };
        if (info.Params.Length > 0) e.A = info.Params[0].Default;
        if (info.Params.Length > 1) e.B = info.Params[1].Default;
        if (info.Params.Length > 2) e.C = info.Params[2].Default;
        if (info.Params.Length > 3) e.D = info.Params[3].Default;
        return e;
    }

    public static double Get(ClipEffect e, int index) => index switch { 0 => e.A, 1 => e.B, 2 => e.C, _ => e.D };
    public static void Set(ClipEffect e, int index, double v)
    {
        switch (index) { case 0: e.A = v; break; case 1: e.B = v; break; case 2: e.C = v; break; default: e.D = v; break; }
    }

    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>The ffmpeg filter(s) for a picture effect, or null when it does nothing (or is an audio effect).</summary>
    public static string? VideoFilter(ClipEffect e)
    {
        if (!e.On) return null;
        switch (e.Type)
        {
            case "color": return $"eq=brightness={F(e.A)}:contrast={F(e.B)}:saturation={F(e.C)}:gamma={F(e.D <= 0 ? 1 : e.D)}";
            case "balance": return $"colorbalance=rs={F(e.A)}:rm={F(e.A)}:rh={F(e.A)}:bs={F(-e.A)}:bm={F(-e.A)}:bh={F(-e.A)}";
            case "bw": return "hue=s=0";
            case "sepia": return "colorchannelmixer=.393:.769:.189:0:.349:.686:.168:0:.272:.534:.131:0";
            case "invert": return "negate";
            case "blur": return e.A <= 0.05 ? null : $"gblur=sigma={F(e.A)}";
            case "sharpen": return e.A <= 0.01 ? null : $"unsharp=luma_msize_x=5:luma_msize_y=5:luma_amount={F(e.A)}";
            case "noise": return e.A <= 0.5 ? null : $"noise=alls={F(e.A)}:allf=t";
            case "mirror": return e.A >= 1.5 ? "hflip,vflip" : e.A >= 0.5 ? "vflip" : "hflip";
            case "vignette": return $"vignette=angle={F(e.A)}";
            case "pixelate":
            {
                double s = Math.Max(2, e.A);
                return $"scale=w='max(2,trunc(iw/{F(s)}))':h='max(2,trunc(ih/{F(s)}))':flags=neighbor,scale=w='trunc(iw*{F(s)}/2)*2':h='trunc(ih*{F(s)}/2)*2':flags=neighbor";
            }
            case "edges": return "edgedetect=mode=colormix:high=0";
            case "crop":
            {
                double l = Math.Clamp(e.A, 0, 49), t = Math.Clamp(e.B, 0, 49), r = Math.Clamp(e.C, 0, 49), b = Math.Clamp(e.D, 0, 49);
                if (l + t + r + b < 0.01) return null;
                // cut the edges away, then give the picture its old size back (transparent) so everything stays where it was
                return $"format=yuva420p,crop=w='trunc(iw*(100-{F(l)}-{F(r)})/200)*2':h='trunc(ih*(100-{F(t)}-{F(b)})/200)*2':x='iw*{F(l)}/100':y='ih*{F(t)}/100'," +
                       $"pad=w='trunc(iw*100/(100-{F(l)}-{F(r)})/2)*2':h='trunc(ih*100/(100-{F(t)}-{F(b)})/2)*2':x='trunc(iw*{F(l)}/(100-{F(l)}-{F(r)})/2)*2':y='trunc(ih*{F(t)}/(100-{F(t)}-{F(b)})/2)*2':color=black@0";
            }
            case "greenscreen": return $"format=yuva420p,chromakey=color=0x00FF00:similarity={F(e.A)}:blend={F(e.B)}";
        }
        return null;
    }

    public static string? AudioFilter(ClipEffect e)
    {
        if (!e.On) return null;
        switch (e.Type)
        {
            case "gain": return Math.Abs(e.A) < 0.01 ? null : $"volume={F(e.A)}dB";
            case "highpass": return $"highpass=f={F(e.A)}";
            case "lowpass": return $"lowpass=f={F(e.A)}";
            case "denoise": return $"afftdn=nr={F(e.A)}";
            case "normalize": return "dynaudnorm";
            case "echo": return $"aecho=0.8:0.9:{F(e.A)}:{F(e.B)}";
            case "bass": return $"bass=g={F(e.A)}";
            case "treble": return $"treble=g={F(e.A)}";
        }
        return null;
    }
}
