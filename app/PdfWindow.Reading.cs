using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Reading comfort: a dark reading mode (the pages are shown inverted and softened; only for looking: it is off while you edit, and nothing is saved in the file)
/// and read aloud (Windows' own voices, from the page in view or from the chosen words; works page after page).
/// </summary>
public sealed partial class PdfWindow
{
    // ---------- dark reading mode ----------
    private static string DarkMarker => System.IO.Path.Combine(App.DataDir, "pdf-dark-reading");
    private bool _darkRead = File.Exists(DarkMarker);
    private bool _darkApplied = File.Exists(DarkMarker);           // what the pictures on screen show now
    private TextBlock? _darkGlyph, _readGlyph;

    /// <summary>The pages are shown dark: switched on, and not while editing (added things would look wrong on an inverted page).</summary>
    private bool ReadingDark => _darkRead && !_editing;

    private void ToggleDark()
    {
        _darkRead = !_darkRead;
        try { if (_darkRead) File.WriteAllText(DarkMarker, ""); else File.Delete(DarkMarker); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* not kept this time */ }
        SyncDark();
        Toast(_darkRead ? (_editing ? "Dark reading is on: the pages turn dark when you leave Edit" : "Dark reading mode is on (only for looking: the file is not changed)") : "Dark reading mode is off");
    }

    /// <summary>The pages are drawn again when what they should show changed (the mode was switched, or editing began / ended).</summary>
    private void SyncDark()
    {
        if (_darkGlyph != null) _darkGlyph.SetResourceReference(TextBlock.ForegroundProperty, _darkRead ? "AccentBrush" : "TextBrush");
        if (_darkGlyph != null && !_darkRead) _darkGlyph.Foreground = Brushes.White;
        if (ReadingDark == _darkApplied) return;
        _darkApplied = ReadingDark;
        RedrawPages();
    }

    /// <summary>White paper becomes dark grey, black text becomes light grey; colours are inverted (pictures too: it is for reading).</summary>
    private static BitmapSource DarkPicture(BitmapSource source)
    {
        int w = source.PixelWidth, h = source.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        source.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(28 + (255 - pixels[i]) * 0.85);              // blue
            pixels[i + 1] = (byte)(30 + (255 - pixels[i + 1]) * 0.85);      // green
            pixels[i + 2] = (byte)(34 + (255 - pixels[i + 2]) * 0.85);      // red
        }
        var dark = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        dark.Freeze();
        return dark;
    }

    // ---------- read aloud ----------
    private SpeechSynthesizer? _voice;
    private int _voiceRate;                                         // -10 .. 10
    private int _readPage = -1;
    private bool _paused;

    private bool IsReading => _voice != null && _readPage >= 0;

    private void ReadMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu();
        void Item(string text, Action action, bool enabled = true, bool check = false, string? id = null)
        {
            var m = new MenuItem { Header = text, IsEnabled = enabled, IsCheckable = check, IsChecked = check };
            if (id != null) System.Windows.Automation.AutomationProperties.SetAutomationId(m, id);
            m.Click += (_, _) => action();
            menu.Items.Add(m);
        }
        if (IsReading)
        {
            Item(_paused ? "Go on reading" : "Pause", PauseResume, id: "PdfReadPause");
            Item("Stop reading", StopReading, id: "PdfReadStop");
        }
        else
        {
            Item("Read from this page", () => StartReading(null), _pdf != null, id: "PdfReadPage");
            Item("Read the chosen words", () => StartReading(HasSelection ? SelectedWords() : null), HasSelection, id: "PdfReadSelection");
        }
        menu.Items.Add(new Separator());
        foreach (var (label, rate) in new[] { ("Slow", -3), ("Normal", 0), ("Fast", 3), ("Very fast", 6) })
            Item("Speed: " + label, () => { _voiceRate = rate; if (_voice != null) _voice.Rate = rate; }, true, _voiceRate == rate, "PdfReadSpeed" + label.Replace(" ", ""));
        Themed(menu);
        menu.PlacementTarget = anchor; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }

    private string? SelectedWords()
    {
        if (!HasSelection || Text(_selPage) is not { } t) return null;
        var (s, e) = SelectionRange;
        return t.Slice(s, e);
    }

    private void StartReading(string? words)
    {
        if (_pdf == null) return;
        StopReading();
        try
        {
            _voice = new SpeechSynthesizer { Rate = _voiceRate };
            _voice.SetOutputToDefaultAudioDevice();
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or PlatformNotSupportedException or TypeInitializationException)
        {
            Toast("Windows has no voice to read with here (" + e.Message + ")");
            _voice?.Dispose(); _voice = null;
            return;
        }
        _voice.SpeakCompleted += (_, e) => Dispatcher.BeginInvoke(() => { if (!e.Cancelled) ReadNextPage(); });
        _paused = false;
        if (words != null)
        {
            if (words.Trim().Length == 0) { StopReading(); return; }
            _readPage = int.MaxValue;                              // (one go: nothing after it)
            _voice.SpeakAsync(words);
        }
        else
        {
            _readPage = _current - 1;
            ReadNextPage();
        }
        UpdateReadGlyph();
    }

    private void ReadNextPage()
    {
        if (_voice == null || _pdf == null) return;
        if (_readPage == int.MaxValue) { StopReading(); return; }
        for (int page = _readPage + 1; page < _pdf.PageCount; page++)
        {
            string text;
            try { text = Text(page)?.Text ?? ""; }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { text = ""; }
            if (text.Trim().Length == 0) { if (page == _current) Toast("This page has no text to read (a scan?): use Tools > OCR first"); continue; }
            _readPage = page;
            if (page != _current) GoTo(page);
            _voice.SpeakAsync(text);
            UpdateReadGlyph();
            return;
        }
        StopReading();
        Toast("Finished reading");
    }

    private void PauseResume()
    {
        if (_voice == null) return;
        if (_paused) _voice.Resume(); else _voice.Pause();
        _paused = !_paused;
        UpdateReadGlyph();
    }

    private void StopReading()
    {
        var voice = _voice;
        _voice = null; _readPage = -1; _paused = false;
        if (voice != null) { try { voice.SpeakAsyncCancelAll(); voice.Dispose(); } catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { } }
        UpdateReadGlyph();
    }

    private void UpdateReadGlyph()
    {
        if (_readGlyph == null) return;
        if (IsReading) _readGlyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); else { _readGlyph.ClearValue(TextBlock.ForegroundProperty); _readGlyph.Foreground = Brushes.White; }
    }
}
