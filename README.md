# DeskHush

> 安静桌面管理器：在一个轻量的 Windows 工具中管理弹窗规则、资源管理器右键菜单和常见登录启动项。

[English](README_EN.md) · [下载最新版](https://github.com/sanrokamlan-prog/DeskHush/releases/latest) · [安全与恢复](docs/safety-and-recovery.md) · [架构](docs/architecture.md)

[![CI](https://github.com/sanrokamlan-prog/DeskHush/actions/workflows/ci.yml/badge.svg)](https://github.com/sanrokamlan-prog/DeskHush/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/sanrokamlan-prog/DeskHush?display_name=tag)](https://github.com/sanrokamlan-prog/DeskHush/releases/latest)
[![License](https://img.shields.io/github/license/sanrokamlan-prog/DeskHush)](LICENSE)

DeskHush 是一个开源的 Windows 桌面工具。它不安装驱动，不向其他进程注入代码，也不会为了刷新右键菜单而自动重启资源管理器。涉及注册表或启动文件夹的变更会保留恢复状态，发生路径冲突时默认拒绝覆盖。

![DeskHush 概览](docs/images/overview.png)

## 直接下载使用

当前发行包面向 **Windows 10/11 x64**。

1. 打开 [Releases](https://github.com/sanrokamlan-prog/DeskHush/releases/latest)，下载 `DeskHush-v*-win-x64.zip` 和 `SHA256SUMS.txt`。
2. 校验压缩包的 SHA-256，然后解压到一个固定、可写的目录，例如 `D:\Apps\DeskHush`。不要直接在压缩包内运行。
3. 启动 `DeskHush.exe`。发行包为自包含构建，不要求另行安装 .NET 运行时。
4. 如需持续拦截，在“设置”中保留“关闭窗口时最小化到托盘”；如需登录后自动运行，再启用“开机启动”。

> 当前公开发行版未进行代码签名，Windows SmartScreen 可能显示未知发布者。只应从本仓库的 Release 下载并核对校验和；无法确认来源时不要绕过警告。

窗口关闭后，DeskHush 默认继续在通知区域运行。双击托盘图标可重新打开，完整退出请使用托盘菜单中的“退出”。

## 功能范围

| 模块 | 当前能力 | 变更方式 |
| --- | --- | --- |
| 弹窗拦截 | 按进程名/路径、窗口类和标题匹配；支持包含、精确、通配符、正则；动作可选关闭或隐藏 | 监听 `EVENT_OBJECT_SHOW`，发送 `WM_CLOSE` 或隐藏窗口 |
| 右键菜单 | 文件、文件夹、文件夹空白处、磁盘、桌面、所有文件系统对象；静态 verb 与 shell extension；HKCU/HKLM、32/64 位视图 | 静态 verb 使用 `LegacyDisable`；扩展处理器使用当前用户 `Shell Extensions\Blocked` |
| 启动项 | 当前用户/所有用户的 `Run`、`RunOnce`，当前用户/公共启动文件夹，以及可对应的任务管理器启用状态 | 保存注册表值和 `StartupApproved` 原状态；启动文件夹项目移入恢复目录 |
| 后台运行 | 单实例、托盘运行、关闭/最小化到托盘、后台参数 | `DeskHush.exe --background` |
| 开机启动 | 当前用户登录后静默启动到托盘 | 写入 HKCU `Run` 中仅属于 DeskHush 的值 |

启动项管理器目前**不枚举或修改计划任务、Windows 服务、驱动和其他自动启动扩展点**。枚举模型中预留的类型不代表这些能力已经实现。

## 弹窗规则

推荐从“当前窗口”列表选中目标窗口后创建规则，再逐步缩小匹配条件。每条规则必须同时满足：

- 限定到具体进程名或完整进程路径；
- 指定窗口标题或窗口类，避免把程序主窗口当作弹窗；
- 通过系统关键进程保护校验。

规则匹配不区分大小写。窗口类使用通配符匹配，标题支持包含、完全相同、通配符和带超时限制的正则表达式。系统会记录命中次数和最近命中时间，但不会收集或上传窗口数据。

“关闭”会向窗口发送正常关闭消息，目标程序仍可拒绝处理；“隐藏”不会结束进程，但被隐藏的窗口通常需要由目标程序重新显示或重启目标程序后恢复。为避免未保存内容丢失，请先用“隐藏”验证规则。

## 右键菜单管理

DeskHush 会扫描当前用户和本机范围内的 32/64 位注册表视图，并展示注册表来源、处理器命令和可解析的发布者信息。

- 静态菜单项：通过增加或移除 `LegacyDisable` 标记切换状态。
- Shell 扩展：通过当前用户级 `Shell Extensions\Blocked` 列表按 CLSID 切换状态；这可以覆盖当前用户和本机注册的处理器。
- 原始标记状态会写入恢复文件，恢复时回到 DeskHush 首次修改前的状态。
- 资源管理器可能缓存菜单。DeskHush 不会自动结束 `explorer.exe`；必要时请自行关闭并重新打开相关窗口，或重新登录。

本机范围的静态菜单项通常需要管理员权限。应用默认以普通用户运行，可在设置页选择“以管理员身份重新启动”。

## 启动项管理

DeskHush 会同时读取 Windows 任务管理器为持久 `Run` 项和启动文件夹维护的 `StartupApproved` 状态。关闭原本启用的注册表启动项时，会保存原始值类型、内容以及存在对应关系时的批准状态，随后删除活动值；恢复时逐项校验。对任务管理器原本已禁用的项目执行启用时，则会保存其完整批准值并在再次关闭时恢复原状态。启动文件夹项目采用同样的批准状态保护，实际文件或目录会移动到 DeskHush 的恢复目录。`RunOnce` 没有可靠的一一对应批准入口，因此只按其源注册表值管理，不会错误套用同名 `Run` 状态。

如果原位置出现同名注册表值或文件，恢复操作会停止并保留备份，不会覆盖新内容。所有用户范围的 `Run`/`RunOnce` 和公共启动文件夹项目需要管理员权限。

“启动项”页面管理的是其他程序的登录启动入口；设置页的“开机启动”只控制 DeskHush 自身，二者相互独立。

## 安全设计

- 弹窗监听使用 WinEvent out-of-context 回调，不注入目标进程，不加载第三方模块。
- 拦截规则必须限定程序和窗口特征，并拒绝系统关键进程。
- 右键菜单和启动项都先记录恢复状态，再执行可逆变更。
- 恢复时遇到并发修改或目标冲突会停止，不静默覆盖。
- 默认以当前用户权限运行；仅在修改本机范围项目时按需提权。
- 配置保存在本机，不包含遥测、云同步或规则下载功能。

这些保护不能替代系统还原点、注册表导出或完整备份。首次管理关键软件前，请阅读 [安全与恢复](docs/safety-and-recovery.md)。

## 数据位置与卸载

DeskHush 的状态默认保存在：

```text
%LOCALAPPDATA%\DeskHush\
├── settings.json
├── context-menu-state.json
├── startup-state.json
└── disabled-startup\
```

不要在仍有禁用项目时删除此目录。正确卸载顺序是：先在 DeskHush 中恢复需要保留的右键菜单和启动项，关闭 DeskHush 自身的开机启动，从托盘完整退出，再删除程序目录；确认恢复完成后才删除数据目录。详细步骤见 [安全与恢复](docs/safety-and-recovery.md)。

## 从源码构建

要求：Windows 10/11、[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```powershell
dotnet restore DeskHush.sln
dotnet build DeskHush.sln -c Release --no-restore
dotnet run --project tests/DeskHush.Tests/DeskHush.Tests.csproj -c Release
```

生成自包含的 x64 发行目录：

```powershell
dotnet publish src/DeskHush.App/DeskHush.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o artifacts/publish
```

测试项目是无第三方测试框架依赖的控制台测试运行器，包含规则匹配与安全校验、设置持久化、窗口枚举、右键菜单只读枚举和启动项只读枚举。后两类冒烟测试会读取当前 Windows 环境，但不会修改系统状态。

## 架构

```text
DeskHush.App       WPF 界面、托盘、单实例与应用生命周期
    ├── DeskHush.Core      模型、接口、规则匹配与持久化
    └── DeskHush.Windows   Win32 事件、注册表与启动文件夹适配
```

依赖只从 UI/平台层指向 Core；平台行为通过接口交给 ViewModel。更多状态流、恢复事务和扩展边界见 [架构说明](docs/architecture.md)。

## 参与项目

提交问题前请附上 Windows 版本、DeskHush 版本、目标项目的“范围/类型/注册表来源”，并删除路径中的个人信息。代码贡献请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md)；安全问题请按 [SECURITY.md](SECURITY.md) 私下报告。

## 免责声明

本工具会修改用户选择的注册表值和启动文件夹内容。请审阅目标项目并保留系统备份；使用风险由使用者自行承担。项目按 MIT License 以“现状”提供，不附带任何保证。

## 许可证

[MIT License](LICENSE) © 2026 DeskHush contributors.
