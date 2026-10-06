using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Fillable form fields made in the editor: a text box (drag its size) and a check box (click). They are real PDF fields: when the file is
/// saved they are written as form fields (<see cref="PdfFormFields"/>), and then fill in and print in any PDF reader (and here, in the form bar).
/// </summary>
public sealed partial class PdfWindow
{
    private sealed class FieldItem : EditItem
    {
        public PdfNewFieldKind Kind;
        public Rect Box;
        public string Name = "";
        public double FontSize = 12;

        public override Rect Bounds => Box;
        public override bool KeepAspect => Kind == PdfNewFieldKind.CheckBox;
        public override EditItem Clone() => (FieldItem)MemberwiseClone();
        public override void MoveBy(Vector d) => Box.Offset(d);
        public override void ResizeTo(Rect r) => Box = r;

        public override FrameworkElement Build()
        {
            var fill = new SolidColorBrush(Color.FromArgb(70, 0x5B, 0x8D, 0xEF));
            var edge = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0xEA));
            var host = new Border { Width = Box.Width, Height = Box.Height, Background = fill, BorderBrush = edge, BorderThickness = new Thickness(1) };
            if (Kind == PdfNewFieldKind.Text)
                host.Child = new TextBlock { Text = Name, FontSize = Math.Min(10, Math.Max(5, Box.Height * 0.6)), Foreground = new SolidColorBrush(Color.FromArgb(150, 0x1B, 0x3A, 0x8A)), Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
            Canvas.SetLeft(host, Box.X); Canvas.SetTop(host, Box.Y);
            return host;
        }

        public override IEnumerable<PdfMark> Marks() { yield return new PdfFieldMark(Page, Box, Kind, Name, FontSize); }
    }

    /// <summary>A name nobody else on the page uses yet: "Text 1", "Text 2" ... / "Check box 1" ...</summary>
    private string NewFieldName(PdfNewFieldKind kind)
    {
        string stem = kind == PdfNewFieldKind.Text ? "Text" : "Check box";
        var taken = new HashSet<string>(_items.OfType<FieldItem>().Select(f => f.Name));
        for (int n = 1; ; n++) if (!taken.Contains(stem + " " + n)) return stem + " " + n;
    }

    private void StartField(PageView pv, Point p)
    {
        var kind = _tool == EditTool.TextField ? PdfNewFieldKind.Text : PdfNewFieldKind.CheckBox;
        _drawing = new FieldItem { Page = pv.Index, Kind = kind, Box = new Rect(p, p), Color = Colors.Blue, Name = NewFieldName(kind), FontSize = _textSize };
        _drag = DragMode.Draw;
    }

    /// <summary>The mouse is let go: the field is as big as dragged (a plain click gives the usual size).</summary>
    private void FinishField(FieldItem field, Point start)
    {
        var r = field.Box;
        bool check = field.Kind == PdfNewFieldKind.CheckBox;
        if (check)
        {
            double s = Math.Max(r.Width, r.Height) >= 8 ? Math.Max(r.Width, r.Height) : 16;
            r = Math.Max(r.Width, r.Height) >= 8 ? new Rect(r.TopLeft, new Size(s, s)) : new Rect(start.X - s / 2, start.Y - s / 2, s, s);
        }
        else if (r.Width < 8 || r.Height < 8) r = new Rect(start.X - 2, start.Y - 11, 150, 22);
        field.Box = r;
        field.FontSize = Math.Min(_textSize, r.Height * 0.7);
        Add(field, select: true);
    }
}
