using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnotherDSHL.Services;

public sealed record PackEngineProgress(string Stage, string Detail);
public sealed record PackInstallInfo(string Name, string Title, string Version, string Type,
    string DshVersion, string[] Profiles, string DefaultProfile, int BundleCount,
    int DependencyCount, int FileCount, string Sha256, long Size);
public sealed record PackEngineTools(string Node, string NpmCli, string? PnpmCli);
public sealed record InstalledPack(string Id, string Title, string Version, string SourceSha256, string DshVersion,
    string Profile, string HomeRelativePath, string RuntimeEntryRelativePath, string Workspace,
    DateTimeOffset InstalledAt)
{
    public string DisplayName => $"{Title} · v{Version} · {Profile}";
}

/// <summary>Runs the bundled core in a private Node child; the desktop manager is optional.</summary>
public sealed class PackForgeEngineService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex SafeId = new("^[a-z0-9-]{1,96}$", RegexOptions.CultureInvariant);
    public string InstancesRoot { get; }
    private readonly string _enginePath;

    public PackForgeEngineService(string? instancesRoot = null, string? enginePath = null)
    {
        InstancesRoot = Path.GetFullPath(instancesRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnotherDSHL", "Instances"));
        _enginePath = enginePath ?? Path.Combine(AppContext.BaseDirectory, "Resources", "PackForge", "engine.mjs");
    }

    public static PackEngineTools ResolveTools(string? node)
    {
        if (string.IsNullOrWhiteSpace(node) || !File.Exists(node))
            throw new InvalidOperationException("请先在工作区配置可用的 Node.js。");
        var nodeDirectory = Path.GetDirectoryName(node)!;
        var npm = Path.Combine(nodeDirectory, "node_modules", "npm", "bin", "npm-cli.js");
        if (!File.Exists(npm))
            npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "npm", "bin", "npm-cli.js");
        if (!File.Exists(npm)) throw new InvalidOperationException("未找到 npm，请检查工作区选择的 Node.js 安装目录。");
        var roots = new[] { nodeDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)).Distinct();
        var pnpm = roots.SelectMany(root => new[] { "pnpm.cjs", "pnpm.js" }.Select(name =>
                Path.Combine(root.Trim('"'), "node_modules", "pnpm", "bin", name)))
            .FirstOrDefault(File.Exists);
        return new(node, npm, pnpm);
    }

    public async Task<PackInstallInfo> InspectAsync(string source, PackEngineTools tools, CancellationToken token = default)
    {
        ValidateArchive(source);
        var result = await RunAsync(tools.Node, new { command = "inspect", source }, null, token);
        return result.Deserialize<PackInstallInfo>(JsonOptions) ?? throw new InvalidDataException("整合包预览结果为空。");
    }

    public string CreateInstanceId(PackInstallInfo info)
    {
        var name = Regex.Replace(info.Name.ToLowerInvariant(), "[^a-z0-9-]", "-").Trim('-');
        if (name.Length == 0) name = "pack";
        return name[..Math.Min(name.Length, 48)] + "-" + Guid.NewGuid().ToString("N")[..12];
    }

    public async Task<InstalledPack> InstallAsync(string source, PackInstallInfo info, string id,
        string dshVersion, string profile, string workspace, PackEngineTools tools,
        IProgress<PackEngineProgress>? progress = null, CancellationToken token = default)
    {
        if (!SafeId.IsMatch(id) || !DshVersionService.IsSafeVersion(dshVersion) ||
            !info.Profiles.Contains(profile, StringComparer.Ordinal) || profile.Equals("desktop", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("实例名称、DSH 版本或启动 Profile 不可用。");
        if (info.DshVersion.Length > 0 && info.DshVersion != dshVersion)
            throw new InvalidDataException($"此整合包要求 DSH {info.DshVersion}。");
        if (!Directory.Exists(workspace)) throw new DirectoryNotFoundException("请选择已存在的工作目录。");
        var destination = InstanceDirectory(id);
        if (Directory.Exists(destination)) throw new IOException("该实例已存在，请创建新的实例。");
        Directory.CreateDirectory(InstancesRoot);
        var staging = ResolveChild(InstancesRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var published = false;
        try
        {
            // Keep the confirmed package immutable throughout runtime preparation and import.
            var snapshot = Path.Combine(staging, "source.dspack");
            await using (var input = File.OpenRead(source))
            await using (var output = File.Create(snapshot))
                await input.CopyToAsync(output, token);
            ValidateArchive(snapshot);
            await using (var stream = File.OpenRead(snapshot))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
                if (stream.Length != info.Size || !hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("整合包在预览后发生了变化，请重新预览。");
            }
            var runtime = Path.Combine(staging, "runtime");
            Directory.CreateDirectory(runtime);
            await File.WriteAllTextAsync(Path.Combine(runtime, "package.json"), "{\"private\":true}", token);
            progress?.Report(new("runtime", $"准备 DSH {dshVersion} 运行环境…"));
            await RunNodeCommandAsync(tools.Node, tools.NpmCli,
                ["install", "--prefix", runtime, "--no-audit", "--no-fund", $"@deepseek-ai/dsh@{dshVersion}"],
                staging, progress, token);
            var packageDirectory = Path.Combine(runtime, "node_modules", "@deepseek-ai", "dsh");
            using var package = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(packageDirectory, "package.json"), token));
            var actualVersion = package.RootElement.GetProperty("version").GetString();
            if (actualVersion != dshVersion) throw new InvalidDataException("安装到的 DSH 版本与整合包要求不一致。");
            var bin = package.RootElement.GetProperty("bin");
            var entry = bin.ValueKind == JsonValueKind.String ? bin.GetString()! : bin.GetProperty("dsh").GetString()!;
            var runtimeEntry = ResolveChild(packageDirectory, entry);
            if (!File.Exists(runtimeEntry)) throw new FileNotFoundException("DSH 启动入口缺失。");

            var pnpm = tools.PnpmCli;
            if (pnpm is null)
            {
                progress?.Report(new("tools", "准备依赖安装工具…"));
                var toolDirectory = Path.Combine(staging, "tools");
                Directory.CreateDirectory(toolDirectory);
                await File.WriteAllTextAsync(Path.Combine(toolDirectory, "package.json"), "{\"private\":true}", token);
                await RunNodeCommandAsync(tools.Node, tools.NpmCli,
                    ["install", "--prefix", toolDirectory, "--no-audit", "--no-fund", "--ignore-scripts", "pnpm@10.17.1"],
                    staging, progress, token);
                pnpm = Path.Combine(toolDirectory, "node_modules", "pnpm", "bin", "pnpm.cjs");
            }
            if (!File.Exists(pnpm)) throw new FileNotFoundException("依赖安装工具未准备好。");
            var home = Path.Combine(staging, "home");
            await RunAsync(tools.Node, new { command = "install", source = snapshot, home, pnpmCli = pnpm, dshVersion }, progress, token);
            if (!File.Exists(Path.Combine(home, "profiles", profile, "package.json")))
                throw new InvalidDataException("安装未产生所选 Profile。");
            var installed = new InstalledPack(id, info.Title, info.Version, info.Sha256, dshVersion, profile,
                "home", Path.GetRelativePath(staging, runtimeEntry), Path.GetFullPath(workspace), DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(Path.Combine(staging, "instance.json"), JsonSerializer.Serialize(installed), token);
            File.Delete(snapshot);
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            published = true;
            progress?.Report(new("done", "安装完成，可以从首页选择此整合包启动。"));
            return installed;
        }
        finally
        {
            // Only the randomly created staging directory belongs to this operation.
            if (!published && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public IReadOnlyList<InstalledPack> LoadInstalled()
    {
        if (!Directory.Exists(InstancesRoot)) return [];
        var result = new List<InstalledPack>();
        foreach (var directory in Directory.EnumerateDirectories(InstancesRoot))
        {
            var id = Path.GetFileName(directory);
            if (!SafeId.IsMatch(id)) continue;
            try
            {
                var pack = JsonSerializer.Deserialize<InstalledPack>(File.ReadAllText(Path.Combine(directory, "instance.json")), JsonOptions);
                if (pack is null || pack.Id != id || !Regex.IsMatch(pack.SourceSha256, "^[a-fA-F0-9]{64}$") || !DshVersionService.IsSafeVersion(pack.DshVersion) ||
                    !Regex.IsMatch(pack.Profile, "^[a-z0-9-]+$") || pack.Profile == "desktop") continue;
                if (File.Exists(ResolveChild(directory, pack.RuntimeEntryRelativePath)) &&
                    File.Exists(Path.Combine(ResolveChild(directory, pack.HomeRelativePath), "profiles", pack.Profile, "package.json")))
                    result.Add(pack);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        }
        return result.OrderByDescending(pack => pack.InstalledAt).ToArray();
    }

    public ProcessStartInfo CreateLaunchInfo(InstalledPack pack, string node, int port)
    {
        if (port is < 1 or > 65535 || !File.Exists(node) || !Directory.Exists(pack.Workspace))
            throw new InvalidOperationException("Node.js、工作目录或 Web 端口不可用。");
        var directory = InstanceDirectory(pack.Id);
        var entry = ResolveChild(directory, pack.RuntimeEntryRelativePath);
        var home = ResolveChild(directory, pack.HomeRelativePath);
        if (!File.Exists(entry) || !File.Exists(Path.Combine(home, "profiles", pack.Profile, "package.json")))
            throw new InvalidOperationException("整合包实例不完整，请重新安装。");
        var start = new ProcessStartInfo(node)
        {
            WorkingDirectory = pack.Workspace, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { entry, "--profile", pack.Profile, "--no-open", "--port", port.ToString() })
            start.ArgumentList.Add(argument);
        start.Environment["DSH_HOME"] = home;
        start.Environment["PATH"] = Path.GetDirectoryName(node) + Path.PathSeparator +
            (start.Environment.TryGetValue("PATH", out var path) ? path : "");
        return start;
    }

    public Task<JsonElement> InspectProfileAsync(string source, string node, CancellationToken token = default) =>
        RunAsync(node, new { command = "inspectProfile", source }, null, token);

    public Task<JsonElement> ExportAsync(string source, string output, string dshVersion, string node,
        IProgress<PackEngineProgress>? progress, CancellationToken token = default) =>
        RunAsync(node, new { command = "export", source, output, dshVersion }, progress, token);

    private string InstanceDirectory(string id)
    {
        if (!SafeId.IsMatch(id)) throw new InvalidDataException("实例名称不安全。");
        return ResolveChild(InstancesRoot, id);
    }

    private static string ResolveChild(string root, string relative)
    {
        var absoluteRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        if (!candidate.StartsWith(absoluteRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("实例路径超出安装目录。");
        return candidate;
    }

    private static void ValidateArchive(string source)
    {
        if (!Path.GetExtension(source).Equals(".dspack", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(source) || new FileInfo(source).Length is <= 0 or > 1_000_000_000)
            throw new InvalidDataException("请选择完整的 .dspack 整合包（最大 1 GB）。");
        using var archive = ZipFile.OpenRead(source);
        if (archive.Entries.Count > 100_000) throw new InvalidDataException("整合包文件数量过多。");
        long expanded = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/').TrimEnd('/');
            if (name.Length == 0 || name.StartsWith('/') || !names.Add(name) ||
                name.Split('/').Any(part => part.Length == 0 || part is "." or ".." ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.EndsWith('.') || part.EndsWith(' ') ||
                    Regex.IsMatch(part, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", RegexOptions.IgnoreCase)) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException($"包内路径不安全或重复：{entry.FullName}");
            expanded = checked(expanded + entry.Length);
            if (expanded > 2_000_000_000) throw new InvalidDataException("整合包解压后超过 2 GB。");
        }
    }

    private async Task<JsonElement> RunAsync(string node, object request, IProgress<PackEngineProgress>? progress, CancellationToken token)
    {
        if (!File.Exists(_enginePath)) throw new FileNotFoundException("内置整合包引擎缺失，请重新安装启动器。", _enginePath);
        var start = NewNodeStart(node);
        start.ArgumentList.Add(_enginePath);
        start.ArgumentList.Add("--stdio");
        start.RedirectStandardInput = true;
        using var process = new Process { StartInfo = start };
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        using var registration = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var errors = DrainLogAsync(process.StandardError, progress, timeout.Token);
        JsonElement? result = null;
        string? failure = null;
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.Length > 4_000_000) throw new InvalidDataException("引擎返回的数据过大。");
                using var message = JsonDocument.Parse(line);
                var root = message.RootElement;
                switch (root.GetProperty("type").GetString())
                {
                    case "result": result = root.GetProperty("result").Clone(); break;
                    case "error": failure = root.GetProperty("detail").GetString(); break;
                    default: progress?.Report(new(root.TryGetProperty("stage", out var stage) ? stage.GetString()! : "log",
                        root.TryGetProperty("detail", out var detail) ? detail.GetString()! : "")); break;
                }
            }
            await process.WaitForExitAsync(timeout.Token);
            await errors;
            timeout.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || result is null) throw new InvalidOperationException(failure ?? "内置引擎未完成操作，请查看日志。");
            return result.Value;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await errors; } catch (OperationCanceledException) { }
            if (timeout.IsCancellationRequested && !token.IsCancellationRequested) throw new TimeoutException("整合包操作超过 20 分钟，已停止。");
            token.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static ProcessStartInfo NewNodeStart(string node)
    {
        var start = new ProcessStartInfo(node)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["PATH"] = Path.GetDirectoryName(node) + Path.PathSeparator +
            (start.Environment.TryGetValue("PATH", out var path) ? path : "");
        return start;
    }

    private static async Task RunNodeCommandAsync(string node, string cli, string[] arguments, string directory,
        IProgress<PackEngineProgress>? progress, CancellationToken token)
    {
        var start = NewNodeStart(node);
        start.WorkingDirectory = directory;
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        using var registration = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var output = DrainLogAsync(process.StandardOutput, progress, timeout.Token);
        var errors = DrainLogAsync(process.StandardError, progress, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, errors);
            timeout.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException($"运行环境准备失败（退出代码 {process.ExitCode}），请查看日志。");
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(output, errors); } catch (OperationCanceledException) { }
            if (timeout.IsCancellationRequested && !token.IsCancellationRequested) throw new TimeoutException("运行环境准备超过 20 分钟，已停止。");
            token.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static async Task DrainLogAsync(StreamReader reader, IProgress<PackEngineProgress>? progress, CancellationToken token)
    {
        while (await reader.ReadLineAsync(token) is { } line) progress?.Report(new("log", line[..Math.Min(line.Length, 4000)]));
    }
}
