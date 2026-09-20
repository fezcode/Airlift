using Airlift.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Airlift.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly PackageManager _manager;
    private readonly ISelfUpdateLauncher _selfUpdateLauncher;
    private readonly ContentControl _body = new();
    private readonly TextBlock _breadcrumb = Ui.MutedText("Workspace   ›   Discover", 12);
    private readonly TextBlock _notice = Ui.MutedText("Local-first. Connected to your public GitHub releases.", 11);
    private readonly TextBox _search = new() { PlaceholderText = "Search apps, tools, possibilities…    Ctrl K", Width = 330 };
    private readonly Button _clearSearch;
    private readonly Dictionary<string, Button> _nav = [];
    private readonly TextBlock _updateCount = Ui.Text("", 10, "#25311C", FontWeight.SemiBold);
    private Border? _updateBadge;
    private readonly Ellipse _footerDot = new() { Width = 7, Height = 7, Fill = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _footerState = Ui.MutedText("Updates not checked yet", 10);
    private readonly Grid _header;
    private readonly Border _status;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _page = "Discover", _category = "All apps", _query = "", _collection = "Everyday essentials";
    private bool _checking, _dirty, _list, _initializing, _hideInstalled;
    private DateTimeOffset _lastScheduledCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _lastInventoryCheck = DateTimeOffset.MinValue;
    private bool _refreshingInventory;
    // Content gutters widen once the window fills the screen, so a maximized layout breathes.
    private double Gutter => WindowState == WindowState.Maximized ? 44 : 32;
    public MainWindow(PackageManager manager, bool startServices = true, ISelfUpdateLauncher? selfUpdateLauncher = null)
    {
        _selfUpdateLauncher = selfUpdateLauncher ?? new SelfUpdateLauncher(manager.Store);
        _manager = manager; _initializing = startServices; Title = "Airlift by Fezcode"; Width = 1330; Height = 920; MinWidth = 960; MinHeight = 640;
        using (var icon = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Airlift/Assets/airlift.ico"))) Icon = new WindowIcon(icon);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Chrome.Extend(this);
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("228,*") };
        root.Children.Add(BuildSidebar());
        var main = new Grid { RowDefinitions = new RowDefinitions($"{Chrome.Height},70,*,38") }; Grid.SetColumn(main, 1); root.Children.Add(main);
        main.Children.Add(Chrome.TitleBar(this, null, resizable: true));
        _clearSearch = Ui.Button("", () => { _search.Text = ""; _search.Focus(); }, "clear");
        var clearGlyph = Ui.Glyph(Chrome.CloseGlyph, 8);
        clearGlyph.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, new Avalonia.Data.Binding("Foreground") { Source = _clearSearch });
        _clearSearch.Content = clearGlyph;
        ToolTip.SetTip(_clearSearch, "Clear search"); Avalonia.Automation.AutomationProperties.SetName(_clearSearch, "Clear search");
        _search.InnerRightContent = _clearSearch;
        _header = Ui.Between(_breadcrumb, _search); _header.Name = "HeaderBar"; _header.Margin = new Thickness(Gutter, 16);
        var headerBar = new Border { Child = _header, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
        Grid.SetRow(headerBar, 1); main.Children.Add(headerBar);
        var scroll = new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2); main.Children.Add(scroll);
        _status = new Border { Child = _notice, Padding = new Thickness(Gutter, 10), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
        Grid.SetRow(_status, 3); main.Children.Add(_status);
        Content = root;
        Chrome.KeepInsideScreen(this, root);
        // A maximized window gets wider gutters, so the layout breathes instead of stretching.
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) Render(); };
        _search.TextChanged += (_, _) =>
        {
            var query = _search.Text ?? "";
            if (query == _query) return; // Navigation clears search; that must not navigate back to Discover.
            _query = query; if (_page is not ("Discover" or "My library" or "Updates")) _page = "Discover"; Render();
        };
        KeyDown += (_, e) => { if (e.Key == Key.K && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))) { _search.Focus(); e.Handled = true; } };
        _search.KeyDown += (_, e) => { if (e.Key == Key.Escape && !string.IsNullOrEmpty(_search.Text)) { _search.Text = ""; e.Handled = true; } };
        _manager.Changed += OnChanged;
        _timer.Tick += async (_, _) =>
        {
            if (_dirty) { _dirty = false; Render(); }
            if (IsActive && DateTimeOffset.UtcNow - _lastInventoryCheck > TimeSpan.FromSeconds(5)) await RefreshInventoryView();
            if (!_checking && DateTimeOffset.UtcNow - _lastScheduledCheck > TimeSpan.FromHours(_manager.Settings.CheckIntervalHours)) await CheckReleases(false);
        };
        Closing += (_, e) => { if (_manager.HasActiveOperations) { e.Cancel = true; Notice("A package operation is active. Pause downloads or finish the installer before closing Airlift."); } };
        Closed += (_, _) => { _timer.Stop(); _manager.Changed -= OnChanged; };
        SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1) Render(); };
        if (startServices) Activated += async (_, _) => await RefreshInventoryView();
        if (startServices) Opened += async (_, _) =>
        {
            _lastScheduledCheck = DateTimeOffset.UtcNow; _timer.Start();
            try { await Task.Run(_manager.RefreshInventory); Render(); if (_manager.Settings.CheckOnStartup) await CheckReleases(true); }
            catch (Exception e) { Notice(e.Message); }
            finally { _initializing = false; Render(); }
        };
        Render();
    }
    private void OnChanged() => Dispatcher.UIThread.Post(() => _dirty = true);
    private async Task RefreshInventoryView()
    {
        if (_refreshingInventory || _manager.HasActiveOperations) return;
        _refreshingInventory = true; _lastInventoryCheck = DateTimeOffset.UtcNow;
        try { if (await Task.Run(_manager.RefreshInventory) && IsVisible) Render(); }
        catch (Exception error) { Notice("Could not refresh installed versions: " + error.Message); }
        finally { _refreshingInventory = false; }
    }
    private void Notice(string message) => Dispatcher.UIThread.Post(() => _notice.Text = message);
    public void Navigate(string page)
    {
        _page = page; _query = ""; _category = "All apps"; _search.Text = ""; Render();
    }
    private async Task CheckReleases(bool force)
    {
        if (_checking) return; _checking = true; _lastScheduledCheck = DateTimeOffset.UtcNow; Render(); Notice("Checking public GitHub releases…");
        try
        {
            await Task.Run(_manager.RefreshInventory); await _manager.CheckReleasesAsync(force);
            await CheckSelfUpdate(force);
            var updates = _manager.Apps.Count(_manager.HasUpdate) + (_manager.SelfUpdate.HasUpdate ? 1 : 0);
            var failed = _manager.Apps.Count(a => _manager.Release(a)?.Error != null) + (_manager.SelfUpdate.Cached?.Error != null ? 1 : 0);
            Notice($"Release check complete. {updates} update{(updates == 1 ? "" : "s")} available." + (failed > 0 ? $" {failed} repositories could not be checked; see Sources." : ""));
        }
        catch (Exception e) { Notice(e.Message); }
        finally { _checking = false; Render(); }
    }
    private Control BuildSidebar()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 15) };
        var logo = Brand.Mark(44);
        var heading = Ui.Stack(31,
            Ui.Row(12, logo, Ui.Stack(4, Ui.Text("airlift", 31, weight: FontWeight.Bold), Ui.MutedText("B Y  F E Z C O D E", 8))),
            Ui.Card(Ui.Stack(5, Ui.Text("Personal workspace", 12), Ui.MutedText("Your apps. Your space.", 10)), 13));
        // The sidebar reaches the top edge, so its brand block stands in for a title bar. The
        // platform finds that region by hit-testing, which a panel without a background fails.
        var caption = new Border { Name = "SidebarDrag", Background = Brushes.Transparent, Padding = new Thickness(16, 24, 16, 0), Child = heading };
        Chrome.Draggable(caption);
        grid.Children.Add(caption);
        var navigation = Ui.Stack(5); navigation.Margin = new Thickness(16, 20, 16, 12);
        navigation.Children.Add(Ui.MutedText("W O R K S P A C E", 9));
        foreach (var name in new[] { "Discover", "My library", "Updates", "Downloads", "Collections", "Sources" })
        {
            if (name == "Collections") { var label = Ui.MutedText("O R G A N I Z E", 9); label.Margin = new Thickness(0, 25, 0, 6); navigation.Children.Add(label); }
            var button = Brand.Navigation(name, () => Navigate(name)); _nav[name] = button; navigation.Children.Add(button);
            if (name == "Updates")
            {
                _updateBadge = new Border { Name = "UpdateBadge", Child = _updateCount, Background = Ui.Lime, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2), IsVisible = false };
                ((StackPanel)button.Content!).Children.Add(_updateBadge);
            }
        }
        var ecosystem = Ui.Stack(5, Ui.Text("Built for your ecosystem", 11), Ui.MutedText("Powered by Forge", 10));
        ecosystem.Margin = new Thickness(0, 20, 0, 0); navigation.Children.Add(ecosystem);
        var navigationScroll = new ScrollViewer { Name = "SidebarNavigation", Content = navigation,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, ClipToBounds = true };
        Grid.SetRow(navigationScroll, 1); grid.Children.Add(navigationScroll);
        var settings = Brand.Navigation("Settings", () => Navigate("Settings")); _nav["Settings"] = settings;
        var bottom = Ui.Stack(12, Ui.Separator(), settings, VersionCard());
        bottom.Margin = new Thickness(16, 0, 16, 0); bottom.Name = "SidebarFooter"; Grid.SetRow(bottom, 2); grid.Children.Add(bottom);
        return new Border { Child = grid, Background = Brush.Parse("#151715"), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 1, 0) };
    }
    // The footer used to be a decorative profile chip. It now reports the running version and
    // whether a newer Airlift release is waiting, and opens Updates when one is.
    private Control VersionCard()
    {
        var card = Ui.Button("", () => Navigate("Updates"), "version");
        card.HorizontalAlignment = HorizontalAlignment.Stretch; card.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        card.Content = Ui.Stack(8,
            Ui.Between(Ui.Row(10, Brand.Mark(22), Ui.Text("Airlift", 12, weight: FontWeight.SemiBold)), Ui.MutedText(AppVersion.Current, 10)),
            Ui.Row(8, _footerDot, _footerState));
        Avalonia.Automation.AutomationProperties.SetName(card, "Airlift version and update status");
        return card;
    }
    private void RenderVersionCard()
    {
        var cache = _manager.SelfUpdate.Cached;
        var (text, colour) = _checkingSelf ? ("Checking for updates…", "#92988D")
            : _manager.SelfUpdate.HasUpdate ? ($"Version {cache!.Release!.Version} available", "#D7F59A")
            : (_selfCheckError ?? cache?.Error) != null ? ("Update check unavailable", "#92988D")
            : cache?.Release != null ? ("Up to date", "#92988D")
            : ("Updates not checked yet", "#92988D");
        _footerState.Text = text; _footerState.Foreground = Brush.Parse(colour);
        _footerDot.Fill = Brush.Parse(_manager.SelfUpdate.HasUpdate ? "#D7F59A" : "#4E5A44");
    }
    private void Render()
    {
        _header.Margin = new Thickness(Gutter, 16); _status.Padding = new Thickness(Gutter, 10);
        _clearSearch.IsVisible = !string.IsNullOrEmpty(_search.Text);
        RenderVersionCard();
        foreach (var (name, button) in _nav) button.Classes.Set("active", name == _page);
        var updateCount = _manager.Apps.Count(_manager.HasUpdate) + (_manager.SelfUpdate.HasUpdate ? 1 : 0); _updateCount.Text = updateCount.ToString();
        if (_updateBadge != null) _updateBadge.IsVisible = updateCount > 0;
        _breadcrumb.Text = "Workspace   ›   " + _page;
        var titles = new Dictionary<string, (string Title, string Sub)>
        {
            ["Discover"] = ("Good tools. Great possibilities.", "Discover thoughtfully built apps. Make your computer feel like yours."),
            ["My library"] = ("Your apps, all together.", "Manage versions, pin your favorites, and make room for what’s next."),
            ["Updates"] = ("Keep a good thing going.", "Review published releases and update on your terms."),
            ["Downloads"] = ("Everything in motion.", "Every download, installation, and update in one place."),
            ["Collections"] = ("A better setup starts here.", "Thoughtful combinations of apps, ready for your workflow."),
            ["Sources"] = ("Know where your apps come from.", "Public GitHub repositories. Direct from the people who build your tools."),
            ["Settings"] = ("Make Airlift your own.", "A few thoughtful defaults. The rest is up to you.")
        };
        var (title, subtitle) = titles[_page];
        var heading = Ui.Stack(9, Ui.MutedText(_page == "Discover" ? "A LITTLE DISCOVERY GOES A LONG WAY" : "YOUR WORKSPACE / " + _page.ToUpperInvariant(), 9),
            Ui.Text(title, 26, weight: FontWeight.SemiBold), Ui.MutedText(subtitle, 12));
        var platform = Ui.Card(Ui.Text($"{HostPlatform.Os.ToUpperInvariant()}   ·   {HostPlatform.Arch}", 10), 10); platform.VerticalAlignment = VerticalAlignment.Center;
        var content = Ui.Stack(21, Ui.Between(heading, platform)); content.Margin = new Thickness(Gutter, 30, Gutter, 22);
        switch (_page)
        {
            case "Discover": case "My library": case "Updates": BuildCatalog(content); break;
            case "Downloads": BuildDownloads(content); break;
            case "Collections": BuildCollections(content); break;
            case "Sources": BuildSources(content); break;
            case "Settings": BuildSettings(content); break;
        }
        content.Children.Add(Ui.Separator()); content.Children.Add(Ui.Between(Ui.MutedText($"●  {_manager.Apps.Count} apps  ·  GitHub Releases", 10), Ui.MutedText("A little lift for your everyday.  ↗", 10)));
        _body.Content = content;
    }
    private Control Hero()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), MinHeight = 290, ClipToBounds = true };
        var copy = Ui.Stack(13, Ui.Text("✦  THE FEZCODE COLLECTION", 10, "#B8D68F"), Ui.Text("Small apps.\nA big lift.", 44, "#EDF4DF", FontWeight.SemiBold),
            Ui.MutedText("Less friction. More doing. Meet independent tools\nthat make the everyday a little extraordinary.", 12),
            Ui.Button("Find your essentials     →", () => Navigate("Collections"), "primary"), Ui.MutedText("—   Thoughtfully made. Yours to explore.", 9)); copy.HorizontalAlignment = HorizontalAlignment.Left; copy.Margin = new Thickness(30, 26);
        grid.Children.Add(copy);
        var art = new Canvas { ClipToBounds = true, MinWidth = 290 }; Grid.SetColumn(art, 1); grid.Children.Add(art);
        foreach (var diameter in new[] { 240d, 340d, 440d })
        {
            var orbit = new Ellipse { Width = diameter, Height = diameter, Stroke = Brush.Parse("#354529"), StrokeThickness = 1 }; Canvas.SetLeft(orbit, 215 - diameter / 2); Canvas.SetTop(orbit, 165 - diameter / 2); art.Children.Add(orbit);
        }
        var mark = Brand.Mark(120); mark.RenderTransform = new RotateTransform(-9); Canvas.SetLeft(mark, 155); Canvas.SetTop(mark, 102); art.Children.Add(mark);
        foreach (var (app, x, y) in new[] { (_manager.Apps[0], 92d, 40d), (_manager.Apps[2], 318d, 72d), (_manager.Apps[4], 96d, 230d) })
        { var icon = Ui.Icon(app, 57); Canvas.SetLeft(icon, x); Canvas.SetTop(icon, y); art.Children.Add(icon); }
        var star = Ui.Text("✦", 27, "#BAD491"); Canvas.SetLeft(star, 344); Canvas.SetTop(star, 242); art.Children.Add(star);
        var hero = Ui.Card(grid, 0);
        hero.Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative), GradientStops = [new GradientStop(Color.Parse("#252F1E"), 0), new GradientStop(Color.Parse("#1B2418"), 1)] };
        return hero;
    }
    private void BuildCatalog(StackPanel content)
    {
        var updateCount = _manager.Apps.Count(_manager.HasUpdate);
        if (_page == "Updates" || _manager.SelfUpdate.HasUpdate) content.Children.Add(SelfUpdateCard());
        if (_page is "Discover" or "My library" && updateCount > 0)
            content.Children.Add(Ui.Card(Ui.Between(Ui.Stack(6, Ui.Text($"{updateCount} app update{(updateCount == 1 ? "" : "s")} available", 16, "#D7F59A", FontWeight.SemiBold), Ui.MutedText("New releases are ready for your installed apps. Review what’s changed.", 11)), Ui.Button("Review updates →", () => Navigate("Updates"), "primary")), 18, "#232E1C"));
        if (_page == "Discover" && string.IsNullOrWhiteSpace(_query))
        {
            content.Children.Add(Hero()); content.Children.Add(Ui.Between(Ui.MutedText($"◇  {_manager.Apps.Count} independent apps     ·     Your setup, your control     ·     Made by Fezcode", 11), Ui.Button("Meet your source  ↗", () => Navigate("Sources"), "link")));
        }
        var visible = _manager.Apps.Where(a => (_category == "All apps" || a.Category == _category) && $"{a.Name} {a.Description} {a.Category}".Contains(_query, StringComparison.OrdinalIgnoreCase));
        if (_page == "My library") visible = visible.Where(a => _manager.Installed(a) != null);
        if (_page == "Updates") visible = visible.Where(_manager.HasUpdate);
        var matching = visible.ToList();
        // Discover is for finding apps, so it can drop the ones already on this computer.
        var hiding = _page == "Discover" && _hideInstalled;
        var apps = hiding ? matching.Where(a => _manager.Installed(a) == null).ToList() : matching;
        if (_page == "Updates" && !_checking && !_initializing)
        {
            var missing = _manager.Apps.Count(a => _manager.Installed(a) != null && !_manager.Preferences(a).Pinned && (_manager.Release(a)?.Release == null || _manager.Release(a)?.Error != null));
            if (missing > 0) content.Children.Add(Ui.Card(Ui.Between(Ui.Stack(5, Ui.Text($"{missing} installed apps couldn’t be checked", 14), Ui.MutedText("Their repositories have no available release data or returned an error.", 11)), Ui.Button("See sources →", () => Navigate("Sources"), "link")), 16));
        }
        var controls = Ui.Row(7);
        if (_page == "Discover")
        {
            var hide = Ui.Button("Hide installed", () => { _hideInstalled = !_hideInstalled; Render(); }, "chip");
            hide.Classes.Set("active", _hideInstalled);
            Avalonia.Automation.AutomationProperties.SetName(hide, _hideInstalled ? "Show installed apps" : "Hide installed apps");
            controls.Children.Add(hide);
        }
        controls.Children.Add(Ui.Button(_list ? "Grid" : "List", () => { _list = !_list; Render(); }));
        controls.Children.Add(Ui.AsyncButton(_checking ? "Checking…" : "↻ Check releases", () => CheckReleases(true), enabled: !_checking));
        content.Children.Add(Ui.Between(Ui.Text(($"{(_page == "Discover" ? "Find your next favorite" : _page == "Updates" ? "Updates for your apps" : "Installed apps")}   {apps.Count}"), 18, weight: FontWeight.SemiBold), controls));
        var categories = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var category in new[] { "All apps", "Productivity", "Developer tools", "Creative", "Media", "Utilities", "Gaming" })
        { var button = Ui.Button(category, () => { _category = category; Render(); }, "chip"); button.Classes.Set("active", _category == category); categories.Children.Add(button); }
        content.Children.Add(categories);
        if (apps.Count == 0 && _page == "Updates")
        {
            var installed = _manager.Apps.Where(a => _manager.Installed(a) != null).ToList();
            var eligible = installed.Where(a => !_manager.Preferences(a).Pinned).ToList();
            var unknown = eligible.Count(a => _manager.Release(a)?.Release == null || _manager.Release(a)?.Error != null);
            var pinned = installed.Count - eligible.Count;
            var title = _initializing || _checking ? "Checking for updates…" : updateCount > 0 ? "No updates match these filters." : installed.Count == 0 ? "No installed apps found yet." : unknown > 0 ? "Some updates couldn’t be checked." : eligible.Count == 0 ? "Your installed versions are pinned." : "You’re all caught up.";
            if (title == "You’re all caught up." && _manager.SelfUpdate.HasUpdate) title = "Your other apps are up to date.";
            var detail = _initializing || _checking ? "Updates appear here as each repository responds." : unknown > 0 ? $"{unknown} installed apps have missing or unavailable release data. Check releases again or see Sources for details." : installed.Count == 0 ? "Apps installed with Forge appear here when a newer release is available." : "No newer releases were found for the apps checked.";
            if (pinned > 0) detail += $" {pinned} pinned app{(pinned == 1 ? " is" : "s are")} excluded from updates.";
            content.Children.Add(Empty(title, detail));
        }
        else if (apps.Count == 0 && hiding && matching.Count > 0)
            content.Children.Add(Empty("You already have them all.", $"All {matching.Count} apps here are installed. Turn off Hide installed to see them again."));
        else if (apps.Count == 0) content.Children.Add(Empty(_page == "My library" ? "Your next setup starts with one app." : "No matches this time.", "Try the catalog, another category, or check GitHub for new releases."));
        else if (_list) { foreach (var app in apps) content.Children.Add(AppCard(app, true)); }
        else content.Children.Add(Cards(apps));
        if (_page == "Discover") content.Children.Add(Ui.Card(Ui.Between(Ui.Stack(7, Ui.Text("A fresh start, without the setup.", 15), Ui.MutedText("Your favorite apps, bundled into collections. Get back to what matters.", 11)), Ui.Button("Explore collections  →", () => Navigate("Collections"), "link"))));
    }
    private Control Cards(IEnumerable<CatalogApp> apps)
    {
        var panel = new AppGrid();
        foreach (var app in apps) panel.Children.Add(AppCard(app));
        return panel;
    }
    private Control AppCard(CatalogApp app, bool list = false)
    {
        var installed = _manager.Installed(app); var release = _manager.Release(app)?.Release;
        var identity = Ui.AsyncButton("", () => Details(app), "identity"); identity.Content = Ui.Row(11, Ui.Icon(app, 44), Ui.Stack(4, Ui.Text(app.Name, 14, weight: FontWeight.SemiBold), Ui.MutedText(app.Category, 10)));
        Avalonia.Automation.AutomationProperties.SetName(identity, $"About {app.Name}");
        var available = false; string? size = null;
        try { var plan = _manager.Plan(app); available = true; size = Ui.Bytes(plan.Asset.Size); } catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { }
        var hasUpdate = _manager.HasUpdate(app);
        var action = Ui.AsyncButton(hasUpdate ? "Update" : installed != null ? "Manage" : available ? "+ Get" : "Details", () => Details(app), hasUpdate ? "primary" : "", enabled: !_manager.IsBusy(app));
        var description = Ui.MutedText(app.Description, 11); description.MinHeight = list ? 0 : 48; description.LineHeight = 18;
        var version = hasUpdate ? $"Update available · {installed!.Version} → {release!.Version}" : installed != null ? $"Installed {installed.Version}" : release != null ? $"v{release.Version}" : "Release not checked";
        Control card;
        if (list)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("210,*,140,Auto"), ColumnSpacing = 20 };
            row.Children.Add(identity); row.Children.Add(description); Grid.SetColumn(description, 1);
            var metadata = Ui.Stack(4, Ui.MutedText(version, 10), Ui.MutedText(size ?? "", 10)); metadata.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(metadata); Grid.SetColumn(metadata, 2);
            row.Children.Add(action); Grid.SetColumn(action, 3); card = row;
        }
        else card = Ui.Stack(13, Ui.Between(identity, action), description,
            new Border { Background = Ui.Line, Height = 1 }, Ui.Between(Ui.MutedText(version + (_manager.Preferences(app).Pinned ? " · pinned" : ""), 10), Ui.MutedText(size ?? "", 10)));
        var surface = Ui.Card(card, 17); surface.ClearValue(Border.BorderBrushProperty); surface.Classes.Add("app-card"); return surface;
    }
    private static Control Empty(string title, string subtitle) => Ui.Card(Ui.Stack(15, Ui.Text(title, 21), Ui.MutedText(subtitle, 12)), 35);
}
