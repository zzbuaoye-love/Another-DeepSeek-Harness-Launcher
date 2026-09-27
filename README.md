# AnotherDeepSeekHarnessLauncher

**Yet Another Deepseek Harness Launcher** 
一个使用 WinUI 3 制作的 Windows 下的DeepSeek Harness图形化桌面启动器
窗口使用原生 NavigationView 侧栏和透明标题栏区域，帮助你快捷上手

## 前言
本项目使用Codex、Antigravity等AI工具共同完成
项目有大量AI参与，系VibeCoding

## 简介
ADL/YADL
首次启动 Harness 时，npx 需要联网获取官方包
ADL `不内置` DeepSeek Harness，`也不处理 API 密钥`；
这些配置在 Harness 自身界面中完成，未来或许会在APP内支持直接的预设置

自定义目录 JSON 示例：`{"plugins":[{"name":"示例插件","owner":"author","description":"功能说明","category":"tools","icon":"https://example.com/icon.png","installSpec":"@author/dsh-example","url":"https://github.com/author/dsh-example"}]}`。`installSpec` 支持 npm 包名或 `github:owner/repo`；安装前会显示确认框。目录也可使用 `description.zh/en` 和 `install` 字段（`dsh plugin --profile web add <spec>`）
> 缺少图标时显示通用插件图标
> 必要的安装要求`pnpm`否则无法安装成功

默认的 1024Store 属于第三方目录，不是 DeepSeek 官方目录；其热门排序依据关联 GitHub 仓库 Star，不能代表插件的安装量或质量

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

演示模式为演示功能而制作，无需连接真实的DSH，所有安装和设置写入均被禁用(Demo)：

```powershell
& ".\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\AnotherDSHL.exe" --demo
# 录制时可隐藏步骤控制条，固定打开第 7 幕（从 0 计数）
& ".\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\AnotherDSHL.exe" --demo --demo-clean --demo-step=7
```

点击“启动 Harness”后，资源检查、虚拟下载百分比、Web 服务启动和绿色完成提示会自动播放；
全程不下载资源或启动真实服务，完成提示几秒后自动消失。运行状态下点击“停止 Harness”会回到未启动状态
窗口获得焦点时支持通过方向键或 PageUp / PageDown 手动切幕；普通 `--demo` 模式也提供上一步/下一步按钮
手动切换会取消自动播放。`--demo-clean` 隐藏控制条，适合以正常小窗尺寸采集画面

## 项目结构

- `MainPage.xaml`：启动台布局
- `MainPage.xaml.cs`：环境检查、进程管理、服务就绪检测
- `Services/LauncherSettings.cs`：工作目录、端口、版本和背景模式持久化
- `Services/DshVersionService.cs`：读取 npm 发布版本
- `Services/PluginCatalogService.cs`：插件目录源获取与解析
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


