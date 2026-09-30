using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AnotherDSHL.Installer.Core;

namespace AnotherDSHL.Services;

public sealed record AppReleaseAsset([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("browser_download_url")] string Url);
public sealed record AppRelease([property: JsonPropertyName("tag_name")] string Tag, [property: JsonPropertyName("body")] string Notes,
    [property: JsonPropertyName("draft")] bool Draft, [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("assets")] List<AppReleaseAsset> Assets);
public sealed record PreparedUpdate(string Path, bool Differential, string Version = "", string Sha256 = "");

// GitHub/checksum/atomic-download workflow adapted from ZSnaper's AppUpdateService.
public sealed class AppUpdateService
{
    private const string Repository = "zzbuaoye-love/Another-DeepSeek-Harness-Launcher";
    private static readonly HttpClient Client = CreateClient();
    private readonly HttpClient _client;
    private readonly string _version;
    private readonly bool _installed;
    private readonly string _cacheRoot;
    public AppUpdateService(HttpClient? client = null, string? version = null, bool? installed = null, string? cacheRoot = null)
    {
        _client = client ?? Client;
        _version = version ?? CurrentVersion;
        _installed = installed ?? IsInstalled;
        _cacheRoot = cacheRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnotherDSHL", "Updates");
    }
    public static string CurrentVersion
    {
        get
        {
            try { return PayloadManifest.Read(AppContext.BaseDirectory).Version; }
            catch { return Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.2-beta"; }
        }
    }
    public static bool IsInstalled => string.Equals(new Installation().InstalledDirectory?.TrimEnd('\\'), AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AnotherDSHL-Updater/0.0.2");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
    public async Task<AppRelease?> CheckAsync(CancellationToken token = default, bool? includePrereleases = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _client.GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=100", deadline.Token);
        response.EnsureSuccessStatusCode();
        var releases = JsonSerializer.Deserialize<List<AppRelease>>(await response.Content.ReadAsStringAsync(deadline.Token)) ?? [];
        return SelectRelease(releases, _version, includePrereleases ?? _version.Contains('-'));
    }
    public static AppRelease? SelectRelease(IEnumerable<AppRelease> releases, string version, bool includePrereleases) =>
        releases.Where(r => !r.Draft && (includePrereleases || !r.Prerelease) && ReleaseVersion.IsValid(r.Tag) && CompareVersions(r.Tag, version) > 0 &&
            r.Assets is not null && r.Assets.Count(a => a.Name == "SHA256SUMS.txt") == 1 &&
            r.Assets.Count(a => a.Name.EndsWith("-win-x64-Setup.exe", StringComparison.OrdinalIgnoreCase)) == 1)
            .OrderByDescending(r => r.Tag, Comparer<string>.Create(CompareVersions)).FirstOrDefault();
    public static int CompareVersions(string left, string right) => ReleaseVersion.Compare(left, right);
    public async Task<PreparedUpdate> PrepareAsync(AppRelease release, IProgress<double>? progress = null, CancellationToken token = default)
    {
        string directory = Path.Combine(_cacheRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
        AppReleaseAsset Single(Func<AppReleaseAsset, bool> predicate) => release.Assets.Where(predicate).ToArray() is [var match] ? match : throw new InvalidDataException("发布版本缺少唯一的安装包或 SHA256SUMS.txt。");
        var checksums = Single(a => a.Name == "SHA256SUMS.txt");
        if (checksums.Size > 1024 * 1024) throw new InvalidDataException("校验文件过大。");
        string checksumPath = await Download(checksums, directory, null, token);
        var hashes = ParseChecksums(await File.ReadAllTextAsync(checksumPath, token));
        async Task<string> Verified(AppReleaseAsset asset)
        {
            if (!hashes.TryGetValue(asset.Name, out var expected)) throw new InvalidDataException("校验清单缺少 " + asset.Name);
            string path = await Download(asset, directory, progress, token);
            using var file = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("下载校验失败：" + asset.Name);
            return path;
        }
        var delta = release.Assets.Where(a => a.Name.EndsWith("-win-x64-Update.adup", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (_installed && delta.Length == 1)
        {
            string path = await Verified(delta[0]);
            var manifest = await Task.Run(() => UpdatePackageService.ValidatePackage(path, token), token);
            if (CompareVersions(manifest.Target.Version, release.Tag) != 0) throw new InvalidDataException("更新包版本与发布版本不一致。");
            if (manifest.From == _version) return SavePrepared(new PreparedUpdate(path, true, manifest.Target.Version, hashes[delta[0].Name]));
        }
        var setup = Single(a => a.Name.EndsWith("-win-x64-Setup.exe", StringComparison.OrdinalIgnoreCase));
        return SavePrepared(new PreparedUpdate(await Verified(setup), false, release.Tag.TrimStart('v'), hashes[setup.Name]));
        }
        catch { Installation.TryCleanup(directory); throw; }
    }

    public async Task<PreparedUpdate> PrepareLocalAsync(string source, CancellationToken token = default)
    {
        if (!_installed) throw new InvalidOperationException("差分更新需要安装版；当前版本请使用完整安装包。");
        string directory = Path.Combine(_cacheRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string snapshot = Path.Combine(directory, "local.adup");
            await using (var input = File.OpenRead(source))
            await using (var output = File.Create(snapshot)) await input.CopyToAsync(output, token);
            var manifest = await Task.Run(() => UpdatePackageService.ValidatePackage(snapshot, token), token);
            if (manifest.From != _version) throw new InvalidDataException($"此更新包适用于 {manifest.From}，当前版本为 {_version}。请使用完整安装包。");
            using var file = File.OpenRead(snapshot);
            return SavePrepared(new(snapshot, true, manifest.Target.Version, Convert.ToHexString(await SHA256.HashDataAsync(file, token))));
        }
        catch { Installation.TryCleanup(directory); throw; }
    }

    public PreparedUpdate? LoadPrepared()
    {
        try
        {
            var update = JsonSerializer.Deserialize<PreparedUpdate>(File.ReadAllText(Path.Combine(_cacheRoot, "pending.json")));
            if (update is null || (update.Differential && !_installed) || !ReleaseVersion.IsValid(update.Version) || CompareVersions(update.Version, _version) <= 0 ||
                update.Sha256 is not { Length: 64 } || !update.Sha256.All(Uri.IsHexDigit)) return null;
            string root = Path.GetFullPath(_cacheRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(update.Path).StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(update.Path) ||
                !Path.GetExtension(update.Path).Equals(update.Differential ? ".adup" : ".exe", StringComparison.OrdinalIgnoreCase)) return null;
            return update;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    private PreparedUpdate SavePrepared(PreparedUpdate update)
    {
        string temporary = Path.Combine(_cacheRoot, "pending-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(update));
            File.Move(temporary, Path.Combine(_cacheRoot, "pending.json"), true);
            return update;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task VerifyPreparedAsync(PreparedUpdate update, CancellationToken token = default)
    {
        if (update.Sha256.Length != 64) throw new InvalidDataException("已下载更新缺少校验信息，请重新准备更新。");
        await using var file = File.OpenRead(update.Path);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("已下载更新发生变化，请重新准备更新。");
    }
    public static Dictionary<string, string> ParseChecksums(string text)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit)) hashes.Add(parts[1].Trim().TrimStart('*'), parts[0]);
        }
        return hashes;
    }
    private async Task<string> Download(AppReleaseAsset asset, string directory, IProgress<double>? progress, CancellationToken token)
    {
        if (asset.Size < 0 || asset.Name != Path.GetFileName(asset.Name) || asset.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.Ordinal)) throw new InvalidDataException("发布资源地址无效。");
        string target = Path.Combine(directory, asset.Name), temporary = target + ".download";
        try
        {
            using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(token);
            long total = 0;
            await using (var output = File.Create(temporary))
            {
                byte[] buffer = new byte[81920]; int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    total += read;
                    if (total > asset.Size) throw new InvalidDataException("下载大小与发布信息不一致。");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    progress?.Report(total * 100d / Math.Max(1, asset.Size));
                }
            }
            if (total != asset.Size) throw new InvalidDataException("下载文件不完整。");
            File.Move(temporary, target, true); return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void Launch(PreparedUpdate update)
    {
        var start = new ProcessStartInfo(update.Differential ? Path.Combine(AppContext.BaseDirectory, "AnotherDSHL.Installer.exe") : update.Path) { UseShellExecute = false };
        if (update.Differential) { start.ArgumentList.Add("--update"); start.ArgumentList.Add("--package=" + update.Path); }
        _ = Process.Start(start) ?? throw new IOException("无法启动更新程序。");
    }
}
