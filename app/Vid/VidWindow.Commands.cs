using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace IdmClone.Vid;

/// <summary>The commands of the menus, the monitors and the ruler: marks, markers, lift / extract, paste, transitions, frame export.</summary>
public sealed partial class VidWindow
{
    private void PasteClips()
    {
        if (!_s.CanPaste) { SetStatus("Nothing copied yet (select clips, then Ctrl+C)."); return; }
        var made = _s.Paste(_s.Playhead);
        if (made.Count > 0) _timeline.EnsureVisible(made.Min(c => c.Start));
    }

    private void DoLift()
    {
        if (!_s.Lift()) SetStatus("Mark an In and an Out point first (I and O).");
    }

    private void DoExtract()
    {
        if (!_s.Extract()) SetStatus("Mark an In and an Out point first (I and O).");
    }

    private void ApplyTransition(TransitionKind kind)
    {
        var targets = _s.SelectedClips.Where(c => _s.Project.TrackOf(c)?.Kind == TrackKind.Video).ToList();
        if (targets.Count == 0) { SetStatus("Choose a picture clip first: the transition goes at its start."); return; }
        _s.SetTransition(targets, kind, 1.0);
    }

    // ---------- In / Out ----------
    private void MarkIn()
    {
        if (_sourceActive && _srcMedia != null) SrcMarkIn(); else _s.SetWorkIn(_s.Playhead);
    }

    private void MarkOut()
    {
        if (_sourceActive && _srcMedia != null) SrcMarkOut(); else _s.SetWorkOut(_s.Playhead);
    }

    // ---------- markers ----------
    private void AddMarkerHere()
    {
        var m = _s.AddMarker(_s.Playhead);
        SetStatus("Marker added at " + VidTimeline.Timecode(m.Time, Fps) + ". Double-click it on the ruler to name it.");
    }

    private void GoToMarker(int direction)
    {
        var times = _s.Project.Markers.Select(m => m.Time).OrderBy(t => t).ToList();
        double now = _s.Playhead;
        double? target = direction < 0 ? times.Where(t => t < now - 0.001).Select(t => (double?)t).LastOrDefault() : times.Where(t => t > now + 0.001).Select(t => (double?)t).FirstOrDefault();
        if (target == null) { SetStatus("No more markers that way."); return; }
        SeekTo(target.Value);
        _timeline.EnsureVisible(target.Value);
    }

    private void RenameMarkerPrompt(Marker m)
    {
        string? name = TextPrompt.Ask(this, "Marker", "Name this marker (leave empty to show just the flag):", m.Name);
        if (name != null) _s.RenameMarker(m, name.Trim());
    }

    private void ShowRulerMenu(double time)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("ThemedMenu"), Placement = PlacementMode.MousePoint };
        menu.Items.Add(Item("Add a marker here", () => _s.AddMarker(time)));
        var near = _s.Project.Markers.OrderBy(m => Math.Abs(m.Time - time)).FirstOrDefault();
        if (near != null && Math.Abs(near.Time - time) < 0.5)
        {
            menu.Items.Add(Item("Rename the marker…", () => RenameMarkerPrompt(near)));
            menu.Items.Add(Item("Delete the marker", () => _s.RemoveMarker(near)));
        }
        menu.Items.Add(new Separator { Style = (Style)FindResource("ThemedSeparator") });
        menu.Items.Add(Item("Mark In here", () => _s.SetWorkIn(time)));
        menu.Items.Add(Item("Mark Out here", () => _s.SetWorkOut(time)));
        menu.Items.Add(Item("Clear In and Out", () => _s.ClearWorkArea()));
        menu.Items.Add(new Separator { Style = (Style)FindResource("ThemedSeparator") });
        menu.Items.Add(Item("Put the playhead here", () => SeekTo(time)));
        menu.IsOpen = true;
    }

    private void ShowTrackMenu(Track t)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("ThemedMenu"), Placement = PlacementMode.MousePoint };
        menu.Items.Add(Item("Rename the track…", () =>
        {
            string? name = TextPrompt.Ask(this, "Track name", "Name this track:", t.Name);
            if (!string.IsNullOrWhiteSpace(name)) _s.RenameTrack(t, name.Trim());
        }));
        menu.Items.Add(Item(t.Muted ? "Unmute" : "Mute", () => _s.Do(t.Muted ? "Unmute track" : "Mute track", () => t.Muted = !t.Muted)));
        menu.Items.Add(Item(t.Locked ? "Unlock" : "Lock", () => _s.Do(t.Locked ? "Unlock track" : "Lock track", () => t.Locked = !t.Locked)));
        if (t.Kind == TrackKind.Audio) menu.Items.Add(Item(t.Solo ? "Unsolo" : "Solo", () => _s.Do(t.Solo ? "Unsolo track" : "Solo track", () => t.Solo = !t.Solo)));
        menu.Items.Add(new Separator { Style = (Style)FindResource("ThemedSeparator") });
        menu.Items.Add(Item("Add a video track", () => _s.AddTrack(TrackKind.Video)));
        menu.Items.Add(Item("Add a sound track", () => _s.AddTrack(TrackKind.Audio)));
        var del = Item("Delete this track", () =>
        {
            if (t.Clips.Count > 0)
            {
                var r = UMessage.Ask(this, $"\"{t.Name}\" has {t.Clips.Count} clip{(t.Clips.Count == 1 ? "" : "s")} on it. Delete the track and its clips?", "Utylix Vid Editor",
                    MessageBoxImage.Question, MessageBoxResult.No, MessageBoxResult.No, ("Delete them", MessageBoxResult.Yes), ("Keep", MessageBoxResult.No));
                if (r != MessageBoxResult.Yes) return;
            }
            if (!_s.DeleteTrack(t)) SetStatus("The last track of its kind can't be deleted.");
        });
        menu.Items.Add(del);
        menu.IsOpen = true;
    }

    // ---------- preview quality ----------
    private void CyclePreviewQuality()
    {
        double[] steps = { 1, 0.5, 0.25 };
        string[] names = { "Full", "1/2", "1/4" };
        int i = Array.IndexOf(steps, _player.PreviewScale);
        i = (i + 1) % steps.Length;
        _player.PreviewScale = steps[i];
        _qualityText.Text = names[i];
        _player.Refresh();
    }

    // ---------- one picture ----------
    private async void ExportFrame()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save this frame as a picture", Filter = "PNG picture (*.png)|*.png|JPEG picture (*.jpg)|*.jpg", DefaultExt = ".png",
            FileName = "frame " + VidTimeline.Timecode(_s.Playhead, Fps).Replace(':', '-') + ".png",
            InitialDirectory = Directory.Exists(VidSettings.Current.ExportFolder) ? VidSettings.Current.ExportFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dlg.ShowDialog(this) != true) return;
        SetStatus("Making the picture…");
        string? error = await VidExport.FrameAsync(_s.Project, _s.Playhead, dlg.FileName);
        SetStatus(error == null ? "Saved " + Path.GetFileName(dlg.FileName) : error.Split('\n')[0]);
    }
}
