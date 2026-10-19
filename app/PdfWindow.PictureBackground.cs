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
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// "Remove background" and "Background" for a picture: the picture's background is taken out with the same AI model as Background Remover (it runs on this PC), and a colour can be put behind it.
/// A picture added here keeps both as settings (the colour can be changed or taken away again until you save); a picture already in the PDF is changed in the document itself (Undo takes it back until you save).
/// </summary>
public sealed partial class PdfWindow
{
    private StackPanel? _pictureRow;
    private bool _pictureBusy;

    private UIElement PictureBackgroundRow()
    {
        Button Make(string text, string tip, string id, Action<Button> click)
        {
            var b = new Button { Content = BarLabel(text), Template = BarButtonTemplate(), Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 6, 0), Focusable = false, Background = Brushes.Transparent, Foreground = Brushes.White, ToolTip = tip };
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
            b.Click += (_, _) => click(b);
            return b;
        }
        _pictureRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), Visibility = Visibility.Collapsed };
        _pictureRow.Children.Add(Make("Remove background", "Take the background out of this picture (the AI model runs on this PC)", "PdfPictureRemoveBackground", _ => RemovePictureBackground()));
        _pictureRow.Children.Add(Make("Background ▾", "Put a colour behind this picture (it shows where the picture is see-through)", "PdfPictureBackground", ShowPictureBackgroundMenu));
        return _pictureRow;
    }

    /// <summary>The colour shape put just under a picture of the PDF by "Background" (it travels with the picture: moved, resized, deleted, copied), or null.</summary>
    private int? BackdropOf(int page, int imageIndex)
    {
        var list = PageObjects(page);
        var image = list.FirstOrDefault(o => o.Index == imageIndex);
        var before = list.FirstOrDefault(o => o.Index == imageIndex - 1);
        if (image == null || before == null || image.Kind != Pdfium.ObjImage || before.Kind != Pdfium.ObjPath) return null;
        bool same = Math.Abs(before.Box.X - image.Box.X) < 0.6 && Math.Abs(before.Box.Y - image.Box.Y) < 0.6 && Math.Abs(before.Box.Width - image.Box.Width) < 0.6 && Math.Abs(before.Box.Height - image.Box.Height) < 0.6;
        return same ? before.Index : null;
    }

    /// <summary>The indexes of a picked thing, with its background shape when it is one picture that has one.</summary>
    private List<int> WithBackdrop(PageObjectItem po)
    {
        var indices = po.Indices.ToList();
        if (indices.Count == 1 && BackdropOf(po.Page, indices[0]) is int back) indices.Add(back);
        return indices;
    }

    /// <summary>The chosen thing is a picture that can be given or relieved of its background.</summary>
    private bool PictureChosen => _editing && (_selected is ImageItem || TryChosenPicture(out _, out _));

    private void ShowPictureBackgroundMenu(FrameworkElement anchor)
    {
        if (_selected is ImageItem image) ShowColourMenu(anchor, image.BackColor, "No colour (see-through)", "PdfPictureBack", color => SetPictureBackground(color));
        else if (TryChosenPicture(out _, out _)) ShowColourMenu(anchor, null, null, "PdfPictureBack", color => SetPictureBackground(color));
    }

    /// <summary>Runs the AI model on its own thread (it takes a few seconds); asks first for the model when it isn't on this PC yet.</summary>
    private async Task<BitmapSource?> CutOutAsync(BitmapSource source)
    {
        if (!IdmClone.App.EnsureBackgroundModel()) return null;
        if (source.CanFreeze && !source.IsFrozen) source = (BitmapSource)source.GetAsFrozen();
        var done = new TaskCompletionSource<BitmapSource>();
        var thread = new Thread(() =>
        {
            try { done.SetResult(BackgroundRemover.RemoveFrom(source)); }
            catch (Exception e) { done.SetException(e); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { return await done.Task; }
        catch (Exception e) when (e is IOException or InvalidOperationException or OutOfMemoryException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
        {
            Toast("Couldn't take the background out: " + e.Message);
            return null;
        }
    }

    private async void RemovePictureBackground()
    {
        if (_pictureBusy || !PictureChosen) return;
        _pictureBusy = true;
        try
        {
            if (_selected is ImageItem image)
            {
                var start = image.Pixels;
                if (!BackgroundRemover.HasModel && !IdmClone.App.EnsureBackgroundModel()) return;
                Toast("Taking the background out… a few seconds");
                var cut = await CutOutAsync(start);
                if (cut == null || !_items.Contains(image)) return;
                Snapshot();
                image.Pixels = cut; image.Jpeg = null;                                  // (see-through now: a JPEG can't be)
                _dirty = true; UpdateTitle();
                RenderItems(image.Page); UpdateEditButtons();
                Toast("Background removed. Undo brings it back");
            }
            else if (_pdf != null && TryChosenPicture(out var choice, out int index))
            {
                BitmapSource? bitmap;
                try { bitmap = _pdf.GetPictureBitmap(choice.Page, index); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { bitmap = null; }
                if (bitmap == null) { Toast("This picture can't be read, so its background can't be taken out"); return; }
                if (!BackgroundRemover.HasModel && !IdmClone.App.EnsureBackgroundModel()) return;
                Toast("Taking the background out… a few seconds");
                var cut = await CutOutAsync(bitmap);
                if (cut == null || _pdf == null) return;
                int page = choice.Page;
                if (PageOp(p => p.ReplacePicture(page, index, new PdfPicture(null, cut)), new[] { page }, "Background removed. Undo puts the picture back until you save", keepView: true)) AfterPictureChange(page, index);
            }
        }
        finally { _pictureBusy = false; }
    }

    private void SetPictureBackground(Color? colour)
    {
        if (_selected is ImageItem image)
        {
            Snapshot();
            image.BackColor = colour;
            _dirty = true; UpdateTitle();
            RenderItems(image.Page); UpdateEditButtons();
            return;
        }
        if (colour is not Color c || _pdf == null || !TryChosenPicture(out var choice, out int index)) return;
        int page = choice.Page, now = index;
        // (a shape of the colour, just under the picture: it shows where the picture is see-through, and the picture itself is not touched)
        if (PageOp(p => now = p.AddPictureBackdrop(page, index, c), new[] { page }, "Background added behind the picture. Undo takes it away until you save", keepView: true)) AfterPictureChange(page, now);
    }
}
