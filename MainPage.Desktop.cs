using System.Diagnostics;
using AnotherDSHL.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace AnotherDSHL;

public sealed partial class MainPage
{
    private DesktopClientInstallation? _desktopClient;
    private string _desktopManualPath = LauncherSettings.LoadDesktopPath();
    private bool _desktopDetecting;
    private bool _desktopBusy;
    private CancellationTokenSource? _desktopDownloadCancellation;
    private bool _desktopLaunchMode;
    private bool _launchModeReady;

    private void LaunchModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_launchModeReady) return;
        _desktopLaunchMode = (LaunchModeComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "desktop";
        if (!_demoMode) LauncherSettings.SaveLaunchMode(_desktopLaunchMode ? "desktop" : "web");
        UpdateHomeLaunchMode();
    }

    private void UpdateHomeLaunchMode()
    {
        WebLaunchButtons.Visibility = WebStatusPanel.Visibility = WebHomeFooter.Visibility =
            _desktopLaunchMode ? Visibility.Collapsed : Visibility.Visible;
        DesktopHomeButton.Visibility = DesktopHomeStatusText.Visibility =
            _desktopLaunchMode ? Visibility.Visible : Visibility.Collapsed;
        UpdateVersionSelectionUi();
        if (_initialized) UpdateBranding();
    }

    private async Task RefreshDesktopClientAsync()
    {
        if (_demoMode || _desktopDetecting) return;
        _desktopDetecting = true;
        try
        {
            var path = _desktopManualPath;
            _desktopClient = await Task.Run(() => DesktopClientService.Detect(path));
            UpdateDesktopUi();
        }
        catch (Exception ex)
        {
            DesktopClientStatusText.Text = $"桌面客户端检测失败：{ex.Message}";
            AppendLog($"[ADL] 桌面客户端检测失败：{ex.Message}");
        }
        finally { _desktopDetecting = false; }
    }

    private void UpdateDesktopUi()
    {
        var installed = _desktopClient is not null;
        var version = string.IsNullOrWhiteSpace(_desktopClient?.Version) ? "" : $" · v{_desktopClient.Version}";
        DesktopClientStatusText.Text = installed ? $"已安装{version}" : "尚未检测到官方桌面客户端";
        DesktopClientPathText.Text = _desktopClient is { } client
            ? $"{(client.IsManual ? "手动指定" : "自动检测")}：{client.ExecutablePath}"
            : "可下载安装，或手动选择已安装目录中的 DeepSeek Harness.exe。";
        DesktopLaunchButton.IsEnabled = installed && !_desktopBusy && !_demoMode;
        DesktopInstallButton.Content = installed ? "安装 / 更新" : "下载安装";
        DesktopInstallButton.IsEnabled = DesktopSelectButton.IsEnabled = !_desktopBusy && !_demoMode;
        DesktopHomeButton.Content = _desktopBusy ? "安装处理中…" : installed ? "启动桌面版" : "安装桌面版";
        DesktopHomeButton.IsEnabled = !_desktopBusy && !_demoMode;
        if (!_desktopBusy)
            DesktopHomeStatusText.Text = _demoMode ? "官方桌面客户端 · 演示状态"
                : installed ? $"官方桌面客户端已安装{version}" : "官方桌面版自带运行环境";
        if (_initialized && _desktopLaunchMode) UpdateBranding();
    }

    private async void DesktopHomeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode || _desktopBusy) return;
        await RefreshDesktopClientAsync();
        if (_desktopClient is not null) LaunchDesktopClient();
        else await InstallDesktopClientAsync();
    }

    private async void DesktopLaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode || _desktopBusy) return;
        await RefreshDesktopClientAsync();
        LaunchDesktopClient();
    }

    private void LaunchDesktopClient()
    {
        if (_desktopClient is not { } client)
        {
            DesktopInstallStatusText.Text = "尚未找到桌面客户端，请安装或重新选择程序。";
            return;
        }
        try
        {
            using var process = Process.Start(new ProcessStartInfo(client.ExecutablePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(client.ExecutablePath)!
            });
            DesktopHomeStatusText.Text = DesktopInstallStatusText.Text = "已发送桌面客户端启动请求。";
            AppendLog("[ADL] 已发送官方桌面客户端启动请求。");
        }
        catch (Exception ex)
        {
            DesktopHomeStatusText.Text = DesktopInstallStatusText.Text = $"桌面客户端启动失败：{ex.Message}";
            AppendLog($"[ADL] 桌面客户端启动失败：{ex.Message}");
        }
    }

    private async void DesktopInstallButton_Click(object sender, RoutedEventArgs e) => await InstallDesktopClientAsync();

    private async Task InstallDesktopClientAsync()
    {
        if (_demoMode || _desktopBusy) return;
        if (!Environment.Is64BitOperatingSystem)
        {
            DesktopInstallStatusText.Text = "官方 Windows 桌面客户端需要 64 位操作系统。";
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _desktopClient is null ? "安装官方桌面客户端" : "安装或更新官方桌面客户端",
            Content = "从 DeepSeek 官方下载 Windows x64 安装包。下载并校验发布者签名后，会打开安装程序，由你选择安装目录和完成安装。",
            PrimaryButtonText = "开始下载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        _desktopBusy = true;
        UpdateDesktopUi();
        var approved = false;
        try { approved = await dialog.ShowAsync() == ContentDialogResult.Primary; }
        catch (Exception ex) { DesktopInstallStatusText.Text = $"无法打开安装确认：{ex.Message}"; }
        if (!approved)
        {
            _desktopBusy = false;
            UpdateDesktopUi();
            return;
        }
        DesktopInstallProgressBar.Visibility = Visibility.Visible;
        DesktopInstallProgressBar.Value = 0;
        DesktopInstallProgressBar.IsIndeterminate = true;
        DesktopCancelInstallButton.Visibility = Visibility.Visible;
        DesktopHomeStatusText.Text = DesktopInstallStatusText.Text = "正在连接官方安装包下载…";
        string? installerPath = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        _desktopDownloadCancellation = cancellation;
        try
        {
            var progress = new Progress<DesktopDownloadProgress>(download =>
            {
                if (_desktopDownloadCancellation != cancellation) return;
                DesktopInstallProgressBar.IsIndeterminate = download.Total is null;
                if (download.Total is long total)
                    DesktopInstallProgressBar.Value = download.Received * 100.0 / total;
                var detail = download.Total is long length
                    ? $"{download.Received / 1048576.0:F1} / {length / 1048576.0:F1} MB"
                    : $"{download.Received / 1048576.0:F1} MB";
                DesktopHomeStatusText.Text = DesktopInstallStatusText.Text =
                    download.Total == download.Received ? "下载完成，正在校验官方发布者签名…" : $"正在下载桌面客户端：{detail}";
            });
            installerPath = await DesktopClientService.DownloadInstallerAsync(progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _desktopDownloadCancellation = null;
            DesktopCancelInstallButton.Visibility = Visibility.Collapsed;
            DesktopInstallProgressBar.Visibility = Visibility.Collapsed;
            DesktopHomeStatusText.Text = DesktopInstallStatusText.Text = "签名校验通过，等待你在安装程序中完成安装…";
            using var installer = Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
            AppendLog("[ADL] 官方桌面客户端安装包已通过签名校验，已打开安装程序。");
            if (installer is not null) await installer.WaitForExitAsync();
            await RefreshDesktopClientAsync();
            DesktopInstallStatusText.Text = _desktopClient is not null
                ? "已检测到桌面客户端，可以点击“启动桌面版”。"
                : "安装程序已关闭，尚未检测到客户端。可刷新检测或手动选择程序。";
        }
        catch (OperationCanceledException)
        {
            DesktopInstallStatusText.Text = cancellation.IsCancellationRequested
                ? "下载已取消或超时，可以重试。" : "下载已取消。";
        }
        catch (Exception ex)
        {
            DesktopInstallStatusText.Text = $"桌面客户端安装未完成：{ex.Message} 可使用“官方网站”手动下载。";
            AppendLog($"[ADL] 桌面客户端安装失败：{ex.Message}");
        }
        finally
        {
            _desktopDownloadCancellation = null;
            _desktopBusy = false;
            DesktopInstallProgressBar.Visibility = DesktopCancelInstallButton.Visibility = Visibility.Collapsed;
            if (installerPath is not null) DesktopClientService.TryDeleteInstaller(installerPath);
            UpdateDesktopUi();
            if (_desktopClient is null)
                DesktopHomeStatusText.Text = "尚未检测到桌面版，请在工作区查看安装结果。";
        }
    }

    private void CancelDesktopInstallButton_Click(object sender, RoutedEventArgs e) => _desktopDownloadCancellation?.Cancel();

    private async void RefreshDesktopButton_Click(object sender, RoutedEventArgs e) => await RefreshDesktopClientAsync();

    private async void SelectDesktopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode || _desktopBusy) return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(((App)Application.Current).MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        if (!DesktopClientService.IsClientExecutable(file.Path))
        {
            DesktopInstallStatusText.Text = "请选择安装目录中的 DeepSeek Harness.exe。安装文件不完整时请重新安装。";
            return;
        }
        _desktopManualPath = file.Path;
        LauncherSettings.SaveDesktopPath(file.Path);
        await RefreshDesktopClientAsync();
        DesktopInstallStatusText.Text = "已保存桌面客户端路径。";
    }

    private void DesktopWebsiteButton_Click(object sender, RoutedEventArgs e) => OpenUrl(DesktopClientService.WebsiteUrl);

    private void InitializeDesktopDemo()
    {
        _desktopClient = new DesktopClientInstallation(@"C:\Users\Demo\AppData\Local\Programs\DeepSeek Harness\DeepSeek Harness.exe",
            "0.1.7-rc.2", false);
        UpdateDesktopUi();
    }
}
