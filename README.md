# AnotherDSHL (ADL)

**Yet Another Deepseek Harness Launcher** 
一个使用 WinUI 3 编写的 Windows 桌面启动器。窗口使用原生 NavigationView 侧栏和透明标题栏区域，提供启动、工作区、插件、日志与设置页面。支持亚克力半透明模糊和 Mica 两种窗口材质，随心你的选择

## 功能

- 选择并记住 Harness 的默认工作目录。
- 检测 Node.js 版本和 npx；要求 Node.js 22.19+ 或 24+。
- Node.js 缺失、版本过低或缺少 npx 时，可从工作区或主页进入修复对话框，使用 WinGet 交互安装 Node.js LTS，或打开官方下载安装页。
- 也可手动选择本机 `node.exe`；ADL 会验证版本、查找同目录的 `npx.cmd`，并保存路径供后续启动使用。
- 本机未检测到 DSH 包时显示 ADL 标志；检测到本机缓存包后显示 DeepSeek Harness 官方矢量标志和缓存版本。
- 启动 DSH 时也会切换为官方标志，并在窗口左上角显示无背景 ADL 标志；黑底、米白底 SVG 变体由用户提供的 `Assets/AppIcon.svg` 生成。
- 主页显示具体环境问题；启动时显示进度和服务状态。
- 插件中心默认读取 [DeepSeek Harness 官方社区插件讨论区](https://github.com/deepseek-ai/deepseek-harness/discussions/categories/show-your-plugins)的近期作品、DeepSeek 官方发布的 bundle，以及独立社区维护的 [1024Store 目录](https://deepseek1024.com/api/v1/registry)（当前接口提供至多 500 条快照）。支持搜索、来源和类型筛选、GitHub Star 热门排序、详情和 DSH CLI 安装。其他运行模式的核心 bundle 仅供查看。
- 可添加自定义 HTTPS 第三方 JSON 目录；已安装列表继续读取 DSH Web profile。社区帖子和独立目录中的作品由各自作者发布，不代表 DeepSeek 审核或推荐。
- 从 npm 获取 DSH 版本列表，选择默认发布版或锁定具体版本；下次启动使用相应 `@deepseek-ai/dsh@版本` 包。
- 运行官方 Web 入口：`npx --yes @deepseek-ai/dsh web --no-open --port 3080`（端口可配置）。
- 识别已在运行的本机 DSH；运行时首页按钮变为“停止 Harness”，仅在明确点击后停止对应服务进程。
- 等待本地服务就绪后打开浏览器，显示进程输出和分阶段进度。
- `--demo` 提供固定示例数据和上一步/下一步控制，用于安全录制演示画面。
- 工作目录保存于 `%LOCALAPPDATA%\AnotherDSHL\settings.json`。

首次启动 Harness 时，npx 需要联网获取官方包。ADL 不内置 DeepSeek Harness，也不处理 API 密钥；这些配置在 Harness 自身界面中完成。

自定义目录 JSON 示例：`{"plugins":[{"name":"示例插件","owner":"author","description":"功能说明","category":"tools","icon":"https://example.com/icon.png","installSpec":"@author/dsh-example","url":"https://github.com/author/dsh-example"}]}`。`installSpec` 支持 npm 包名或 `github:owner/repo`；安装前会显示确认框。目录也可使用 `description.zh/en` 和 `install` 字段（`dsh plugin --profile web add <spec>`）。缺少图标时显示通用插件图标。

默认的 1024Store 属于第三方目录，不是 DeepSeek 官方目录；其热门排序依据关联 GitHub 仓库 Star，不能代表插件的安装量或质量。

## 构建和运行

需要 Windows 10 19041+、.NET 8 SDK 和能访问 NuGet 的网络环境。项目使用自包含的 Windows App SDK，运行时无需另行安装 MSIX。

```powershell
dotnet build AnotherDSHL.csproj -c Debug -p:Platform=x64
& ".\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\AnotherDSHL.exe"
```

如需使用启动按钮，机器上还需安装 Node.js 22.19+ 或 24+，且 `node.exe`、`npx.cmd` 应在 PATH 中。Harness 默认监听 `http://127.0.0.1:3080/`；可在设置中更改端口，启动器会用相同端口启动、识别和打开 Web 服务。重新打开启动器时会识别仍在运行的 DSH；若端口由其他程序占用，则不会重复启动。启动过程在首页底部显示阶段进度，完成后短暂显示绿色提示。

版本选择位于“工作区 → DSH 运行环境”。“npm 默认发布版”跟随 npm 的 `latest` 标签；锁定具体版本可控制下次启动所用的包。正在运行的服务不会被切换版本，需要停止后再启动。本机显示的缓存版本不一定等于一个由其他程序启动的服务版本。

演示模式无需连接真实 DSH，所有安装和设置写入均被禁用：

```powershell
& ".\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\AnotherDSHL.exe" --demo
# 录制时可隐藏步骤控制条，固定打开第 7 幕（从 0 计数）
& ".\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\AnotherDSHL.exe" --demo --demo-clean --demo-step=7
```

演示模式共 10 幕，依次展示启动、资源准备、下载、Web 服务启动、就绪、工作区与版本、插件列表与详情、设置和关于页。普通 `--demo` 模式可手动切换步骤；`--demo-clean` 用于无控制条画面采集。

## 项目结构

- `MainPage.xaml`：启动台布局
- `MainPage.xaml.cs`：环境检查、进程管理、服务就绪检测
- `Services/LauncherSettings.cs`：工作目录、端口、版本和背景模式持久化
- `Services/DshVersionService.cs`：读取 npm 发布版本
- `Services/ListeningProcessResolver.cs`：定位本地 DSH 监听进程
- `tools/generate_assets.py`：从 `Assets/AppIcon.svg` 生成配色变体和 Windows 图块；重新生成需先安装 `tools/requirements-assets.txt`
- `THIRD_PARTY_NOTICES.md`：Lucide 图标和 DSH 官方标志来源

DeepSeek Harness 官方说明：[GitHub](https://github.com/deepseek-ai/deepseek-harness)。
