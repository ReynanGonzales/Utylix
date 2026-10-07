using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Fillable form fields made in the editor: a text box (drag its size), a check box, a round (radio) button and a signature box (click or drag). They are real PDF fields: when the file is
/// saved they are written as form fields (<see cref="PdfFormFields"/>), and then fill in and print in any PDF reader (and here, in the form bar).
/// Round buttons placed one after the other (without choosing another tool in between) are one group: only one of them can be chosen.
/// </summary>
public sealed partial class PdfWindow
{
    private sealed class FieldItem : EditItem
    {
        public PdfNewFieldKind Kind;
        public Rect Box;
        public string Name = "";                        // a round button: the name of its GROUP
        public string Value = "";                       // a round button: what this one stands for
        public double FontSize = 12;
        public PdfFontKind Font;
        public bool Bold;
        // what the "Field options" window sets
        public bool Required, Multiline, Ticked;
        public int MaxLength;
        public string DefaultText = "";
        public List<string> Choices = new();

        public FieldFormat? Format;                     // text box: shows a number / date / time in a fixed form
        public FieldCalc? Calc;                         // text box: worked out from other boxes

        public PdfFieldExtra Extra => new(Required, Multiline, MaxLength, DefaultText, Choices.Count > 0 ? Choices.ToList() : null, Ticked, Format, Calc);
        public override EditItem Clone() { var c = (FieldItem)MemberwiseClone(); c.Choices = Choices.ToList(); return c; }

        public bool Square => Kind is PdfNewFieldKind.CheckBox or PdfNewFieldKind.Radio;
        public override Rect Bounds => Box;
        public override bool KeepAspect => Square;
        public override void MoveBy(Vector d) => Box.Offset(d);
        public override void ResizeTo(Rect r) => Box = r;

        public override FrameworkElement Build()
        {
            // the same look the saved field gets: no background, a solid edge in the colour
            var edge = new SolidColorBrush(Color);
            FrameworkElement host;
            switch (Kind)
            {
                case PdfNewFieldKind.Radio:
                    host = new Ellipse { Width = Box.Width, Height = Box.Height, Stroke = edge, StrokeThickness = 1, Fill = Brushes.Transparent };
                    break;
                case PdfNewFieldKind.Signature:
                    host = new Border
                    {
                        Width = Box.Width, Height = Box.Height, BorderBrush = edge, BorderThickness = new Thickness(0), SnapsToDevicePixels = true,
                        Child = new Grid
                        {
                            Children =
                            {
                                new Rectangle { Stroke = edge, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }, Fill = Brushes.Transparent },
                                new Border { BorderBrush = edge, BorderThickness = new Thickness(0, 0, 0, 0.8), Margin = new Thickness(Box.Width * 0.06, 0, Box.Width * 0.06, Box.Height * 0.28), VerticalAlignment = VerticalAlignment.Bottom, Height = 0.8 },
                                new TextBlock { Text = "Sign here", FontSize = Math.Min(10, Math.Max(5, Box.Height * 0.25)), Foreground = new SolidColorBrush(Color.FromArgb(150, 0x60, 0x60, 0x60)), Margin = new Thickness(4, 2, 0, 0), IsHitTestVisible = false },
                            },
                        },
                    };
                    break;
                default:
                    var box = new Border { Width = Box.Width, Height = Box.Height, Background = Brushes.Transparent, BorderBrush = edge, BorderThickness = new Thickness(1), SnapsToDevicePixels = true };
                    if (Kind == PdfNewFieldKind.Text)
                        box.Child = new TextBlock { Text = DefaultText.Length > 0 ? DefaultText : Name, FontFamily = TextItem.Family(Font), FontWeight = Bold ? FontWeights.Bold : FontWeights.Normal, FontSize = Math.Min(FontSize, Math.Max(5, Box.Height * 0.7)), Foreground = new SolidColorBrush(Color.FromArgb(DefaultText.Length > 0 ? (byte)255 : (byte)150, 0x60, 0x60, 0x60)), Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
                    else if (Kind == PdfNewFieldKind.Dropdown)
                    {
                        // the chosen start text (or the name), and the little arrow of a drop-down list
                        var row = new DockPanel { LastChildFill = true };
                        var arrow = new TextBlock { Text = "▾", FontSize = Math.Max(8, Math.Min(14, Box.Height * 0.6)), Foreground = edge, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
                        DockPanel.SetDock(arrow, Dock.Right);
                        row.Children.Add(arrow);
                        row.Children.Add(new TextBlock { Text = DefaultText.Length > 0 ? DefaultText : Name, FontFamily = TextItem.Family(Font), FontWeight = Bold ? FontWeights.Bold : FontWeights.Normal, FontSize = Math.Min(FontSize, Math.Max(5, Box.Height * 0.7)), Foreground = new SolidColorBrush(Color.FromArgb(DefaultText.Length > 0 ? (byte)255 : (byte)150, 0x60, 0x60, 0x60)), Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false });
                        box.Child = row;
                    }
                    host = box;
                    break;
            }
            Canvas.SetLeft(host, Box.X); Canvas.SetTop(host, Box.Y);
            return host;
        }

        public override IEnumerable<PdfMark> Marks() { yield return new PdfFieldMark(Page, Box, Kind, Name, FontSize, Color, Font, Bold, Value, Extra); }
    }

    /// <summary>A name nobody else on the page uses yet: "Text 1", "Text 2" ... / "Check box 1" ... / "Signature 1" ...</summary>
    private string NewFieldName(PdfNewFieldKind kind)
    {
        string stem = kind switch { PdfNewFieldKind.Text => "Text", PdfNewFieldKind.Signature => "Signature", PdfNewFieldKind.Radio => "Choice", PdfNewFieldKind.Dropdown => "Dropdown", _ => "Check box" };
        var taken = new HashSet<string>(_items.OfType<FieldItem>().Select(f => f.Name));
        if (_pdf != null) for (int pg = 0; pg < Math.Min(_pdf.PageCount, 60); pg++) foreach (var f in Fields(pg)) taken.Add(f.Name);       // (the names the file has already)
        for (int n = 1; ; n++) if (!taken.Contains(stem + " " + n)) return stem + " " + n;
    }

    private string? _radioGroup;                          // the group the round buttons being placed belong to (a new one each time the tool is chosen)

    /// <summary>The value for a round button of a group: "Option 1", "Option 2" ... (the ones not used yet in that group).</summary>
    private string NewRadioValue(string group)
    {
        var taken = new HashSet<string>(_items.OfType<FieldItem>().Where(f => f.Kind == PdfNewFieldKind.Radio && f.Name == group).Select(f => f.Value));
        if (_pdf != null) for (int pg = 0; pg < Math.Min(_pdf.PageCount, 60); pg++) foreach (var o in OwnFields(pg).Where(o => o.Kind == PdfNewFieldKind.Radio && o.Name == group)) taken.Add(o.Value);
        for (int n = 1; ; n++) if (!taken.Contains("Option" + n)) return "Option" + n;
    }

    private void StartField(PageView pv, Point p)
    {
        var kind = _tool switch { EditTool.TextField => PdfNewFieldKind.Text, EditTool.RadioField => PdfNewFieldKind.Radio, EditTool.SignField => PdfNewFieldKind.Signature, EditTool.DropField => PdfNewFieldKind.Dropdown, _ => PdfNewFieldKind.CheckBox };
        string name;
        string value = "";
        if (kind == PdfNewFieldKind.Radio)
        {
            _radioGroup ??= NewFieldName(PdfNewFieldKind.Radio);
            name = _radioGroup;
            value = NewRadioValue(name);
        }
        else name = NewFieldName(kind);
        _drawing = new FieldItem { Page = pv.Index, Kind = kind, Box = new Rect(p, p), Color = _toolColors[_tool], Name = name, Value = value, FontSize = _textSize, Font = _font, Bold = _bold };
        _drag = DragMode.Draw;
    }

    /// <summary>The mouse is let go: the field is as big as dragged (a plain click gives the usual size).</summary>
    private void FinishField(FieldItem field, Point start)
    {
        var r = field.Box;
        if (field.Square)
        {
            double s = Math.Max(r.Width, r.Height) >= 8 ? Math.Max(r.Width, r.Height) : 16;
            r = Math.Max(r.Width, r.Height) >= 8 ? new Rect(r.TopLeft, new Size(s, s)) : new Rect(start.X - s / 2, start.Y - s / 2, s, s);
        }
        else if (field.Kind == PdfNewFieldKind.Signature && (r.Width < 8 || r.Height < 8)) r = new Rect(start.X - 2, start.Y - 20, 180, 40);
        else if (r.Width < 8 || r.Height < 8) r = new Rect(start.X - 2, start.Y - 11, 150, 22);
        field.Box = r;
        field.FontSize = Math.Min(_textSize, r.Height * 0.7);
        if (field.Kind == PdfNewFieldKind.Text) field.Multiline = r.Height > field.FontSize * 2.4;           // (a tall box takes several lines)
        Add(field, select: true);
        if (field.Kind == PdfNewFieldKind.Dropdown) Dispatcher.BeginInvoke(new Action(() => EditFieldOptions(field, firstTime: true)));      // (a drop-down list needs its choices)
    }
}
