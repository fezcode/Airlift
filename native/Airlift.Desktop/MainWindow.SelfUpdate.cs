using Airlift.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Airlift.Desktop;

public sealed partial class MainWindow
{
    private bool _checkingSelf;
    private string? _selfCheckError;

    private Control SelfUpdateCard()
    {
        var updater = _manager.SelfUpdate;
        var cache = updater.Cached;
        var text = _checkingSelf ? "Checking Airlift releases…" : _selfCheckError ?? cache?.Error ??
            (updater.HasUpdate ? $"Version {cache!.Release!.Version} is available" : cache?.Release != null ? "You’re running the latest Airlift version." : "Check for a new version of Airlift.");
        var actions = Ui.Row(8);
        if (updater.HasUpdate) actions.Children.Add(Ui.AsyncButton("Update Airlift", ReviewSelfUpdate, "primary", !_checkingSelf));
        actions.Children.Add(Ui.AsyncButton(_checkingSelf ? "Checking…" : "Check for Airlift updates", () => CheckSelfUpdate(true, announce: true), enabled: !_checkingSelf));
        var card = Ui.Stack(14,
            Ui.Row(12, Brand.Mark(40), Ui.Text(AppVersion.Display, 18)), Ui.MutedText(text, 11), actions,
            Ui.AsyncButton(SelfUpdater.Repository + " · Release notes ↗", () => OpenUrl($"https://github.com/{SelfUpdater.Repository}/releases"), "link"));
        return Ui.Card(card, 18, updater.HasUpdate ? p => p.CardAccent : p => p.Card);
    }

    private async Task CheckSelfUpdate(bool force, bool announce = false)
    {
        if (_checkingSelf) return;
        _checkingSelf = true; _selfCheckError = null; Render();
        try { await _manager.SelfUpdate.CheckAsync(force); }
        catch (Exception error) { _selfCheckError = error.Message; }
        finally { _checkingSelf = false; Render(); }
        if (!announce) return;
        // A check the person asked for owes them its outcome, not silence.
        var reason = _selfCheckError ?? _manager.SelfUpdate.Cached?.Error;
        if (reason == null) Notice(_manager.SelfUpdate.HasUpdate ? "A new version of Airlift is available." : "Airlift is up to date.");
        else { Notice("Airlift’s release check could not run. " + reason, problem: true); await Problem("Airlift could not check for updates", ReasonAdvice(reason), reason); }
    }

    private async Task ReviewSelfUpdate()
    {
        if (_manager.HasActiveOperations) { Notice("Finish or pause active package operations before updating Airlift."); return; }
        var release = _manager.SelfUpdate.Cached?.Release;
        if (release == null || !_manager.SelfUpdate.HasUpdate) return;
        PackagePlan? plan = null;
        try { plan = SelfUpdater.Plan(release, HostPlatform.Os, HostPlatform.Arch); }
        catch (NotSupportedException) { /* Other platforms retain release notes and the public download link. */ }
        var window = Dialog("Update Airlift", 700); window.SizeToContent = SizeToContent.Manual; window.Height = 700;
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        window.Closed += (_, _) => cancellation.Cancel();
        var status = Ui.MutedText(plan == null ? "There is no automatic installer for this platform in this release. Open its downloads to update manually." : "Setup will open after verification and Airlift will close. Keep “Open Airlift” selected in Setup to launch the installed version. A portable copy stays in its original folder.", 12);
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, IsVisible = false };
        var digestConsent = new CheckBox { Content = "Allow this update without a published SHA-256 digest", IsVisible = plan?.Asset.Sha256 == null && plan != null };
        var silent = new CheckBox { Name = "SilentSelfUpdate", Content = "Install this update silently and reopen Airlift", IsChecked = false,
            IsVisible = plan != null && _selfUpdateLauncher.SupportsSilentUpdate };
        silent.IsCheckedChanged += (_, _) => status.Text = silent.IsChecked == true
            ? "Airlift will close cleanly, accept this release’s license and update in its current installation folder without the wizard. It will reopen after success. Windows may still request permission."
            : "Setup will open after verification and Airlift will close. Keep “Open Airlift” selected in Setup to launch the installed version.";
        var cancel = Ui.Button("Cancel", window.Close);
        var install = Ui.Button("Download & update", () => { }, "primary", plan != null && plan.Asset.Sha256 != null);
        digestConsent.IsCheckedChanged += (_, _) => install.IsEnabled = plan != null && (plan.Asset.Sha256 != null || digestConsent.IsChecked == true);
        var running = false;
        install.Click += async (_, _) =>
        {
            if (running) return;
            running = true; install.IsEnabled = false; digestConsent.IsEnabled = false; silent.IsEnabled = false; progress.IsVisible = true;
            try
            {
                using var lease = _manager.Store.AcquireOperationLock();
                var prepared = await _manager.SelfUpdate.PrepareAsync(release, digestConsent.IsChecked == true,
                    (percent, message) => Dispatcher.UIThread.Post(() => { if (running && !token.IsCancellationRequested) { progress.Value = percent; status.Text = message; } }), token);
                token.ThrowIfCancellationRequested();
                if (_manager.HasActiveOperations) throw new InvalidOperationException("Finish or pause active package operations before updating Airlift.");
                _selfUpdateLauncher.Start(prepared with { Silent = silent.IsChecked == true });
                window.Close(); Close();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { status.Text = "Airlift is still open. " + error.Message; }
            finally
            {
                running = false; digestConsent.IsEnabled = true; silent.IsEnabled = true;
                install.IsEnabled = plan != null && (plan.Asset.Sha256 != null || digestConsent.IsChecked == true);
            }
        };
        var header = Ui.Stack(12, Ui.Row(14, Brand.Mark(48), Ui.Stack(5, Ui.Text("A little lift for Airlift", 25, weight: FontWeight.SemiBold), Ui.MutedText($"{AppVersion.Current} → {release.Version} · Stable release", 12))),
            Ui.MutedText(plan == null ? release.Tag : $"{plan.Asset.Name} · {Ui.Bytes(plan.Asset.Size)} · {(plan.Asset.Sha256 == null ? "No published digest" : "SHA-256 verified before launch")}", 11));
        var notes = new ScrollViewer { Content = new MarkdownNotes(string.IsNullOrWhiteSpace(release.Notes) ? "No release notes were published." : release.Notes, release.Url, OpenUrl), Margin = new Thickness(0, 20), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var buttons = Ui.Row(8, cancel, Ui.AsyncButton("Release downloads ↗", () => OpenUrl(release.Url), "link"), install); buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var footer = Ui.Stack(12, silent, status, progress, digestConsent, buttons);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(27) };
        layout.Children.Add(header); layout.Children.Add(notes); Grid.SetRow(notes, 1); layout.Children.Add(footer); Grid.SetRow(footer, 2);
        window.Content = layout; await window.ShowDialog(this);
    }
}
