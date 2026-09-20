using Airlift.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Text.Json;

namespace Airlift.Desktop;
public sealed partial class MainWindow
{
    private void BuildDownloads(StackPanel content)
    {
        content.Children.Add(Ui.Between(Ui.Text("Activity", 18), Ui.Button("Clear completed", () => { _manager.ClearHistory(); Render(); }, "link")));
        var operations = _manager.Store.All<Operation>("operations").OrderByDescending(o => o.Started).ToList();
        if (operations.Count == 0) { content.Children.Add(Empty("A clear runway.", "Downloads, installations and updates will appear here.")); return; }
        foreach (var operation in operations)
        {
            var app = _manager.Apps.Single(a => a.Id == operation.AppId);
            var progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = operation.Progress, IsIndeterminate = operation.Status is "Installing" or "Uninstalling" or "Reconciling" };
            var actions = Ui.Row(8);
            if (operation.CanCancel) actions.Children.Add(Ui.Button("Pause", () => _manager.Pause(operation.Id)));
            else if (!operation.Active && operation.Status is "Paused" or "Failed" or "Interrupted") actions.Children.Add(Ui.AsyncButton("Review & retry", () => Details(app)));
            if (operation.File is { } file && File.Exists(file)) actions.Children.Add(Ui.AsyncButton("Show file", async () => await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(Path.GetDirectoryName(file)!))));
            var heading = Ui.Between(Ui.Row(13, Ui.Icon(app, 38), Ui.Stack(4, Ui.Text(operation.AppName + "  ·  " + operation.Action, 14), Ui.MutedText(operation.Version + "  ·  " + operation.Status, 10))), actions);
            content.Children.Add(Ui.Card(Ui.Stack(15, heading, progress, Ui.MutedText(operation.Message, 11)), 20));
        }
    }
    private void BuildCollections(StackPanel content)
    {
        var choices = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 14 };
        var index = 0;
        foreach (var (collection, description, color) in new[] {
            ("Everyday essentials", "A little less friction. A lot more flow.", "#28331F"),
            ("The creative desk", "Space for your next good idea.", "#2D272F"),
            ("After hours", "Good tools for your downtime.", "#253036") })
        {
            var number = ++index;
            var button = Ui.Button("", () => { _collection = collection; Render(); }, "identity"); button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            var card = Ui.Card(Ui.Stack(15, Ui.MutedText($"COLLECTION  /  0{number}", 9), Ui.Text(collection, 21, weight: FontWeight.SemiBold), Ui.MutedText(description, 11), Ui.Text(_collection == collection ? "Exploring this collection  ↓" : "Explore collection  →", 11, "#D7F59A")), 22, color);
            if (_collection == collection) card.BorderBrush = Ui.Lime;
            button.Content = card; choices.Children.Add(button); Grid.SetColumn(button, number - 1);
        }
        content.Children.Add(choices);
        var apps = _manager.Apps.Where(a => _collection == "The creative desk" ? a.Category is "Creative" or "Productivity" : _collection == "After hours" ? a.Category is "Media" or "Gaming" : a.Name is "clockt" or "Hisashi" or "Atelier" or "pidi").ToList();
        content.Children.Add(Ui.Between(Ui.Text($"{apps.Count} apps in this collection", 17), Ui.AsyncButton("Download available apps  ↓", async () =>
        {
            var plans = new List<PackagePlan>(); foreach (var app in apps) { try { plans.Add(_manager.Plan(app)); } catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { } }
            if (plans.Count == 0) { Notice("Check releases first. This collection has no mapped packages available for this computer yet."); return; }
            if (!await Confirm("Download this collection?", $"Download {plans.Count} packages ({Ui.Bytes(plans.Sum(p => p.Asset.Size))}) from their public GitHub releases. Installation can be started individually after review.", "Download collection")) return;
            foreach (var plan in plans) _ = RunOperation(plan.App, "download", false);
            Navigate("Downloads");
        })));
        content.Children.Add(Cards(apps));
    }
    private void BuildSources(StackPanel content)
    {
        content.Children.Add(Ui.Card(Ui.Between(Ui.Stack(8, Ui.Text("GitHub Releases", 21, weight: FontWeight.SemiBold), Ui.MutedText($"{_manager.Apps.Count} public repositories · No sign-in required", 12)), Ui.AsyncButton(_checking ? "Checking…" : "↻ Check releases", () => CheckReleases(true), enabled: !_checking))));
        foreach (var app in _manager.Apps)
        {
            var cache = _manager.Release(app); var release = cache?.Release; var preferences = _manager.Preferences(app);
            var metadata = Ui.Stack(9, Ui.AsyncButton(app.Repository + "  ↗", () => OpenUrl($"https://github.com/{app.Repository}/releases"), "link"),
                Ui.MutedText(release == null ? cache?.Error ?? "Not checked yet" : $"{release.Tag}  ·  {release.Published.LocalDateTime:d}  ·  {(preferences.IncludePrerelease ? "Prereleases included" : "Stable channel")}", 11));
            if (cache?.Error != null && release != null) metadata.Children.Add(Ui.MutedText("Cached release · " + cache.Error, 10));
            var card = Ui.Stack(14, Ui.Between(Ui.Row(13, Ui.Icon(app, 38), metadata), Ui.AsyncButton("Releases", () => Details(app, true))));
            if (release != null)
            {
                foreach (var asset in release.Assets)
                    card.Children.Add(Ui.Between(Ui.AsyncButton(asset.Name + "  ↗", () => OpenUrl(asset.Url), "link"), Ui.MutedText(Ui.Bytes(asset.Size) + (asset.Sha256 != null ? "  ·  SHA-256 available" : "  ·  no digest"), 10)));
            }
            content.Children.Add(Ui.Card(card, 20));
        }
    }
    private void BuildSettings(StackPanel content)
    {
        var startup = new CheckBox { Content = "Check for releases when Airlift starts", IsChecked = _manager.Settings.CheckOnStartup };
        startup.IsCheckedChanged += (_, _) => _manager.SetSettings(_manager.Settings with { CheckOnStartup = startup.IsChecked == true });
        var interval = new ComboBox { ItemsSource = new[] { 6, 12, 24 }, SelectedItem = _manager.Settings.CheckIntervalHours, Width = 100 };
        interval.SelectionChanged += (_, _) => { if (interval.SelectedItem is int hours) _manager.SetSettings(_manager.Settings with { CheckIntervalHours = hours }); };
        content.Children.Add(Ui.Card(Ui.Stack(20, Ui.Text("Release tracking", 19), startup, Ui.Between(Ui.Stack(5, Ui.Text("Background check interval", 13), Ui.MutedText("Hours between checks while Airlift is open. Updates always wait for your review.", 11)), interval))));
        var inventory = Ui.Between(Ui.Stack(5, Ui.Text("Installed apps", 13), Ui.MutedText("Reconcile Airlift with installed Forge apps on this computer.", 11)), Ui.AsyncButton("Refresh inventory", async () => { try { await Task.Run(_manager.RefreshInventory); Notice("Installed apps refreshed."); Render(); } catch (Exception e) { Notice(e.Message); } }));
        var storage = Ui.Between(Ui.Stack(5, Ui.Text("Local storage", 13), Ui.MutedText(_manager.Store.Root, 10)), Ui.AsyncButton("Open folder", async () => await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(_manager.Store.Root))));
        content.Children.Add(Ui.Card(Ui.Stack(18, Ui.Text("Your workspace", 19), inventory, storage)));
        var setupActions = Ui.Row(10, Ui.AsyncButton("Export setup", ExportSetup), Ui.AsyncButton("Import setup", ImportSetup));
        content.Children.Add(Ui.Card(Ui.Stack(17, Ui.Text("Take your setup with you", 19), Ui.MutedText("Export app identities, version pins and channel preferences. Importing updates preferences; it does not install or remove applications.", 12), setupActions)));
        content.Children.Add(SelfUpdateCard());
    }
    private async Task ExportSetup()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export Airlift setup", SuggestedFileName = "airlift-setup.json", FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }] });
        if (file == null) return;
        var setup = new SetupDocument(1, _manager.Apps.Where(a => _manager.Installed(a) != null || _manager.Preferences(a) != new AppPreferences()).Select(a => new SetupEntry(a.Id, _manager.Installed(a)?.Version, _manager.Preferences(a))).ToList());
        await using var stream = await file.OpenWriteAsync(); stream.SetLength(0); await JsonSerializer.SerializeAsync(stream, setup, JsonData.Options); Notice("Setup exported.");
    }
    private async Task ImportSetup()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import Airlift setup", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }] });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync(); using var reader = new StreamReader(stream); var json = await reader.ReadToEndAsync();
            var setup = SetupDocument.Parse(json, _manager.Apps);
            if (!await Confirm("Import setup preferences?", $"Apply version pins and release channels for {setup.Apps.Count} apps. Installed apps will remain unchanged.", "Import preferences")) return;
            foreach (var entry in setup.Apps) _manager.SetPreferences(_manager.Apps.Single(a => a.Id == entry.AppId), entry.Preferences);
            Notice("Setup preferences imported. Use the library to review and install missing apps."); Render();
        }
        catch (Exception e) { Notice("Could not import setup: " + e.Message); }
    }
    private async Task Details(CatalogApp app, bool showReleases = false)
    {
        var window = Dialog(app.Name, 760); window.SizeToContent = SizeToContent.Manual; window.Height = 760; window.MinHeight = 500; window.CanResize = true;
        using var lifetime = new CancellationTokenSource(); var token = lifetime.Token;
        window.Closed += (_, _) => lifetime.Cancel();
        var history = new ReleaseHistoryView(_manager, app, OpenUrl, token);
        var latest = new ContentControl(); var facts = Ui.Stack(18); var status = Ui.MutedText("", 11);
        var pinned = new CheckBox { Name = "PinVersion", Content = "Pin installed version (exclude from updates)", IsChecked = _manager.Preferences(app).Pinned, IsEnabled = _manager.Installed(app) != null };
        var previews = new CheckBox { Name = "IncludePrereleases", Content = "Include prereleases", IsChecked = _manager.Preferences(app).IncludePrerelease };
        var body = Ui.Stack(18, Ui.MutedText(app.Description, 13), latest, Ui.Separator(), facts, Ui.Stack(4, pinned, previews), status);
        var header = Ui.Between(Ui.Row(17, Ui.Icon(app, 56), Ui.Stack(6, Ui.Text(app.Name, 28, weight: FontWeight.SemiBold), Ui.MutedText(app.Tagline, 12))), Ui.Button("Close", window.Close, "link"));
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        var tabs = Ui.Row(8); var overviewTab = Ui.Button("Overview", () => { }, "chip"); var releasesTab = Ui.Button("Releases", () => { }, "chip");
        tabs.Children.Add(overviewTab); tabs.Children.Add(releasesTab);
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Margin = new Thickness(0, 18) };
        void SelectTab(bool releases)
        {
            overviewTab.Classes.Set("active", !releases); releasesTab.Classes.Set("active", releases);
            scroll.Content = releases ? history : body; scroll.Offset = default;
        }
        overviewTab.Click += (_, _) => SelectTab(false); releasesTab.Click += (_, _) => SelectTab(true);
        void RefreshOverview(bool checking = false)
        {
            var installed = _manager.Installed(app); var cache = _manager.Release(app); var release = cache?.Release;
            PackagePlan? plan = null; string? unavailable = null;
            try { plan = _manager.Plan(app); } catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { unavailable = e.Message; }
            facts.Children.Clear();
            foreach (var (label, value) in new[] { ("Publisher", app.Publisher), ("Repository", app.Repository), ("Published version", release?.Tag ?? "Not available"), ("Installed version", installed?.Version ?? "Not installed"), ("Download", plan == null ? "No mapped package" : Ui.Bytes(plan.Asset.Size)), ("Integrity", plan?.Asset.Sha256 != null ? "GitHub SHA-256 digest available" : "No download digest published") })
                facts.Children.Add(Ui.Between(Ui.MutedText(label, 11), Ui.Text(value, 11)));
            latest.Content = null; latest.IsVisible = release != null;
            if (release != null)
            {
                var notes = string.IsNullOrWhiteSpace(release.Notes) ? "No release notes were published." : release.Notes;
                var preview = new Border { Child = new MarkdownNotes(notes, release.Url, OpenUrl), MaxHeight = 160, ClipToBounds = true };
                latest.Content = Ui.Card(Ui.Stack(10, Ui.Between(Ui.Stack(5, Ui.Text("Latest release  ·  " + release.Tag, 14, weight: FontWeight.SemiBold), Ui.MutedText(release.Published.ToLocalTime().ToString("MMM d, yyyy"), 10)), Ui.Button("All releases →", () => SelectTab(true), "link")), preview), 16);
            }
            status.Text = checking ? "Checking the selected release channel…" : cache?.Error ?? unavailable;
            status.IsVisible = !string.IsNullOrEmpty(status.Text);
            actions.Children.Clear();
            if (installed?.Executable is { } executable && File.Exists(executable)) actions.Children.Add(Ui.AsyncButton("Open app ↗", async () => { var file = await StorageProvider.TryGetFileFromPathAsync(executable); if (file != null) await Launcher.LaunchFileAsync(file); }));
            if (plan != null)
            {
                var canInstall = installed == null || SemVersion.IsNewer(plan.Release.Version, installed.Version);
                if (canInstall) actions.Children.Add(Ui.AsyncButton((installed == null ? "Install " : "Update to ") + plan.Release.Version, async () => { window.Close(); await ReviewOperation(app, installed == null ? "install" : "update"); }, "primary", !checking && !_manager.IsBusy(app)));
                actions.Children.Add(Ui.AsyncButton("Download only", async () => { window.Close(); await ReviewOperation(app, "download"); }, enabled: !checking && !_manager.IsBusy(app)));
            }
            if (installed != null) actions.Children.Add(Ui.AsyncButton("Uninstall", async () => { window.Close(); await ReviewOperation(app, "uninstall"); }, "danger", !_manager.IsBusy(app)));
            foreach (var child in actions.Children) child.Margin = new Thickness(0, 0, 8, 8);
        }
        pinned.IsCheckedChanged += (_, _) => _manager.SetPreferences(app, _manager.Preferences(app) with { Pinned = pinned.IsChecked == true });
        var channelRevision = 0;
        previews.IsCheckedChanged += async (_, _) =>
        {
            var revision = ++channelRevision;
            _manager.SetPreferences(app, _manager.Preferences(app) with { IncludePrerelease = previews.IsChecked == true });
            RefreshOverview(checking: true);
            try
            {
                await _manager.CheckReleaseAsync(app, token);
                if (token.IsCancellationRequested || revision != channelRevision) return;
                RefreshOverview(); await history.LoadAsync();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (token.IsCancellationRequested || revision != channelRevision) return;
                RefreshOverview(); status.Text = "Could not refresh this channel: " + error.Message; status.IsVisible = true;
            }
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(27) };
        header.Margin = new Thickness(0, 0, 24, 22); layout.Children.Add(header); layout.Children.Add(tabs); Grid.SetRow(tabs, 1);
        layout.Children.Add(scroll); Grid.SetRow(scroll, 2);
        var actionbar = new Border { Child = actions, Padding = new Thickness(0, 16, 0, 0), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) }; layout.Children.Add(actionbar); Grid.SetRow(actionbar, 3);
        RefreshOverview(); SelectTab(showReleases); window.Content = layout;
        window.Opened += async (_, _) => await history.LoadAsync();
        await window.ShowDialog(this); Render();
    }
    private async Task ReviewOperation(CatalogApp app, string action)
    {
        try
        {
            PackagePlan? plan = action == "uninstall" ? null : _manager.Plan(app);
            var message = action == "uninstall" ? $"Remove {app.Name} and its installed files from this computer. Personal settings and data will be kept. Save your work and close the app before continuing." :
                $"{(action == "download" ? "Download" : "Download and install")} {app.Name} {plan!.Release.Version} from {app.Repository}.\n\nPackage: {plan.Asset.Name}\nSize: {Ui.Bytes(plan.Asset.Size)}\n\n{(plan.Asset.Sha256 != null ? "Airlift will verify the published SHA-256 digest." : "This release has no SHA-256 digest. Its download cannot be verified against a published hash.")}\n\n{(action != "download" && plan.Format == "forge-exe" ? "Forge will open its setup wizard. Review its license and installation options there. Windows may ask for administrator permission." : "")}";
            var silent = action == "update" && plan?.Format == "forge-exe" && OperatingSystem.IsWindows() && _manager.Installed(app) != null
                ? new CheckBox { Name = "SilentUpdate", Content = "Install this update silently", IsChecked = false } : null;
            var option = silent == null ? null : Ui.Stack(8, silent, Ui.MutedText("Skip the wizard and accept this release’s license using its default options. Keep the current installation folder. Save your work and close the app first. Windows may still request permission.", 11));
            if (!await Confirm(action == "uninstall" ? $"Uninstall {app.Name}?" : $"{(action == "download" ? "Download" : action == "update" ? "Update" : "Install")} {app.Name}?", message, action == "uninstall" ? "Uninstall app" : action == "download" ? "Download" : "Continue to installation", action == "uninstall", option)) return;
            _ = RunOperation(app, action, plan?.Asset.Sha256 == null, silent?.IsChecked == true); Navigate("Downloads");
        }
        catch (Exception e) { Notice(e.Message); }
    }
    private async Task RunOperation(CatalogApp app, string action, bool allowUnverified, bool silentUpdate = false)
    {
        try { await _manager.ExecuteAsync(app, action, allowUnverified, silentUpdate: silentUpdate); }
        catch (Exception e) { Notice(e.Message); }
        await Dispatcher.UIThread.InvokeAsync(Render);
    }
    private static Window Dialog(string title, double width) => new() { Title = title, Width = width, SizeToContent = SizeToContent.Height, MaxHeight = 800, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
    private async Task<bool> Confirm(string title, string message, string action, bool danger = false, Control? option = null)
    {
        var window = Dialog(title, 485); var body = Ui.Stack(22, Ui.Text(title, 23, weight: FontWeight.SemiBold), Ui.MutedText(message, 12));
        if (option != null) body.Children.Add(option);
        var buttons = Ui.Row(9, Ui.Button("Cancel", () => window.Close(false)), Ui.Button(action, () => window.Close(true), danger ? "danger" : "primary")); buttons.HorizontalAlignment = HorizontalAlignment.Right; body.Children.Add(buttons);
        window.Content = new Border { Child = body, Padding = new Thickness(28) }; return await window.ShowDialog<bool>(this);
    }
    private async Task OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo != "" || !uri.IsDefaultPort) { Notice("Only HTTPS web links are supported."); return; }
        await Launcher.LaunchUriAsync(uri);
    }
}

