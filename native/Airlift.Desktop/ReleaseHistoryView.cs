using Airlift.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Airlift.Desktop;

/// <summary>A read-only release browser. Viewing older releases never changes the selected install version.</summary>
public sealed class ReleaseHistoryView : StackPanel
{
    private readonly PackageManager _manager;
    private readonly CatalogApp _app;
    private readonly Func<string, Task> _openUrl;
    private readonly CancellationToken _ct;
    private readonly StackPanel _entries = Ui.Stack(12);
    private readonly TextBlock _status = Ui.MutedText("Loading public releases…", 11);
    private readonly Button _refresh, _older;
    private readonly HashSet<string> _tags = new(StringComparer.Ordinal);
    private int _page;
    private bool _loading;

    public ReleaseHistoryView(PackageManager manager, CatalogApp app, Func<string, Task> openUrl, CancellationToken ct = default)
    {
        _manager = manager; _app = app; _openUrl = openUrl; _ct = ct; Spacing = 16;
        _refresh = Ui.AsyncButton("Refresh", () => LoadAsync(true));
        _older = Ui.AsyncButton("Load older releases", () => LoadPageAsync(_page + 1, false)); _older.IsVisible = false;
        Children.Add(Ui.Between(Ui.Stack(5, Ui.Text("Release history", 20, weight: FontWeight.SemiBold), Ui.MutedText(app.Repository + " · stable & prerelease", 11)), _refresh));
        Children.Add(_status); Children.Add(_entries); Children.Add(_older);
        if (manager.CachedHistory(app) is { } cached) Display(cached, 1);
        else if (manager.Release(app)?.Release is { } latest)
        {
            _entries.Children.Add(ReleaseCard(latest, true));
            _status.Text = "Saved latest release · loading history…";
        }
    }
    public Task LoadAsync(bool force = false) => LoadPageAsync(1, force);
    private async Task LoadPageAsync(int page, bool force)
    {
        if (_loading || _ct.IsCancellationRequested) return;
        _loading = true; _refresh.IsEnabled = false; _older.IsEnabled = false;
        _status.Text = page == 1 ? "Checking public releases…" : "Loading older releases…";
        try
        {
            var result = await _manager.GetReleaseHistoryAsync(_app, page, force, _ct);
            if (!_ct.IsCancellationRequested) Display(result, page);
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested) { }
        catch (Exception error) { if (!_ct.IsCancellationRequested) _status.Text = "Could not load releases: " + error.Message; }
        finally { _loading = false; _refresh.IsEnabled = true; _older.IsEnabled = true; }
    }
    private void Display(ReleaseHistoryPage result, int page)
    {
        if (result.Error != null && result.Releases.Count == 0)
        {
            _status.Text = "Could not load releases. " + result.Error;
            return; // Keep anything already visible and let Refresh / Load older retry.
        }
        if (page == 1) { _entries.Children.Clear(); _tags.Clear(); }
        foreach (var release in result.Releases)
            if (_tags.Add(release.Tag)) _entries.Children.Add(ReleaseCard(release, page == 1 && _tags.Count == 1));
        _page = page; _older.IsVisible = result.HasMore;
        _status.Text = result.Error != null ? $"Saved releases · {result.Error}" : result.Releases.Count == 0 && page == 1 ? "No public releases yet." :
            $"{_tags.Count} releases loaded · checked {result.CheckedAt.LocalDateTime:g}";
    }
    private Control ReleaseCard(AppRelease release, bool expanded)
    {
        var badges = new List<string>();
        if (release.Tag == _manager.Release(_app)?.Release?.Tag) badges.Add("Latest in your channel");
        if (release.Version == _manager.Installed(_app)?.Version) badges.Add("Installed");
        if (release.Prerelease) badges.Add("Prerelease");
        var heading = Ui.Stack(6, Ui.Text(release.Tag, 16, weight: FontWeight.SemiBold),
            Ui.MutedText(release.Published.ToLocalTime().ToString("MMM d, yyyy") + (badges.Count == 0 ? " · Stable" : " · " + string.Join(" · ", badges)), 10));
        var detail = Ui.Stack(15);
        var notes = string.IsNullOrWhiteSpace(release.Notes) ? "No release notes were published." : release.Notes;
        detail.Children.Add(new MarkdownNotes(notes, release.Url, _openUrl));
        detail.Children.Add(Ui.Text($"ASSETS  ·  {release.Assets.Count}", 10, "#B6C5A5", FontWeight.SemiBold));
        if (release.Assets.Count == 0) detail.Children.Add(Ui.MutedText("No downloadable assets were attached.", 11));
        foreach (var asset in release.Assets)
        {
            var name = Ui.Text(asset.Name, 11); name.TextWrapping = TextWrapping.Wrap;
            var download = Ui.AsyncButton("Download ↗", () => _openUrl(asset.Url));
            ToolTip.SetTip(download, "Open this release asset in your browser");
            detail.Children.Add(Ui.Between(Ui.Stack(5, name, Ui.MutedText(Ui.Bytes(asset.Size) + (asset.Sha256 != null ? " · SHA-256 published" : ""), 10)), download));
        }
        detail.IsVisible = expanded;
        var toggle = Ui.Button(expanded ? "Hide notes & assets  ↑" : "Release notes & assets  ↓", () => { }, "link");
        toggle.Click += (_, _) => { detail.IsVisible = !detail.IsVisible; toggle.Content = detail.IsVisible ? "Hide notes & assets  ↑" : "Release notes & assets  ↓"; };
        return Ui.Card(Ui.Stack(16, Ui.Between(heading, Ui.AsyncButton("GitHub ↗", () => _openUrl(release.Url), "link")), toggle, detail), 18);
    }
}
