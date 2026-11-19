# PortProxy 管理器 — WinUI 3 版

用 **WinUI 3（Windows App SDK）** 重写 [PortProxy 管理器](../README.md) 的界面，业务逻辑层（`Models/`、`Services/`）通过 **Compile 链接** 直接复用根目录源码，零复制、根目录零改动。

## 阶段进度

| 阶段 | 内容 | 状态 |
|---|---|---|
| 一 | 主窗口（表格/工具栏/过滤/排序/横幅/状态栏）、新增/编辑/删除、校验、设置持久化 | ✅ |
| 二 | 诊断窗口（10 项检查+修复+复查循环）、JSON 导入导出、目标跟随同步（定时/网络变化）、地址候选与跟随选择器 | ✅ |
| 二.5 | 对齐 v2.0.0：单一目标候选下拉+跟随开关、单实例静默激活、CONOUT$ 重绑、`--hints` 命令 | ✅ |
| 三 | 系统托盘（H.NotifyIcon：右键菜单/双击恢复/气泡/重启按钮/开机自启开关）、关闭进托盘、`--tray`、激活补 Focus、窗口最小尺寸 820x460、开机自启开关、日志查看器 | ✅ |

> 托盘用 [H.NotifyIcon.WinUI](https://github.com/HavenDV/H.NotifyIcon)（命令模式：`DoubleClickCommand`；菜单走 `ContextFlyout`）。
>
> 新增/编辑映射、操作日志和诊断页不再单独开窗：改为在主窗口内切换页面显示，点左上角返回或按 Esc 回到列表。表格空白处双击可直接进入新增页。
>
> 图标复用 Wails 项目的静态资源（已复制到本项目 `Assets`，不依赖 Wails 目录）：exe/窗口图标用 `Assets/AppIcon.ico`，托盘图标用 `Assets/TrayIcon.png`（嵌入程序集，运行时不依赖外部文件）。

## 构建

依赖：.NET SDK（≥11；当前目标框架 `net11.0-windows10.0.26100.0`）、[winapp CLI](https://github.com/microsoft/winappCli)（可选，用于运行调试与 UI 自动化验证）。

```bat
:: 开发调试运行（自动构建 + 启动）
winapp run PortProxyManager.WinUI\PortProxyManager.WinUI.csproj

:: 用临时数据目录启动（settings/meta/logs 写到 %TEMP%\PortProxyManager.WinUI，不碰便携数据目录）
run-tempdata.bat

:: 发布绿色目录形态（exe + 自包含运行时，无需安装 .NET / Windows App Runtime）
dotnet publish PortProxyManager.WinUI\PortProxyManager.WinUI.csproj -c Release -r win-x64 --self-contained true -o dist
```

> **为什么不发单文件**：业务层用 `AppContext.BaseDirectory` 锚定"数据跟 exe"的便携目录，
> 而 .NET 单文件的 BaseDirectory 指向 Temp 提取目录（随机 hash），会破坏该语义，
> 故目录形态是本版的主发布形态。

> **目标框架降级（实测结论）**：`net10.0-windows10.0.26100.0` 用 11.0.100 SDK 实测，只需改 `TargetFramework`，包与代码零改动；`net9.0` 需把 `H.NotifyIcon.WinUI` 降到 2.3.2，且若用 .NET 9 SDK（C# 13）编译，还需把 31 处 `[ObservableProperty] partial` 改成字段式（`CommunityToolkit.Mvvm` 8.4.2 的源生成器要求 C# 14）。`net8.0`/`net9.0` 均已 EOL，不建议。降级时保留 `windows10.0.26100.0` 平台版本——WinAppSDK 2.7 要求 `Microsoft.Windows.SDK.NET.Ref >= 10.0.26100.38`。

## 形态

- **未打包**（`WindowsPackageType=None`）+ **自包含运行时**（`WindowsAppSDKSelfContained=true`），与旧版一致保持绿色便携
- 全程不走 MSIX identity：本应用需要写 HKLM 并调用 netsh，打包运行的注册表虚拟化会导致调试行为失真
- 单实例 Mutex / UAC 自提权 / `--dump` 只读输出等宿主行为与旧版保持一致

## 与旧版的关系

- 业务层（注册表读写、netsh 封装、校验、诊断、目标跟随、备份等）**链接自根目录**，两个版本共享同一份实现；根目录的修复会自动同步到本版
- 本目录只包含：XAML 视图、MVVM 视图模型、WinUI 适配层（DispatcherQueue 线程切换、托盘宿主等）和精简后的 CLI 宿主
