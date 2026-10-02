using System.Diagnostics;
using System.Text.Json;

namespace AnotherDSHL.Services;

internal sealed record DshRuntime(string Version, string Entry);

internal sealed class DshRuntimeService
{
    private readonly string _root;
    private readonly bool _reuseExisting;
    private readonly string? _npmCache;
    public DshRuntimeService(string? root = null, bool reuseExisting = true, string? npmCache = null)
    {
        _root = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnotherDSHL", "Runtimes"));
        _reuseExisting = reuseExisting;
        _npmCache = npmCache;
    }

    public async Task<DshRuntime> PrepareAsync(string version, PackEngineTools tools, NpmRegistry registry,
        Action<string>? log, CancellationToken token = default)
    {
        if (!DshVersionService.IsSafeVersion(version)) throw new ArgumentException("DSH 版本无效。");
        var destination = Path.Combine(_root, version);
        if (TryRead(destination, version) is { } cached)
        {
            log?.Invoke($"[ADL] 复用已安装的 DSH v{version}，跳过 npm 安装。");
            return cached;
        }
        if (_reuseExisting && FindExisting(version, tools) is { } existing)
        {
            log?.Invoke($"[ADL] 复用本机已有的完整 DSH v{version}，跳过重复下载：{existing.Entry}");
            return existing;
        }
        Directory.CreateDirectory(_root);
        var staging = Path.Combine(_root, ".staging-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(staging);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(staging, "package.json"), "{\"private\":true}", token);
            var arguments = new[] { "install", "--prefix", staging, "--no-audit", "--no-fund", $"@deepseek-ai/dsh@{version}" };
            log?.Invoke($"[ADL] 安装 DSH v{version} · {registry.Name}；完成后缓存供下次直接启动。");
            try { await NpmProcessRunner.RunAsync(tools.Node, tools.NpmCli, arguments, staging, registry, log, token, npmCache: _npmCache); }
            catch (Exception ex) when (registry != NpmRegistryService.Official && ex is not OperationCanceledException)
            {
                log?.Invoke($"[ADL] {registry.Name} 安装失败：{ex.Message}；使用 npm 官方源重试。");
                await NpmProcessRunner.RunAsync(tools.Node, tools.NpmCli, arguments, staging, NpmRegistryService.Official, log, token, npmCache: _npmCache);
            }
            if (TryRead(staging, version) is null) throw new InvalidDataException("DSH 安装结果与指定版本不符，或启动入口缺失。");
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(destination))
            {
                // A failed or incomplete cache is preserved for diagnosis; never replace another process's valid cache.
                if (TryRead(destination, version) is { } concurrent) return concurrent;
                Directory.Move(destination, destination + ".invalid-" + Guid.NewGuid().ToString("N"));
            }
            Directory.Move(staging, destination);
            return TryRead(destination, version)!;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static DshRuntime? TryRead(string directory, string expectedVersion)
    {
        try
        {
            var package = Path.Combine(directory, "node_modules", "@deepseek-ai", "dsh");
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "package.json")));
            if (document.RootElement.GetProperty("version").GetString() != expectedVersion) return null;
            if (document.RootElement.TryGetProperty("dependencies", out var dependencies))
                foreach (var dependency in dependencies.EnumerateObject())
                    if (!File.Exists(Path.Combine(directory, "node_modules", dependency.Name.Replace('/', Path.DirectorySeparatorChar), "package.json"))) return null;
            var bin = document.RootElement.GetProperty("bin");
            var relative = bin.ValueKind == JsonValueKind.String ? bin.GetString() : bin.GetProperty("dsh").GetString();
            if (string.IsNullOrWhiteSpace(relative)) return null;
            var entry = Path.GetFullPath(Path.Combine(package, relative));
            if (!entry.StartsWith(package + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(entry)) return null;
            return new(expectedVersion, entry);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return null; }
    }

    private static DshRuntime? FindExisting(string version, PackEngineTools tools)
    {
        var global = TryRead(Path.GetDirectoryName(tools.Node)!, version);
        if (global is not null) return global;
        var cacheRoots = new[] { Environment.GetEnvironmentVariable("npm_config_cache"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm-cache") };
        foreach (var cache in cacheRoots.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct())
        {
            try
            {
                var npx = Path.Combine(cache!, "_npx");
                if (!Directory.Exists(npx)) continue;
                foreach (var directory in new DirectoryInfo(npx).EnumerateDirectories().OrderByDescending(info => info.LastWriteTimeUtc).Take(25))
                    if (TryRead(directory.FullName, version) is { } runtime) return runtime;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public static ProcessStartInfo CreateLaunchInfo(DshRuntime runtime, string node, string workspace, int port, NpmRegistry registry,
        string? dshHome = null, string? npmCache = null)
    {
        var start = new ProcessStartInfo(node) { WorkingDirectory = workspace, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { runtime.Entry, "web", "--no-open", "--port", port.ToString() }) start.ArgumentList.Add(argument);
        NpmRegistryService.Apply(start, registry);
        if (dshHome is not null)
        {
            Directory.CreateDirectory(dshHome);
            start.Environment["DSH_HOME"] = dshHome;
        }
        if (npmCache is not null) start.Environment["npm_config_cache"] = npmCache;
        return start;
    }

    public string? FindCachedVersion() => Directory.Exists(_root)
        ? new DirectoryInfo(_root).EnumerateDirectories().Where(info => DshVersionService.IsSafeVersion(info.Name))
            .OrderByDescending(info => info.LastWriteTimeUtc).Select(info => TryRead(info.FullName, info.Name)?.Version).FirstOrDefault(version => version is not null)
        : null;
}
