using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnotherDSHL.Services;

internal static class LauncherSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AnotherDSHL", "settings.json");

    public static string LoadWorkspace()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                var path = document.RootElement.GetProperty("WorkspacePath").GetString();
                if (!string.IsNullOrWhiteSpace(path))
                    return path;
            }
        }
        catch
        {
            // An unreadable settings file should not prevent the launcher from opening.
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public static void SaveWorkspace(string path)
    {
        SaveValue("WorkspacePath", path);
    }

    public static string LoadBackdropMode()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (document.RootElement.TryGetProperty("BackdropMode", out var mode) &&
                    mode.GetString() == "Mica")
                    return "Mica";
            }
        }
        catch (Exception)
        {
            // Fall back to Acrylic when settings are unavailable.
        }
        return "Acrylic";
    }

    public static void SaveBackdropMode(string mode)
    {
        SaveValue("BackdropMode", mode);
    }

    public static string LoadCatalogUrl()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (document.RootElement.TryGetProperty("CatalogUrl", out var url))
                    return url.GetString() ?? "";
            }
        }
        catch
        {
            // Invalid settings should not prevent browsing the official catalog.
        }
        return "";
    }

    public static void SaveCatalogUrl(string url) => SaveValue("CatalogUrl", url);

    public static string LoadNodePath()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (document.RootElement.TryGetProperty("NodePath", out var path))
                    return path.GetString() ?? "";
            }
        }
        catch
        {
            // Fall back to PATH detection if the saved selection cannot be read.
        }
        return "";
    }

    public static void SaveNodePath(string path) => SaveValue("NodePath", path);

    public static string LoadNodeMode()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (document.RootElement.TryGetProperty("NodeMode", out var mode))
                {
                    var value = mode.GetString();
                    if (value is "Auto" or "Manual") return value;
                }
            }
        }
        catch
        {
            // Existing installs without a mode keep their previous node.exe choice.
        }
        return string.IsNullOrWhiteSpace(LoadNodePath()) ? "Auto" : "Manual";
    }

    public static void SaveNodeMode(string mode) => SaveValue("NodeMode", mode);

    public static string LoadLastWebUrl()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (document.RootElement.TryGetProperty("LastWebUrl", out var url))
                    return url.GetString() ?? "";
            }
        }
        catch
        {
            // Fall back to default loopback URL when unavailable.
        }
        return "";
    }

    public static void SaveLastWebUrl(string url) => SaveValue("LastWebUrl", url);

    public static int LoadWebPort()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (document.RootElement.TryGetProperty("WebPort", out var port) &&
                    int.TryParse(port.ValueKind == JsonValueKind.String ? port.GetString() : port.GetRawText(),
                        out var value) && value is >= 1 and <= 65535)
                    return value;
            }
        }
        catch { /* Invalid settings fall back to the DSH default. */ }
        return 3080;
    }

    public static void SaveWebPort(int port) => SaveValue("WebPort", port.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static string LoadDshVersion()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (document.RootElement.TryGetProperty("DshVersion", out var version))
                {
                    var value = version.GetString();
                    if (DshVersionService.IsSafeVersion(value)) return value!;
                }
            }
        }
        catch { /* Use the npm default tag when settings cannot be read. */ }
        return "";
    }

    public static void SaveDshVersion(string version) => SaveValue("DshVersion", version);

    public static string LoadDesktopPath()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (document.RootElement.TryGetProperty("DesktopPath", out var path))
                    return path.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }

    public static void SaveDesktopPath(string path) => SaveValue("DesktopPath", path);

    public static string LoadLaunchMode()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (document.RootElement.TryGetProperty("LaunchMode", out var mode) && mode.GetString() == "desktop")
                    return "desktop";
            }
        }
        catch { }
        return "web";
    }

    public static void SaveLaunchMode(string mode) => SaveValue("LaunchMode", mode);

    public static string LoadPackForgePath()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (document.RootElement.TryGetProperty("PackForgePath", out var path))
                    return path.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }

    public static bool SavePackForgePath(string path) => SaveValue("PackForgePath", path);

    private static bool SaveValue(string key, string value)
    {
        try
        {
            JsonObject data;
            try { data = File.Exists(SettingsPath) ? JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? new() : new(); }
            catch { data = new(); }
            data[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, data.ToJsonString());
            return true;
        }
        catch { return false; /* Keep the current in-memory choice when settings cannot be written. */ }
    }
}
