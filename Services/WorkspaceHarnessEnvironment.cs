using System.Security.Cryptography;
using System.Text;

namespace AnotherDSHL.Services;

internal sealed class WorkspaceHarnessEnvironment
{
    public string Root { get; }
    public string Home => Path.Combine(Root, "home");
    public string RuntimeRoot => Path.Combine(Root, "runtimes");
    public string NpmCache => Path.Combine(Root, "npm-cache");
    public string InstancesRoot => Path.Combine(Root, "instances");

    public WorkspaceHarnessEnvironment(string workspace, string? storageRoot = null)
    {
        var normalized = WorkspaceCatalogService.Normalize(workspace).ToUpperInvariant();
        // 96 bits identifies the path while keeping Windows npm lifecycle working directories below MAX_PATH.
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24].ToLowerInvariant();
        Root = Path.Combine(storageRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AnotherDSHL", "WorkspaceEnvironments"), id);
    }

    public DshRuntimeService CreateRuntimeService() => new(RuntimeRoot, reuseExisting: false, npmCache: NpmCache);

    public async Task<InstalledPack> PreparePackAsync(InstalledPack sourcePack, PackForgeEngineService sourceEngine,
        PackForgeEngineService privateEngine, string workspace, PackEngineTools tools, NpmRegistry registry,
        IProgress<PackEngineProgress>? progress = null, CancellationToken token = default)
    {
        if (!string.Equals(Path.GetFullPath(privateEngine.InstancesRoot), Path.GetFullPath(InstancesRoot), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("隔离整合包实例路径不匹配。");
        var installed = privateEngine.LoadInstalled().FirstOrDefault(pack => pack.Id == sourcePack.Id);
        if (installed is not null)
        {
            if (installed.SourceSha256 != sourcePack.SourceSha256 || installed.DshVersion != sourcePack.DshVersion)
                throw new InvalidDataException("工作区整合包记录与原始包不一致，请重新选择实例。");
            privateEngine.VerifyActivation(installed);
            return installed;
        }
        // Import the immutable archive, never copy another workspace's accounts, sessions or modified profile.
        var archive = sourceEngine.GetSourceArchive(sourcePack);
        var info = await privateEngine.InspectAsync(archive, tools, token);
        if (!info.Sha256.Equals(sourcePack.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("原始整合包校验失败。");
        return await privateEngine.InstallAsync(archive, info, sourcePack.Id, sourcePack.DshVersion, sourcePack.Profile,
            workspace, tools, progress, token, registry.Url, NpmCache);
    }
}
