# PortProxyManager → Go + Wails 重写规划

## Context（背景与可行性结论）

现有项目是 C# / .NET 9 WinForms 应用（PortProxyManager v1.2.1，约 1 万行），本质是 `netsh interface portproxy` 的 GUI 封装 + 客户机 IP 探测/目标跟随引擎：注册表为真相源、netsh 为唯一写通道、iphlpsvc 实际执行转发，另含 WSL/Hyper-V/VMware/VirtualBox/Windows 沙盒五类目标的 IP 探测跟随、一键诊断、防火墙联动、托盘、UAC 提权等。

**结论：可行。** 全部系统操作走外部命令（netsh/arp/wsl/powershell/wsb/schtasks 等）+ 少量 Win32 互操作，无内嵌服务端，Go 完全能胜任。业务编排与文本解析可逐行翻译；真正的移植难点集中在 4 处（见下文风险节）。Wails v3 已于 2026 年 10 月发布稳定版，原生支持系统托盘，正好覆盖原应用的托盘需求（v2 无托盘，不考虑）。

用户已定：前端 **Vue 3 + TypeScript**；节奏 **分阶段里程碑**；数据 **兼容现有格式并共用数据目录**。

## 硬约束

- **只在 `wails/` 子目录新增文件，现有代码零改动**。唯一接触根目录的点：`.gitignore` 追加条目（node_modules、build/bin 等，只增不改）。
- 共用数据目录，**两版不得同时运行**（避免 meta.json/settings.json 并发写冲突）。
- 数据目录逻辑照搬 `Services/Paths.cs`：`--data-dir` → exe 同目录 → `%ProgramData%\PortProxyManager`，实测可写才用；开发评估期用 `--data-dir` 显式指向 C# 版现有数据目录。

## 目录结构（`wails/` 子目录，独立 go.mod）

```
wails/
├── go.mod / main.go / app.go        # Wails v3 入口；app.go 承载绑定给前端的方法与事件
├── build/                            # wails3 构建配置、图标、app.manifest（asInvoker + PerMonitorV2）
├── internal/                         # 按 C# Services 一一对应移植
│   ├── paths/       ← Paths.cs      ├── meta/      ← MetaStore.cs+EntryMeta.cs（JSON 格式兼容）
│   ├── settings/    ← SettingsStore ├── proc/      ← ProcRunner.cs（30s 超时/KillTree/三编码解码）
│   ├── shellrun/    ← ShellProcessRunner.cs（用户令牌注入，最难点）
│   ├── registryio/  ← RegistryReader.cs + RegistryWatcher.cs
│   ├── netsh/       ← PortProxyService.cs + NetshRunner.cs（写后重读注册表核对）
│   ├── iphelper/    ← IpHelperService.cs（x/sys/windows/svc/mgr）
│   ├── firewall/    ← FirewallService.cs        ├── probe/   ← PortProbe.cs
│   ├── hints/       ← NetworkHintService.cs + GuestHintCache.cs（最大件）
│   ├── follow/      ← TargetFollowService.cs    ├── diag/    ← DiagnosticsService.cs
│   ├── reservation/ ← PortReservationService.cs ├── autorun/ ← AutorunService.cs（schtasks XML UTF-16 BOM）
│   ├── elevation/   ← Elevation.cs（ShellExecuteEx "runas"） ├── backup/  ← BackupService.cs
│   ├── logs/        ← LogService.cs             ├── cli/     ← Cli.cs（--dump/--tray/--no-elevate/--data-dir）
│   └── validator/   ← Validator.cs
├── bindings/                         # wails3 generate 产出的 TS 绑定
└── frontend/                         # Vue 3 + TS：主表格、EntryDialog、Diagnostics、ImportPreview、LogViewer
```

架构对应：C# 的定时器/事件（5 分钟跟随轮询、NetworkAddressChanged 3s 防抖、RegistryWatcher 事件驱动刷新、写后 1.5s 复查）→ Go 后端 goroutine/ticker，通过 Wails events 推送前端；托盘菜单、气泡通知用 Wails v3 原生 tray API。

## 分阶段里程碑（每阶段独立可运行、可验证）

**阶段 0 — 脚手架**：确认/安装工具链（go、node、`wails3` CLI，Bash 在规划态被拦未能预检）；`wails3 init` 建立项目骨架并跑通 hello world；`.gitignore` 追加。

**阶段 1 — 核心增删改 + 数据层**：移植 paths/meta/settings/proc/netsh/registryio/validator + 主表格 UI（列表、增删改、netsh 命令实时预览、校验、重复/占用检查）。关键语义保留：netsh 退出码 0 不代表落地，写后必须重读注册表核对；`*` 字面量脏条目的多候选删除。验证：与 C# 版 `--dump --json` 输出对比；共用数据目录下 C# 版建映射 Go 版可见，反向亦然。

**阶段 2 — 目标跟随（最大件）**：移植 shellrun（explorer 令牌 → DuplicateTokenEx → CreateProcessWithTokenW，照搬 `ShellProcessRunner.cs` 注释里的坑）、hints（ARP 解析、WSL/Hyper-V/VBox/VMware/沙盒探测、运行状态确认过滤、MAC OUI、两阶段缓存发布、负缓存）、follow。验证：开一个 Windows 沙盒 + WSL 实测 IP 识别与跟随重建。

**阶段 3 — 诊断与配套**：diag 十步流水线（带修复按钮）、firewall 联动、iphelper 状态横幅与一键启动、保留端口区间解析、备份/导入导出 JSON（预览差异）。

**阶段 4 — 系统集成**：Wails v3 托盘（关窗进托盘、右键菜单、气泡通知）、单实例 Mutex（含 AbandonedMutex 接管）、UAC 自提权重启（保留只读免提权路径与 1223 取消码）、schtasks 开机自启、CLI 只读模式（--dump/--tray/--no-elevate/--data-dir）。

**阶段 5 — 打包发布**：`wails3 build` 单 exe（预期 <15MB，对照 C# 自包含版 48MB）、沿用现有版本号与 release-notes 流程；可选移植 SelfTest。

## 主要风险与对策

1. **令牌注入跑 wsb**（最难，~400 行 syscall）：用 `golang.org/x/sys/windows` 手写，行为逐项对照 C# 版；不移植则“提权运行 + 沙盒打开”必挂 30s。
2. **子进程输出三态编码**（ANSI GetACP/UTF-8/UTF-16LE 无 BOM）：`golang.org/x/text/encoding` + GetACP 映射；netsh/arp 在中文系统上的解码是最易回归点。
3. **RegNotifyChangeKeyValue 注册表监听**：Go 无现成库，手写 CreateEvent + 通知循环（含 5s 重订阅、250ms 去抖、键不存在退化 2s 轮询）。
4. **踩坑补丁清单整体保留**：wsb CLI 包内路径优先（App Execution Alias 提权卡死）、`wsb ip` 停用、沙盒残留 IP 的 ICMP 去重、未确认邻居过滤、探测超时矩阵、防重入——全部来自 git log 六连修复，逐条核对翻译，不许“顺手简化”。

## 验证方式

- 每阶段：`wails3 dev` 手测 + Go 单元测试（netsh 输出解析、注册表读取、meta.json 兼容读、编码解码——用 C# 版产生的真实文件做夹具）。
- 阶段 1/2 起：C# 版与 Go 版交替开关，共用同一数据目录互验；注册表状态用 `netsh interface portproxy show all` 与注册表双对照。
- 最终：真实场景回归——增删改映射、目标跟随（WSL + 沙盒各一）、外部 netsh 修改后界面自动刷新、托盘、提权路径、--dump 只读路径。
