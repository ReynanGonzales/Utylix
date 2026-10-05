using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using LibVLCSharp.Shared;

namespace IdmClone;

public sealed partial class MusicWindow
{
    private Track NewTrack(string path) => new() { Path = path, IsFavourite = _saved.Favourites.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase)) };

    /// <summary>Opens the song info editor for these songs.</summary>
    private void EditInfo(List<Track> tracks)
    {
        tracks = tracks.Where(t => File.Exists(t.Path)).Distinct().ToList();
        if (tracks.Count == 0) return;
        var window = new MusicTagWindow(tracks, ReleasePlayingAsync, AfterTagsSaved) { Owner = this };
        window.ShowDialog();
    }

    /// <summary>
    /// Windows won't let a file be written while it plays: the playing song (if it is one of these) is stopped, and the returned action starts it
    /// again where it was once the file is written.
    /// </summary>
    private async Task<Action> ReleasePlayingAsync(IEnumerable<Track> tracks)
    {
        var current = CurrentTrack;
        if (current == null || !tracks.Contains(current)) return () => { };
        var snap = _snap;
        bool wasPlaying = snap.Playing; long time = snap.Time;
        Post(mp => mp.Stop());
        await Task.Delay(600);
        return () =>
        {
            int index = _list.IndexOf(current);
            if (index < 0) return;
            PlayTrack(index);
            _ = Task.Delay(900).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (time > 1500) Post(mp => mp.Time = time);
                if (!wasPlaying) Post(mp => mp.SetPause(true));
            })));
        };
    }

    /// <summary>The playing song's name, artist and cover follow what was just written.</summary>
    private void AfterTagsSaved()
    {
        var current = CurrentTrack;
        if (current == null) return;
        UpdateNow(current);
        _ = LoadCoverAsync(current);
        foreach (var t in _list) t.RefreshDisplay();
    }
}
