using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace AnotherDSHL.Services;

public sealed record DesktopClientInstallation(string ExecutablePath, string Version, bool IsManual);
public sealed record DesktopDownloadProgress(long Received, long? Total);

public static class DesktopClientService
{
    public const string WebsiteUrl = "https://www.deepseek.com/harness/";
    public const string InstallerUrl = "https://download.deepseek.com/desktop/dsh-latest-windows-x64.exe";
    private const string ExecutableName = "DeepSeek Harness.exe";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(20) };

    public static DesktopClientInstallation? Detect(string manualPath = "")
    {
        if (IsClientExecutable(manualPath)) return FromPath(manualPath, "", true);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is not string title ||
                        !title.Equals("DeepSeek Harness", StringComparison.OrdinalIgnoreCase) ||
                        entry.GetValue("InstallLocation") is not string location) continue;
                    var executable = Path.Combine(location.Trim('"'), ExecutableName);
                    if (IsClientExecutable(executable) && HasOfficialPublisher(executable))
                        return FromPath(executable, entry.GetValue("DisplayVersion") as string ?? "", false);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException) { }
        }
        var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "DeepSeek Harness", ExecutableName);
        return IsClientExecutable(standard) && HasOfficialPublisher(standard) ? FromPath(standard, "", false) : null;
    }

    public static bool IsClientExecutable(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
                   Path.GetFileName(path).Equals(ExecutableName, StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(path) && File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "resources", "app.asar"));
        }
        catch (ArgumentException) { return false; }
    }

    private static DesktopClientInstallation FromPath(string path, string version, bool manual)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            try { version = FileVersionInfo.GetVersionInfo(path).ProductVersion ?? ""; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new DesktopClientInstallation(path, version, manual);
    }

    private static bool HasOfficialPublisher(string path)
    {
        try
        {
            using var certificate = X509Certificate.CreateFromSignedFile(path);
            return certificate.Subject.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase) ||
                   certificate.Subject.Contains("深度求索", StringComparison.Ordinal);
        }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
        catch (IOException) { return false; }
    }

    public static async Task<string> DownloadInstallerAsync(IProgress<DesktopDownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AnotherDSHL", "Installers");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"dsh-desktop-{Guid.NewGuid():N}.exe");
        try
        {
            using var response = await Client.GetAsync(InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri?.Scheme != Uri.UriSchemeHttps || !finalUri.Host.Equals("download.deepseek.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装包下载地址不是官方 HTTPS 地址。");
            var total = response.Content.Headers.ContentLength;
            if (total is <= 0 or > 1_000_000_000) throw new InvalidDataException("安装包大小异常。");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                long received = 0;
                long lastReport = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    received += read;
                    if (received > 1_000_000_000 || (total is long expected && received > expected))
                        throw new InvalidDataException("安装包下载大小超出预期。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    if (received - lastReport > 1_000_000)
                    {
                        progress?.Report(new DesktopDownloadProgress(received, total));
                        lastReport = received;
                    }
                }
                if (received == 0 || (total is long expectedLength && received != expectedLength))
                    throw new InvalidDataException("安装包下载不完整。");
                progress?.Report(new DesktopDownloadProgress(received, total));
            }
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => VerifyInstallerSignature(path), cancellationToken);
            return path;
        }
        catch
        {
            TryDeleteInstaller(path);
            throw;
        }
    }

    public static void TryDeleteInstaller(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Authenticode validates the complete PE file as well as the Windows trust chain.
    public static void VerifyInstallerSignature(string path)
    {
        var file = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = path };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        Marshal.StructureToPtr(file, filePointer, false);
        var data = new WinTrustData
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2, UnionChoice = 1,
            FileInfo = filePointer, StateAction = 1
        };
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try
        {
            var status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (status != 0 || !HasOfficialPublisher(path))
                throw new InvalidDataException($"安装包未通过 DeepSeek 发布者签名校验（0x{status:X8}）。");
        }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
}
