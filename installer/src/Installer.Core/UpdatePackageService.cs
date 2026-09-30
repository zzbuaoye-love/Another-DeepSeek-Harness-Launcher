using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AnotherDSHL.Installer.Core;

// Adapted from ZSnaper: base-version checks, changed files, delete lists and checksums.
// Reuse the full install transaction to commit files, metadata and rollback together.
public sealed record UpdateManifest(string Format, string From, PayloadManifest Target, List<string> Changed, List<string> Delete);

public sealed class UpdatePackageService
{
    private const string ManifestName = "update.manifest.json";
    private readonly Installation _installation;
    public UpdatePackageService(Installation? installation = null) => _installation = installation ?? new Installation();

    public static void Create(string baseline, string payload, string packagePath)
    {
        var old = PayloadManifest.Read(baseline);
        var next = PayloadManifest.Read(payload);
        if (ReleaseVersion.Compare(next.Version, old.Version) <= 0) throw new InvalidDataException("差分更新的版本必须高于基础版本。");
        var previous = old.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var changed = next.Files.Where(f => !previous.TryGetValue(f.Path, out var before) || f.Sha256 != before.Sha256 || f.Size != before.Size).Select(f => f.Path).ToList();
        var current = next.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deleted = old.Files.Where(f => !current.Contains(f.Path)).Select(f => f.Path).ToList();
        string temporary = packagePath + ".building";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                using (var stream = archive.CreateEntry(ManifestName).Open()) JsonSerializer.Serialize(stream, new UpdateManifest("anotherdshl-update-1", old.Version, next, changed, deleted));
                foreach (var relative in changed)
                {
                    var file = next.Files.Single(f => f.Path == relative);
                    string source = Installation.SafePath(payload, relative);
                    Verify(source, file);
                    archive.CreateEntryFromFile(source, "files/" + relative.Replace('\\', '/'), CompressionLevel.SmallestSize);
                }
            }
            File.Move(temporary, packagePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static UpdateManifest ReadManifest(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var entries = archive.Entries.ToDictionary(e => e.FullName, StringComparer.OrdinalIgnoreCase);
        if (!entries.TryGetValue(ManifestName, out var entry) || entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("更新清单缺失或过大。");
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(stream) ?? throw new InvalidDataException("更新清单为空。");
        if (manifest.Format != "anotherdshl-update-1" || string.IsNullOrWhiteSpace(manifest.From) || manifest.Target == null || manifest.Changed == null || manifest.Delete == null)
            throw new InvalidDataException("不支持此更新包。");
        string validationRoot = Path.Combine(Path.GetTempPath(), "ADL-validation");
        manifest.Target.Validate(validationRoot);
        if (ReleaseVersion.Compare(manifest.Target.Version, manifest.From) <= 0) throw new InvalidDataException("更新版本必须高于基础版本。");
        var targetFiles = manifest.Target.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relative in manifest.Changed)
        {
            Installation.SafePath(validationRoot, relative);
            if (!seen.Add(relative) || !targetFiles.TryGetValue(relative, out var file) ||
                !entries.TryGetValue("files/" + relative.Replace('\\', '/'), out var content) || content.Length != file.Size)
                throw new InvalidDataException("更新文件清单或大小无效：" + relative);
        }
        foreach (string relative in manifest.Delete)
        {
            Installation.SafePath(validationRoot, relative);
            if (!seen.Add(relative) || targetFiles.ContainsKey(relative)) throw new InvalidDataException("更新删除清单无效。");
        }
        if (entries.Count != manifest.Changed.Count + 1) throw new InvalidDataException("更新包包含清单之外的文件。");
        return manifest;
    }

    public static UpdateManifest ValidatePackage(string packagePath, CancellationToken token = default)
    {
        var manifest = ReadManifest(packagePath);
        using var archive = ZipFile.OpenRead(packagePath);
        var files = manifest.Target.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var relative in manifest.Changed)
        {
            token.ThrowIfCancellationRequested();
            using var stream = archive.GetEntry("files/" + relative.Replace('\\', '/'))!.Open();
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(files[relative].Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新文件校验失败：" + relative);
        }
        return manifest;
    }

    public void Apply(string packagePath, string destination, IProgress<double>? progress = null)
    {
        destination = Installation.NormalizeDirectory(destination);
        if (!destination.Equals(_installation.InstalledDirectory, StringComparison.OrdinalIgnoreCase)) throw new IOException("只能更新当前用户注册的安装目录。");
        var current = PayloadManifest.Read(destination);
        var update = ReadManifest(packagePath);
        if (!current.Version.Equals(update.From, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"此更新包适用于 {update.From}，当前版本为 {current.Version}。请使用完整安装包。");
        var before = current.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var changed = update.Changed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedDeletes = current.Files.Select(f => f.Path).Except(update.Target.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expectedDeletes.SetEquals(update.Delete)) throw new InvalidDataException("更新包删除清单与已安装版本不一致。");
        string assembled = Path.Combine(Path.GetTempPath(), "AnotherDSHL", "update-payload-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(assembled);
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                foreach (var file in update.Target.Files)
                {
                    string target = Installation.SafePath(assembled, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (changed.Contains(file.Path))
                    {
                        var entry = archive.GetEntry("files/" + file.Path.Replace('\\', '/')) ?? throw new InvalidDataException("更新文件缺失。");
                        using var input = entry.Open(); using var output = File.Create(target);
                        input.CopyTo(output);
                    }
                    else
                    {
                        if (!before.TryGetValue(file.Path, out var old) || old.Sha256 != file.Sha256 || old.Size != file.Size)
                            throw new InvalidDataException("更新包缺少需要替换的文件：" + file.Path);
                        File.Copy(Installation.SafePath(destination, file.Path), target);
                    }
                    Verify(target, file);
                }
            }
            File.WriteAllText(Path.Combine(assembled, PayloadManifest.FileName), JsonSerializer.Serialize(update.Target));
            _installation.Install(assembled, destination, false, false, progress, preserveShortcuts: true);
        }
        finally { Installation.TryCleanup(assembled); }
    }
    private static void Verify(string path, PayloadFile file)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length != file.Size || !Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新文件校验失败：" + file.Path);
    }
}
