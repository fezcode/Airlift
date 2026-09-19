using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Airlift.Core;

public sealed class Downloader(HttpClient http, StateStore store)
{
    public async Task<string> DownloadAsync(PackagePlan plan, Action<double, string> progress, CancellationToken ct)
    {
        var asset = plan.Asset;
        var directory = Path.Combine(store.Root, "downloads", plan.App.Id, asset.Id.ToString());
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, asset.Name);
        if (Path.GetFileName(destination) != asset.Name) throw new InvalidDataException("Unsafe asset filename.");
        if (File.Exists(destination))
        {
            if (await IsValidAsync(destination, asset, ct)) { progress(100, "Verified cached download"); return destination; }
            File.Delete(destination);
        }
        var partial = destination + ".part";
        // A changed GitHub asset ID gets a separate directory; a changed digest invalidates the partial.
        var identity = $"{asset.Id}|{asset.Size}|{asset.Sha256}|{asset.Url}";
        var stamp = partial + ".identity";
        if (!File.Exists(stamp) || await File.ReadAllTextAsync(stamp, ct) != identity) { if (File.Exists(partial)) File.Delete(partial); await File.WriteAllTextAsync(stamp, identity, ct); }
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > asset.Size) { File.Delete(partial); offset = 0; }
        if (offset < asset.Size)
        {
            var uri = GitHubClient.ValidateAssetUrl(asset.Url, plan.App.Repository);
            using var response = await SendFollowingRedirectsAsync(uri, offset, ct);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.From != offset || range.Length != asset.Size) throw new InvalidDataException("Server returned an inconsistent resume range.");
            }
            else offset = 0; // A server may ignore Range. Restart; never append a full response.
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.Read, 81920, true);
            var buffer = new byte[81920]; var total = offset; var lastReport = DateTime.UtcNow;
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct); idle.CancelAfter(TimeSpan.FromSeconds(60));
                var count = await input.ReadAsync(buffer, idle.Token); if (count == 0) break;
                total += count; if (total > asset.Size) throw new InvalidDataException("Download exceeds its declared size.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
                if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(200)) { progress(100d * total / asset.Size, $"{total / 1048576d:F1} / {asset.Size / 1048576d:F1} MB"); lastReport = DateTime.UtcNow; }
            }
            await output.FlushAsync(ct);
        }
        progress(100, "Checking file integrity");
        if (!await IsValidAsync(partial, asset, ct)) { File.Delete(partial); throw new InvalidDataException("Download size or SHA-256 mismatch. The file was discarded."); }
        File.Move(partial, destination, true); File.Delete(stamp);
        progress(100, asset.Sha256 != null ? "SHA-256 verified" : "Downloaded; GitHub did not publish a SHA-256 digest");
        return destination;
    }
    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(Uri uri, long offset, CancellationToken ct)
    {
        for (var hop = 0; hop < 6; hop++)
        {
            if (uri.Scheme != "https" || uri.UserInfo.Length > 0 || !uri.IsDefaultPort ||
                uri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com"))
                throw new InvalidDataException("Download redirected outside GitHub's asset hosts.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location is null) throw new InvalidDataException("Missing download redirect location.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue;
            }
            return response;
        }
        throw new InvalidDataException("Too many download redirects.");
    }
    public static async Task<bool> IsValidAsync(string path, ReleaseAsset asset, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != asset.Size) return false;
        if (asset.Sha256 is null) return true;
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        return hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
