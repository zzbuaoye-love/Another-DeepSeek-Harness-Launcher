using System.Diagnostics;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using AnotherDSHL.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace AnotherDSHL;

public sealed partial class MainPage : Page
{
    private static readonly Uri DocsAddress = new("https://deepseek-harness.github.io/deepseek-harness/");
    private static readonly Regex DshWebUrlRegex = new(@"dsh\s+web:\s*(?<url>https?://[^\s()]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] DemoScenes =
    {
        "欢迎", "准备启动", "下载资源", "启动 Web 服务", "服务就绪",
        "工作区与版本", "社区插件", "插件详情", "设置", "关于"
    };
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
    private readonly bool _demoMode = Environment.GetCommandLineArgs().Any(arg =>
        arg.Equals("--demo", StringComparison.OrdinalIgnoreCase));
    private readonly bool _demoClean = Environment.GetCommandLineArgs().Any(arg =>
        arg.Equals("--demo-clean", StringComparison.OrdinalIgnoreCase));
    private int _demoStep;
    private int _demoPlaybackSessionId;
    private Process? _service;
    private string _webSessionUrl = LauncherSettings.LoadLastWebUrl();
    private int _configuredWebPort = LauncherSettings.LoadWebPort();
    private int _activeWebPort;
    private bool _serviceReady;
    private bool _stoppingService;
    private bool _launchTipsActive;
    private int _tipsSessionId;
    private double _launchProgressValue;
    private string? _npxPath;
    private string? _nodePath;
    private string _manualNodePath = LauncherSettings.LoadNodePath();
    private bool _manualNodeMode = LauncherSettings.LoadNodeMode() == "Manual";
    private bool _nodeModeReady;
    private bool _nodeSupported;
    private bool _starting;
    private bool _appearanceReady;
    private bool _initialized;
    private string? _dshVersion;
    private string? _runningDshVersion;
    private string _selectedDshVersion = LauncherSettings.LoadDshVersion();
    private bool _versionChoicesReady;
    private string _nodeVersionText = "";
    private bool _repairingNode;
    private bool _docsInitialized;
    private int _logLines = 1;
    private bool _catalogLoaded;
    private bool _catalogLoading;
    private bool _installingPlugin;
    private CatalogPlugin? _selectedPlugin;
    private readonly HashSet<string> _installedPlugins = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<CatalogPlugin> _catalogItems = Array.Empty<CatalogPlugin>();

    public MainPage()
    {
        InitializeComponent();
        IsTabStop = true;
        Loaded += (s, e) => Focus(FocusState.Programmatic);
        PointerPressed += (s, e) => Focus(FocusState.Programmatic);
        AddHandler(KeyDownEvent, new KeyEventHandler(DemoNavigation_KeyDown), true);
        if (_demoMode) _selectedDshVersion = "0.1.5-rc.3";
        _activeWebPort = _configuredWebPort;
        WebPortTextBox.Text = _configuredWebPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        WebPortStatusText.Text = $"当前设置：{_configuredWebPort}";
        DshVersionComboBox.Items.Add(new ComboBoxItem { Content = "npm 默认发布版", Tag = "" });
        if (DshVersionService.IsSafeVersion(_selectedDshVersion))
            DshVersionComboBox.Items.Add(new ComboBoxItem { Content = $"v{_selectedDshVersion} · 已锁定", Tag = _selectedDshVersion });
        DshVersionComboBox.SelectedIndex = _selectedDshVersion.Length == 0 ? 0 : 1;
        _versionChoicesReady = true;
        WorkspaceTextBox.Text = _demoMode ? @"C:\Demo\SampleProject" : LauncherSettings.LoadWorkspace();
        CatalogUrlTextBox.Text = _demoMode ? "" : LauncherSettings.LoadCatalogUrl();
        UpdateWorkspaceHint();
        Loaded += async (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            var mode = LauncherSettings.LoadBackdropMode();
            if (mode == "Mica")
                MicaRadio.IsChecked = true;
            else
                AcrylicRadio.IsChecked = true;
            _appearanceReady = true;
            ((MainWindow)((App)Application.Current).MainWindow).SetBackdropMode(mode);
            if (_manualNodeMode)
                ManualNodeRadio.IsChecked = true;
            else
                AutoNodeRadio.IsChecked = true;
            UpdateNodeModeUi();
            _nodeModeReady = true;
            var infoVer = typeof(MainPage).Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            AboutVersionText.Text = $"版本 {(!string.IsNullOrWhiteSpace(infoVer) ? infoVer : typeof(MainPage).Assembly.GetName().Version?.ToString(3) ?? "0.0.1 Beta")}";
            ShellNavigation.SelectedItem = HomeItem;
            if (_demoMode)
            {
                InitializeDemoMode();
                return;
            }
            await CheckEnvironmentAsync();
            _ = RefreshDshVersionsAsync();
        };
    }

    private void InitializeDemoMode()
    {
        _configuredWebPort = _activeWebPort = 3080;
        WebPortTextBox.Text = "3080";
        WebPortStatusText.Text = "演示模式 · 不修改本机设置";
        WorkspaceNameText.Text = "SampleProject";
        WorkspaceHint.Visibility = Visibility.Collapsed;
        NodeStatusText.Text = "v24.13.0 · 满足版本要求（演示）";
        NpxStatusText.Text = "已找到，可以启动官方 Harness 包（演示）";
        _dshVersion = "0.1.5-rc.3";
        _runningDshVersion = "0.1.5-rc.3";
        WorkspaceDshText.Text = "演示版本 · v0.1.5-rc.3";
        DshVersionStatusText.Text = "演示模式 · 版本选择不会修改本机配置";
        _catalogItems =
        [
            new CatalogPlugin("@deepseek-ai/dsh-web-app", "0.1.5-rc.3", "官方 Web 运行界面与交互能力。",
                "DeepSeek 官方 · 演示", "@deepseek-ai/dsh-web-app", null, true, "deepseek-ai", "core",
                RepositoryUrl: "https://github.com/deepseek-ai/deepseek-harness"),
            new CatalogPlugin("@deepseek-ai/dsh-base", "0.1.5-rc.3", "官方基础能力与默认插件组合。",
                "DeepSeek 官方 · 演示", "@deepseek-ai/dsh-base", null, true, "deepseek-ai", "core",
                RepositoryUrl: "https://github.com/deepseek-ai/deepseek-harness"),
            new CatalogPlugin("dsh-web", "版本以安装时为准", "社区维护的 Web 插件合集，包含任务看板、远程界面与更多扩展。",
                "社区项目 · 演示数据", "@linxin666/dsh-web-all", "https://github.com/zhu1090093659/dsh-web",
                false, "zhu1090093659", "ui", 8043,
                RepositoryUrl: "https://github.com/zhu1090093659/dsh-web", PublisherKind: "repository")
        ];
        CatalogStatusText.Text = "演示目录 · 不联网、不安装插件";
        _catalogLoaded = true;
        FilterCatalog();
        PluginsList.Items.Add("@deepseek-ai/dsh-web-app   ·   已启用（演示）");
        PluginsList.Visibility = Visibility.Visible;
        PluginsEmptyText.Visibility = Visibility.Collapsed;
        DemoControls.Visibility = _demoClean ? Visibility.Collapsed : Visibility.Visible;
        var startArg = Environment.GetCommandLineArgs().FirstOrDefault(arg =>
            arg.StartsWith("--demo-step=", StringComparison.OrdinalIgnoreCase));
        var startStep = startArg is not null && int.TryParse(startArg[12..], out var parsed) ? parsed : 0;
        SetDemoStep(startStep);

        var demoViewArg = Environment.GetCommandLineArgs().FirstOrDefault(arg =>
            arg.StartsWith("--demo-view=", StringComparison.OrdinalIgnoreCase))?[12..].ToLowerInvariant();
        if (demoViewArg is "workspace-dsh" or "version")
        {
            SetDemoStep(5);
            EnvironmentView.Loaded += (_, _) => EnvironmentView.ChangeView(null, 280, null, true);
            DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(250);
                EnvironmentView.ChangeView(null, 280, null, true);
            });
        }
        else if (demoViewArg is "plugins-installed" or "installed")
        {
            SetDemoStep(6);
            PluginTab_Click(InstalledPluginsTab, new RoutedEventArgs());
        }
        else if (demoViewArg is "plugins-sources" or "sources" or "catalog-sources")
        {
            SetDemoStep(6);
            PluginTab_Click(PluginSourcesTab, new RoutedEventArgs());
        }

        var captureArg = Environment.GetCommandLineArgs().FirstOrDefault(arg =>
            arg.StartsWith("--demo-capture=", StringComparison.OrdinalIgnoreCase))?[15..];
        if (!string.IsNullOrWhiteSpace(captureArg))
        {
            var fullPath = Path.GetFullPath(captureArg);
            DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(900);
                try
                {
                    var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                    await rtb.RenderAsync(this);
                    var pixels = await rtb.GetPixelsAsync();
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    if (File.Exists(fullPath)) File.Delete(fullPath);
                    using var stream = File.OpenWrite(fullPath);
                    var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                        Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
                    encoder.SetPixelData(
                        Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                        Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                        (uint)rtb.PixelWidth,
                        (uint)rtb.PixelHeight,
                        96, 96,
                        pixels.ToArray());
                    await encoder.FlushAsync();
                }
                catch
                {
                }
                finally
                {
                    Environment.Exit(0);
                }
            });
        }
    }

    private void DemoPrevious_Click(object sender, RoutedEventArgs e) =>
        SetDemoStep((_demoStep + DemoScenes.Length - 1) % DemoScenes.Length);

    private void DemoNext_Click(object sender, RoutedEventArgs e) =>
        SetDemoStep((_demoStep + 1) % DemoScenes.Length);

    private void DemoNavigation_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_demoMode) return;
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Right:
            case Windows.System.VirtualKey.PageDown:
                DemoNext_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Left:
            case Windows.System.VirtualKey.PageUp:
                DemoPrevious_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private void SetDemoStep(int step, bool autoPlayback = false)
    {
        if (!_demoMode) return;
        if (!autoPlayback) ++_demoPlaybackSessionId;
        _demoStep = Math.Clamp(step, 0, DemoScenes.Length - 1);
        ++_tipsSessionId;
        _launchTipsActive = false;
        EnvironmentWarning.Visibility = Visibility.Collapsed;
        PluginDetailView.Visibility = Visibility.Collapsed;
        PluginCatalogView.Visibility = Visibility.Visible;
        CatalogList.SelectedItem = null;
        DemoStepText.Text = $"演示 {_demoStep + 1}/{DemoScenes.Length} · {DemoScenes[_demoStep]}";
        _serviceReady = _demoStep >= 4;
        _starting = _demoStep is >= 1 and <= 3;
        LaunchButton.IsEnabled = !_starting;
        LaunchButton.Content = _serviceReady ? "停止 Harness" : _starting ? "启动中…" : "启动 Harness";
        OpenWebButton.Visibility = _serviceReady ? Visibility.Visible : Visibility.Collapsed;
        OpenWebButton.IsEnabled = _serviceReady;
        LaunchProgress.IsActive = _starting;
        LaunchProgress.Visibility = _starting ? Visibility.Visible : Visibility.Collapsed;
        ServiceStatusText.Text = _demoStep switch
        {
            1 => "正在模拟检查启动资源…", 2 => "正在模拟下载资源…",
            3 => "正在模拟启动 Web 服务…", >= 4 => "Harness 正在运行 · 端口 3080（演示）",
            _ => "本地服务未启动（演示）"
        };
        UpdateBranding();
        ShellNavigation.SelectedItem = _demoStep switch
        {
            5 => ShellNavigation.MenuItems[1],
            6 or 7 => ShellNavigation.MenuItems[2],
            8 => SettingsItem,
            9 => AboutItem,
            _ => HomeItem
        };
        switch (_demoStep)
        {
            case 1: ShowLaunchStepTip("准备启动", "模拟检查 DSH 资源", 12); break;
            case 2: ShowLaunchStepTip("下载资源", "虚拟资源下载 0%", 30); break;
            case 3: ShowLaunchStepTip("启动 Web 服务", "模拟启动本机服务", 82); break;
            case 4: _ = ShowLaunchCompletedTipAsync(hold: !autoPlayback); break;
            case 7:
                CatalogList.SelectedItem = _catalogItems.First(item => item.Name == "dsh-web");
                break;
        }
    }

    private async Task PlayDemoStartupAsync()
    {
        var session = ++_demoPlaybackSessionId;
        SetDemoStep(1, autoPlayback: true);
        await Task.Delay(700);
        if (session != _demoPlaybackSessionId) return;

        SetDemoStep(2, autoPlayback: true);
        foreach (var percent in new[] { 10, 25, 42, 63, 81, 100 })
        {
            await Task.Delay(340);
            if (session != _demoPlaybackSessionId) return;
            ShowLaunchStepTip("下载资源", $"虚拟资源下载 {percent}%", 30 + percent * 0.45);
        }

        await Task.Delay(400);
        if (session != _demoPlaybackSessionId) return;
        SetDemoStep(3, autoPlayback: true);
        await Task.Delay(950);
        if (session != _demoPlaybackSessionId) return;
        SetDemoStep(4, autoPlayback: true);
    }

    public void TogglePane() => ShellNavigation.IsPaneOpen = !ShellNavigation.IsPaneOpen;

    private void ShellNavigation_SelectionChanged(
        NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var section = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        if (section is null) return;
        HomeView.Visibility = section == "home" ? Visibility.Visible : Visibility.Collapsed;
        EnvironmentView.Visibility = section == "environment" ? Visibility.Visible : Visibility.Collapsed;
        PluginsView.Visibility = section == "plugins" ? Visibility.Visible : Visibility.Collapsed;
        LogsView.Visibility = section == "logs" ? Visibility.Visible : Visibility.Collapsed;
        AppearanceView.Visibility = section == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        DocsView.Visibility = section == "docs" ? Visibility.Visible : Visibility.Collapsed;
        AboutView.Visibility = section == "about" ? Visibility.Visible : Visibility.Collapsed;
        if (section == "plugins")
        {
            if (!_demoMode)
            {
                RefreshPlugins();
                if (!_catalogLoaded) _ = RefreshCatalogAsync();
            }
        }
        if (section == "home" && _initialized && !_demoMode) _ = CheckEnvironmentAsync();
        if (section == "docs" && !_demoMode) _ = InitializeDocsBrowserAsync();
    }

    private async Task InitializeDocsBrowserAsync()
    {
        if (_docsInitialized) return;
        _docsInitialized = true;
        DocsStatusText.Text = "正在加载官方文档…";
        try
        {
            await DocsBrowser.EnsureCoreWebView2Async();
            DocsBrowser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) &&
                    uri.Scheme is "https" or "http")
                    DocsBrowser.Source = uri;
            };
            DocsBrowser.Source = DocsAddress;
        }
        catch (Exception ex)
        {
            _docsInitialized = false;
            DocsStatusText.Text = "文档加载失败，请检查网络或 WebView2 运行时。";
            AppendLog($"[ADL] 文档加载失败：{ex.Message}");
        }
    }

    private void DocsBrowser_NavigationCompleted(object sender, object args)
    {
        DocsBackButton.IsEnabled = DocsBrowser.CanGoBack;
        DocsForwardButton.IsEnabled = DocsBrowser.CanGoForward;
        DocsStatusText.Text = DocsBrowser.Source?.Host ?? "DeepSeek Harness 官方文档";
    }

    private void DocsBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (DocsBrowser.CanGoBack) DocsBrowser.GoBack();
    }

    private void DocsForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (DocsBrowser.CanGoForward) DocsBrowser.GoForward();
    }

    private void DocsHomeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_docsInitialized)
            DocsBrowser.Source = DocsAddress;
        else
            _ = InitializeDocsBrowserAsync();
    }

    private void AboutDocsButton_Click(object sender, RoutedEventArgs e)
        => ShellNavigation.SelectedItem = DocsItem;

    private void AboutProjectGitHub_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://github.com/zzbuaoye-love/Another-DeepSeek-Harness-Launcher");

    private void AboutDeveloperGitHub_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://github.com/zzbuaoye-love");

    private void EnvironmentButton_Click(object sender, RoutedEventArgs e)
        => ShellNavigation.SelectedItem = ShellNavigation.MenuItems[1];

    private void BackdropRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (!_appearanceReady)
            return;
        var mode = ReferenceEquals(sender, MicaRadio) ? "Mica" : "Acrylic";
        ((MainWindow)((App)Application.Current).MainWindow).SetBackdropMode(mode);
        if (!_demoMode) LauncherSettings.SaveBackdropMode(mode);
    }

    private async void SaveWebPortButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode)
        {
            WebPortStatusText.Text = "演示模式不会修改真实端口设置。";
            return;
        }
        if (!int.TryParse(WebPortTextBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            WebPortStatusText.Text = "请输入 1–65535 之间的端口。";
            return;
        }
        _configuredWebPort = port;
        LauncherSettings.SaveWebPort(port);
        if (_serviceReady || _service is { HasExited: false } || _starting)
        {
            WebPortStatusText.Text = $"已保存 {port}；当前服务仍使用 {_activeWebPort}，下次启动生效。";
            return;
        }
        _activeWebPort = port;
        WebPortStatusText.Text = $"已保存 {port}。";
        await CheckEnvironmentAsync();
    }

    private async void NodeModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_demoMode) return;
        if (!_nodeModeReady) return;
        var manual = ReferenceEquals(sender, ManualNodeRadio);
        if (_manualNodeMode == manual) return;
        _manualNodeMode = manual;
        LauncherSettings.SaveNodeMode(manual ? "Manual" : "Auto");
        NodeSelectionStatusText.Text = "";
        UpdateNodeModeUi();
        await CheckEnvironmentAsync();
    }

    private async void RefreshDshVersionsButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshDshVersionsAsync();

    private async Task RefreshDshVersionsAsync()
    {
        if (_demoMode) return;
        DshVersionStatusText.Text = "正在查询 npm 版本…";
        try
        {
            var catalog = await DshVersionService.GetAvailableAsync();
            _versionChoicesReady = false;
            DshVersionComboBox.Items.Clear();
            DshVersionComboBox.Items.Add(new ComboBoxItem
            {
                Content = $"npm 默认发布版 · v{catalog.Latest}", Tag = ""
            });
            foreach (var version in catalog.Versions)
                DshVersionComboBox.Items.Add(new ComboBoxItem { Content = $"v{version}", Tag = version });
            if (_selectedDshVersion.Length > 0 && !catalog.Versions.Contains(_selectedDshVersion))
                DshVersionComboBox.Items.Add(new ComboBoxItem
                {
                    Content = $"v{_selectedDshVersion} · 已锁定", Tag = _selectedDshVersion
                });
            DshVersionComboBox.SelectedItem = DshVersionComboBox.Items.OfType<ComboBoxItem>()
                .First(item => string.Equals(item.Tag as string, _selectedDshVersion, StringComparison.Ordinal));
            _versionChoicesReady = true;
            DshVersionStatusText.Text = _selectedDshVersion.Length == 0
                ? $"npm 默认发布版：v{catalog.Latest}。也可锁定上方的预发布版本。"
                : $"已锁定 v{_selectedDshVersion}；下次启动将使用此版本。";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException)
        {
            DshVersionStatusText.Text = $"暂时无法获取版本列表：{ex.Message}；保留当前选择。";
        }
    }

    private void DshVersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_versionChoicesReady || DshVersionComboBox.SelectedItem is not ComboBoxItem choice) return;
        var version = choice.Tag as string ?? "";
        if (version.Length > 0 && !DshVersionService.IsSafeVersion(version)) return;
        _selectedDshVersion = version;
        if (!_demoMode) LauncherSettings.SaveDshVersion(version);
        DshVersionStatusText.Text = version.Length == 0
            ? "已选择 npm 默认发布版；下次启动生效。"
            : $"已锁定 v{version}；下次启动生效。";
    }

    private void UpdateNodeModeUi()
    {
        AutoNodePathText.Visibility = _manualNodeMode ? Visibility.Collapsed : Visibility.Visible;
        ManualNodePicker.Visibility = _manualNodeMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task CheckEnvironmentAsync()
    {
        NodeStatusText.Text = "检测中…";
        NpxStatusText.Text = "检测中…";
        var manualValid = _manualNodeMode && !string.IsNullOrWhiteSpace(_manualNodePath) &&
                          File.Exists(_manualNodePath) &&
                          string.Equals(Path.GetFileName(_manualNodePath), "node.exe", StringComparison.OrdinalIgnoreCase);
        _nodePath = _manualNodeMode ? (manualValid ? _manualNodePath : null) : FindOnPath("node.exe");
        var versionText = _nodePath is null ? "" : await ReadNodeVersionAsync(_nodePath);
        _nodeVersionText = versionText;
        _nodeSupported = Version.TryParse(versionText.TrimStart('v', 'V'), out var version)
                         && ((version.Major == 22 && version >= new Version(22, 19))
                             || version.Major >= 24);
        NodeStatusText.Text = string.IsNullOrEmpty(versionText)
            ? _manualNodeMode ? "未选择可用的 node.exe。请在上方手动选择。"
                : "未检测到本机 Node.js。请安装 Node.js 22.19+ / 24+。"
            : _nodeSupported ? $"{versionText} · 满足版本要求" : $"{versionText} · 需要 22.19+ 或 24+";
        _npxPath = _manualNodeMode
            ? manualValid ? Path.Combine(Path.GetDirectoryName(_nodePath!)!, "npx.cmd") : null
            : FindOnPath("npx.cmd");
        if (_npxPath is not null && !File.Exists(_npxPath)) _npxPath = null;
        NpxStatusText.Text = _npxPath is null ? "未检测到 npx。请检查 Node.js 安装。" : "已找到，可以启动官方 Harness 包";
        AutoNodePathText.Text = _manualNodeMode ? "" : _nodePath is not null
            ? $"自动检测：{_nodePath}" : "未找到 node.exe，可切换到手动选择。";
        NodePathText.Text = manualValid ? $"已选择：{_manualNodePath}"
            : string.IsNullOrWhiteSpace(_manualNodePath) ? "尚未选择 node.exe。"
            : $"已保存的路径不可用：{_manualNodePath}";
        ToolTipService.SetToolTip(AutoNodePathText, AutoNodePathText.Text);
        ToolTipService.SetToolTip(NodePathText, NodePathText.Text);
        _dshVersion = FindInstalledDshVersion(WorkspaceTextBox.Text.Trim(), _npxPath);
        var installed = _dshVersion is not null;
        WorkspaceDshText.Text = installed ? $"本机检测到缓存版本 · v{_dshVersion}" : "本机未找到缓存版本，将在启动时获取";

        if (!_starting && !_stoppingService)
        {
            var running = await IsWebServerRespondingAsync();
            if (running)
            {
                _serviceReady = true;
                ApplyRunningUiState();
            }
            else if (_service is null || _service.HasExited)
            {
                _serviceReady = false;
                _runningDshVersion = null;
                LaunchButton.IsEnabled = true;
                LaunchButton.Content = "启动 Harness";
                ServiceStatusText.Text = "本地服务未启动";
                OpenWebButton.IsEnabled = false;
                OpenWebButton.Visibility = Visibility.Collapsed;
                OpenPluginManagerButton.IsEnabled = false;
            }
        }

        UpdateBranding();
        var nodeNeedsRepair = !_nodeSupported || _npxPath is null;
        NodeRepairButton.Visibility = nodeNeedsRepair ? Visibility.Visible : Visibility.Collapsed;
        if (_launchTipsActive)
            return;

        var issues = new List<string>();
        if (!_nodeSupported) issues.Add(string.IsNullOrEmpty(versionText) ? "未安装 Node.js" : $"Node.js {versionText} 版本过低");
        if (_npxPath is null) issues.Add("缺少 npx");
        if (issues.Count > 0)
        {
            ShowEnvironmentIssueTip(string.Join(" · ", issues), nodeNeedsRepair);
        }
        else
        {
            EnvironmentWarning.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowEnvironmentIssueTip(string message, bool showRepairButton)
    {
        EnvironmentWarning.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1D, 0xFF, 0xFF, 0xFF));
        EnvironmentWarning.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        EnvironmentWarningIcon.Glyph = "\uE7BA";
        EnvironmentWarningIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xED, 0xE7, 0xDA));
        EnvironmentWarningTitle.Text = "环境未就绪";
        EnvironmentWarningTitle.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        EnvironmentWarningText.Text = message;
        EnvironmentWarningText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xBF, 0xFF, 0xFF, 0xFF));
        LaunchStepPercentText.Visibility = Visibility.Collapsed;
        LaunchStepProgressBar.Visibility = Visibility.Collapsed;
        WarningRepairButton.Visibility = showRepairButton ? Visibility.Visible : Visibility.Collapsed;
        EnvironmentWarning.Visibility = Visibility.Visible;
        ToolTipService.SetToolTip(EnvironmentWarning, message + "。在工作区页面查看详情。");
    }

    private void ShowLaunchStepTip(string title, string detail, double progress)
    {
        _launchTipsActive = true;
        _launchProgressValue = Math.Clamp(progress, 0, 99);
        EnvironmentWarning.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 0x3B, 0x82, 0xF6));
        EnvironmentWarning.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, 0x60, 0xA5, 0xFA));
        EnvironmentWarningIcon.Glyph = "\uE895";
        EnvironmentWarningIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x93, 0xC5, 0xFD));
        EnvironmentWarningTitle.Text = title;
        EnvironmentWarningTitle.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        EnvironmentWarningText.Text = detail;
        EnvironmentWarningText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        LaunchStepPercentText.Text = $"阶段 {(int)_launchProgressValue}%";
        LaunchStepPercentText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0x93, 0xC5, 0xFD));
        LaunchStepPercentText.Visibility = Visibility.Visible;
        LaunchStepProgressBar.Value = _launchProgressValue;
        LaunchStepProgressBar.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA));
        LaunchStepProgressBar.Visibility = Visibility.Visible;
        WarningRepairButton.Visibility = Visibility.Collapsed;
        EnvironmentWarning.Visibility = Visibility.Visible;
        ToolTipService.SetToolTip(EnvironmentWarning, $"{title}：{detail}");
    }

    private async Task ShowLaunchCompletedTipAsync(bool hold = false)
    {
        var session = ++_tipsSessionId;
        _launchTipsActive = true;
        _launchProgressValue = 100;
        EnvironmentWarning.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2E, 0x16, 0xA3, 0x4A));
        EnvironmentWarning.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x88, 0x4A, 0xDE, 0x80));
        EnvironmentWarningIcon.Glyph = "\uE73E";
        EnvironmentWarningIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80));
        EnvironmentWarningTitle.Text = "启动完成";
        EnvironmentWarningTitle.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x86, 0xEF, 0xAC));
        EnvironmentWarningText.Text = "DeepSeek Harness 本地 Web 服务已就绪";
        EnvironmentWarningText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0xD1, 0xFA, 0xE5));
        LaunchStepPercentText.Text = "100%";
        LaunchStepPercentText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x86, 0xEF, 0xAC));
        LaunchStepPercentText.Visibility = Visibility.Visible;
        LaunchStepProgressBar.Value = 100;
        LaunchStepProgressBar.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80));
        LaunchStepProgressBar.Visibility = Visibility.Visible;
        WarningRepairButton.Visibility = Visibility.Collapsed;
        EnvironmentWarning.Visibility = Visibility.Visible;
        ToolTipService.SetToolTip(EnvironmentWarning, "DeepSeek Harness 已启动完成。");
        if (hold) return;
        await Task.Delay(3200);
        if (_tipsSessionId == session)
        {
            _launchTipsActive = false;
            EnvironmentWarning.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowLaunchErrorTip(string detail)
    {
        ++_tipsSessionId;
        _launchTipsActive = false;
        ShowEnvironmentIssueTip(detail, false);
        EnvironmentWarningTitle.Text = "启动失败";
    }

    private void UpdateBranding()
    {
        var showDsh = _dshVersion is not null || _starting || _serviceReady || _service is { HasExited: false };
        BrandLogo.Width = BrandLogo.Height = showDsh ? 72 : 48;
        BrandLogo.Source = showDsh
            ? new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri("ms-appx:///Assets/DeepSeekHarness.svg"))
            : new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri("ms-appx:///Assets/AppIconHome.svg"));
        BrandTitle.Text = showDsh ? "DeepSeek Harness" : "Another";
        BrandSubtitle.Text = _serviceReady
            ? !string.IsNullOrEmpty(_runningDshVersion)
                ? $"服务运行中 · v{_runningDshVersion}"
                : $"服务运行中 · 端口 {_activeWebPort}"
            : _starting || _service is { HasExited: false }
                ? "正在准备本地服务"
                : _dshVersion is not null ? $"本机缓存 · v{_dshVersion}" : "Yet Another Deepseek Harness Launcher";
        ((MainWindow)((App)Application.Current).MainWindow).SetCornerBrandVisible(false);
    }

    private async void RepairNodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode) return;
        if (_repairingNode) return;
        var winget = FindOnPath("winget.exe");
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = string.IsNullOrWhiteSpace(_nodeVersionText) ? "安装 Node.js" : "修复 Node.js",
            Content = string.IsNullOrWhiteSpace(_nodeVersionText)
                ? "未检测到已在本机安装的 Node.js。可通过 WinGet 安装官方 Node.js LTS，安装程序可能请求管理员权限，或者你也可以手动选择本机已有的Nodejs路径"
                : $"当前 Node.js {_nodeVersionText} 不满足 DSH 要求，或缺少 npx。可通过 WinGet 安装或升级官方 Node.js LTS，也可以手动选择本机已有的 node.exe。",
            PrimaryButtonText = winget is null ? "打开官方下载页" : "使用 WinGet 修复",
            SecondaryButtonText = "切换到手动选择",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.None) return;
        if (choice == ContentDialogResult.Secondary)
        {
            ShellNavigation.SelectedItem = ShellNavigation.MenuItems[1];
            ManualNodeRadio.IsChecked = true;
            return;
        }
        if (winget is null)
        {
            OpenUrl("https://nodejs.org/en/download");
            return;
        }
        await InstallNodeWithWingetAsync(winget);
    }

    private async void SelectNodeButton_Click(object sender, RoutedEventArgs e) => await SelectNodePathAsync();

    private async Task SelectNodePathAsync()
    {
        if (!_manualNodeMode) return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(((App)Application.Current).MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        if (!string.Equals(file.Name, "node.exe", StringComparison.OrdinalIgnoreCase))
        {
            NodeSelectionStatusText.Text = "请选择 node.exe。";
            return;
        }
        var version = await ReadNodeVersionAsync(file.Path);
        if (!Version.TryParse(version.TrimStart('v', 'V'), out _))
        {
            NodeSelectionStatusText.Text = "所选文件无法作为 Node.js 运行。";
            return;
        }
        _manualNodePath = file.Path;
        LauncherSettings.SaveNodePath(file.Path);
        await CheckEnvironmentAsync();
        NodeSelectionStatusText.Text = _nodeSupported && _npxPath is not null
            ? $"已使用 {file.Path}。"
            : "已保存路径；请检查版本要求及同目录中的 npx.cmd。";
    }

    private async Task InstallNodeWithWingetAsync(string winget)
    {
        _repairingNode = true;
        NodeRepairButton.IsEnabled = false;
        WarningRepairButton.IsEnabled = false;
        NodeRepairStatusText.Text = "已打开 WinGet；请完成安装程序中的步骤。";
        AppendLog("[ADL] 使用 WinGet 安装或升级 OpenJS.NodeJS.LTS。");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(winget,
                "install --id OpenJS.NodeJS.LTS --exact --source winget --installer-type wix --interactive")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            });
            if (process is null) throw new InvalidOperationException("无法启动 WinGet。");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                NodeRepairStatusText.Text = $"WinGet 未完成（代码 {process.ExitCode}）。可使用官方下载页。";
                AppendLog($"[ADL] Node.js 修复未完成，WinGet 退出代码 {process.ExitCode}。");
                return;
            }
            RefreshProcessPath();
            await CheckEnvironmentAsync();
            NodeRepairStatusText.Text = _nodeSupported && _npxPath is not null
                ? "Node.js 已就绪。"
                : !string.IsNullOrWhiteSpace(_manualNodePath)
                    ? "安装已完成；当前仍使用手动指定的 Node.js，请重新选择或改用自动检测。"
                    : "安装已完成；若仍显示旧版本，请重启 ADL 后刷新。";
            AppendLog($"[ADL] {NodeRepairStatusText.Text}");
        }
        catch (Exception ex)
        {
            NodeRepairStatusText.Text = $"无法启动修复：{ex.Message}";
            AppendLog($"[ADL] Node.js 修复失败：{ex.Message}");
        }
        finally
        {
            _repairingNode = false;
            NodeRepairButton.IsEnabled = true;
            WarningRepairButton.IsEnabled = true;
        }
    }

    private static void RefreshProcessPath()
    {
        var machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
        var user = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
        var existing = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator,
            new[] { machine, user, existing }.Where(value => !string.IsNullOrWhiteSpace(value))));
    }

    private static string? FindInstalledDshVersion(string workspace, string? npxPath)
    {
        var npxDirectory = Path.GetDirectoryName(npxPath ?? "") ?? "";
        var dshHome = Environment.GetEnvironmentVariable("DSH_HOME");
        if (string.IsNullOrWhiteSpace(dshHome))
            dshHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        var packagePaths = new List<string>
        {
            Path.Combine(workspace, "node_modules", "@deepseek-ai", "dsh", "package.json"),
            Path.Combine(npxDirectory, "node_modules", "@deepseek-ai", "dsh", "package.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@deepseek-ai", "dsh", "package.json"),
            Path.Combine(dshHome, "profiles", "web", "node_modules", "@deepseek-ai", "dsh", "package.json"),
            Path.Combine(dshHome, "profiles", "node_modules", "@deepseek-ai", "dsh", "package.json")
        };
        try
        {
            var npxCacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm-cache", "_npx");
            if (Directory.Exists(npxCacheRoot))
            {
                var cached = new DirectoryInfo(npxCacheRoot)
                    .GetDirectories()
                    .OrderByDescending(dir => dir.LastWriteTimeUtc)
                    .Select(dir => Path.Combine(dir.FullName, "node_modules", "@deepseek-ai", "dsh", "package.json"));
                packagePaths.AddRange(cached);
            }
        }
        catch (Exception) { }

        foreach (var packagePath in packagePaths)
        {
            try
            {
                if (!File.Exists(packagePath)) continue;
                using var json = JsonDocument.Parse(File.ReadAllText(packagePath));
                var version = json.RootElement.GetProperty("version").GetString();
                if (!string.IsNullOrWhiteSpace(version)) return version;
            }
            catch (Exception) { }
        }
        return null;
    }

    private void RefreshPlugins()
    {
        PluginsList.Items.Clear();
        _installedPlugins.Clear();
        PluginsEmptyText.Text = "尚未找到 DSH Web 配置。启动 Harness 后再刷新。";
        var dshHome = Environment.GetEnvironmentVariable("DSH_HOME");
        if (string.IsNullOrWhiteSpace(dshHome))
            dshHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        var manifest = Path.Combine(dshHome, "profiles", "web", "package.json");
        try
        {
            if (File.Exists(manifest))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(manifest));
                var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (json.RootElement.TryGetProperty("dsh", out var dsh) &&
                    dsh.TryGetProperty("profile", out var profile) &&
                    profile.TryGetProperty("bundles", out var bundles) && bundles.ValueKind == JsonValueKind.Array)
                    foreach (var bundle in bundles.EnumerateArray())
                    {
                        var name = bundle.GetString();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        enabled.Add(name);
                        _installedPlugins.Add(name);
                        PluginsList.Items.Add($"{name}   ·   已启用");
                    }
                if (json.RootElement.TryGetProperty("dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Object)
                    foreach (var package in dependencies.EnumerateObject())
                        if (!enabled.Contains(package.Name))
                        {
                            _installedPlugins.Add(package.Name);
                            if (package.Value.ValueKind == JsonValueKind.String && package.Value.GetString() is { } spec)
                                _installedPlugins.Add(spec);
                            PluginsList.Items.Add($"{package.Name}   ·   已安装 · {package.Value.GetString()}");
                        }
            }
        }
        catch (Exception ex)
        {
            PluginsEmptyText.Text = $"无法读取插件配置：{ex.Message}";
        }
        PluginsList.Visibility = PluginsList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PluginsEmptyText.Visibility = PluginsList.Items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdatePluginDetailState();
    }

    private void RefreshPluginsButton_Click(object sender, RoutedEventArgs e) => RefreshPlugins();
    private void OpenPluginManager_Click(object sender, RoutedEventArgs e) => OpenUrl(GetActiveWebUrl());

    private async Task RefreshCatalogAsync()
    {
        if (_demoMode) return;
        if (_catalogLoading) return;
        _catalogLoading = true;
        RefreshCatalogButton.IsEnabled = false;
        CatalogStatusText.Text = "正在获取社区插件与官方 bundle…";
        var all = new List<CatalogPlugin>();
        var errors = new List<string>();
        try
        {
            try { all.AddRange(await PluginCatalogService.GetCommunityCatalogAsync()); UpdateCatalogItems(all); }
            catch (Exception ex) { errors.Add($"社区目录：{ex.Message}"); }
            try { all.AddRange(await PluginCatalogService.GetOfficialAsync()); UpdateCatalogItems(all); }
            catch (Exception ex) { errors.Add($"官方发布：{ex.Message}"); }
            try { all.AddRange(await PluginCatalogService.GetCommunityAsync()); UpdateCatalogItems(all); }
            catch (Exception ex) { errors.Add($"官方社区：{ex.Message}"); }
            var customUrl = CatalogUrlTextBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(customUrl))
            {
                try { all.AddRange(await PluginCatalogService.GetCustomAsync(customUrl)); UpdateCatalogItems(all); }
                catch (Exception ex) { errors.Add($"自定义目录：{ex.Message}"); }
            }
            CatalogStatusText.Text = $"已收录 {_catalogItems.Count} 个插件 · 社区作品由各自作者发布" +
                                     (errors.Count > 0 ? $"；{string.Join("；", errors)}" : "");
            _catalogLoaded = true;
        }
        finally
        {
            RefreshCatalogButton.IsEnabled = true;
            _catalogLoading = false;
        }
    }

    private void UpdateCatalogItems(IReadOnlyList<CatalogPlugin> items)
    {
        _catalogItems = items.GroupBy(item => item.InstallSpec, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => item.Source.StartsWith("自定义", StringComparison.Ordinal) ? 0 :
                item.IsOfficial ? 1 : item.Source.StartsWith("社区目录", StringComparison.Ordinal) ? 2 : 3).First())
            .ToArray();
        FilterCatalog();
    }

    private async void RefreshCatalogButton_Click(object sender, RoutedEventArgs e) => await RefreshCatalogAsync();

    private void PluginTab_Click(object sender, RoutedEventArgs e)
    {
        var selected = (sender as Button)?.Tag as string ?? "discover";
        DiscoverPluginsPanel.Visibility = selected == "discover" ? Visibility.Visible : Visibility.Collapsed;
        InstalledPluginsPanel.Visibility = selected == "installed" ? Visibility.Visible : Visibility.Collapsed;
        PluginSourcesPanel.Visibility = selected == "sources" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { DiscoverPluginsTab, InstalledPluginsTab, PluginSourcesTab })
        {
            var active = (button.Tag as string) == selected;
            button.Background = new SolidColorBrush(active
                ? Windows.UI.Color.FromArgb(36, 255, 255, 255)
                : Windows.UI.Color.FromArgb(0, 255, 255, 255));
            button.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(active ? (byte)255 : (byte)175, 255, 255, 255));
        }
    }

    private void CatalogSearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => FilterCatalog();

    private void CatalogFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => FilterCatalog();

    private void FilterCatalog()
    {
        if (CatalogList is null || CatalogSourceFilter is null || CatalogTypeFilter is null || CatalogSortFilter is null ||
            CatalogResultText is null || CatalogSearchTextBox is null) return;
        var query = CatalogSearchTextBox.Text.Trim();
        var authorQuery = query.TrimStart('@');
        var source = (CatalogSourceFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var category = (CatalogTypeFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var sort = (CatalogSortFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "popular";
        var filtered = _catalogItems.Where(item =>
            (query.Length == 0 || item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             item.Publisher.Contains(authorQuery, StringComparison.OrdinalIgnoreCase) ||
             (item.RepositoryLabel?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
             (item.RepositoryLabel?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
             item.InstallSpec.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             item.Description.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
            (category == "all" || item.Category == category) &&
            (source == "all" || source switch
            {
                "catalog" => item.Source.StartsWith("社区目录", StringComparison.Ordinal),
                "discussion" => item.Source.StartsWith("官方社区", StringComparison.Ordinal),
                "official" => item.IsOfficial,
                "custom" => item.Source.StartsWith("自定义", StringComparison.Ordinal),
                _ => true
            }));
        var ordered = sort switch
        {
            "recent" => filtered.OrderByDescending(item => item.PublishedAt).ThenBy(item => item.DisplayName),
            "name" => filtered.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderByDescending(item => item.Stars ?? 0).ThenBy(item => item.DisplayName)
        };
        var result = ordered.ToArray();
        CatalogList.ItemsSource = result;
        CatalogResultText.Text = $"{result.Length} 个结果";
    }

    private void OpenCommunityButton_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://github.com/deepseek-ai/deepseek-harness/discussions/categories/show-your-plugins");

    private async void SaveCatalogButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode) return;
        var url = CatalogUrlTextBox.Text.Trim();
        if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"))
        {
            CatalogStatusText.Text = "第三方目录必须使用 HTTPS URL。";
            return;
        }
        LauncherSettings.SaveCatalogUrl(url);
        await RefreshCatalogAsync();
    }

    private void CatalogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CatalogList.SelectedItem is not CatalogPlugin plugin) return;
        _selectedPlugin = plugin;
        DetailIconImage.Source = plugin.IconImage;
        DetailNameText.Text = plugin.DisplayName;
        DetailSourceText.Text = plugin.DetailSourceLine;
        DetailMetaText.Text = $"{plugin.CategoryLabel}  ·  {plugin.Version}" +
                              (plugin.Stars is > 0 ? $"  ·  GitHub ★ {plugin.Stars:N0}" : "");
        DetailDescriptionText.Text = plugin.Description;
        DetailInstallText.Text = PluginCatalogService.IsSafeInstallSpec(plugin.InstallSpec)
            ? $"安装命令：npx --yes @deepseek-ai/dsh plugin --profile web add {plugin.InstallSpec}"
            : "该帖子未提供可自动识别的安装命令，请查看原帖。";
        OpenRepositoryButton.Visibility = plugin.RepositoryUrl is null ? Visibility.Collapsed : Visibility.Visible;
        var hasSeparateSourcePage = plugin.Homepage is { } homepage &&
            !string.Equals(homepage.TrimEnd('/'), plugin.RepositoryUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        OpenPluginSourceButton.Visibility = hasSeparateSourcePage ? Visibility.Visible : Visibility.Collapsed;
        OpenPluginSourceButton.Content = plugin.PublisherKind == "discussion" ? "查看社区帖子" :
            plugin.IsOfficial ? "查看源码目录" : "查看来源页面";
        UpdatePluginDetailState();
        PluginCatalogView.Visibility = Visibility.Collapsed;
        PluginDetailView.Visibility = Visibility.Visible;
    }

    private void BackToCatalog_Click(object sender, RoutedEventArgs e)
    {
        PluginDetailView.Visibility = Visibility.Collapsed;
        PluginCatalogView.Visibility = Visibility.Visible;
        CatalogList.SelectedItem = null;
    }

    private void OpenPluginSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlugin?.Homepage is { } url) OpenUrl(url);
    }

    private void OpenRepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlugin?.RepositoryUrl is { } url) OpenUrl(url);
    }

    private void PluginIcon_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is Image image)
            image.Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri("ms-appx:///Assets/Icons/puzzle.svg"));
    }

    private void UpdatePluginDetailState()
    {
        if (_selectedPlugin is null) return;
        if (_demoMode)
        {
            InstallPluginButton.IsEnabled = false;
            DetailStatusText.Text = "演示模式只展示流程，不执行安装。";
            return;
        }
        var installed = IsPluginInstalled(_selectedPlugin);
        var webCompatible = CanInstallToWeb(_selectedPlugin);
        InstallPluginButton.IsEnabled = !_installingPlugin && !installed && webCompatible && PluginCatalogService.IsSafeInstallSpec(_selectedPlugin.InstallSpec);
        DetailStatusText.Text = installed ? "已安装在 Web profile。" :
            _installingPlugin ? "正在安装，请等待命令完成。" :
            !webCompatible ? "这是官方其他运行模式的核心 bundle，不适合安装到 Web profile。" :
            !PluginCatalogService.IsSafeInstallSpec(_selectedPlugin.InstallSpec) ? "请打开社区帖子查看安装说明。" :
            "安装可能运行 npm 构建脚本；运行中的 Harness 可能需要重启。";
    }

    private static bool CanInstallToWeb(CatalogPlugin plugin) => !plugin.IsOfficial ||
        plugin.Name is "@deepseek-ai/dsh-base" or "@deepseek-ai/dsh-web-app";

    private bool IsPluginInstalled(CatalogPlugin plugin) =>
        _installedPlugins.Contains(plugin.Name) || _installedPlugins.Contains(plugin.InstallSpec);

    private async void InstallPluginButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode)
        {
            DetailStatusText.Text = "演示模式不会安装插件。";
            return;
        }
        var plugin = _selectedPlugin;
        if (plugin is null || _installingPlugin || IsPluginInstalled(plugin)) return;
        if (!CanInstallToWeb(plugin) || !PluginCatalogService.IsSafeInstallSpec(plugin.InstallSpec)) return;
        var npx = _npxPath ?? FindOnPath("npx.cmd");
        if (npx is null)
        {
            DetailStatusText.Text = "未找到 npx.cmd，请先安装 Node.js。";
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "安装插件",
            Content = $"安装目标：{plugin.InstallSpec}\n收录来源：{plugin.Source}" +
                      (plugin.RepositoryLabel is { } repo ? $"\nGitHub 仓库：{repo}" : "") +
                      "\n\n将安装到 DSH Web profile。安装包可能执行构建脚本。",
            PrimaryButtonText = "安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _installingPlugin = true;
        UpdatePluginDetailState();
        AppendLog($"[ADL] 安装插件：{plugin.InstallSpec}");
        try
        {
            var result = await RunPluginInstallAsync(npx, _nodePath, plugin.InstallSpec);
            AppendLog($"[ADL] 插件安装退出代码：{result.ExitCode}。{result.Output}");
            RefreshPlugins();
            DetailStatusText.Text = result.ExitCode == 0 && IsPluginInstalled(plugin)
                ? "安装完成。若 Harness 正在运行，请重启后使用。"
                : result.ExitCode == 0 ? "命令已完成，但未在 Web profile 配置中确认到该插件。请查看运行日志。" :
                  $"安装未完成（退出代码 {result.ExitCode}）。请查看运行日志。";
        }
        catch (Exception ex)
        {
            AppendLog($"[ADL] 插件安装失败：{ex.Message}");
            DetailStatusText.Text = $"安装失败：{ex.Message}";
        }
        finally
        {
            _installingPlugin = false;
            InstallPluginButton.IsEnabled = !IsPluginInstalled(plugin) && CanInstallToWeb(plugin) && PluginCatalogService.IsSafeInstallSpec(plugin.InstallSpec);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunPluginInstallAsync(string npx, string? nodePath, string spec)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c \"\"{npx}\" --yes @deepseek-ai/dsh plugin --profile web add {spec}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        PrependNodeDirectory(start, nodePath);
        using var process = new Process { StartInfo = start };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("安装超过 5 分钟，已停止本次安装命令。");
        }
        var output = (await stdout + "\n" + await stderr).Trim();
        return (process.ExitCode, output.Length > 1200 ? output[^1200..] : output);
    }

    private static async Task<string> ReadNodeVersionAsync(string nodePath)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(nodePath, "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return "";
            }
            return process.ExitCode == 0 ? (await output).Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    private static string? FindOnPath(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (Exception)
            {
                // Skip invalid PATH entries.
            }
        }
        return null;
    }

    private static void PrependNodeDirectory(ProcessStartInfo startInfo, string? nodePath)
    {
        if (nodePath is null) return;
        var directory = Path.GetDirectoryName(nodePath);
        if (string.IsNullOrWhiteSpace(directory)) return;
        startInfo.Environment["PATH"] = directory + Path.PathSeparator +
            (Environment.GetEnvironmentVariable("PATH") ?? "");
    }

    private void WorkspaceTextBox_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateWorkspaceHint();

    private void UpdateWorkspaceHint()
    {
        var path = WorkspaceTextBox.Text.Trim();
        var valid = Directory.Exists(path);
        WorkspaceNameText.Text = valid ? new DirectoryInfo(path).Name : "选择工作区";
        WorkspaceHint.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceHint.Text = valid ? "将以这个目录作为 Harness 的默认工作区。" : "请选择已存在的文件夹。";
        WorkspaceHint.Foreground = new SolidColorBrush(valid
            ? Windows.UI.Color.FromArgb(180, 255, 255, 255)
            : Windows.UI.Color.FromArgb(255, 255, 255, 255));
        if (valid && !_demoMode)
            LauncherSettings.SaveWorkspace(path);
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode) return;
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(((App)Application.Current).MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            WorkspaceTextBox.Text = folder.Path;
            await CheckEnvironmentAsync();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_demoMode) await CheckEnvironmentAsync();
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode)
        {
            if (_serviceReady)
                SetDemoStep(0);
            else if (!_starting)
                await PlayDemoStartupAsync();
            return;
        }
        if (_stoppingService) return;
        if (_serviceReady)
        {
            await StopHarnessAsync();
            return;
        }
        if (_service is { HasExited: false } || _starting)
            return;
        var workspace = WorkspaceTextBox.Text.Trim();
        if (!Directory.Exists(workspace))
        {
            WorkspaceHint.Text = "请先选择已存在的工作目录。";
            return;
        }
        await CheckEnvironmentAsync();
        if (_serviceReady)
        {
            ApplyRunningUiState();
            return;
        }
        _activeWebPort = _configuredWebPort;
        if (await IsWebServerRespondingAsync())
        {
            _serviceReady = true;
            ApplyRunningUiState();
            return;
        }
        if (!_nodeSupported || _npxPath is null)
        {
            AppendLog("[ADL] Node.js 或 npx 未就绪，无法启动。");
            ShellNavigation.SelectedItem = ShellNavigation.MenuItems[1];
            return;
        }
        if (await IsPortOccupiedAsync(_activeWebPort))
        {
            AppendLog($"[ADL] 端口 {_activeWebPort} 已被其他服务占用。");
            ServiceStatusText.Text = $"端口 {_activeWebPort} 已被其他服务占用";
            return;
        }

        _starting = true;
        _runningDshVersion = _selectedDshVersion;
        ++_tipsSessionId;
        ShowLaunchStepTip("准备启动", "正在检查 DSH 资源", 12);
        UpdateBranding();
        LaunchButton.IsEnabled = false;
        LaunchButton.Content = "启动中…";
        ServiceStatusText.Text = _dshVersion is null ? "正在获取 DSH 并启动服务…" : "正在启动 DSH 服务…";
        LaunchProgress.Visibility = Visibility.Visible;
        LaunchProgress.IsActive = true;
        AppendLog($"[ADL] 工作目录：{workspace}");
        var packageSpec = _selectedDshVersion.Length == 0 ? "@deepseek-ai/dsh" : $"@deepseek-ai/dsh@{_selectedDshVersion}";
        AppendLog($"[ADL] 运行 npx --yes {packageSpec} web --no-open --port {_activeWebPort}");

        try
        {
            // The command is fixed; the user-selected path is passed only as WorkingDirectory.
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/d /c \"\"{_npxPath}\" --yes {packageSpec} web --no-open --port {_activeWebPort}\"",
                WorkingDirectory = workspace,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            PrependNodeDirectory(startInfo, _nodePath);
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => OnProcessLine(args.Data);
            process.ErrorDataReceived += (_, args) => OnProcessLine(args.Data);
            process.Exited += (_, _) => DispatcherQueue.TryEnqueue(() => OnProcessExited(process));
            process.Start();
            _service = process;
            ShowLaunchStepTip("准备资源", "正在获取或检查 DSH 依赖", 28);
            UpdateBranding();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _ = WatchReadinessAsync(process);
        }
        catch (Exception ex)
        {
            _runningDshVersion = null;
            AppendLog($"[ADL] 启动失败：{ex.Message}");
            ServiceStatusText.Text = "启动失败";
            LaunchProgress.IsActive = false;
            LaunchProgress.Visibility = Visibility.Collapsed;
            LaunchButton.IsEnabled = true;
            LaunchButton.Content = "启动 Harness";
            ShowLaunchErrorTip(ex.Message);
        }
        finally
        {
            _starting = false;
            UpdateBranding();
        }
    }

    private Uri ActiveWebAddress => new($"http://127.0.0.1:{_activeWebPort}/");

    private static async Task<bool> IsPortOccupiedAsync(int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(1));
            return true;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            return false;
        }
    }

    private string GetActiveWebUrl() => Uri.TryCreate(_webSessionUrl, UriKind.Absolute, out var url) &&
        url.Scheme == Uri.UriSchemeHttp && url.IsLoopback && url.Port == _activeWebPort &&
        string.IsNullOrEmpty(url.UserInfo) ? url.ToString() : ActiveWebAddress.ToString();

    private async Task<bool> IsWebServerRespondingAsync()
    {
        try
        {
            using var response = await _http.GetAsync(ActiveWebAddress);
            // DSH deliberately answers 401 before a browser receives its session token.
            // That response still proves the Web server is ready; a plain TCP connect does not.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return (await response.Content.ReadAsStringAsync()).Contains(
                    "dsh web authentication required", StringComparison.OrdinalIgnoreCase);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "text/html")
                return (await response.Content.ReadAsStringAsync()).Contains(
                    "DeepSeek Harness", StringComparison.OrdinalIgnoreCase);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private void ApplyRunningUiState()
    {
        LaunchButton.IsEnabled = true;
        LaunchButton.Content = "停止 Harness";
        LaunchProgress.IsActive = false;
        LaunchProgress.Visibility = Visibility.Collapsed;
        OpenWebButton.IsEnabled = true;
        OpenWebButton.Visibility = Visibility.Visible;
        OpenPluginManagerButton.IsEnabled = true;
        ServiceStatusText.Text = $"Harness 正在运行 · 端口 {_activeWebPort}";
        UpdateBranding();
    }

    private async Task WatchReadinessAsync(Process process)
    {
        for (var attempt = 0; attempt < 180 && ReferenceEquals(_service, process) && !process.HasExited; attempt++)
        {
            if (await IsWebServerRespondingAsync() && ReferenceEquals(_service, process))
            {
                _serviceReady = true;
                ApplyRunningUiState();
                LaunchProgress.IsActive = false;
                LaunchProgress.Visibility = Visibility.Collapsed;
                AppendLog($"[ADL] Web 界面已就绪：{ActiveWebAddress}");
                _ = ShowLaunchCompletedTipAsync();
                return;
            }
            if (attempt == 5 && _launchProgressValue < 55)
                ShowLaunchStepTip("启动中", "等待 DSH 加载组件", 55);
            await Task.Delay(1000);
        }
        if (ReferenceEquals(_service, process) && !process.HasExited)
        {
            ServiceStatusText.Text = "进程运行中，等待服务响应";
            LaunchProgress.IsActive = false;
            LaunchProgress.Visibility = Visibility.Collapsed;
            AppendLog("[ADL] 服务暂未响应。请查看运行记录，确认 Web 地址后重试。");
            ShowLaunchErrorTip("Web 服务未在预期端口响应，请查看运行日志。");
        }
    }

    private void OnProcessLine(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
            DispatcherQueue.TryEnqueue(() =>
            {
                var match = DshWebUrlRegex.Match(line);
                if (match.Success && Uri.TryCreate(match.Groups["url"].Value.TrimEnd('.', ','), UriKind.Absolute, out var url) &&
                    url.Scheme == Uri.UriSchemeHttp && url.IsLoopback && url.Port == _activeWebPort &&
                    string.IsNullOrEmpty(url.UserInfo))
                {
                    _webSessionUrl = url.ToString();
                    LauncherSettings.SaveLastWebUrl(_webSessionUrl);
                    AppendLog($"[ADL] DSH Web 会话地址已获取：{ActiveWebAddress}（认证参数已隐藏）");
                    if (!_serviceReady)
                    {
                        ServiceStatusText.Text = "Web 服务正在就绪…";
                        ShowLaunchStepTip("连接 Web 服务", $"端口 {_activeWebPort} 已启动，正在验证响应", 90);
                    }
                    return;
                }
                AppendLog(DshWebUrlRegex.Replace(line, "dsh web: [会话地址已隐藏]"));
                if (_serviceReady) return;
                if (_launchProgressValue < 70 && (line.Contains("install", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("download", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("fetch", StringComparison.OrdinalIgnoreCase)))
                {
                    ServiceStatusText.Text = "正在下载或准备 DSH 资源…";
                    var percent = Regex.Match(line, @"(?<!\d)(?<percent>100|[1-9]?\d)%");
                    var detail = percent.Success ? $"资源下载 {percent.Groups["percent"].Value}%" : "正在获取和安装启动依赖";
                    var stage = percent.Success ? 30 + int.Parse(percent.Groups["percent"].Value) * 0.35 : 42;
                    ShowLaunchStepTip("下载资源", detail, Math.Max(_launchProgressValue, stage));
                }
                else if (line.Contains("web", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("listening", StringComparison.OrdinalIgnoreCase))
                {
                    ServiceStatusText.Text = "正在启动 Web 服务…";
                    ShowLaunchStepTip("启动 Web 服务", $"等待本机端口 {_activeWebPort} 响应", Math.Max(_launchProgressValue, 72));
                }
            });
    }

    private async void OnProcessExited(Process process)
    {
        if (!ReferenceEquals(_service, process))
            return;
        var code = process.ExitCode;
        _service = null;
        process.Dispose();
        if (_stoppingService) return;
        if (await IsWebServerRespondingAsync())
        {
            _serviceReady = true;
            ApplyRunningUiState();
            AppendLog($"[ADL] 启动命令已退出（代码 {code}），DSH Web 服务仍在运行。");
            if (_launchTipsActive && _launchProgressValue < 100) _ = ShowLaunchCompletedTipAsync();
            return;
        }
        _serviceReady = false;
        _runningDshVersion = null;
        UpdateBranding();
        LaunchButton.IsEnabled = true;
        LaunchButton.Content = "启动 Harness";
        OpenWebButton.IsEnabled = false;
        OpenPluginManagerButton.IsEnabled = false;
        OpenWebButton.Visibility = Visibility.Collapsed;
        ServiceStatusText.Text = code == 0 ? "服务已停止" : $"进程退出，代码 {code}";
        LaunchProgress.IsActive = false;
        LaunchProgress.Visibility = Visibility.Collapsed;
        AppendLog($"[ADL] Harness 进程已退出（代码 {code}）。");
        if (_launchTipsActive) ShowLaunchErrorTip($"进程退出，代码 {code}。请查看运行日志。");
    }

    private void StopService()
    {
        var process = _service;
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception ex)
        {
            AppendLog($"[ADL] 无法停止进程：{ex.Message}");
        }
    }

    private async Task StopHarnessAsync()
    {
        if (_stoppingService) return;
        _stoppingService = true;
        LaunchButton.IsEnabled = false;
        LaunchButton.Content = "正在停止…";
        ServiceStatusText.Text = "正在停止 Harness…";
        try
        {
            StopService();
            await Task.Delay(350);
            if (await IsWebServerRespondingAsync())
            {
                var pid = ListeningProcessResolver.FindLoopbackListener(_activeWebPort);
                if (pid is null || pid == Environment.ProcessId)
                    throw new InvalidOperationException("无法确认 DSH 服务进程，请在原启动窗口停止。");
                using var listener = Process.GetProcessById(pid.Value);
                if (!string.Equals(listener.ProcessName, "node", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("端口对应的进程不是 node.exe，已取消停止。");
                // Recheck the DSH response immediately before ending an externally started process.
                if (!await IsWebServerRespondingAsync())
                    throw new InvalidOperationException("服务状态已变化，请刷新后重试。");
                listener.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await listener.WaitForExitAsync(timeout.Token);
            }
            if (await IsWebServerRespondingAsync())
                throw new InvalidOperationException("DSH 仍在响应，请查看运行日志。");
            _serviceReady = false;
            _runningDshVersion = null;
            LaunchButton.Content = "启动 Harness";
            OpenWebButton.Visibility = Visibility.Collapsed;
            OpenWebButton.IsEnabled = false;
            OpenPluginManagerButton.IsEnabled = false;
            ServiceStatusText.Text = "Harness 已停止";
            UpdateBranding();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or
                                   System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            AppendLog($"[ADL] 停止 Harness 失败：{ex.Message}");
            _serviceReady = await IsWebServerRespondingAsync();
            if (_serviceReady) ApplyRunningUiState();
            ServiceStatusText.Text = $"停止失败：{ex.Message.Trim()}";
        }
        finally
        {
            _stoppingService = false;
            LaunchButton.IsEnabled = true;
        }
    }

    private void OpenWebButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode) SetDemoStep(5);
        else OpenUrl(GetActiveWebUrl());
    }
    private void OpenUrl(string url)
    {
        if (_demoMode) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"[ADL] 无法打开浏览器：{ex.Message}");
        }
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        LogText.Text = "[ADL] 记录已清空。";
        _logLines = 1;
    }

    private void AppendLog(string text)
    {
        if (_logLines >= 160)
        {
            var lines = LogText.Text.Split('\n');
            LogText.Text = string.Join("\n", lines.Skip(40));
            _logLines -= 40;
        }
        LogText.Text += $"\n{text}";
        _logLines++;
        LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null);
    }

}

