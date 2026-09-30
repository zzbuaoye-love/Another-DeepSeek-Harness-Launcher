# AnotherDeepSeekHarnessLauncher

**Yet Another Deepseek Harness Launcher** 
一个使用 WinUI 3 制作的 Windows 下的DeepSeek Harness图形化桌面启动器
窗口使用原生 NavigationView 侧栏和透明标题栏区域，帮助你快捷上手

## 前言
本项目使用Codex、Antigravity等AI工具共同完成
项目有大量AI参与，系VibeCoding

## 简介
ADL/YADL
首次启动 Web 版时，npx 需要联网获取官方包
ADL `不内置` DeepSeek Harness，`也不处理 API 密钥`；
这些配置在 Harness 自身界面中完成，未来或许会在APP内支持直接的预设置

自定义目录 JSON 示例：`{"plugins":[{"name":"示例插件","owner":"author","description":"功能说明","category":"tools","icon":"https://example.com/icon.png","installSpec":"@author/dsh-example","url":"https://github.com/author/dsh-example"}]}`。`installSpec` 支持 npm 包名或 `github:owner/repo`；安装前会显示确认框。目录也可使用 `description.zh/en` 和 `install` 字段（`dsh plugin --profile web add <spec>`）
> 缺少图标时显示通用插件图标
> 必要的安装要求`pnpm`否则无法安装成功

默认的 1024Store 属于第三方目录，不是 DeepSeek 官方目录；其热门排序依据关联 GitHub 仓库 Star，不能代表插件的安装量或质量

## 官方桌面客户端

首页右上角的下拉栏可切换“常规版”和“桌面版”，并记住选择。常规版启动本地 Web 服务；桌面版启动官方 DeepSeek Harness 客户端，未安装时按钮进入下载安装流程。切换方式不会停止已运行的 Web 服务。“工作区 → 官方桌面客户端”提供版本与路径检测、启动、安装/更新、取消下载，以及手动选择程序。

安装包来自 [DeepSeek 官网](https://www.deepseek.com/harness/) 的 Windows x64 最新下载入口。启动器下载后验证 Windows Authenticode 签名和 DeepSeek 发布者，再打开交互式安装程序；安装结果以重新检测到程序为准。桌面版自带运行时，工作区和更新在官方客户端中管理。启动器的工作目录、npm 版本和 Web 端口设置用于 Web 版。

## 整合包

侧边栏“整合包”读取 [DSH PackForge 市场](https://github.com/DSH-PackForge/dsh-pack-market) 的 schemaVersion 2 索引，采用与插件页一致的浏览列表和独立详情页，可搜索、查看摘要和来源仓库。缺少文件大小或 SHA-256 的条目也会显示，但仅提供仓库入口。下载 `.dspack` 时核对索引中的文件大小与 SHA-256，再由启动器内置的 PackForge 核心引擎解析和安装，无需安装 PackForge 桌面管理器。

安装前显示包内容、DSH 版本、目标路径和工作目录，可选择启动 Profile。每次安装创建独立的 `LocalAppData/AnotherDSHL/Instances` 实例，包含自己的 DSH 运行时、DSH_HOME 和 Profile。底部显示安装进度并支持取消；失败或取消会清理本次临时安装目录，保留下载缓存。安装完成后可在首页左上角选择实例，选择会持久保存；需要先停止当前服务再切换实例。整合包实例使用“常规版”启动，官方桌面客户端仍由其自身管理工作区。

安装和导出需要在工作区配置 Node.js 22.19+ 或 24+。安装会联网下载 DSH 和依赖；未检测到可用 pnpm 时，在实例内准备固定版本的 pnpm，不修改全局安装。支持 `.dspack` v2 / manifest v4 和 `.dspack` v3 / manifest v5 的 Profile 与 DSH_HOME 包；当前不支持旧 `.tgz` 和包含 vendored 离线依赖的扩展包，安装前会明确提示。

“导入 / 导出”标签提供本地 `.dspack` 安装、已安装实例选择及 Profile 导出。导出也使用内置引擎，可先预览文件扫描结果，再生成 manifest v5 / `.dspack` v3，过滤 `.env` 等敏感文件。格式规范见 [DSH-PackForge](https://github.com/DSH-PackForge/DSH-PackForge)。

[DSH PackForge 管理器](https://github.com/DSH-PackForge/dsh-packforge-app) 保留为可选入口。“工作区 → PackForge 管理器”可选择安装版或便携版程序，立即固定并保存路径，重启仍生效；“恢复自动检测”取消固定。已下载包可通过“用外部管理器打开”交给所选程序，无需系统文件关联；路径失效时提示重新选择。“显示文件”可定位本地包。

内置引擎的来源、构建步骤和测试说明见 `packforge/README.md`。运行 `dotnet run --project packforge/tests/Engine.Smoke.csproj` 可验证安装、导出、实例启动参数、完整性检查和取消清理；添加 `-- --real --launch` 可联网验证真实市场包安装及独立 Profile 的 Web 启动。

## 构建和运行

环境要求:
Windows 10 19041+、.NET 8 SDK 和能访问 NuGet 的网络环境
项目使用自包含的 Windows App SDK，运行时无需另行安装 MSIX

```powershell
dotnet build AnotherDSHL.csproj -c Debug -p:Platform=x64
& ".\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\AnotherDSHL.exe"
```

如需使用启动功能，设备还需安装 Node.js 22.19+ 或 24+
且 `node.exe`、`npx.cmd` 应在 PATH 中
Harness 默认监听 `http://127.0.0.1:3080/`；
可在设置中更改端口，启动器会用相同端口启动、识别和打开 Web 服务
重新打开启动器时会识别仍在运行的 DSH；若端口由其他程序占用，则不会重复启动
启动过程在首页底部显示阶段进度，完成后短暂显示绿色提示

版本选择位于“工作区 → DSH 运行环境”。“npm 默认发布版”跟随 npm 的 `latest` 标签；锁定具体版本可控制下次启动所用的包
正在运行的服务不会被切换版本，需要停止后再启动
本机显示的缓存版本不一定等于一个由其他程序启动的服务版本

## WinUI 安装器

下载 [GitHub Releases](https://github.com/zzbuaoye-love/Another-DeepSeek-Harness-Launcher/releases) 中的 `Setup.exe` 安装版，或解压 `Portable.zip` 使用便携版；两者均包含运行时与整合包引擎。`SHA256SUMS.txt` 提供资源校验值。

安装器位于 `installer/`，使用 WinUI 官方控件，提供安装位置选择、桌面与开始菜单快捷方式、文件校验、失败回滚及卸载。构建需 PowerShell 7、.NET 8 SDK 和 Visual Studio C++ Build Tools（含 Windows SDK）。

```powershell
pwsh -File installer/scripts/Build-Installer.ps1
dotnet run --project installer/tests/Installer.Smoke.csproj -c Release
```

安装包输出至 `installer/artifacts/<版本>-win-x64/`。打包脚本共享启动器和安装界面的运行时，以 LZX 压缩生成离线 Setup EXE；构建中间目录与产物由 Git 忽略。默认安装到当前用户的 `LocalAppData/Programs/AnotherDSHL`。
构建盘空间不足时，可通过 `-WorkDirectory C:\Temp\ADL-build` 指定其他磁盘上的临时目录。

### 启动器更新

“关于 → 启动器更新”支持手动检查 GitHub Releases、选择是否包含测试版本、取消检查或下载，以及稍后安装。下载会核对资源大小与 SHA-256，失败或取消后清理本次临时缓存；校验成功的更新会记录下来，重启后仍可继续安装，启动安装前再次校验。下载与退出安装分别确认；正在进行其他安装任务时，需要先完成或取消该任务。

安装版优先使用基础版本匹配的 `.adup` 差分包，否则使用完整 Setup。可手动选择本地 `.adup`，启动器先复制并校验其中每个变更文件，再交给维护工具安装。更新保留设置、已有快捷方式和非安装包拥有的文件，出现错误时回滚文件及注册信息。便携版和首版 `v0.0.1-beta` ZIP 用户使用完整安装包升级；本次发布为后续差分更新提供安装基础。

安装器构建同时生成完整 Setup、便携 ZIP 和校验清单。保留实际发布的 `build-info.json` 中记录的 Payload 目录，可用 `-BasePayloadDirectory` 为后续版本生成差分包。详细说明见 `installer/README.md`。

演示模式为演示功能而制作，无需连接真实的DSH，所有安装和设置写入均被禁用(Demo)：

```powershell
& ".\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\AnotherDSHL.exe" --demo
# 录制时可隐藏步骤控制条，固定打开第 7 幕（从 0 计数）
& ".\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\AnotherDSHL.exe" --demo --demo-clean --demo-step=7
```

点击“启动 Web 版”后，资源检查、虚拟下载百分比、Web 服务启动和绿色完成提示会自动播放；
全程不下载资源或启动真实服务，完成提示几秒后自动消失。运行状态下点击“停止 Web 版”会回到未启动状态
窗口获得焦点时支持通过方向键或 PageUp / PageDown 手动切幕；普通 `--demo` 模式也提供上一步/下一步按钮
手动切换会取消自动播放。`--demo-clean` 隐藏控制条，适合以正常小窗尺寸采集画面

## 项目结构

- `MainPage.xaml`：启动台布局
- `MainPage.xaml.cs`：环境检查、进程管理、服务就绪检测
- `Services/LauncherSettings.cs`：工作目录、端口、版本和背景模式持久化
- `Services/DshVersionService.cs`：读取 npm 发布版本
- `Services/PluginCatalogService.cs`：插件目录源获取与解析
- `Services/PackForgeMarketService.cs`：整合包市场索引与下载完整性校验
- `Services/PackForgeLauncherService.cs`：管理器检测、直接打开与文件关联回退
- `Services/PackForgeEngineService.cs`：内置引擎调用、独立实例安装与启动、取消清理
- `MainPage.PackInstall.cs`：整合包安装预览、进度和实例选择
- `packforge/`：PackForge 核心源码、引擎构建及烟雾测试
- `Resources/PackForge/`：随启动器打包的引擎与许可证
- `Services/DesktopClientService.cs`：官方桌面客户端安装检测、下载与签名校验
- `Services/AppUpdateService.cs`：发布检查、更新下载校验与稍后安装缓存
- `MainPage.Updates.cs`：更新操作与取消、确认流程
- `installer/`：WinUI 安装及维护工具、差分包构建和验证
- `Services/ListeningProcessResolver.cs`：定位本地 DSH 监听进程
- `tools/generate_assets.py`：从 `Assets/AppIcon.svg` 生成配色变体和 Windows 图块；重新生成需先安装 `tools/requirements-assets.txt`
- `THIRD_PARTY_NOTICES.md`：Lucide 图标和 DSH 官方标志来源

DeepSeek Harness 官方说明：[GitHub](https://github.com/deepseek-ai/deepseek-harness)

---

## LICENSE 与免责声明
- 本项目遵循 `GPLv3` 开源协议进行开源
- 项目内 DeepSeek 相关内容非本项目产物，均以原开源协议为主
- 详情参见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)

## 尾声
若觉得本项目有帮助，请点个Star感谢！


