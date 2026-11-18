# WinUI 3 重写计划 — 阶段一（使用 winapp CLI 工具链）

## Context

PortProxy 管理器（netsh portproxy 管理 GUI，.NET 9 WinForms，约 7000 行业务 + 6500 行 UI）迁移到 WinUI 3，在 `winui\` 子目录操作，**不动根目录任何现有文件**。

探索结论：业务层（`Models\`、`Services\` 共 26 文件）零 WinForms 依赖可直接复用；仅 `Cli.cs` 的 `ErrorBox` 和 `Program.cs` 引用 WinForms。**可行**。

已确认决策：① 业务代码**链接引用**（新 csproj 只读链接根目录源文件）；② 引入**社区工具包**（CommunityToolkit.Mvvm + DataGrid + H.NotifyIcon）；③ **分阶段交付**，本次实施阶段一。

工具链（本机已装 `winappcli` dotnet global tool，即微软官方 microsoft/winappCli）：
- `winapp new` — 从官方 Windows App SDK 模板生成 WinUI 项目（模板与当前 SDK 版本匹配，避免手写 csproj 踩版本坑）
- `winapp run -p WindowsPackageType=None --debug-output` — 构建+启动+捕获崩溃输出
- `winapp ui inspect/screenshot/invoke` — GUI 自动化验证
- `winapp find-api/find-ui` — 查 API 用法

**形态决策：全程 unpackaged（`WindowsPackageType=None` + `WindowsAppSDKSelfContained=true`）**。理由：本应用要写 HKLM、跑 netsh；MSIX 打包运行会触发注册表虚拟化导致调试失真；绿色便携是现有定位。不用 sparse identity（无 toast 等 identity API 需求）。

## 项目结构（全部新增于 winui\ 子目录）

```
winui\
├── README.md                        # 新版说明、阶段进度
└── PortProxyManager.WinUI\          # 由 winapp new 生成骨架后定制
    ├── PortProxyManager.WinUI.csproj
    ├── app.manifest                 # asInvoker（自提权策略与旧版一致）
    ├── App.xaml / App.xaml.cs       # 启动管道：CLI 分发→自提权→单实例→服务组装
    ├── Cli\Cli.cs                   # 新写精简版（EnsureConsole/SafeWriteLine/Dump/RelaunchElevated；ErrorBox 改日志+ContentDialog）；命名空间保持 PortProxyManager，满足链接进来的 SelfTest.cs 对 Cli.EnsureConsole 的调用
    ├── ViewModels\                  # MainViewModel、EntryRowViewModel、EntryEditViewModel
    └── Views\                       # MainWindow、EntryDialogWindow
```

## csproj 定制要点（在 winapp new 生成的骨架上改）

- 保留模板的 WindowsAppSDK / Microsoft.Windows.SDK.BuildTools.WinApp 引用，TFM 按模板（若为 net10.x 而本机无 net10 SDK，则降为 `net9.0-windows10.0.22621.0`，以 `dotnet --list-sdks` 和 NuGet 解析为准）
- `<WindowsPackageType>None</WindowsPackageType>`、`<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>`
- 删除模板的示例页面/Package.appxmanifest（不用打包形态）
- 链接编译根目录源码（零复制、零改动）：
  ```xml
  <Compile Include="..\..\Models\**\*.cs" Link="Core\Models\%(Filename)%(Extension)" />
  <Compile Include="..\..\Services\**\*.cs" Link="Core\Services\%(Filename)%(Extension)" />
  <Compile Include="..\..\CliOptions.cs" Link="Core\CliOptions.cs" />
  ```
- NuGet：CommunityToolkit.Mvvm、CommunityToolkit.WinUI.Controls.DataGrid、H.NotifyIcon（各取最新稳定版）

## 阶段一实施步骤

1. **脚手架**：`winapp new`（先 `--list` 确认模板名，优先 `winui-mvvm`，否则空白 `winui` 模板 + 手搭 MVVM）→ 按上述要点定制 csproj → 删模板示例页
2. **App 启动管道**（移植 `Program.cs` 流程）：`CliOptions.Parse` → `--help/--dump/--selftest` 控制台路径 → 非提权时 `Cli.RelaunchElevated`（1223 取消处理）→ 命名 `Mutex`（`Local\PortProxyManager.SingleInstance`）+ `EventWaitHandle` 激活已有实例 → `PathContext.Resolve` → `CrashLogger.Initialize` + `LogService`；异常兜底三件套 → `CrashLogger.Report`
3. **MainWindow**（对照 `UI\MainForm.cs` 阶段一子集）：CommandBar（新增 Ctrl+N/编辑 Ctrl+E/删除 Del/刷新 F5/过滤框/分组下拉）；DataGrid 10 列（展示逻辑从 `UI\EntryRow.cs` 重写为 EntryRowViewModel）；表头排序、文本过滤、双击编辑；InfoBar 横幅（IP Helper 停止 + 启动服务按钮，复用 `IpHelperService`）；状态栏（条目数/IP Helper 状态/数据目录）；行右键 MenuFlyout（编辑/删除/复制 netsh 命令/测试连通性）
   - **线程适配**：`RegistryWatcher.Changed` 与 `NetworkChange.NetworkAddressChanged` 后台线程 → `DispatcherQueue.TryEnqueue`；WinForms Timer → `DispatcherQueueTimer`
4. **EntryDialogWindow**（对照 `UI\EntryDialog.cs`）：类型（family 自动推断）/监听/目标输入；实时校验（`Validator`，含保留端口拦截）；实时 netsh 命令预览（`PortProxyEntry` 已有）；测试连通性（`PortProbe`）；内联错误。候选地址探测与跟随选择器留阶段二
5. **增删改通道**：`PortProxyService.Add/Delete/Update`（自带核对与回滚）+ 写后 1.5s 健康复查照搬
6. **设置持久化**：`SettingsStore`/`MetaStore`/`PathContext` 直接复用；窗口位置尺寸→AppWindow、LastFilter/LastGroup、DataGrid 列宽
7. **发布**：`dotnet publish -c Release -r win-x64 --self-contained true`，产物放 `winui\dist\`

## 验证

- `dotnet build` 干净；`git status` 确认根目录零改动（全程只读链接）
- `winapp run -p WindowsPackageType=None --debug-output` 启动无崩溃
- `winapp ui screenshot` 截图人工核验布局与交互
- `winui\dist\...\PortProxyManager.WinUI.exe --dump` 输出与旧版 exe 对比一致
- `--selftest` 全绿（含写入阶段：临时映射增删核对 + 前后快照比对，SelfTest 内置污染防护）
- GUI 手测 + `winapp ui` 自动化：高位端口新增映射→核实→编辑→删除；过滤/排序；重启后窗口位置与过滤恢复；外部改动自动刷新（RegistryWatcher）
- 未提权运行弹 UAC；取消 UAC 退出码 1223
- **验收标准：能完整管理映射（增删改查+校验+持久化），等价旧版核心日常操作**

## 风险与预案

- WindowsAppSDK 2.6.5 / 模板默认 TFM（可能 net10）与本机 SDK 不匹配 → 以 `dotnet --list-sdks` + restore 实际要求调整 TFM 或回退 SDK 包版本
- DataGrid/H.NotifyIcon 与当前 WindowsAppSDK 版本兼容性 → 优先选与模板 SDK 匹配的稳定版，必要时以 ListView 方案兜底（仅 DataGrid 部分）
- 模板生成的 Package.appxmanifest 与 unpackaged 无关，删除即可；`winapp run` 对 unpackaged 项目文档明确支持
- 测试写真实注册表/netsh → SelfTest 有快照恢复；GUI 手测用高位端口，测后即删

## 后续阶段（本次不实施）

- **阶段二**：DiagnosticsDialog（10 项检查+修复）、导入/导出+ImportPreviewDialog、跟随同步全套（TargetFollowService UI+定时器+InfoBar 横幅全套）、EntryDialog 候选探测与跟随选择器
- **阶段三**：H.NotifyIcon 托盘（关闭进托盘/--tray）、开机自启 UI、LogViewerForm、完整 CLI 帮助文本