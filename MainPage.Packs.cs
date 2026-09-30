using System.Diagnostics;
using AnotherDSHL.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace AnotherDSHL;

public sealed partial class MainPage
{
    private IReadOnlyList<MarketPack> _marketPacks = Array.Empty<MarketPack>();
    private MarketPack? _selectedPack;
    private bool _packsLoaded;
    private bool _packsLoading;
    private bool _packDownloading;
    private string? _packProfilePath;
    private string? _packOutputPath;
    private bool _packExporting;

    private async Task RefreshPacksAsync()
    {
        if (_packsLoading || _demoMode) return;
        _packsLoading = true;
        RefreshPacksButton.IsEnabled = false;
        PacksStatusText.Text = "正在读取官方整合包市场…";
        try
        {
            _marketPacks = await PackForgeMarketService.LoadAsync();
            _packsLoaded = true;
            PacksStatusText.Text = $"已收录 {_marketPacks.Count} 个整合包 · 数据来自 DSH PackForge 市场";
            FilterPacks();
        }
        catch (Exception ex)
        {
            PacksStatusText.Text = $"市场加载失败：{ex.Message}";
            AppendLog($"[ADL] 整合包市场加载失败：{ex.Message}");
        }
        finally
        {
            _packsLoading = false;
            RefreshPacksButton.IsEnabled = true;
        }
    }

    private async void RefreshPacksButton_Click(object sender, RoutedEventArgs e) => await RefreshPacksAsync();

    private void PacksSearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => FilterPacks();

    private void FilterPacks()
    {
        if (PacksList is null || PacksSearchTextBox is null || PacksEmptyText is null) return;
        var query = PacksSearchTextBox.Text.Trim();
        var filtered = _marketPacks.Where(pack => query.Length == 0 ||
            pack.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            pack.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            pack.Author.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            pack.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        PacksList.ItemsSource = filtered;
        PacksEmptyText.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PacksResultText.Text = $"显示 {filtered.Length} / {_marketPacks.Count} 个整合包";
    }

    private void PacksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedPack = PacksList.SelectedItem as MarketPack;
        PackDetailCard.Visibility = _selectedPack is null ? Visibility.Collapsed : Visibility.Visible;
        if (_selectedPack is not { } pack)
        {
            PackCatalogView.Visibility = Visibility.Visible;
            return;
        }
        PackCatalogView.Visibility = Visibility.Collapsed;
        PacksView.ChangeView(null, 0, null, true);
        PackDetailTitle.Text = $"{pack.Title}  v{pack.Version}";
        PackDetailMeta.Text = $"{pack.Author} · {pack.Owner}/{pack.Repo} · manifest v{pack.ManifestVersion} · " +
                              (pack.Type == "dshhome" ? "DSH_HOME" : "Profile") +
                              (pack.DshVersion.Length > 0 ? $" · DSH {pack.DshVersion}" : "");
        PackDetailDescription.Text = pack.Description.Length > 0 ? pack.Description : "作者未提供描述。";
        PackDetailContents.Text = pack.Type == "dshhome"
            ? $"{pack.ProfileCount} 个 Profile · {pack.BundleCount} 个 Bundle · {pack.DepCount} 个依赖"
            : $"{pack.BundleCount} 个 Bundle · {pack.DepCount} 个依赖";
        DownloadPackButton.IsEnabled = pack.CanDownload && !_packDownloading && !_demoMode;
        PackActionStatusText.Text = pack.CanDownload
            ? "下载后会核对大小和 SHA-256，再通过 .dspack 文件关联打开管理器。"
            : pack.IsDspack ? "此条目缺少有效的大小或 SHA-256 校验信息，请到仓库查看安装方式。"
                : "此条目是历史格式，请到仓库查看安装方式。";
    }

    private void BackToPacks_Click(object sender, RoutedEventArgs e)
    {
        PackDetailCard.Visibility = Visibility.Collapsed;
        PackCatalogView.Visibility = Visibility.Visible;
        PacksList.SelectedItem = null;
        PacksView.ChangeView(null, 0, null, true);
    }

    private void PackTab_Click(object sender, RoutedEventArgs e)
    {
        var browse = (sender as Button)?.Tag as string == "browse";
        PacksBrowsePanel.Visibility = browse ? Visibility.Visible : Visibility.Collapsed;
        PacksLocalPanel.Visibility = browse ? Visibility.Collapsed : Visibility.Visible;
        BrowsePacksTab.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(browse ? (byte)0x24 : (byte)0, 255, 255, 255));
        LocalPacksTab.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(browse ? (byte)0 : (byte)0x24, 255, 255, 255));
        BrowsePacksTab.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(browse ? (byte)0xFF : (byte)0xAF, 255, 255, 255));
        LocalPacksTab.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(browse ? (byte)0xAF : (byte)0xFF, 255, 255, 255));
        PacksView.ChangeView(null, 0, null, true);
    }

    private async void DownloadPackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode || _packDownloading || _selectedPack is not { } pack || !pack.CanDownload) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "下载整合包",
            Content = $"{pack.Title} v{pack.Version}\n作者：{pack.Author}\n大小：{pack.Size:N0} 字节\n\n下载并校验后，将交给 DSH PackForge 管理器查看和安装。安装操作在管理器中确认。",
            PrimaryButtonText = "下载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _packDownloading = true;
        DownloadPackButton.IsEnabled = false;
        PackActionStatusText.Text = "正在下载并校验整合包…";
        try
        {
            var path = await PackForgeMarketService.DownloadVerifiedAsync(pack);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            PackActionStatusText.Text = "已通过大小与 SHA-256 校验，已交给系统的 .dspack 文件关联。";
            AppendLog($"[ADL] 整合包校验完成：{pack.Owner}/{pack.Repo} v{pack.Version}");
        }
        catch (Exception ex)
        {
            PackActionStatusText.Text = $"未能打开整合包：{ex.Message} 如未安装管理器，请先获取 DSH PackForge 管理器。";
            AppendLog($"[ADL] 整合包下载或打开失败：{ex.Message}");
        }
        finally
        {
            _packDownloading = false;
            DownloadPackButton.IsEnabled = _selectedPack?.CanDownload == true;
        }
    }

    private void OpenPackRepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPack is { } pack) OpenUrl(pack.RepositoryUrl);
    }

    private async void OpenLocalPackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode) return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".dspack");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(((App)Application.Current).MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PacksStatusText.Text = $"无法打开本地包：{ex.Message} 请安装 DSH PackForge 管理器。";
        }
    }

    private void OpenPackForgeButton_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://github.com/DSH-PackForge/dsh-packforge-app/releases");

    private async void SelectPackProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode) return;
        var folder = await PickPackFolderAsync();
        if (folder is null) return;
        _packProfilePath = folder;
        PackProfilePathText.Text = folder;
    }

    private async void SelectPackOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode) return;
        var folder = await PickPackFolderAsync();
        if (folder is null) return;
        _packOutputPath = folder;
        PackOutputPathText.Text = folder;
    }

    private async Task<string?> PickPackFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(((App)Application.Current).MainWindow));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private async void PreviewPackButton_Click(object sender, RoutedEventArgs e) =>
        await RunPackCommandAsync(preview: true);

    private async void ExportPackButton_Click(object sender, RoutedEventArgs e) =>
        await RunPackCommandAsync(preview: false);

    private async Task RunPackCommandAsync(bool preview)
    {
        if (_demoMode || _packExporting) return;
        if (_packProfilePath is null || (!preview && _packOutputPath is null))
        {
            PackExportStatusText.Text = preview ? "请先选择 Profile 目录。" : "请先选择 Profile 和输出目录。";
            return;
        }
        RefreshProcessPath();
        var cli = FindOnPath("dspack.exe");
        if (cli is null)
        {
            PackExportStatusText.Text = "未找到 dspack CLI。请安装 DSH PackForge Setup，然后重新打开启动器。";
            return;
        }
        if (!preview)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "导出 Profile 整合包",
                Content = $"来源：{_packProfilePath}\n输出：{_packOutputPath}\n\nPackForge 将扫描并过滤敏感文件，生成 manifest v5 的 .dspack。",
                PrimaryButtonText = "导出",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        _packExporting = true;
        PreviewPackButton.IsEnabled = ExportPackButton.IsEnabled = false;
        PackExportStatusText.Text = preview ? "正在扫描 Profile…" : "正在导出整合包…";
        try
        {
            var start = new ProcessStartInfo(cli)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(preview ? "inspect" : "pack");
            start.ArgumentList.Add(_packProfilePath);
            if (!preview)
            {
                start.ArgumentList.Add("--out");
                start.ArgumentList.Add(_packOutputPath!);
            }
            using var process = new Process { StartInfo = start };
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("PackForge 命令超过 10 分钟，已停止。");
            }
            var message = (await output + "\n" + await errors).Trim();
            PackExportStatusText.Text = (process.ExitCode == 0 ? "完成。\n" : $"未完成（退出代码 {process.ExitCode}）。\n") +
                                        (message.Length > 2400 ? message[..2400] + "\n…" : message);
            AppendLog($"[ADL] PackForge {(preview ? "预览" : "导出")}退出代码：{process.ExitCode}");
        }
        catch (Exception ex)
        {
            PackExportStatusText.Text = $"PackForge 命令失败：{ex.Message}";
            AppendLog($"[ADL] PackForge 命令失败：{ex.Message}");
        }
        finally
        {
            _packExporting = false;
            PreviewPackButton.IsEnabled = ExportPackButton.IsEnabled = true;
        }
    }
}
