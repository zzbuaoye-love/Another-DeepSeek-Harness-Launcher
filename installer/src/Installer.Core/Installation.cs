using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace AnotherDSHL.Installer.Core;

public sealed record PayloadFile(string Path, long Size, string Sha256);
public sealed record PayloadManifest(string Product, string Version, List<PayloadFile> Files)
{
    public const string FileName = "installation.json";
    public long InstalledBytes => Files.Sum(f => f.Size);
    public static PayloadManifest Read(string root)
    {
        var manifest = JsonSerializer.Deserialize<PayloadManifest>(File.ReadAllText(System.IO.Path.Combine(root, FileName)))
            ?? throw new InvalidDataException("安装清单为空");
        if (manifest.Product != "AnotherDSHL" || string.IsNullOrWhiteSpace(manifest.Version) ||
            manifest.Files.Count == 0 || !manifest.Files.Any(f => f.Path == "AnotherDSHL.exe") ||
            manifest.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count)
            throw new InvalidDataException("安装清单无效");
        foreach (var file in manifest.Files)
        {
            Installation.SafePath(root, file.Path);
            if (file.Size < 0 || file.Sha256.Length != 64) throw new InvalidDataException("文件校验信息无效。");
        }
        return manifest;
    }
}

public sealed class Installation
{
    public const string ProductName = "AnotherDSHL";
    public const string ExecutableName = "AnotherDSHL.exe";
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProductName);
    private readonly string _key;
    private readonly string _shortcutDirectory;
    private readonly string _desktopDirectory;
    public Installation(string identity = ProductName, string? shortcutDirectory = null, string? desktopDirectory = null)
    {
        _key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + identity;
        _shortcutDirectory = shortcutDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        _desktopDirectory = desktopDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }
    public string? InstalledDirectory
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(_key); return key?.GetValue("InstallLocation") as string; }
    }
    public static string NormalizeDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(directory.Trim())))
            throw new IOException("请选择完整的安装路径。");
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory.Trim())).TrimEnd('\\', '/');
        if (full.Length < 4 || full.Length > 180 || full.Equals(Path.GetPathRoot(full)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new IOException("不能安装到磁盘根目录，或路径过长。");
        RejectReparse(full);
        return full;
    }
    private static void RejectReparse(string path)
    {
        for (string? part = path; part != null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("安装目录不能包含符号链接或目录联接。");
    }
    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Split('/', '\\').Any(s => s is ".." or "." or ""))
            throw new InvalidDataException("安装清单包含无效路径。");
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装文件越界。");
        RejectReparse(full);
        return full;
    }
    private static void EnsureClosed(string root)
    {
        foreach (var process in Process.GetProcessesByName(ProductName))
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, Path.Combine(root, ExecutableName), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("请先关闭此目录中的 AnotherDSHL，再重试。");
                }
                catch (System.ComponentModel.Win32Exception) { throw new IOException("无法检查运行中的 AnotherDSHL，请关闭程序后重试。"); }
                catch (InvalidOperationException) { }
            }
    }
    public void Install(string source, string destination, bool desktop, bool startMenu, IProgress<double>? progress = null)
    {
        source = NormalizeDirectory(source);
        destination = NormalizeDirectory(destination);
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(destination + "\\", StringComparison.OrdinalIgnoreCase) || destination.StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("安装目录不能与安装器的临时目录重叠。");
        var manifest = PayloadManifest.Read(source);
        PayloadManifest? previous = null;
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            if (!string.Equals(InstalledDirectory, destination, StringComparison.OrdinalIgnoreCase))
                throw new IOException("此目录已有文件，请选择空目录或已安装的 ADL 目录");
            previous = PayloadManifest.Read(destination);
        }
        EnsureClosed(destination);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var drive = new DriveInfo(Path.GetPathRoot(destination)!);
        if (drive.AvailableFreeSpace < manifest.InstalledBytes * 2 + 32 * 1024 * 1024)
            throw new IOException("磁盘可用空间不足，请清理磁盘后重试");
        string stage = Path.Combine(parent, ".adl-stage-" + Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(parent, ".adl-backup-" + Guid.NewGuid().ToString("N"));
        var shortcutPaths = new[] { Path.Combine(_desktopDirectory, ProductName + ".lnk"), Path.Combine(_shortcutDirectory, ProductName + ".lnk") };
        var shortcuts = shortcutPaths.ToDictionary(p => p, p => File.Exists(p) ? File.ReadAllBytes(p) : null);
        using var oldKey = Registry.CurrentUser.OpenSubKey(_key);
        var metadata = oldKey?.GetValueNames().ToDictionary(n => n, n => (oldKey.GetValue(n)!, oldKey.GetValueKind(n)));
        bool moved = false, replaced = false;
        try
        {
            Directory.CreateDirectory(stage);
            // Preserve files not owned by the previous package during repair/upgrade.
            if (previous != null)
            {
                var owned = previous.Files.Select(f => f.Path.Replace('/', '\\')).Append(PayloadManifest.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (string file in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(destination, file);
                    SafePath(destination, relative);
                    if (owned.Contains(relative)) continue;
                    var target = SafePath(stage, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }
            }
            long complete = 0;
            foreach (var file in manifest.Files)
            {
                var from = SafePath(source, file.Path);
                var target = SafePath(stage, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(from, target, true);
                using var stream = File.OpenRead(target);
                if (stream.Length != file.Size || !Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装文件校验失败：" + file.Path);
                complete += file.Size;
                progress?.Report(complete * 95d / Math.Max(1, manifest.InstalledBytes));
            }
            File.Copy(Path.Combine(source, PayloadManifest.FileName), Path.Combine(stage, PayloadManifest.FileName));
            if (Directory.Exists(destination)) { Directory.Move(destination, backup); moved = true; }
            Directory.Move(stage, destination); replaced = true;
            for (int i = 0; i < shortcutPaths.Length; i++)
            {
                if (i == 0 ? desktop : startMenu) ShortcutService.Create(shortcutPaths[i], Path.Combine(destination, ExecutableName));
                else ShortcutService.DeleteIfOwned(shortcutPaths[i], Path.Combine(destination, ExecutableName));
            }
            using var key = Registry.CurrentUser.CreateSubKey(_key);
            key.SetValue("DisplayName", ProductName);
            key.SetValue("DisplayVersion", manifest.Version);
            key.SetValue("Publisher", "ZZBuAoYe");
            key.SetValue("InstallLocation", destination);
            key.SetValue("DisplayIcon", Path.Combine(destination, ExecutableName));
            key.SetValue("UninstallString", "\"" + Path.Combine(destination, "AnotherDSHL.Installer.exe") + "\" --uninstall");
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, manifest.InstalledBytes / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            progress?.Report(100);
        }
        catch
        {
            if (replaced) Directory.Delete(destination, true);
            if (moved) Directory.Move(backup, destination);
            foreach (var (path, bytes) in shortcuts)
            {
                if (bytes != null) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }
                else if (File.Exists(path)) File.Delete(path);
            }
            Registry.CurrentUser.DeleteSubKeyTree(_key, false);
            if (metadata != null)
            {
                using var restore = Registry.CurrentUser.CreateSubKey(_key);
                foreach (var (name, value) in metadata) restore.SetValue(name, value.Item1, value.Item2);
            }
            throw;
        }
        finally { TryCleanup(stage); }
        TryCleanup(backup);
    }
    public void Uninstall(string destination)
    {
        destination = NormalizeDirectory(destination);
        if (!string.Equals(destination, InstalledDirectory, StringComparison.OrdinalIgnoreCase))
            throw new IOException("此目录不是当前用户注册的安装目录。");
        var manifest = PayloadManifest.Read(destination);
        EnsureClosed(destination);
        // Validate all paths before deleting anything; foreign files and user settings remain.
        var paths = manifest.Files.Select(f => SafePath(destination, f.Path)).ToArray();
        foreach (string path in paths) if (File.Exists(path)) File.Delete(path);
        File.Delete(Path.Combine(destination, PayloadManifest.FileName));
        foreach (var dir in Directory.EnumerateDirectories(destination, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        if (!Directory.EnumerateFileSystemEntries(destination).Any()) Directory.Delete(destination);
        ShortcutService.DeleteIfOwned(Path.Combine(_desktopDirectory, ProductName + ".lnk"), Path.Combine(destination, ExecutableName));
        ShortcutService.DeleteIfOwned(Path.Combine(_shortcutDirectory, ProductName + ".lnk"), Path.Combine(destination, ExecutableName));
        Registry.CurrentUser.DeleteSubKeyTree(_key, false);
    }
    public static void TryCleanup(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
