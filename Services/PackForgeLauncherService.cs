using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AnotherDSHL.Services;

public enum PackOpenResult { Opened, ManagerMissing }

public static class PackForgeLauncherService
{
    public const string ReleasesUrl = "https://github.com/DSH-PackForge/dsh-packforge-app/releases";
    private const string ExecutableName = "DSH PackForge.exe";

    public static bool IsManagerExecutable(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return false;
            var name = Path.GetFileName(path);
            // The portable release is named "DSH PackForge <version>.exe"; exclude Setup and CLI.
            return name.Equals(ExecutableName, StringComparison.OrdinalIgnoreCase) ||
                   Regex.IsMatch(name, @"^DSH PackForge \d[\w.-]*\.exe$", RegexOptions.IgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    public static string? FindManager(string manualPath = "")
    {
        if (IsManagerExecutable(manualPath)) return manualPath;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var key in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(key);
                    if (entry?.GetValue("DisplayName") is not string title ||
                        !(title.Equals("DSH PackForge", StringComparison.OrdinalIgnoreCase) ||
                          title.StartsWith("DSH PackForge ", StringComparison.OrdinalIgnoreCase)) ||
                        entry.GetValue("InstallLocation") is not string folder) continue;
                    var candidate = Path.Combine(folder.Trim('"'), ExecutableName);
                    if (IsManagerExecutable(candidate)) return candidate;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException) { }
        }
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        })
        {
            var candidate = Path.Combine(root, "DSH PackForge", ExecutableName);
            if (IsManagerExecutable(candidate)) return candidate;
        }
        return null;
    }

    public static PackOpenResult Open(string packPath, string manualManagerPath = "")
    {
        if (!Path.IsPathFullyQualified(packPath) || !File.Exists(packPath))
            throw new FileNotFoundException("整合包文件不存在，请重新下载或选择文件。", packPath);
        if (!Path.GetExtension(packPath).Equals(".dspack", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 .dspack 整合包文件。");

        var manager = FindManager(manualManagerPath);
        if (manager is not null)
        {
            var start = new ProcessStartInfo(manager)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(manager)!
            };
            start.ArgumentList.Add(packPath);
            try
            {
                using var process = Process.Start(start);
                return process is null ? PackOpenResult.ManagerMissing : PackOpenResult.Opened;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3 or 1155) { }
        }
        // Query the Windows default handler before ShellExecute, avoiding its "no application" error.
        if (!HasFileAssociation()) return PackOpenResult.ManagerMissing;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(packPath) { UseShellExecute = true });
            return PackOpenResult.Opened;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3 or 31 or 1155)
        {
            return PackOpenResult.ManagerMissing;
        }
    }

    public static bool HasFileAssociation()
    {
        uint length = 0;
        // ASSOCSTR_EXECUTABLE = 2; NULL output requests the required buffer length.
        var status = AssocQueryString(0, 2, ".dspack", "open", null, ref length);
        if (status != 1 || length is 0 or > 32768) return false;
        var output = new StringBuilder((int)length);
        return AssocQueryString(0, 2, ".dspack", "open", output, ref length) == 0 &&
               File.Exists(output.ToString());
    }

    public static void ShowFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("整合包文件不存在。", path);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        {
            UseShellExecute = true,
            Arguments = $"/select,\"{Path.GetFullPath(path)}\""
        };
        using var process = Process.Start(start);
    }

    [DllImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(uint flags, uint value, string association, string? extra,
        StringBuilder? output, ref uint length);
}
