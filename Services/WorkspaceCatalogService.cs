using System.Text.Json;

namespace AnotherDSHL.Services;

internal sealed record SavedWorkspace(string Path, string Version = "", string PackId = "", bool IsolatedHarness = false)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : Path;
    public string Availability => Directory.Exists(Path) ? "目录可用" : "目录不存在，请检查磁盘或路径";
    public string Configuration => PackId.Length > 0 ? "已绑定整合包" : Version.Length > 0 ? $"DSH v{Version} · 不自动更新" : "默认 · 自动更新到最新版";
}

internal sealed record WorkspaceCatalog(IReadOnlyList<SavedWorkspace> Items, string SelectedPath,
    bool AutoDiscover = true, bool RestoreConfiguration = true);

internal sealed class WorkspaceCatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _file;
    public WorkspaceCatalog Current { get; private set; }
    public string? LoadError { get; }

    public WorkspaceCatalogService(string file, string legacyPath, string version = "", string packId = "")
    {
        _file = file;
        try
        {
            if (File.Exists(file))
            {
                var loaded = JsonSerializer.Deserialize<WorkspaceCatalog>(File.ReadAllText(file))
                    ?? throw new JsonException("工作区列表为空。");
                if (loaded.Items is null) throw new JsonException("工作区列表格式无效。");
                var items = loaded.Items.Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Path))
                    .Select(item => item with { Path = Normalize(item.Path), Version = item.Version ?? "", PackId = item.PackId ?? "" })
                    .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
                var selected = items.FirstOrDefault(item => SamePath(item.Path, loaded.SelectedPath))?.Path
                    ?? items.FirstOrDefault()?.Path ?? "";
                Current = loaded with { Items = items, SelectedPath = selected };
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            LoadError = $"无法读取工作区列表，原文件已保留：{ex.Message}";
        }
        string path;
        try { path = string.IsNullOrWhiteSpace(legacyPath) ? "" : Normalize(legacyPath); }
        catch (ArgumentException) { path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        Current = new(path.Length == 0 ? [] : [new(path, version, packId)], path);
    }

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path.Trim()))
            throw new ArgumentException("请输入完整的文件夹路径。");
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path.Trim()));
    }

    public static bool SamePath(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    public SavedWorkspace? Selected => Current.Items.FirstOrDefault(item => SamePath(item.Path, Current.SelectedPath));

    public SavedWorkspace Add(string path)
    {
        path = Normalize(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("目录不存在，请选择已存在的项目文件夹。");
        var item = Current.Items.FirstOrDefault(item => SamePath(item.Path, path));
        if (item is not null) return item;
        item = new(path);
        Commit(Current with { Items = Current.Items.Append(item).ToArray() });
        return item;
    }

    public void Select(string path)
    {
        var item = Current.Items.FirstOrDefault(item => SamePath(item.Path, path))
            ?? throw new ArgumentException("此工作区未添加到列表。");
        Commit(Current with { SelectedPath = item.Path });
    }

    public void Remove(string path)
    {
        var items = Current.Items.Where(item => !SamePath(item.Path, path)).ToArray();
        var selected = SamePath(path, Current.SelectedPath) ? items.FirstOrDefault()?.Path ?? "" : Current.SelectedPath;
        Commit(Current with { Items = items, SelectedPath = selected });
    }

    public void SaveConfiguration(string version, string packId)
    {
        Commit(Current with { Items = Current.Items.Select(item => SamePath(item.Path, Current.SelectedPath)
            ? item with { Version = version, PackId = packId } : item).ToArray() });
    }

    public void SetAutomation(bool discover, bool restore) => Commit(Current with { AutoDiscover = discover, RestoreConfiguration = restore });

    public void SetIsolation(bool isolated) => Commit(Current with { Items = Current.Items.Select(item => SamePath(item.Path, Current.SelectedPath)
        ? item with { IsolatedHarness = isolated } : item).ToArray() });

    private void Commit(WorkspaceCatalog value)
    {
        if (LoadError is not null) throw new IOException(LoadError);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_file))!);
        var temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temporary, _file, overwrite: true);
            Current = value;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Only inspect a bounded first level of known roots. Never traverse node_modules or linked directories.
    public static IReadOnlyList<SavedWorkspace> Discover(IEnumerable<string> roots, IEnumerable<string> savedPaths)
    {
        var known = new HashSet<string>(savedPaths, StringComparer.OrdinalIgnoreCase);
        var found = new List<SavedWorkspace>();
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(20))
        {
            try
            {
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) continue;
                Inspect(root);
                foreach (var directory in Directory.EnumerateDirectories(root).Take(120)) Inspect(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Take(40).ToArray();

        void Inspect(string directory)
        {
            try
            {
                var path = Normalize(directory);
                if (known.Contains(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
                if (Directory.Exists(System.IO.Path.Combine(path, ".git")) || File.Exists(System.IO.Path.Combine(path, ".git")) ||
                    File.Exists(System.IO.Path.Combine(path, "package.json")) || File.Exists(System.IO.Path.Combine(path, "pyproject.toml")) ||
                    File.Exists(System.IO.Path.Combine(path, "Cargo.toml")) ||
                    Directory.EnumerateFiles(path).Take(80).Any(file => System.IO.Path.GetExtension(file) is ".sln" or ".slnx" or ".csproj"))
                {
                    known.Add(path);
                    found.Add(new(path));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
    }
}
