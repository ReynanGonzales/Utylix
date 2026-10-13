using System;
using System.Collections.Generic;

namespace IdmClone.Vid;

/// <summary>
/// The transitions that are drawn by a "mask": the incoming picture is shown where an expression of the position (X, Y, W, H) and the progress
/// (0 .. 1) is true, or between 0 and 1 for a soft edge. They all end fully visible at progress 1. Plus the grouping used by the Effects panel
/// and the directions of the "whip" kinds (a push with motion blur).
/// </summary>
public static class VidTransitionDefs
{
    public static readonly string[] GroupOrder = { "Dissolve", "Wipe", "Slide and push", "Zoom and spin", "Shapes", "Stylized" };

    public static string GroupOf(TransitionKind k) => k switch
    {
        TransitionKind.Dissolve or TransitionKind.DipToBlack or TransitionKind.DipToWhite or TransitionKind.BlurDissolve or TransitionKind.NoiseDissolve => "Dissolve",
        TransitionKind.WipeRight or TransitionKind.WipeLeft or TransitionKind.WipeDown or TransitionKind.WipeUp
            or TransitionKind.WipeDiagTL or TransitionKind.WipeDiagTR or TransitionKind.WipeDiagBL or TransitionKind.WipeDiagBR
            or TransitionKind.SoftWipeRight or TransitionKind.SoftWipeLeft or TransitionKind.SoftWipeDown or TransitionKind.SoftWipeUp
            or TransitionKind.BlindsH or TransitionKind.BlindsV or TransitionKind.ClockWipe or TransitionKind.ClockWipeReverse => "Wipe",
        TransitionKind.PushLeft or TransitionKind.PushRight or TransitionKind.PushUp or TransitionKind.PushDown
            or TransitionKind.SlideLeft or TransitionKind.SlideRight or TransitionKind.SlideUp or TransitionKind.SlideDown
            or TransitionKind.WhipLeft or TransitionKind.WhipRight or TransitionKind.WhipUp or TransitionKind.WhipDown => "Slide and push",
        TransitionKind.ZoomIn or TransitionKind.ZoomOut or TransitionKind.ZoomThrough or TransitionKind.Spin or TransitionKind.FlipH or TransitionKind.FlipV
            or TransitionKind.Shake => "Zoom and spin",
        TransitionKind.Iris or TransitionKind.IrisClose or TransitionKind.BarnDoors or TransitionKind.BarnDoorsV or TransitionKind.DoorsCloseAcross
            or TransitionKind.DoorsCloseUpDown or TransitionKind.Diamond or TransitionKind.Cross => "Shapes",
        _ => "Stylized",
    };

    public static bool IsWhip(TransitionKind k) => k is TransitionKind.WhipLeft or TransitionKind.WhipRight or TransitionKind.WhipUp or TransitionKind.WhipDown;

    /// <summary>The plain push a whip moves like (the same direction, plus blur); every other kind stays itself.</summary>
    public static TransitionKind Direction(TransitionKind k) => k switch
    {
        TransitionKind.WhipLeft => TransitionKind.PushLeft,
        TransitionKind.WhipRight => TransitionKind.PushRight,
        TransitionKind.WhipUp => TransitionKind.PushUp,
        TransitionKind.WhipDown => TransitionKind.PushDown,
        _ => k,
    };

    public static bool HasMask(TransitionKind k) => Masks.ContainsKey(k);

    /// <summary>An expression for the share of the incoming picture visible at this pixel, given the progress expression <paramref name="p"/>.</summary>
    public static string Mask(TransitionKind k, string p) => Masks[k](p);

    // a stable pseudo-random number 0 .. 1 from two expressions
    private static string Hash(string a, string b) => $"mod(abs(sin(({a})*12.9898+({b})*78.233)*43758.5453),1)";
    private const string Theta = "(mod(atan2(X-W/2,-(Y-H/2))+2*PI,2*PI)/(2*PI))";      // 0 at the top, clockwise, 1 at the end of the turn

    private static readonly Dictionary<TransitionKind, Func<string, string>> Masks = new()
    {
        [TransitionKind.WipeDiagTL] = p => $"lte(X/W+Y/H,2*{p})",
        [TransitionKind.WipeDiagTR] = p => $"lte(1-X/W+Y/H,2*{p})",
        [TransitionKind.WipeDiagBL] = p => $"lte(X/W+1-Y/H,2*{p})",
        [TransitionKind.WipeDiagBR] = p => $"lte(2-X/W-Y/H,2*{p})",
        [TransitionKind.SoftWipeRight] = p => $"clip(({p}*1.25-X/W)/0.25,0,1)",
        [TransitionKind.SoftWipeLeft] = p => $"clip(({p}*1.25-(1-X/W))/0.25,0,1)",
        [TransitionKind.SoftWipeDown] = p => $"clip(({p}*1.25-Y/H)/0.25,0,1)",
        [TransitionKind.SoftWipeUp] = p => $"clip(({p}*1.25-(1-Y/H))/0.25,0,1)",
        [TransitionKind.BlindsH] = p => $"lte(mod(Y,H/10),H/10*{p})",
        [TransitionKind.BlindsV] = p => $"lte(mod(X,W/14),W/14*{p})",
        [TransitionKind.ClockWipe] = p => $"lte({Theta},{p})",
        [TransitionKind.ClockWipeReverse] = p => $"gte({Theta},1-{p})",
        [TransitionKind.IrisClose] = p => $"gte(hypot(X-W/2,Y-H/2),hypot(W/2,H/2)*(1-{p}))",
        [TransitionKind.DoorsCloseAcross] = p => $"gte(abs(X-W/2),W/2*(1-{p}))",
        [TransitionKind.DoorsCloseUpDown] = p => $"gte(abs(Y-H/2),H/2*(1-{p}))",
        [TransitionKind.Diamond] = p => $"lte(abs(X-W/2)/(W/2)+abs(Y-H/2)/(H/2),2*{p})",
        [TransitionKind.Cross] = p => $"lte(min(abs(X-W/2)/(W/2),abs(Y-H/2)/(H/2)),{p})",
        [TransitionKind.NoiseDissolve] = p => $"lte({Hash("X", "Y")},{p})",
        [TransitionKind.Blocks] = p => $"lte({Hash("floor(X/(W/20))", "floor(Y/(W/20))")},{p})",
        [TransitionKind.BlocksLarge] = p => $"lte({Hash("floor(X/(W/8))", "floor(Y/(W/8))")},{p})",
        // bands of two different heights that come in one after the other in a random order (never going away again: a flicker would look like a mistake)
        [TransitionKind.Glitch] = p => $"lte(min({Hash("floor(Y/(H/16))", "7")},{Hash("floor(Y/(H/41))", "3")}),{p})",
    };
}
