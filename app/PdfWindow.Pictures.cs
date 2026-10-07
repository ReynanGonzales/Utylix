using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Pictures that are already in the PDF: pick one with Select, then right-click > Replace this picture, Crop this picture or Save this picture; and Tools > PICTURES > Save all the pictures.
/// Changes go into the open document at once (Undo takes them back until you save).
/// </summary>
public sealed partial class PdfWindow
{
    /// <summary>The chosen thing is exactly one picture of the PDF (not something added in this session).</summary>
    private bool TryChosenPicture(out PageObjectItem choice, out int objectIndex)
    {
        choice = null!; objectIndex = -1;
        if (_editing && _selected is PageObjectItem { Indices.Count: 1 } po && PageObjects(po.Page).FirstOrDefault(o => o.Index == po.Indices[0]) is { Kind: Pdfium.ObjImage } obj)
        {
            choice = po; objectIndex = obj.Index;
            return true;
        }
        return false;
    }

    private void AfterPictureChange(int page, int objectIndex)
    {
        var obj = PageObjects(page).FirstOrDefault(o => o.Index == objectIndex);
        if (obj != null) Select(new PageObjectItem { Page = page, Indices = { objectIndex }, Box = obj.Box, Original = obj.Box });
    }

    private void ReplaceChosenPicture()
    {
        if (_pdf == null || !TryChosenPicture(out var choice, out int index)) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "The picture to put there", Filter = "Pictures|" + string.Join(";", PdfCombiner.PictureExtensions.Select(e => "*." + e)) + "|All files|*.*", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return;
        PdfPicture picture;
        try { picture = PdfCombiner.LoadPicture(File.ReadAllBytes(dlg.FileName), 4000); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or System.Runtime.InteropServices.COMException or InvalidOperationException) { Toast("Couldn't read that picture: " + e.Message); return; }
        int page = choice.Page;
        if (PageOp(p => p.ReplacePicture(page, index, picture), new[] { page }, "Picture replaced. Undo puts the old one back until you save", keepView: true)) AfterPictureChange(page, index);
    }

    private void CropChosenPicture()
    {
        if (_pdf == null || !TryChosenPicture(out var choice, out int index)) return;
        BitmapSource? bitmap;
        try { bitmap = _pdf.GetPictureBitmap(choice.Page, index); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { bitmap = null; }
        if (bitmap == null) { Toast("This picture can't be read, so it can't be cropped"); return; }
        var dlg = new PdfPictureCropDialog(this, bitmap);
        if (dlg.ShowDialog() != true) return;
        int page = choice.Page; var (l, t, r, b) = (dlg.CutLeft, dlg.CutTop, dlg.CutRight, dlg.CutBottom);
        if (PageOp(p => p.CropPicture(page, index, l, t, r, b), new[] { page }, "Picture cropped. Undo brings the whole picture back until you save", keepView: true)) AfterPictureChange(page, index);
    }

    private void SaveChosenPicture()
    {
        if (_pdf == null || !TryChosenPicture(out var choice, out int index)) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save this picture", Filter = "Picture|*.png;*.jpg", FileName = System.IO.Path.GetFileNameWithoutExtension(_path) + $" - page {choice.Page + 1}", InitialDirectory = System.IO.Path.GetDirectoryName(_path) };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            string target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dlg.FileName)!, System.IO.Path.GetFileNameWithoutExtension(dlg.FileName));
            string ext = _pdf.SavePictureTo(choice.Page, index, target);
            Toast("Saved " + System.IO.Path.GetFileName(target) + ext);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Toast("Couldn't save it: " + e.Message); }
    }

    private async void SaveAllPictures()
    {
        if (_pdf == null || _path == null) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Where should the pictures of the PDF be saved?", InitialDirectory = System.IO.Path.GetDirectoryName(_path) };
        if (dlg.ShowDialog(this) != true) return;
        var pdf = _pdf; string folder = dlg.FolderName, prefix = System.IO.Path.GetFileNameWithoutExtension(_path);
        Toast("Saving the pictures…");
        try
        {
            int saved = await Task.Run(() => pdf.SaveAllPictures(folder, prefix, 24, null, CancellationToken.None));
            Toast(saved == 0 ? "This PDF has no pictures to save (pages that are scans are pictures too: Tools > Save pages as pictures)" : $"Saved {saved} picture{(saved == 1 ? "" : "s")} in {folder}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) { Toast("Couldn't save the pictures: " + e.Message); }
    }
}

/// <summary>"Crop this picture": the picture with four sliders (percent cut from each edge) and the cut part shaded.</summary>
internal sealed class PdfPictureCropDialog : Window
{
    private readonly Slider _left = PdfDialogKit.Bar(0, 90, 0, 200), _top = PdfDialogKit.Bar(0, 90, 0, 200), _right = PdfDialogKit.Bar(0, 90, 0, 200), _bottom = PdfDialogKit.Bar(0, 90, 0, 200);
    private readonly TextBlock _leftText = new(), _topText = new(), _rightText = new(), _bottomText = new();
    private readonly Canvas _overlay = new();
    private readonly double _w, _h;
    private const double PreviewMax = 360;

    public double CutLeft => _left.Value / 100; public double CutTop => _top.Value / 100; public double CutRight => _right.Value / 100; public double CutBottom => _bottom.Value / 100;

    public PdfPictureCropDialog(Window owner, BitmapSource picture)
    {
        PdfDialogKit.Setup(this, owner, "Crop this picture", 720);
        double k = Math.Min(PreviewMax / picture.PixelWidth, PreviewMax / picture.PixelHeight);
        _w = Math.Max(40, picture.PixelWidth * k); _h = Math.Max(40, picture.PixelHeight * k);
        var image = new Image { Source = picture, Width = _w, Height = _h, Stretch = Stretch.Fill };
        _overlay.Width = _w; _overlay.Height = _h;
        var box = new Grid { Width = _w, Height = _h, Background = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)) };
        box.Children.Add(image); box.Children.Add(_overlay);
        var left = new StackPanel { Width = 300, Margin = new Thickness(0, 0, 24, 0) };
        left.Children.Add(PdfDialogKit.Heading("Cut off", 0));
        left.Children.Add(PdfDialogKit.LabelRow("Left", _left, _leftText, 60));
        left.Children.Add(PdfDialogKit.LabelRow("Top", _top, _topText, 60));
        left.Children.Add(PdfDialogKit.LabelRow("Right", _right, _rightText, 60));
        left.Children.Add(PdfDialogKit.LabelRow("Bottom", _bottom, _bottomText, 60));
        left.Children.Add(PdfDialogKit.Muted("The darker part is removed from the picture for good (when you save). A picture that has see-through parts may lose them.", 12));
        ((TextBlock)left.Children[^1]).Margin = new Thickness(0, 12, 0, 0);
        foreach (var s in new[] { _left, _top, _right, _bottom }) s.ValueChanged += (_, _) => Changed();
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(box, 1);
        body.Children.Add(left); body.Children.Add(box);
        var go = new Button { Content = "Crop", MinWidth = 140 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(go, "PdfPicCropGo");
        go.Click += (_, _) => { if (CutLeft + CutRight < 0.95 && CutTop + CutBottom < 0.95 && (CutLeft + CutTop + CutRight + CutBottom) > 0) DialogResult = true; };
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(body);
        root.Children.Add(PdfDialogKit.Buttons(go, new Button { Content = "Cancel", MinWidth = 96 }));
        Content = root;
        System.Windows.Automation.AutomationProperties.SetAutomationId(_left, "PdfPicCropLeft"); System.Windows.Automation.AutomationProperties.SetAutomationId(_top, "PdfPicCropTop");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_right, "PdfPicCropRight"); System.Windows.Automation.AutomationProperties.SetAutomationId(_bottom, "PdfPicCropBottom");
        Loaded += (_, _) => Changed();
    }

    private void Changed()
    {
        _leftText.Text = Math.Round(_left.Value) + " %"; _topText.Text = Math.Round(_top.Value) + " %";
        _rightText.Text = Math.Round(_right.Value) + " %"; _bottomText.Text = Math.Round(_bottom.Value) + " %";
        _overlay.Children.Clear();
        double l = _w * CutLeft, t = _h * CutTop, r = _w * CutRight, b = _h * CutBottom;
        var shade = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0));
        void Cover(double x, double y, double cw, double ch)
        {
            if (cw <= 0 || ch <= 0) return;
            var rect = new Rectangle { Width = cw, Height = ch, Fill = shade };
            Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y);
            _overlay.Children.Add(rect);
        }
        Cover(0, 0, _w, t); Cover(0, _h - b, _w, b); Cover(0, t, l, _h - t - b); Cover(_w - r, t, r, _h - t - b);
        var frame = new Rectangle { Width = Math.Max(1, _w - l - r), Height = Math.Max(1, _h - t - b), Stroke = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0xEA)), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 } };
        Canvas.SetLeft(frame, l); Canvas.SetTop(frame, t);
        _overlay.Children.Add(frame);
    }
}
