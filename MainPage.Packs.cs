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
    private string? _packProfilePath;
    private string? _packOutputPath;
    private bool _packExporting;
    private string? _packFilePath;

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
        DownloadPackButton.IsEnabled = pack.CanDownload && !_packInstalling && !_demoMode;
        UpdatePackFileUi();
        RefreshInstalledPacks();
        PackActionStatusText.Text = pack.CanDownload
            ? "下载并校验后，在启动器中安装到独立实例。已下载的包无需重复下载。"
            : pack.IsDspack ? "此条目缺少有效的大小或 SHA-256 校验信息，请到仓库查看安装方式。"
                : "此条目是历史格式，请到仓库查看安装方式。";
    }

    private void UpdatePackFileUi()
    {
        var path = !_demoMode && _selectedPack is { } pack ? PackForgeMarketService.GetLocalPath(pack) : null;
        _packFilePath = path is not null && File.Exists(path) ? path : null;
        ShowPackFileButton.Visibility = _packFilePath is null ? Visibility.Collapsed : Visibility.Visible;
        PackDownloadedPathText.Visibility = ShowPackFileButton.Visibility;
        PackDownloadedPathText.Text = _packFilePath is null ? "" : $"已下载文件：{_packFilePath}";
        DownloadPackButton.Content = _packFilePath is null ? "下载并安装" : "安装已下载包";
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
        if (_demoMode || _packInstalling || _packExporting || _selectedPack is not { } pack || !pack.CanDownload) return;
        await InstallPackInLauncherAsync(async token =>
        {
            PackInstallProgressText.Text = "正在下载并校验整合包…";
            var path = await PackForgeMarketService.DownloadVerifiedAsync(pack, token);
            AppendLog($"[ADL] 整合包已通过大小与 SHA-256 校验：{path}");
            return path;
        }, PackActionStatusText);
    }
    private void OpenPackRepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPack is { } pack) OpenUrl(pack.RepositoryUrl);
    }

    private void ShowPackFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode || _packFilePath is null) return;
        try { ShowDownloadedPack(_packFilePath); }
        catch (Exception ex) { PackActionStatusText.Text = $"无法定位文件：{ex.Message}"; }
    }

    private static void ShowDownloadedPack(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("整合包文件不存在。", path);
        using var process = Process.Start(new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = true, Arguments = $"/select,\"{Path.GetFullPath(path)}\""
        });
    }

    private async void OpenLocalPackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode || _packInstalling || _packExporting) return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".dspack");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(((App)Application.Current).MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await InstallPackInLauncherAsync(_ => Task.FromResult(file.Path), PackManagerStatusText);
    }

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
        if (_demoMode || _packExporting || _packInstalling || _packRemoving || _installingPlugin) return;
        if (_packProfilePath is null || (!preview && _packOutputPath is null))
        {
            PackExportStatusText.Text = preview ? "请先选择 Profile 目录。" : "请先选择 Profile 和输出目录。";
            return;
        }
        if (!preview)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "导出 Profile 整合包",
                Content = $"来源：{_packProfilePath}\n输出：{_packOutputPath}\n\n内置引擎将扫描并过滤敏感文件，生成 .dspack 整合包。",
                PrimaryButtonText = "导出", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        _packExporting = true;
        PreviewPackButton.IsEnabled = ExportPackButton.IsEnabled = false;
        try
        {
            var tools = await GetPackToolsAsync();
            PackExportStatusText.Text = preview ? "正在扫描 Profile…" : "正在导出整合包…";
            var result = preview
                ? await _packEngine.InspectProfileAsync(_packProfilePath, tools.Node)
                : await _packEngine.ExportAsync(_packProfilePath, _packOutputPath!, _selectedDshVersion, tools.Node,
                    new Progress<PackEngineProgress>(message => AppendLog($"[PackForge] {message.Detail}")));
            var message = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            PackExportStatusText.Text = "完成。\n" + (message.Length > 2400 ? message[..2400] + "\n…" : message);
            AppendLog($"[ADL] 内置引擎{(preview ? "预览" : "导出")}完成。");
        }
        catch (Exception ex)
        {
            PackExportStatusText.Text = $"操作未完成：{ex.Message}";
            AppendLog($"[ADL] 整合包导出操作失败：{ex.Message}");
        }
        finally
        {
            _packExporting = false;
            PreviewPackButton.IsEnabled = ExportPackButton.IsEnabled = true;
        }
    }
}
