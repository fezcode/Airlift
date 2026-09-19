using Airlift.Core;
using Airlift.Desktop;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Airlift.Tests.TestAppBuilder))]
namespace Airlift.Tests;
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
public sealed class DesktopTests
{
    [AvaloniaFact]
    public void AppGridFillsViewportAcrossResizesAndFilters()
    {
        using var manager = new PackageManager(new StateStore(CoreTests.TestDirectory()));
        var window = new MainWindow(manager, false); window.Show();
        foreach (var width in new[] { 1330, 1240, 960, 1600, 1220, 1330 })
        {
            window.Width = width; window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var grid = Assert.Single(window.GetVisualDescendants().OfType<AppGrid>());
            Assert.Equal(manager.Apps.Count, grid.Children.Count);
            var columns = grid.Children.Count(c => Math.Abs(c.Bounds.Y) < .1);
            Assert.Equal(Math.Clamp((int)((grid.Bounds.Width + 14) / 314), 1, 3), columns);
            Assert.InRange(Math.Abs(grid.Children[columns - 1].Bounds.Right - grid.Bounds.Width), 0, 1);
            Assert.InRange(grid.Children[0].Bounds.Width, 299, 700);
            Assert.True(grid.Children[columns].Bounds.Y >= grid.Children[0].Bounds.Bottom + 13);
        }
        SaveScreenshot(window, "grid-filled");
        window.GetVisualDescendants().OfType<TextBox>().Single().Text = "osXos";
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var filtered = Assert.Single(window.GetVisualDescendants().OfType<AppGrid>()); Assert.Single(filtered.Children);
        Assert.InRange(filtered.Children[0].Bounds.Width, 299, 400); // A partial final row keeps normal card widths.
        window.Close();
    }

    [AvaloniaFact]
    public async Task SelfUpdateShowsNotesAndOnlyClosesAfterSuccessfulHandoff()
    {
        if (HostPlatform.Os != "windows" || HostPlatform.Arch != "x64") return;
        var data = CoreTests.ForgeFixture(SelfUpdater.App.Id, "999.0.0");
        using var http = new HttpClient(new ReleaseHistoryTests.Handler(_ => new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(data) }));
        using var manager = new PackageManager(new StateStore(CoreTests.TestDirectory()), http);
        manager.Store.Put("releases", SelfUpdater.App.Id + ":stable", new ReleaseCache(SelfUpdateTests.Release(data), null, DateTimeOffset.UtcNow, null));
        var launcher = new FakeSelfUpdateLauncher();
        var main = new MainWindow(manager, false, launcher); main.Show(); main.Navigate("Settings"); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        main.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Update Airlift").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var dialog = Assert.Single(main.OwnedWindows); dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.Contains(dialog.GetVisualDescendants().OfType<SelectableTextBlock>(), t => ReadText(t) == "Improvements");
        SaveScreenshot(dialog, "self-update-review");
        var install = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Download & update");
        install.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 200 && !install.IsEnabled; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.Equal(1, launcher.Calls); Assert.True(main.IsVisible); Assert.True(dialog.IsVisible);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Setup was cancelled") == true);
        launcher.Fail = false;
        install.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 200 && main.IsVisible; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.Equal(2, launcher.Calls); Assert.False(main.IsVisible);
    }

    [AvaloniaFact]
    public async Task CancellingSelfUpdateDownloadLeavesAirliftOpen()
    {
        if (HostPlatform.Os != "windows" || HostPlatform.Arch != "x64") return;
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = false;
        using var http = new HttpClient(new AsyncHandler(_ => { started = true; return pending.Task; }));
        using var manager = new PackageManager(new StateStore(CoreTests.TestDirectory()), http);
        manager.Store.Put("releases", SelfUpdater.App.Id + ":stable", new ReleaseCache(SelfUpdateTests.Release([1]), null, DateTimeOffset.UtcNow, null));
        var launcher = new FakeSelfUpdateLauncher();
        var main = new MainWindow(manager, false, launcher); main.Show(); main.Navigate("Updates"); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        main.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Update Airlift").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var dialog = Assert.Single(main.OwnedWindows); dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Download & update").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 200 && !started; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.True(started); dialog.Close();
        pending.TrySetCanceled();
        for (var i = 0; i < 10; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.Equal(0, launcher.Calls); Assert.True(main.IsVisible);
        using (manager.Store.AcquireOperationLock()) { } // Cancellation releases the workspace operation lock.
        main.Close();
    }

    [AvaloniaFact]
    public void AirliftUpdateIsVisibleWithoutAnyCatalogUpdates()
    {
        using var manager = new PackageManager(new StateStore(CoreTests.TestDirectory()));
        manager.Store.Put("releases", SelfUpdater.App.Id + ":stable", new ReleaseCache(SelfUpdateTests.Release([1]), null, DateTimeOffset.UtcNow, null));
        var window = new MainWindow(manager, false); window.Show(); window.Navigate("Updates"); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Update Airlift");
        Assert.True(window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "UpdateBadge").IsVisible);
        SaveScreenshot(window, "self-update-available"); window.Close();
    }

    private sealed class FakeSelfUpdateLauncher : ISelfUpdateLauncher
    {
        public bool Fail = true;
        public int Calls;
        public void Start(PreparedSelfUpdate update)
        {
            Assert.Equal(SelfUpdater.App.Id, ForgeInspector.Verify(update.File, update.Plan).Id);
            Calls++;
            if (Fail) throw new IOException("Setup was cancelled");
        }
    }

    [AvaloniaFact]
    public void EveryNativePageRendersWithoutMissingAssets()
    {
        using var manager = new PackageManager(new StateStore(CoreTests.TestDirectory()));
        var window = new MainWindow(manager, false); window.Show();
        var destination = Environment.GetEnvironmentVariable("AIRLIFT_SCREENSHOTS");
        foreach (var page in new[] { "Discover", "My library", "Updates", "Downloads", "Collections", "Sources", "Settings" })
        {
            window.Navigate(page); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => !string.IsNullOrWhiteSpace(t.Text));
            Assert.All(window.GetVisualDescendants().OfType<Image>(), image => Assert.NotNull(image.Source));
            if (destination != null)
            {
                Directory.CreateDirectory(destination); using var bitmap = new RenderTargetBitmap(new PixelSize(1330, 920)); bitmap.Render(window);
                bitmap.Save(Path.Combine(destination, page.Replace(" ", "-").ToLowerInvariant() + ".png"), new PngBitmapEncoderOptions());
            }
        }
        window.Close();
    }
    [AvaloniaFact]
    public void SearchNarrowsNativeCatalog()
    {
        using var manager = new PackageManager(new StateStore(CoreTests.TestDirectory())); var window = new MainWindow(manager, false); window.Show();
        window.GetVisualDescendants().OfType<TextBox>().Single().Text = "descry"; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var text = window.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text).ToList();
        Assert.Contains("Descry", text); Assert.DoesNotContain("Typewriter", text); window.Close();
    }
    [AvaloniaFact]
    public void IdentityHoverStaysTransparentAndNavigationContentIsCentered()
    {
        using var manager = new PackageManager(new StateStore(CoreTests.TestDirectory()));
        var app = manager.Apps.Single(a => a.Name == "Cogas");
        manager.Store.Put("installed", app.Id, new InstalledApp(app.Id, "0.15.1", "test", "fixture"));
        var window = new MainWindow(manager, false); window.Show(); window.Navigate("My library"); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("nav")))
        {
            var row = Assert.IsType<StackPanel>(button.Content);
            var offset = row.TranslatePoint(default, button)!.Value;
            Assert.InRange(Math.Abs(offset.Y + row.Bounds.Height / 2 - button.Bounds.Height / 2), 0, 1);
            Assert.Equal(VerticalAlignment.Center, button.VerticalContentAlignment);
        }
        var identity = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("identity"));
        var position = identity.TranslatePoint(new Point(20, 20), window)!.Value;
        window.MouseMove(position); Dispatcher.UIThread.RunJobs();
        Assert.True(identity.IsPointerOver);
        var presenter = identity.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
        Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color.A);
        window.MouseDown(position, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color.A);
        SaveScreenshot(window, "identity-hover");
        // Move away before releasing, so the test doesn't open a details dialog.
        window.MouseMove(new Point(10, 10)); window.MouseUp(new Point(10, 10), MouseButton.Left);
        window.GetVisualDescendants().OfType<TextBox>().Single().Focus();
        identity.Focus(NavigationMethod.Tab); Dispatcher.UIThread.RunJobs();
        Assert.True(identity.IsKeyboardFocusWithin); Assert.Equal(1, presenter.BorderThickness.Top);
        window.Close();
    }
    [AvaloniaFact]
    public async Task ReleaseHistoryRendersNotesAssetsAndLoadsOlderVersions()
    {
        var requests = 0;
        using var http = new HttpClient(new ReleaseHistoryTests.Handler(_ =>
        {
            requests++; var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("[" + ReleaseHistoryTests.ReleaseJson(requests == 1 ? "v2.0.0" : "v1.0.0") + "]") };
            if (requests == 1) response.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/next>; rel=\"next\""); return response;
        }));
        using var manager = new PackageManager(new StateStore(CoreTests.TestDirectory()), http);
        using (var json = System.Text.Json.JsonDocument.Parse(ReleaseHistoryTests.ReleaseJson("v2.0.0")))
            manager.Store.Put("releases", CoreTests.App.Id + ":stable", new ReleaseCache(GitHubClient.ParseRelease(json.RootElement, CoreTests.App.Repository), null, DateTimeOffset.UtcNow, null));
        var main = new MainWindow(manager, false); main.Show(); main.Navigate("Sources"); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        main.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Releases").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var window = Assert.Single(main.OwnedWindows); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var history = window.GetVisualDescendants().OfType<ReleaseHistoryView>().Single();
        await history.LoadAsync(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var text = window.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text).ToList();
        Assert.Contains("v2.0.0", text); Assert.Contains("Descry-Setup-2.0.0.exe", text);
        Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), t => ReadText(t).Contains("Faster startup"));
        var older = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Load older releases");
        older.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "v1.0.0"); Assert.False(older.IsVisible);
        SaveScreenshot(window, "release-history");
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Overview").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Latest release  ·  v2.0.0");
        SaveScreenshot(window, "app-overview"); window.Close(); main.Close();
    }
    [AvaloniaFact]
    public void CheckedGlyphContrastsWithAccentInNormalHoverPressedAndDisabledStates()
    {
        using var manager = new PackageManager(new StateStore(CoreTests.TestDirectory()));
        var window = new MainWindow(manager, false); window.Show(); window.Navigate("Settings"); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var check = window.GetVisualDescendants().OfType<CheckBox>().Single();
        var glyph = check.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(p => p.Name == "CheckGlyph");
        var square = check.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "NormalRectangle");
        void AssertContrast()
        {
            Assert.Equal(Color.Parse("#25311C"), Assert.IsAssignableFrom<ISolidColorBrush>(glyph.Fill).Color);
            Assert.True(Assert.IsAssignableFrom<ISolidColorBrush>(square.Background).Color.G > 180);
            Assert.Equal(1, glyph.Opacity);
        }
        AssertContrast(); var point = check.TranslatePoint(new Point(10, 15), window)!.Value;
        window.MouseMove(point); Dispatcher.UIThread.RunJobs(); AssertContrast();
        window.MouseDown(point, MouseButton.Left); Dispatcher.UIThread.RunJobs(); AssertContrast();
        window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs(); Assert.False(manager.Settings.CheckOnStartup); Assert.Equal(0, glyph.Opacity);
        check.Focus(NavigationMethod.Tab); window.KeyPress(Key.Space, Avalonia.Input.RawInputModifiers.None, PhysicalKey.Space, " "); window.KeyRelease(Key.Space, Avalonia.Input.RawInputModifiers.None, PhysicalKey.Space, " "); Dispatcher.UIThread.RunJobs();
        Assert.True(manager.Settings.CheckOnStartup); AssertContrast(); SaveScreenshot(window, "checkbox-contrast");
        check.IsEnabled = false; Dispatcher.UIThread.RunJobs(); AssertContrast(); window.Close();
    }
    [AvaloniaFact]
    public async Task PreferencesStayOpenAndSlowPreviewCannotOverwriteTheSelectedStableChannel()
    {
        var preview = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previewStarted = false;
        using var http = new HttpClient(new AsyncHandler(request =>
        {
            if (request.RequestUri!.Query == "?per_page=100") { previewStarted = true; return preview.Task; }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("[" + ReleaseHistoryTests.ReleaseJson("v2.0.0") + "]") });
        }));
        var store = new StateStore(CoreTests.TestDirectory()); var app = CoreTests.App;
        using (var json = System.Text.Json.JsonDocument.Parse(ReleaseHistoryTests.ReleaseJson("v2.0.0")))
            store.Put("releases", app.Id + ":stable", new ReleaseCache(GitHubClient.ParseRelease(json.RootElement, app.Repository), null, DateTimeOffset.UtcNow, null));
        store.Put("installed", app.Id, new InstalledApp(app.Id, "1.0.0", "fixture", "fixture"));
        using var manager = new PackageManager(store, http);
        var main = new MainWindow(manager, false); main.Show(); main.Navigate("My library"); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        main.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("identity")).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var dialog = Assert.Single(main.OwnedWindows); dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var pin = dialog.GetVisualDescendants().OfType<CheckBox>().Single(b => b.Name == "PinVersion");
        var channel = dialog.GetVisualDescendants().OfType<CheckBox>().Single(b => b.Name == "IncludePrereleases");
        void ClickCheck(CheckBox check)
        {
            check.BringIntoView(); dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var point = check.TranslatePoint(new Point(10, 15), dialog)!.Value;
            dialog.MouseMove(point); dialog.MouseDown(point, MouseButton.Left); dialog.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        }
        ClickCheck(pin); Assert.True(manager.Preferences(app).Pinned); Assert.True(dialog.IsVisible);
        ClickCheck(channel); Assert.True(previewStarted); Assert.True(manager.Preferences(app).IncludePrerelease); Assert.True(dialog.IsVisible);
        Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<Button>(), b => b.IsEnabled && (b.Content as string)?.StartsWith("Update to ") == true);
        ClickCheck(channel); Assert.False(manager.Preferences(app).IncludePrerelease); Assert.True(dialog.IsVisible);
        preview.SetResult(new(System.Net.HttpStatusCode.OK) { Content = new StringContent("[" + ReleaseHistoryTests.ReleaseJson("v3.0.0-rc.1", true) + "]") });
        for (var i = 0; i < 200 && (manager.Store.Get<ReleaseCache>("releases", app.Id + ":preview")?.Release == null ||
            !dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Latest release  ·  v2.0.0")); i++)
        { await Task.Delay(10); dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        Assert.True(dialog.IsVisible); Assert.False(channel.IsChecked); Assert.True(pin.IsChecked);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Latest release  ·  v2.0.0");
        if (HostPlatform.Os == "windows" && HostPlatform.Arch == "x64")
            Assert.Contains(dialog.GetVisualDescendants().OfType<Button>(), b => b.IsEnabled && b.Content as string == "Update to 2.0.0");
        else Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<Button>(), b => (b.Content as string)?.StartsWith("Update to ") == true);
        Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<Button>(), b => (b.Content as string)?.Contains("3.0.0-rc.1") == true);
        ClickCheck(channel); dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs(); // The cached preview should now update this same dialog.
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Latest release  ·  v3.0.0-rc.1");
        if (HostPlatform.Os == "windows" && HostPlatform.Arch == "x64")
            Assert.Contains(dialog.GetVisualDescendants().OfType<Button>(), b => b.IsEnabled && b.Content as string == "Update to 3.0.0-rc.1");
        SaveScreenshot(dialog, "preferences-open"); dialog.Close(); main.Close();
        var persisted = new StateStore(store.Root).Get<AppPreferences>("preferences", app.Id)!;
        Assert.True(persisted.Pinned); Assert.True(persisted.IncludePrerelease);
    }
    [AvaloniaFact]
    public void MarkdownRendersFormattingAndOnlyActivatesHttpsLinks()
    {
        const string source = "## Improvements\n\n**Bold** and *italic* with `inline code` and ~~removed~~.\n\n- Parent\n  - Child\n\n1. First\n2. Second\n\n> A useful quote\n\n```csharp\nvar version = 2;\n```\n\n| Platform | Status |\n| --- | --- |\n| Windows | Ready |\n\n[Changelog](https://github.com/fezcode/Descry/releases) [Blocked](file:///etc/passwd)\n\n<script>alert(1)</script>";
        var opened = new List<string>();
        var notes = new MarkdownNotes(source, "https://github.com/fezcode/Descry/releases/tag/v2", url => { opened.Add(url); return Task.CompletedTask; });
        var window = new Window { Width = 760, Height = 860, Content = new ScrollViewer { Content = notes, Padding = new Thickness(27) } }; window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var paragraphs = window.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
        Assert.Contains(paragraphs, p => ReadText(p) == "Improvements" && p.FontSize == 19);
        Assert.Contains(paragraphs.SelectMany(p => p.Inlines?.OfType<Span>() ?? []), s => s.FontWeight == FontWeight.Bold);
        Assert.Contains(paragraphs.SelectMany(p => p.Inlines?.OfType<Span>() ?? []), s => s.FontStyle == FontStyle.Italic);
        Assert.Contains(paragraphs, p => ReadText(p).Contains("var version = 2;"));
        Assert.Contains(paragraphs, p => ReadText(p) == "Ready"); Assert.Contains(paragraphs, p => ReadText(p) == "Child");
        var links = window.GetVisualDescendants().OfType<Button>().ToList(); Assert.Single(links);
        links[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Assert.Single(opened);
        Assert.False(MarkdownNotes.TryResolveLink(new Uri("https://github.com/a/b"), "javascript:alert(1)", out _));
        Assert.True(MarkdownNotes.TryResolveLink(new Uri("https://github.com/a/b"), "/a/b/issues/1", out var relative)); Assert.Equal("https://github.com/a/b/issues/1", relative!.AbsoluteUri);
        SaveScreenshot(window, "markdown-notes"); window.Close();
    }
    [AvaloniaFact]
    public async Task StartupRevalidatesAndShowsUpdatesBeforeTheLastRepositoryResponds()
    {
        var app = CoreTests.App; var store = new StateStore(CoreTests.TestDirectory());
        using (var json = System.Text.Json.JsonDocument.Parse(ReleaseHistoryTests.ReleaseJson("v1.0.0")))
            store.Put("releases", app.Id + ":stable", new ReleaseCache(GitHubClient.ParseRelease(json.RootElement, app.Repository), null, DateTimeOffset.UtcNow.AddHours(-1), null));
        var slow = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var http = new HttpClient(new AsyncHandler(request =>
        {
            calls++;
            if (calls == 1) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(ReleaseHistoryTests.ReleaseJson("v2.0.0")) });
            if (calls == 2) return slow.Task;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }));
        using var manager = new PackageManager(store, http, new InstalledFixture(app));
        var window = new MainWindow(manager); window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        for (var i = 0; i < 50 && !window.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "Review updates →"); i++)
        { await Task.Delay(100); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        Assert.Equal(2, calls); Assert.False(slow.Task.IsCompleted);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "1 app update available");
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Update");
        var badge = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "UpdateBadge"); Assert.True(badge.IsVisible);
        SaveScreenshot(window, "updates-discover");
        window.Navigate("Updates"); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Update available · 1.0.0 → 2.0.0");
        slow.SetResult(new(System.Net.HttpStatusCode.NotFound));
        for (var i = 0; i < 50 && !window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.StartsWith("Release check complete.") == true); i++)
        { await Task.Delay(20); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("1 update available.") == true);
        SaveScreenshot(window, "updates-available"); window.Close();
    }
    [AvaloniaFact]
    public void MissingReleasesAndPinnedAppsNeverClaimEverythingWasChecked()
    {
        using var manager = new PackageManager(new StateStore(CoreTests.TestDirectory())); var app = CoreTests.App;
        manager.Store.Put("installed", app.Id, new InstalledApp(app.Id, "1.0.0", "fixture", "fixture"));
        manager.Store.Put("releases", app.Id + ":stable", new ReleaseCache(null, null, DateTimeOffset.UtcNow, "No public release found for this channel."));
        var window = new MainWindow(manager, false); window.Show(); window.Navigate("Updates"); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Some updates couldn’t be checked.");
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "You’re all caught up.");
        manager.SetPreferences(app, new AppPreferences(Pinned: true)); window.Navigate("Updates"); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Your installed versions are pinned.");
        window.Close();
    }
    private sealed class InstalledFixture(CatalogApp installedApp) : IPlatformProvider
    {
        public InstalledApp? FindInstalled(CatalogApp app) => app.Id == installedApp.Id ? new(app.Id, "1.0.0", "fixture", "fixture") : null;
        public Task<InstalledApp> InstallAsync(PackagePlan plan, string file, CancellationToken ct) => throw new NotSupportedException();
        public Task UninstallAsync(CatalogApp app, InstalledApp installed, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request).WaitAsync(ct);
    }
    private static void SaveScreenshot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("AIRLIFT_SCREENSHOTS") is not { } destination) return;
        Directory.CreateDirectory(destination); using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height)); bitmap.Render(window);
        bitmap.Save(Path.Combine(destination, name + ".png"), new PngBitmapEncoderOptions());
    }
    private static string ReadText(SelectableTextBlock text) => text.Inlines?.Text ?? text.Text ?? "";
}


