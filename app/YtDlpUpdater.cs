using System;
using System.Threading.Tasks;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Every few days checks whether a newer yt-dlp exists and, if so, tells the app to ASK the user (once per version).
/// Nothing is ever installed without a click.
/// </summary>
public sealed class YtDlpUpdater
{
    private readonly Manager _manager;
    private readonly Action<string, string> _offer;     // (latest, installed)
    private readonly bool _now;                          // testing: check right away, ignore the "every 3 days" rule

    public YtDlpUpdater(Manager manager, Action<string, string> offer, bool now = false)
    {
        _manager = manager;
        _offer = offer;
        _now = now;
    }

    public void Start() => _ = Task.Run(async () =>
    {
        await Task.Delay(_now ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(45));   // let the app settle first
        while (true)
        {
            try { await CheckAsync(); } catch (Exception) { /* offline etc.: try again later */ }
            await Task.Delay(TimeSpan.FromHours(6));
        }
    });

    private async Task CheckAsync()
    {
        var cfg = _manager.Config;
        if (!cfg.AutoUpdateYtDlp || !Tools.HasYtDlp) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!_now && now - cfg.LastYtDlpCheck < 3 * 86400) return;

        string? latest = await Tools.LatestYtDlpVersionAsync(default);
        if (latest == null) return;
        _manager.UpdateConfig(c => c.LastYtDlpCheck = now);

        string installed = await Tools.YtDlpVersionAsync() ?? "0";
        if (!Tools.IsNewer(latest, installed) || cfg.LastYtDlpOffer == latest) return;
        _manager.UpdateConfig(c => c.LastYtDlpOffer = latest);       // ask once per version
        _offer(latest, installed);
    }
}
