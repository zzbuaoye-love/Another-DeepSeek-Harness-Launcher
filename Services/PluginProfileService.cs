using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AnotherDSHL.Services;

public sealed record InstalledPlugin(string Name, string InstallSpec, bool IsEnabled, bool IsDependency, string Version)
{
    public string Status => IsEnabled ? "已启用" : $"已安装 · {Version}";
}

public static class PluginProfileService
{
    public static bool IsPackageName(string name) => Regex.IsMatch(name,
        @"^(?:@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<InstalledPlugin> Load(string directory)
    {
        var path = Path.Combine(directory, "package.json");
        if (!File.Exists(path)) return [];
        var manifest = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException("插件配置为空。");
        var result = new Dictionary<string, InstalledPlugin>(StringComparer.OrdinalIgnoreCase);
        if (manifest["dsh"]?["profile"]?["bundles"] is JsonArray bundles)
            foreach (var bundle in bundles)
                if (bundle?.GetValue<string>() is { Length: > 0 } name)
                    result[name] = new(name, name, true, false, "");
        foreach (var section in new[] { "dependencies", "devDependencies" })
            if (manifest[section] is JsonObject dependencies)
                foreach (var dependency in dependencies)
                {
                    var spec = dependency.Value?.GetValue<string>() ?? "";
                    result[dependency.Key] = new(dependency.Key, spec, result.GetValueOrDefault(dependency.Key)?.IsEnabled == true, true, spec);
                }
        return result.Values.ToArray();
    }

    // Bundles shipped with the runtime may be enabled without a profile dependency to uninstall.
    public static void RemoveEnabledBundle(string directory, string name)
    {
        if (!IsPackageName(name)) throw new InvalidDataException("插件包名无效。");
        var path = Path.Combine(directory, "package.json");
        var original = File.ReadAllText(path);
        var manifest = JsonNode.Parse(original) ?? throw new InvalidDataException("插件配置为空。");
        if (manifest["dsh"]?["profile"]?["bundles"] is not JsonArray bundles) return;
        var matches = bundles.Where(item => item?.GetValue<string>() == name).ToArray();
        if (matches.Length == 0) return;
        foreach (var item in matches) bundles.Remove(item);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, manifest.ToJsonString(new() { WriteIndented = true }) + Environment.NewLine);
            if (File.ReadAllText(path) != original) throw new IOException("插件配置已被其他程序修改，请刷新后重试。");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
