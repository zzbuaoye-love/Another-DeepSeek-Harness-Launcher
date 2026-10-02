using AnotherDSHL.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AnotherDSHL;

public sealed partial class MainPage
{
    private readonly NpmRegistryService _npmRegistries = new();
    private readonly DshRuntimeService _runtimes = new();
    private string _npmRegistryMode = LauncherSettings.LoadNpmRegistryMode();
    private string _npmRegistryUrl = LauncherSettings.LoadNpmRegistryUrl();
    private bool _npmChoicesReady;
    private CancellationTokenSource? _launchPreparation;
    private readonly System.Diagnostics.Stopwatch _launchElapsed = new();
    private readonly string _sessionLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AnotherDSHL", "Logs", $"launch-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.log");

    private void InitializeRegistryChoices()
    {
        NpmRegistryComboBox.SelectedItem = NpmRegistryComboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag as string == _npmRegistryMode) ?? NpmRegistryComboBox.Items[0];
        _npmRegistryMode = (NpmRegistryComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Auto";
        NpmRegistryUrlTextBox.Text = _npmRegistryUrl;
        NpmRegistryUrlTextBox.Visibility = _npmRegistryMode == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        _npmChoicesReady = true;
    }

    private void NpmRegistryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_npmChoicesReady || _demoMode) return;
        _npmRegistryMode = (NpmRegistryComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Auto";
        NpmRegistryUrlTextBox.Visibility = _npmRegistryMode == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        if (_npmRegistryMode != "Custom") LauncherSettings.SaveNpmRegistryMode(_npmRegistryMode);
        NpmRegistryStatusText.Text = "已选择下载源，下次准备资源时生效；可点击检测。";
    }

    private async void CheckNpmRegistry_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode) return;
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            if (_npmRegistryMode == "Custom")
            {
                _npmRegistryUrl = NpmRegistryService.NormalizeCustomUrl(NpmRegistryUrlTextBox.Text);
                LauncherSettings.SaveNpmRegistryUrl(_npmRegistryUrl);
            }
            LauncherSettings.SaveNpmRegistryMode(_npmRegistryMode);
            NpmRegistryStatusText.Text = "正在检测 npm 源…";
            var registry = await _npmRegistries.SelectAsync(_npmRegistryMode, _npmRegistryUrl, AppendLog, force: true);
            NpmRegistryStatusText.Text = $"当前可用源：{registry.Name} · {registry.Url}";
        }
        catch (Exception ex) { NpmRegistryStatusText.Text = ex.Message; }
        finally { button.IsEnabled = true; }
    }

    private Task<NpmRegistry> SelectNpmRegistryAsync(CancellationToken token = default) =>
        _npmRegistries.SelectAsync(_npmRegistryMode, _npmRegistryUrl, AppendLog, token);

    private void UpdateVersionSelectionUi()
    {
        if (HomeVersionPanel is null) return;
        DshVersionComboBox.IsEnabled = _activePack is null && !_starting && !_serviceReady && _service is not { HasExited: false };
        HomeVersionText.Text = _activePack is { } selectedPack ? $"v{selectedPack.DshVersion} · 整合包"
            : _selectedDshVersion.Length == 0 ? "默认 · 最新版" : $"v{_selectedDshVersion}";
        UpdateWorkspaceConfigurationText();
        DshVersionStatusText.Text = _activePack is { } pack
            ? $"整合包锁定 DSH v{pack.DshVersion}；不自动更新"
            : _selectedDshVersion.Length == 0 ? "默认：每次启动检查最新版；已是最新版则直接复用"
            : $"锁定 v{_selectedDshVersion}；不自动更新";
        PackActivationPanel.Visibility = _activePack is not null && !_desktopLaunchMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void VerifyActivePack_Click(object sender, RoutedEventArgs e)
    {
        if (_activePack is not { } pack || _demoMode) return;
        try
        {
            var evidence = GetWorkspacePackEngine().VerifyActivation(pack);
            PackActivationText.Text = (_serviceReady && _service is { HasExited: false }
                ? "整合包服务已就绪 · " : "实例检查通过，启动后可在 Web 插件管理中确认 · ") + evidence.Summary;
            AppendLog($"[ADL] 整合包检查通过：{evidence.Summary}");
            AppendLog($"[ADL] DSH_HOME：{evidence.Home}；Profile：{evidence.ProfileDirectory}");
        }
        catch (Exception ex)
        {
            PackActivationText.Text = $"整合包检查失败：{ex.Message}";
            AppendLog("[ADL] " + PackActivationText.Text);
        }
    }
}
