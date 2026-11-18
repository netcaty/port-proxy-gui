# PortProxy 管理器 — 工作区指引

`netsh interface portproxy` 的 Windows GUI 管理工具。**注册表是真相源，netsh 是唯一写通道，iphlpsvc 实际执行转发**；另有 WSL/Hyper-V/VMware/VirtualBox/Windows 沙盒的目标 IP 探测与跟随引擎、十步诊断、防火墙联动、单实例、UAC 提权、托盘。

本仓库根目录即 C# 版工程，**三个独立工程并存、共用同一数据格式与数据目录**：

| 目录 | 工程 | 说明 |
|---|---|---|
| 根目录 | C# WinForms / .NET 9（`PortProxyManager.csproj`，v2.0.1） | 基线版本，业务源在 `Models/`、`Services/` |
| `winui/` | WinUI 3 / Windows App SDK（`PortProxyManager.WinUI`，net11.0-windows，v2.0.0） | MVVM 重写，链接复用根目录业务层 |
| `wails/` | Go + Wails v3 + Vue 3/TS（独立 go.mod） | Go 重写，v2.0.1；CLI 用 console 伴生 exe |

**根 csproj 里的 `<Compile/None/EmbeddedResource Remove="winui\**" />` 和 `Remove="wails\**" />` 不能删**——否则 SDK 默认通配会把并行工程和它们的 obj 生成文件卷进本工程编译。

## 构建与验证

- 根（.NET 9，零 NuGet 依赖，无测试工程）：`dotnet build -c Debug`；发布 `dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist\fx`（框架依赖约 296 KB）或 `--self-contained true` 自包含版（约 48 MB）。
- WinUI：`winapp run PortProxyManager.WinUI\PortProxyManager.WinUI.csproj`；发布 `dotnet publish ... -c Release -r win-x64 --self-contained true -o dist`（**目录形态**，理由见下）。
- Wails：`wails3 dev` / `wails3 build` / `wails3 task build:cli`。`--dump`/`--hints` 必须用 `portproxymanager-cli.exe`（主程序是 GUI 子系统，交互式 cmd 不等待它）。
- 验证手段：`--dump [--json]`、`--hints`、`--selftest`（仅 C# 版，Go 版未移植）。
- **工具链现状：本机只装了 .NET 8/9 SDK，但 WinUI 目标框架是 `net11.0-windows`**——本地直接 build WinUI 会失败，需先装 .NET 11 SDK；根目录工程用 .NET 9 正常。Go 1.27.1 已装。

## 架构边界（改动前务必遵守）

1. **写后核对 + 回滚**：netsh 退出码 0 **不代表落地**，写入后必须重读注册表核对；「编辑」= 先删后加，失败自动回滚。`*` 字面量脏条目要按多候选删除。
2. **安全边界**：只有校验过的 IP/端口能进 netsh 命令；名称/分组/备注/跟随标识仅用于展示与本地匹配，**绝不拼进命令行**。只支持 TCP（netsh portproxy 本身不支持 UDP）。
3. **数据目录共用**：解析优先级 `--data-dir` → exe 同目录 → `%ProgramData%\PortProxyManager`（建临时文件实测可写，不靠 ACL 推断），锚定 `AppContext.BaseDirectory`；显式指定的路径失败直接报错，**绝不静默降级**。`meta.json`/`settings.json`/backups/logs 三版格式互通，改动格式须跨版兼容；**两版不得同时运行**（并发写会冲突）。
4. **WinUI 链接复用**：`Models/**`、`Services/**`、`CliOptions.cs` 通过 csproj `<Compile Include Link>` 只读链接根目录，业务层**零 WinForms 依赖**——不要给根目录业务层引入 WinForms，否则 WinUI 编译即断。`SelfTest.cs` 调用的 `Cli.EnsureConsole`/`SafeWriteLine` 由 `winui/Cli/Cli.cs`（同名空间 `PortProxyManager`）提供。
5. **WinUI 发布形态**：未打包（`WindowsPackageType=None`）+ 自包含运行时，**不走 MSIX identity**（写 HKLM + netsh 会因注册表虚拟化而行为失真）。不能发单文件：单文件把 `AppContext.BaseDirectory` 指向 Temp 提取目录，破坏「数据跟 exe」的便携语义。`PublishTrimmed=False` 必须保留（System.Text.Json 反射序列化会被裁掉）。
6. **UI 线程**：后台线程（`RegistryWatcher.Changed`、`NetworkChange.NetworkAddressChanged`、跟随/网络变化定时器）更新 UI 必须经 `DispatcherQueue.TryEnqueue`；WinForms Timer 对应 `DispatcherQueueTimer`。
7. **WinUI 当前进行中的重构**：新增/编辑与日志查看不再单独开窗，已改为主窗口内切换页面（`Views/EntryEditView.xaml`、`Views/LogViewerView.xaml`，左上角返回或 Esc 回列表）。不要假设还是 `EntryEditWindow`/`LogViewerWindow`。

## 踩坑经验（跨版本保留，勿"顺手简化"）

- **子进程输出三态编码**：ANSI(GetACP)/UTF-8/UTF-16LE 无 BOM——netsh/arp 在中文系统上的解码是最易回归点。
- **沙盒探测**：`wsb.exe` CLI 链路（含 App Execution Alias 包内路径解析、提权令牌注入 ShellProcessRunner）已清理——提权跑 wsb 会卡死 30s；`wsb ip` 已停用。沙盒 IP = ARP + Hyper-V 启发式 + ICMP 可达性去重（600ms 并发 ping，全不可达时保留全部）；须按运行状态过滤虚拟网络邻居，关机残留与虚拟交换机内部邻居（如 MyApps vSwitch）不进下拉；负缓存加速。探测超时矩阵、防重入、两阶段缓存发布、未确认邻居过滤均来自 git log 六连修复。运行时确认靠进程 + 可见窗口，`.wsb` 文件名从进程命令行（PowerShell CIM）提取。
- **跟随语义**：虚拟机优先按 MAC 认回（改名不怕），WSL 按发行版名；WSL mirrored 网络固定跟随 `127.0.0.1`；探测不到时保留最后已知地址；已被外部删除的映射不会被重建。
- **单实例**：命名 Mutex + 激活事件，重复启动要静默激活旧窗口并补 Focus；容错 AbandonedMutex；C#/Go 两版锁名不能冲突；UAC 取消码 1223 要区分并退回只读。
- **控制台**：GUI 子系统下控制台输出会丢失——CLI 场景用 console 子系统伴生 exe。
- **下拉与布局回归**：候选 display 字段缺失会导致渲染空白；跟随模式显示会被截断；控件 Dock 会重叠遮挡（用 FlowLayoutPanel 等排列修复）。hints 的 `gw` 类型已重命名为 `gateway`，display 为 `IP - type:id` 格式，需填齐以便用户识别来源。
- **SelfTest** 内置快照污染防护；手测写真实注册表/netsh 时用高位端口，测后即删。

## 先读这些文档再动敏感区域

- 根 `README.md`：功能语义、CLI 参数、目标跟随/诊断说明。
- `winui/README.md`：阶段进度、未打包/目录形态决策、为何不发单文件；`wails/README.md`：功能对照与有意差异、CLI 伴生程序。
- `.zcode/plans/*.md`：目标输入合并方案、WinUI 重写阶段规划、Go+Wails 重写规划（含风险与踩坑补丁清单）。
- `winui/PortProxyManager.WinUI/AGENTS.md` 是 winapp CLI 通用模板（UI 自动化用 `winapp ui inspect/invoke/screenshot`）。
