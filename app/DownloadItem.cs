using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>View-model for one card in the list.</summary>
public sealed class DownloadItem : INotifyPropertyChanged
{
    private DownloadInfo _info;

    public DownloadItem(DownloadInfo info) => _info = info;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id => _info.Id;
    public string Url => _info.Url;
    public string FileName => _info.FileName;
    public DateTime Created => _info.Created;
    public DlStatus Status => _info.Status;
    public string? FilePath => _info.FilePath;

    /// <summary>The file type's own icon from Windows (null when the type has no registered program).</summary>
    public System.Windows.Media.ImageSource? Icon => ShellIcons.ForFile(_info.FileName);
    public bool HasIcon => Icon != null;

    public bool IsCompleted => Status == DlStatus.Completed;
    public bool IsActive => Status != DlStatus.Completed;
    public bool CanPause => Status is DlStatus.Downloading or DlStatus.Queued;
    public bool CanResume => Status is DlStatus.Paused or DlStatus.Error;
    /// <summary>A failed video-site download: the usual cure is a newer yt-dlp.</summary>
    public bool CanUpdateRetry => _info.IsMedia && Status == DlStatus.Error;
    public string ResumeLabel => Status == DlStatus.Error ? "Retry" : "Resume";

    public double Percent => _info.Size > 0 ? Math.Min(100, _info.Downloaded * 100.0 / _info.Size)
                                            : IsCompleted ? 100 : 0;

    public Brush ProgressBrush => (Brush)Application.Current.Resources[Status switch
    {
        DlStatus.Completed => "OkBrush",
        DlStatus.Error => "ErrBrush",
        _ => "AccentBrush",
    }];

    /// <summary>Colour of the per-connection bar in the details: teal, so it isn't mistaken for the main blue bar.</summary>
    public Brush ConnBrush => (Brush)Application.Current.Resources[Status switch
    {
        DlStatus.Completed => "OkBrush",
        DlStatus.Error => "ErrBrush",
        _ => "ConnBrush",
    }];

    public Brush StatusBrush => (Brush)Application.Current.Resources[Status switch
    {
        DlStatus.Completed => "OkBrush",
        DlStatus.Error => "ErrBrush",
        _ => "TextBrush",
    }];

    public string StatusText => Status switch
    {
        DlStatus.Queued => "Queued",
        DlStatus.Downloading => _info.Phase ?? "Downloading",
        DlStatus.Paused => "Paused",
        DlStatus.Completed => "Completed",
        DlStatus.Awaiting => "Waiting for confirmation",
        _ => "Error: " + (_info.Error ?? "failed"),
    };

    public string Details
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();
            parts.Add(_info.Size > 0
                ? $"{Format.Bytes(_info.Downloaded)} / {Format.Bytes(_info.Size)} ({Percent:0.0}%)"
                : Format.Bytes(_info.Downloaded));
            if (Status == DlStatus.Downloading)
            {
                parts.Add(Format.Bytes(_info.Speed) + "/s");
                if (_info.Eta is { } eta) parts.Add(Format.Eta(eta) + " left");
                if (_info.IsMedia) parts.Add("video site");
                else
                {
                    parts.Add(_info.Connections + (_info.Connections == 1 ? " connection" : " connections"));
                    if (!_info.Resumable) parts.Add("single stream");
                }
            }
            return string.Join("   ·   ", parts);
        }
    }

    // ---------- "Show more details" ----------
    private bool _expanded;

    public bool IsExpanded => _expanded;
    public string ToggleLabel => _expanded ? "Hide details  ▴" : "Show more details  ▾";

    public void ToggleDetails()
    {
        _expanded = !_expanded;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public string SegmentData => _info.Segments;
    public long TotalBytes => _info.Size;
    public bool Complete => IsCompleted;

    public string DetAddress => _info.Url;
    public string DetSavePath => _info.SavePath;
    public string DetType => string.IsNullOrEmpty(_info.ContentType) ? "—" : _info.ContentType!;
    public string DetAdded => _info.Created.ToLocalTime().ToString("g");
    public string DetRetries => _info.Retries == 0 ? "None" : _info.Retries.ToString();

    public string DetSize => _info.Size > 0 ? $"{Format.Bytes(_info.Size)}  ({_info.Size:N0} bytes)" : "Unknown";
    public string DetDownloaded => _info.Size > 0
        ? $"{Format.Bytes(_info.Downloaded)}  ({Percent:0.0}%)" : Format.Bytes(_info.Downloaded);
    public string DetSpeed => Status == DlStatus.Downloading ? Format.Bytes(_info.Speed) + "/s" : "—";
    public string DetAvg => _info.AvgSpeed > 0 ? Format.Bytes(_info.AvgSpeed) + "/s" : "—";
    public string DetEta => Status == DlStatus.Downloading && _info.Eta is { } eta ? Format.Eta(eta) : "—";
    public string DetConnections => _info.IsMedia ? "—" : Status == DlStatus.Downloading ? _info.Connections.ToString() : "—";
    public string DetResume => _info.IsMedia ? "Yes — video site (yt-dlp), can pause and resume"
        : _info.Resumable ? "Yes — multi-connection, can pause and resume" : "No — single connection";

    /// <summary>Returns true if anything visible changed.</summary>
    public bool Update(DownloadInfo info)
    {
        if (info == _info) return false;
        bool statusChanged = info.Status != _info.Status;
        _info = info;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));   // refresh every binding
        return statusChanged;
    }
}

public static class Format
{
    public static string Bytes(double n)
    {
        if (n < 0) return "?";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
        return n.ToString(i == 0 ? "0" : "0.0") + " " + units[i];
    }

    public static string Eta(long s)
    {
        long h = s / 3600, m = s % 3600 / 60, x = s % 60;
        return h > 0 ? $"{h}h {m}m" : m > 0 ? $"{m}m {x}s" : $"{x}s";
    }
}
