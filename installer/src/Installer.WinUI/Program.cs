using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using AnotherDSHL.Installer.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.Graphics;
using Windows.UI;

namespace AnotherDSHL.Installer;

internal static class Program
{
    internal static readonly string[] Arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
    [STAThread]
    public static void Main()
    {
        // The installed uninstaller relocates only manifest-owned files so its runtime can be removed.
        if ((Arguments.Contains("--uninstall") || Arguments.Contains("--update")) && !Arguments.Contains("--relocated") && !Arguments.Contains("--preview"))
        {
            string source = AppContext.BaseDirectory;
            string temp = Path.Combine(Path.GetTempPath(), "AnotherDSHL", "uninstall-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                foreach (var file in PayloadManifest.Read(source).Files)
                {
                    string target = Installation.SafePath(temp, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Installation.SafePath(source, file.Path), target);
                }
                File.Copy(Path.Combine(source, PayloadManifest.FileName), Path.Combine(temp, PayloadManifest.FileName));
                var start = new ProcessStartInfo(Path.Combine(temp, "AnotherDSHL.Installer.exe")) { UseShellExecute = false, WorkingDirectory = temp };
                foreach (string argument in Arguments.Where(s => !s.StartsWith("--target="))) start.ArgumentList.Add(argument);
                start.ArgumentList.Add("--relocated");
                start.ArgumentList.Add("--target=" + Path.GetFullPath(source).TrimEnd('\\'));
                using var child = Process.Start(start)!;
                // Original exits immediately; child cleanup is best effort on the next setup run.
                return;
            }
            catch (Exception ex)
            {
                Installation.TryCleanup(temp);
                MessageBox(IntPtr.Zero, ex.Message, "无法启动维护程序", 0x10);
                return;
            }
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(init =>
        {
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            try { _ = new InstallerApp(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "AnotherDSHL-installer-error.txt"), ex.ToString()); throw; }
        });
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);
}

public sealed partial class InstallerApp : Application
{
    private Window? _window;
    public InstallerApp()
    {
        UnhandledException += (_, e) => File.WriteAllText(Path.Combine(Path.GetTempPath(), "AnotherDSHL-installer-error.txt"), e.Exception.ToString());
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { _window = new InstallerWindow(); _window.Activate(); }
        catch (Exception ex) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "AnotherDSHL-installer-error.txt"), ex.ToString()); throw; }
    }
}

internal sealed class InstallerWindow : Window
{
    private readonly Installation _installation = new();
    private readonly bool _uninstall = Program.Arguments.Contains("--uninstall");
    private readonly bool _update = Program.Arguments.Contains("--update");
    private readonly string _package = Program.Arguments.FirstOrDefault(s => s.StartsWith("--package="))?[10..] ?? "";
    private readonly bool _preview = Program.Arguments.Contains("--preview");
    private readonly Grid _root = new();
    private readonly Grid _welcome = new() { Padding = new Thickness(24, 12, 24, 16) };
    private Grid _hero = null!, _body = null!, _footer = null!;
    private readonly StackPanel _options = new() { Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _activity = new() { Spacing = 14, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _path = new() { Height = 32, FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly CheckBox _desktop = new() { Content = "桌面快捷方式", IsChecked = true, FontSize = 12 };
    private readonly CheckBox _startMenu = new() { Content = "添加到开始菜单", IsChecked = true, FontSize = 12 };
    private readonly TextBlock _heading = new() { Text = "安装 AnotherDSHL", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _subtitle = new() { Text = "为 DeepSeek Harness 准备一个便捷的入口。", FontSize = 12 };
    private readonly TextBlock _size = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Text = "正在校验并安装文件…", FontSize = 13 };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100 };
    private readonly Button _primary = new() { Content = "安装", MinWidth = 92, Height = 32, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _cancel = new() { Content = "取消", MinWidth = 68, Height = 32, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _back = new() { Content = "返回", MinWidth = 68, Height = 32, VerticalAlignment = VerticalAlignment.Center };
    private readonly HyperlinkButton _license = new() { Content = "GPLv3 许可", Padding = new Thickness(0), FontSize = 11, MinHeight = 0, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _legal = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private bool _busy, _complete;
    private string _target = "";
    private PayloadManifest? _manifest;

    public InstallerWindow()
    {
        Title = _uninstall ? "卸载 AnotherDSHL" : _update ? "更新 AnotherDSHL" : "AnotherDSHL 安装程序";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        _root.Background = Brush("ApplicationPageBackgroundThemeBrush");
        if (Program.Arguments.Contains("--light")) _root.RequestedTheme = ElementTheme.Light;
        if (Program.Arguments.Contains("--dark")) _root.RequestedTheme = ElementTheme.Dark;
        _subtitle.Foreground = Brush("TextFillColorSecondaryBrush");
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(76) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });
        var title = new Grid { Margin = new Thickness(16, 0, 140, 0) };
        title.Children.Add(new TextBlock { Text = "AnotherDSHL | " + (_uninstall ? "卸载" : _update ? "更新" : "安装"), FontSize = 12, Foreground = Brush("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center });
        _root.Children.Add(title); SetTitleBar(title);
        var hero = _hero = new Grid { Margin = new Thickness(24, 0, 24, 0), ColumnSpacing = 16, VerticalAlignment = VerticalAlignment.Center };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        hero.ColumnDefinitions.Add(new ColumnDefinition());
        var logo = CreateLogo(46);
        hero.Children.Add(logo);
        var brand = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 3 };
        brand.Children.Add(_heading); brand.Children.Add(_subtitle); Grid.SetColumn(brand, 1); hero.Children.Add(brand);
        Grid.SetRow(hero, 1); _root.Children.Add(hero);
        var body = _body = new Grid { Margin = new Thickness(24, 0, 24, 0) };
        _options.Spacing = 24;
        _options.VerticalAlignment = VerticalAlignment.Center;
        var pathSection = new StackPanel { Spacing = 8 };
        pathSection.Children.Add(new TextBlock { Text = "安装位置", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = Brush("TextFillColorSecondaryBrush") });
        var pathRow = new Grid { ColumnSpacing = 8 };
        pathRow.ColumnDefinitions.Add(new ColumnDefinition()); pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathRow.Children.Add(_path);
        var browse = new Button { Content = new FontIcon { Glyph = "\uE8B7", FontSize = 13 }, Width = 36, Height = 32, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(browse, "选择安装目录");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(browse, "选择安装目录");
        browse.Click += Browse; Grid.SetColumn(browse, 1); pathRow.Children.Add(browse); pathSection.Children.Add(pathRow);
        _options.Children.Add(pathSection);
        var checks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 32 };
        checks.Children.Add(_desktop); checks.Children.Add(_startMenu); _options.Children.Add(checks);
        body.Children.Add(_options);
        _activity.Children.Add(_status); _activity.Children.Add(_progress); body.Children.Add(_activity);
        Grid.SetRow(body, 2); _root.Children.Add(body);
        var footer = _footer = new Grid { Padding = new Thickness(24, 0, 24, 0), ColumnSpacing = 8, Background = Brush("LayerFillColorDefaultBrush"), BorderBrush = Brush("DividerStrokeColorDefaultBrush"), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var footerInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        _size.Foreground = Brush("TextFillColorSecondaryBrush");
        footerInfo.Children.Add(_size);
        _legal.Children.Add(new TextBlock { Text = "安装即同意 ", FontSize = 11, Foreground = Brush("TextFillColorTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center });
        _legal.Children.Add(_license);
        footerInfo.Children.Add(_legal);
        footer.Children.Add(footerInfo);
        Grid.SetColumn(_back, 1); footer.Children.Add(_back); Grid.SetColumn(_cancel, 2); footer.Children.Add(_cancel); Grid.SetColumn(_primary, 3); footer.Children.Add(_primary);
        _primary.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        Grid.SetRow(footer, 3); _root.Children.Add(footer);
        BuildWelcomePage();
        Content = _root;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96d;
        AppWindow.Resize(new SizeInt32((int)(529 * scale), (int)(352 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.IsResizable = false; presenter.IsMaximizable = false; }
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(area.X + (area.Width - AppWindow.Size.Width) / 2, area.Y + (area.Height - AppWindow.Size.Height) / 2));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        _path.Text = _installation.InstalledDirectory ?? Installation.DefaultDirectory;
        _cancel.Click += (_, _) => Close();
        _back.Click += (_, _) => ShowWelcome();
        _primary.Click += InstallClick;
        _license.Click += async (_, _) => await ShowDialog("GPLv3 许可", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "LICENSE")));
        AppWindow.Closing += (_, e) => { if (_busy) e.Cancel = true; };
        if (_uninstall)
        {
            _legal.Visibility = Visibility.Collapsed;
            _target = Program.Arguments.FirstOrDefault(s => s.StartsWith("--target="))?[9..] ?? _installation.InstalledDirectory ?? "";
            _heading.Text = "卸载 AnotherDSHL"; _subtitle.Text = "移除程序文件，保留你的个人设置。";
            _options.Visibility = Visibility.Collapsed; _activity.Visibility = Visibility.Visible;
            _status.Text = "确定要卸载吗？"; _progress.Visibility = Visibility.Collapsed;
            _activity.Children.Add(new TextBlock { Text = _target, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            _primary.Content = "卸载";
            _back.Visibility = Visibility.Collapsed;
        }
        else if (_update)
        {
            _legal.Visibility = Visibility.Collapsed;
            _target = Program.Arguments.FirstOrDefault(s => s.StartsWith("--target="))?[9..] ?? _installation.InstalledDirectory ?? "";
            _heading.Text = "更新 AnotherDSHL";
            _subtitle.Text = "更新程序文件，保留设置和快捷方式。";
            _options.Visibility = Visibility.Collapsed; _activity.Visibility = Visibility.Visible;
            _progress.Visibility = _back.Visibility = Visibility.Collapsed;
            _primary.Content = "更新";
            try
            {
                var update = UpdatePackageService.ReadManifest(_package);
                _status.Text = $"{update.From} → {update.Target.Version}";
                _size.Text = $"差分更新 · {new FileInfo(_package).Length / 1048576d:F1} MB";
            }
            catch (Exception ex) { _status.Text = ex.Message; _primary.IsEnabled = false; }
        }
        else
        {
            try { _manifest = PayloadManifest.Read(AppContext.BaseDirectory); _size.Text = $"v{_manifest.Version} · 需要 {_manifest.InstalledBytes / 1048576d:F0} MB"; }
            catch (Exception ex) { _size.Text = _preview ? "界面预览 · 不会安装" : "安装文件不完整"; _primary.IsEnabled = _preview; _status.Text = ex.Message; }
            ShowWelcome();
        }
        if (_preview)
        {
            string page = Program.Arguments.FirstOrDefault(s => s.StartsWith("--preview-page="))?[15..] ?? "welcome";
            if (page is "options" or "complete") ShowOptions();
            if (page == "complete") { _options.Visibility = Visibility.Collapsed; _activity.Visibility = Visibility.Visible; ShowCompletion(); }
            string? capture = Program.Arguments.FirstOrDefault(s => s.StartsWith("--capture="))?[10..];
            if (capture is not null) DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(900);
                try
                {
                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                    await bitmap.RenderAsync(_root);
                    var pixels = await bitmap.GetPixelsAsync();
                    string path = Path.GetFullPath(capture);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var stream = File.Create(path);
                    var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                        Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
                    encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                        (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
                    await encoder.FlushAsync();
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "AnotherDSHL-installer-error.txt"), ex.ToString()); Environment.Exit(1); }
                Environment.Exit(0);
            });
        }
    }
    private Image CreateLogo(double size)
    {
        var source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AeryoAppIcon.svg")));
        var image = new Image { Source = source, Width = size, Height = size };
        void UpdateScale() { source.RasterizePixelWidth = source.RasterizePixelHeight = size * (image.XamlRoot?.RasterizationScale ?? 1); }
        image.Loaded += (_, _) => { UpdateScale(); image.XamlRoot.Changed += (_, _) => UpdateScale(); };
        return image;
    }
    private void BuildWelcomePage()
    {
        _welcome.RowDefinitions.Add(new RowDefinition());
        _welcome.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var brand = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
        var logo = CreateLogo(88); logo.Margin = new Thickness(0, 0, 0, 6); brand.Children.Add(logo);
        brand.Children.Add(new TextBlock { Text = "AnotherDSHL", FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        brand.Children.Add(new TextBlock { Text = "DeepSeek Harness 的 Windows 桌面启动器", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
        var next = new Button { Content = "开始安装", MinWidth = 144, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        next.Click += (_, _) => ShowOptions();
        brand.Children.Add(next);
        _welcome.Children.Add(brand);
        var note = new Grid { ColumnSpacing = 12 };
        note.ColumnDefinitions.Add(new ColumnDefinition()); note.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        note.Children.Add(new TextBlock { Text = _preview ? "界面预览 · 不会安装" : "当前用户安装 · 卸载保留配置", FontSize = 11 });
        var platform = new TextBlock { Text = "Windows 10 / 11 · x64", FontSize = 11 };
        Grid.SetColumn(platform, 1); note.Children.Add(platform);
        Grid.SetRow(note, 1); _welcome.Children.Add(note);
        Grid.SetRow(_welcome, 1); Grid.SetRowSpan(_welcome, 3); _root.Children.Add(_welcome);
        _welcome.Visibility = Visibility.Collapsed;
    }
    private void ShowWelcome()
    {
        _hero.Visibility = _body.Visibility = _footer.Visibility = Visibility.Collapsed;
        _welcome.Visibility = Visibility.Visible;
    }
    private void ShowOptions()
    {
        _welcome.Visibility = Visibility.Collapsed;
        _hero.Visibility = _body.Visibility = _footer.Visibility = Visibility.Visible;
        _path.Focus(FocusState.Programmatic);
    }
    private static SolidColorBrush Brush(string key) => (SolidColorBrush)Application.Current.Resources[key];
    private async void Browse(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null) _path.Text = Path.Combine(folder.Path, Installation.ProductName);
    }
    private async void InstallClick(object sender, RoutedEventArgs e)
    {
        if (_complete)
        {
            if (!_uninstall && !_preview)
            {
                try { Process.Start(new ProcessStartInfo(Path.Combine(_target, Installation.ExecutableName)) { UseShellExecute = true, WorkingDirectory = _target }); }
                catch (Exception ex) { await ShowDialog("无法启动程序", ex.Message); return; }
            }
            Close(); return;
        }
        try
        {
            if (!_uninstall && !_update) _target = Installation.NormalizeDirectory(_path.Text);
            _busy = true; _primary.IsEnabled = _cancel.IsEnabled = false;
            _back.IsEnabled = false;
            _options.Visibility = Visibility.Collapsed; _activity.Visibility = Visibility.Visible;
            _progress.Visibility = Visibility.Visible; _progress.IsIndeterminate = _uninstall;
            _status.Text = _uninstall ? "正在移除程序文件…" : _update ? "正在校验并更新文件…" : "正在校验并安装文件…";
            bool desktop = _desktop.IsChecked == true, startMenu = _startMenu.IsChecked == true;
            var progress = new Progress<double>(value => _progress.Value = value);
            if (_preview) { _progress.Value = 100; }
            else await Task.Run(() => { if (_uninstall) _installation.Uninstall(_target); else if (_update) new UpdatePackageService(_installation).Apply(_package, _target, progress); else _installation.Install(AppContext.BaseDirectory, _target, desktop, startMenu, progress); });
            ShowCompletion();
        }
        catch (Exception ex)
        {
            _options.Visibility = _uninstall || _update ? Visibility.Collapsed : Visibility.Visible;
            _activity.Visibility = _uninstall || _update ? Visibility.Visible : Visibility.Collapsed;
            _status.Text = "操作未完成，可关闭程序后重试";
            await ShowDialog(_uninstall ? "卸载未完成" : _update ? "更新未完成" : "安装未完成", ex.Message);
        }
        finally { _busy = false; _primary.IsEnabled = _cancel.IsEnabled = _back.IsEnabled = true; }
    }
    private void ShowCompletion()
    {
        _complete = true;
        _heading.Text = _uninstall ? "已卸载" : _update ? "更新完成" : "安装完成";
        _subtitle.Text = _uninstall ? "AnotherDSHL 已从这台电脑移除，感谢你的使用。" : "一切就绪，按下「启动」即可打开 ADL";
        _progress.Visibility = Visibility.Collapsed;
        _legal.Visibility = Visibility.Collapsed;
        _back.Visibility = Visibility.Collapsed;
        _primary.Content = _uninstall ? "完成" : "启动";
        _cancel.Content = "关闭";
        _size.Text = _preview ? "界面预览 · 已完成" : "已准备就绪";

        _activity.Children.Clear();
        _activity.Spacing = 14;
        _activity.VerticalAlignment = VerticalAlignment.Center;

        if (_uninstall)
        {
            var card = new Border
            {
                Background = Brush("CardBackgroundFillColorDefaultBrush"),
                BorderBrush = Brush("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 14, 16, 14)
            };
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(new TextBlock { Text = "程序文件已成功移除", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
            stack.Children.Add(new TextBlock { Text = "你的个人配置与工作区数据已保留，随时可重新安装并恢复使用！", FontSize = 12, Foreground = Brush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap });
            card.Child = stack;
            _activity.Children.Add(card);
        }
        else
        {
            var card = new Border
            {
                Background = Brush("CardBackgroundFillColorDefaultBrush"),
                BorderBrush = Brush("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12)
            };
            var stack = new StackPanel { Spacing = 8 };

            var pathLabel = new TextBlock { Text = "安装目录", FontSize = 11, Foreground = Brush("TextFillColorSecondaryBrush") };
            stack.Children.Add(pathLabel);

            var pathRow = new Grid { ColumnSpacing = 8 };
            pathRow.ColumnDefinitions.Add(new ColumnDefinition());
            pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var pathText = new TextBlock { Text = _target, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var openFolder = new HyperlinkButton { Content = "打开目录", FontSize = 11, Padding = new Thickness(0), MinHeight = 0, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            openFolder.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("explorer.exe", _target) { UseShellExecute = true }); } catch {} };

            pathRow.Children.Add(pathText);
            Grid.SetColumn(openFolder, 1);
            pathRow.Children.Add(openFolder);
            stack.Children.Add(pathRow);

            stack.Children.Add(new Border { Height = 1, Background = Brush("DividerStrokeColorDefaultBrush") });

            var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
            if (_desktop.IsChecked == true)
                badges.Children.Add(new TextBlock { Text = "✓ 已创建桌面快捷方式", FontSize = 11, Foreground = Brush("TextFillColorSecondaryBrush") });
            if (_startMenu.IsChecked == true)
                badges.Children.Add(new TextBlock { Text = "✓ 已添加到开始菜单", FontSize = 11, Foreground = Brush("TextFillColorSecondaryBrush") });
            if (badges.Children.Count == 0)
                badges.Children.Add(new TextBlock { Text = "✓ 已配置运行环境", FontSize = 11, Foreground = Brush("TextFillColorSecondaryBrush") });

            stack.Children.Add(badges);
            card.Child = stack;
            _activity.Children.Add(card);

            var tip = new TextBlock { Text = "点击右下方「启动」按钮即可立即打开启动器", FontSize = 12, Foreground = Brush("TextFillColorSecondaryBrush") };
            _activity.Children.Add(tip);
        }
    }
    private async Task ShowDialog(string title, string text)
    {
        var dialog = new ContentDialog { Title = title, CloseButtonText = "关闭", XamlRoot = _root.XamlRoot,
            Content = new ScrollViewer { MaxHeight = 160, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 } } };
        await dialog.ShowAsync();
    }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
