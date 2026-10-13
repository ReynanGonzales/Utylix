using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace IdmClone;

/// <summary>
/// Bullets and numbers for text you add: two buttons next to Bold. Every line of the text (every Enter) is one item: it starts with the marker and, when the box has a width,
/// its wrapped lines hang under the words. Changes the text being typed or chosen and is what the next text starts with. While typing, the markers are drawn beside the box.
/// </summary>
public sealed partial class PdfWindow
{
    private int _listKind;                       // 0 none, 1 bullets, 2 numbers: what the next text starts with
    private ToggleButton _bulletButton = null!, _numberButton = null!;
    private readonly Canvas _typingMarkers = new() { IsHitTestVisible = false };

    private void AddListButtons()
    {
        ToggleButton Make(string glyph, string tip, string id, int kind)
        {
            var b = new ToggleButton { Content = new TextBlock { Text = glyph, FontWeight = FontWeights.Bold, Foreground = Brushes.White }, Template = ToggleTemplate(), Focusable = false, ToolTip = tip };
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            b.Click += (_, _) => SetList(kind, b.IsChecked == true);
            _fontRow.Children.Add(b);
            return b;
        }
        _bulletButton = Make("\u2022\u2261", "Bullet list: every line you type (every Enter) gets a bullet", "PdfBullets", 1);
        _numberButton = Make("1.", "Numbered list: every line you type (every Enter) gets the next number", "PdfNumbers", 2);
    }

    private void ShowList(int kind)
    {
        _bulletButton.IsChecked = kind == 1;
        _numberButton.IsChecked = kind == 2;
    }

    private void SetList(int kind, bool on)
    {
        if (_syncingFont) return;
        int now = on ? kind : 0;
        ChangeTextStyle(t => t.ListKind = now, () => _listKind = now);
        ShowList(now);
    }

    /// <summary>While a text is typed: the markers of its list beside the first line of each paragraph.</summary>
    private void AttachTypingMarkers(Canvas overlay)
    {
        if (_typing is not { ListKind: not 0 }) return;
        _typingMarkers.Children.Clear();
        overlay.Children.Add(_typingMarkers);
        Dispatcher.BeginInvoke(new Action(RefreshTypingMarkers), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void RefreshTypingMarkers()
    {
        _typingMarkers.Children.Clear();
        var item = _typing; var box = _textBox;
        if (item == null || box == null || item.ListKind == 0 || _typingMarkers.Parent == null) return;
        box.UpdateLayout();
        string text = box.Text.Replace("\r\n", "\n");
        int number = 0, start = 0;
        double left = item.TopLeft.X + item.Inset;
        var face = TextItem.Family(item.Font, item.FontName);
        foreach (string paragraph in text.Split('\n'))
        {
            if (paragraph.Trim().Length > 0)
            {
                string mark = item.ListKind == 1 ? "\u2022" : ++number + ".";
                Rect r;
                try { r = box.GetRectFromCharacterIndex(Math.Min(start, box.Text.Length)); } catch (ArgumentException) { r = Rect.Empty; }
                if (!r.IsEmpty && !double.IsInfinity(r.Y))
                {
                    var m = new TextBlock { Text = mark, FontFamily = face, FontSize = item.FontSize, FontWeight = item.Bold ? FontWeights.Bold : FontWeights.Normal, Foreground = new SolidColorBrush(item.Color), IsHitTestVisible = false };
                    Canvas.SetLeft(m, left); Canvas.SetTop(m, Canvas.GetTop(box) + r.Y);
                    _typingMarkers.Children.Add(m);
                }
            }
            start += paragraph.Length + 1;
        }
    }
}
