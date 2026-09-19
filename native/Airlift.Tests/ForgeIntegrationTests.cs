using Airlift.Core;
using System.Diagnostics;
using System.Security.Cryptography;
using Xunit;

namespace Airlift.Tests;
public sealed class ForgeIntegrationTests
{
    [Fact]
    public async Task DisposableForgeInstallUpgradeAndUninstall()
    {
        var root = Environment.GetEnvironmentVariable("AIRLIFT_FORGE_FIXTURE");
        if (root == null || !OperatingSystem.IsWindows()) return; // Explicit opt-in; never touches a real catalog app.
        root = Path.GetFullPath(root);
        if (!root.EndsWith(Path.Combine("artifacts", "forge-fixture"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture must be inside the named artifacts/forge-fixture directory.");
        var app = CoreTests.App with { Id = "com.fezcode.airlift.integration", Name = "Airlift Integration Fixture" };
        var store = new StateStore(Path.Combine(root, "state")); var provider = new WindowsForgeProvider(store, new FixtureRunner());
        if (provider.FindInstalled(app) != null) throw new InvalidOperationException("A prior fixture is installed; review it before rerunning.");
        try
        {
            foreach (var version in new[] { "1.0.0", "1.1.0" })
            {
                var file = Path.Combine(root, "dist", $"Fixture-Setup-{version}.exe"); var bytes = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
                var asset = new ReleaseAsset(1, Path.GetFileName(file), bytes.Length, "https://github.com/fezcode/test/releases/download/v" + version + "/fixture.exe", Convert.ToHexString(SHA256.HashData(bytes)));
                var release = new AppRelease("v" + version, version, "https://github.com/fezcode/test/releases", "", DateTimeOffset.UtcNow, false, [asset]);
                var plan = new PackagePlan(app, release, asset, "windows", "x64", "forge-exe");
                var installed = await provider.InstallAsync(plan, file, TestContext.Current.CancellationToken);
                Assert.Equal(version, installed.Version); Assert.True(File.Exists(Path.Combine(installed.Directory, "fixture.txt")));
            }
            var current = provider.FindInstalled(app)!;
            await provider.UninstallAsync(app, current, TestContext.Current.CancellationToken); Assert.Null(provider.FindInstalled(app)); Assert.False(File.Exists(Path.Combine(current.Directory, "fixture.txt")));
        }
        finally { if (provider.FindInstalled(app) is { } remaining) await provider.UninstallAsync(app, remaining, CancellationToken.None); }
    }
    private sealed class FixtureRunner : IProcessRunner
    {
        public async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, bool elevated, string workingDirectory)
        {
            if (elevated) throw new InvalidOperationException("The disposable fixture must never request elevation.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory };
            for (var i = 0; i < arguments.Count; i++)
            {
                start.ArgumentList.Add(arguments[i]);
                if (arguments[i] is "--user-localappdata" or "--user-appdata")
                { start.ArgumentList.Add(Path.Combine(Environment.GetEnvironmentVariable("AIRLIFT_FORGE_FIXTURE")!, "profile", arguments[i] == "--user-localappdata" ? "local" : "roaming")); i++; }
            }
            if (!arguments.Contains("--silent")) start.ArgumentList.Add("--silent");
            using var process = Process.Start(start)!; await process.WaitForExitAsync(TestContext.Current.CancellationToken); return process.ExitCode;
        }
    }
}
