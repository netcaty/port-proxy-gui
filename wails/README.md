# PortProxyManager（Go + Wails 版）

`netsh interface portproxy` 的 GUI 管理工具 —— C# WinForms 版（仓库根目录）的 Go + Wails v3 重写。本目录独立成 module，不影响根目录的 C# 工程。

## 构建与运行

依赖：Go 1.24+、Node 18+、wails3 CLI（`go install github.com/wailsapp/wails/v3/cmd/wails3@latest`）。

```
wails3 dev       # 开发模式（前端热重载）
wails3 build     # 生产构建，产物 bin/portproxymanager.exe（单文件，约 11 MB）
wails3 task build:cli   # 命令行伴生程序 bin/portproxymanager-cli.exe（console 子系统，注意须带 task 子命令）
```

命令行场景（--dump / --hints / --sync）请用 `portproxymanager-cli.exe`：主程序是 GUI 子系统，交互式 cmd 不等待它，提示符会先返回。

## 无人值守跟随同步

GUI 退出后，跟随定时器也会停止。注册计划任务后由任务计划程序接管：登录时启动一次，
之后每 5 分钟运行一次同步；主程序没在托盘常驻时跟随仍然生效。

```bat
:: 注册 / 删除计划任务（需要管理员权限）
portproxymanager-cli.exe --install-sync
portproxymanager-cli.exe --remove-sync

:: 手动跑一轮（--json / --out 与 --dump 的用法一致）
portproxymanager-cli.exe --sync
portproxymanager-cli.exe --sync --json --out=%TEMP%\sync.json
```

- 任务名 `PortProxyManagerFollowSync`，以 `HighestAvailable` + `InteractiveToken` 运行，不弹 UAC；
- 动作优先指向同目录的 `portproxymanager.exe --sync --no-elevate --data-dir=...`
  （GUI 子系统，计划任务运行时不会闪控制台窗口）；
- 数据目录在注册时固定，和当前实例的 meta.json 保持一致；
- GUI 托盘菜单的「无人值守跟随同步」开关与 CLI 等价，操作结果会写日志并弹窗提示；
- 与 GUI 内每 5 分钟的定时同步通过命名互斥锁 `Local\PortProxyManager.FollowSync` 串行化，
  两边同时触发时后来者直接跳过；
- `--sync` 退出码：`0` 成功/无变化/跳过，`1` 读取或更新失败，`2` 目标已变化但当前未提权。

## 功能对照（与 C# 版）

| 功能 | 状态 | 说明 |
|---|---|---|
| 映射增删改 + netsh 预览 | ✅ | 写后重读注册表核对；修改失败自动回滚 |
| 注册表监听自动刷新 | ✅ | RegNotifyChangeKeyValue 手写 syscall |
| 数据目录（meta/settings/backups/logs） | ✅ | 与 C# 版**共用格式、共用数据目录**，可原地互换 |
| 目标跟随（WSL/Hyper-V/VMware/VBox/沙盒） | ✅ | 启动 4 秒首同步、网络地址变化即时触发（3 秒防抖）、5 分钟定时兜底 + 手动同步；虚拟机优先按 MAC 认回 |
| 无人值守跟随同步 | ✅ | `--install-sync` 注册计划任务；登录时启动 + 每 5 分钟运行 `--sync`，主程序退出后仍生效 |
| 沙盒检测 | ✅ | 见下方"与 C# 版差异" |
| ARP/网关候选 + 设备识别 | ✅ | GetIpNetTable2 邻居表、MAC OUI、Default Switch 锚点 |
| 沙盒多 IP ICMP 去重 | ✅ | 600ms 并发 ping，全不可达时保留全部 |
| 一键诊断（十步检查+修复） | ✅ | 注册表→格式→写法→归属→保留端口→iphelper→监听→连通→目标→防火墙 |
| 目标连通性测试 | ✅ | 编辑对话框按钮 + 行右键菜单（对应 C# EntryDialog / MainForm） |
| 防火墙联动 / IP Helper 启动重启 | ✅ | 规则名只由 IP:端口 生成 |
| 导入导出 JSON（差异预览）/ 自动快照 | ✅ | 文档结构与 C# 版互通 |
| 系统托盘 + 关窗进托盘 | ✅ | Wails v3 原生 tray，右键菜单含自启开关 |
| 单实例互斥（再次启动激活旧窗口） | ✅ | 命名 Mutex + 激活事件，容错 AbandonedMutex |
| UAC 自提权（UAC 取消退回只读） | ✅ | ShellExecuteExW "runas"，错误码 1223 区分 |
| 开机自启（计划任务，登录不弹 UAC） | ✅ | schtasks /xml，UTF-16 BOM，RunLevel=HighestAvailable |
| CLI 只读模式 | ✅ | `--dump [--json] [--out=] [--data-dir=]`，JSON 字段与 C# 版一致 |
| CLI 同步与任务管理 | ✅ | `--sync [--json] [--out=]`、`--install-sync`、`--remove-sync` |
| CLI 伴生程序 | ✅ | `portproxymanager-cli.exe`（console 子系统）：交互式 cmd 会正常等待主 GUI exe（windowsgui 不等待），CLI 场景请用伴生 exe |
| --selftest 自检 | ⬜ 未移植 | |

### 与 C# 版的有意差异（按需求裁剪）

- **去掉 `wsb.exe` CLI 链路**：不再调用 `wsb list`/`wsb ip`，也不再有包内路径解析与提权令牌注入（ShellProcessRunner）。
- **沙盒运行确认只靠进程 + 可见窗口**（`WindowsSandbox` / `WindowsSandboxClient` / `WindowsSandboxRemoteSession`）。
- **沙盒 IP 只走 ARP + Hyper-V 启发式 + ICMP 去重**；`.wsb` 文件名仍从进程命令行（PowerShell CIM）提取。

## 数据目录

优先级与 C# 版一致：`--data-dir` → exe 同目录 → `%ProgramData%\PortProxyManager`（实测可写才用）。

- 与 C# 版**不要同时运行**（避免并发写）。
- 评估期建议 `--data-dir` 指向 C# 版数据目录做互验。

## 界面（前端）

- 视觉方向：**跳线面板**——一行映射 = 一条跳线，跟随中的映射是有源链路（行首 LED 常亮、链路粗箭头）。主色信号青、状态色通电绿；图标全部为行内 SVG，不用 emoji。
- 反馈：应用内 toast + 确认对话框（不用 `window.alert/confirm`）；快捷键 `Ctrl+N` 新增、`Del` 删除、`/` 聚焦搜索、`F5` 刷新。
- 本地 UI 预览（不动真实注册表）：`npm run dev` 后打开 `http://127.0.0.1:9245/mocked.html`。`mocked.html` 只用于开发，`src/mock-api.ts` 按绑定方法 ID 拦截 runtime 调用并回放样例数据，不会进 `wails3 build` 产物。

## 布局

```
main.go / app.go        入口 + Wails 服务层（36 个绑定方法）
internal/               按 C# Services 一一对应：
  registryio  注册表读 + 变更监听    netsh      映射写通道（写后核对）
  hints       地址候选/沙盒确认/WSL/VM 探测    follow   目标跟随引擎
  diagnoses   十步诊断             firewall   防火墙联动
  iphelper    iphlpsvc 管理        backup     导入导出/快照
  autorun     计划任务自启         singleinst 单实例
  synctask    无人值守同步计划任务  syncguard  跨进程同步互斥
  netwatch    网络地址变化监听（NotifyIpInterfaceChange + 3 秒防抖）
  elevationx  UAC 自提权           cli        --dump / --sync 等 CLI
  paths/logging/meta/settings/proc/winansi/... 基础设施
frontend/               Vue 3 + TS（主表格、Entry/Diagnostics/Import/Log 对话框）
```

## 系统集成实现要点

- **邻居表（ARP）**：`GetIpNetTable2` 读结构化的 IP/MAC/接口索引，不解析 `arp.exe` 输出、不受控制台代码页影响。
- **默认网关**：`GetIpForwardTable2` 取 `0.0.0.0/0` 路由的 NextHop（失败才回退 PowerShell）。
- **网络变化**：`NotifyIpInterfaceChange` 回调 + 3 秒防抖触发跟随同步，与 C# 的 `NetworkChange.NetworkAddressChanged` 语义对齐；注册失败时自动退回 5 分钟定时器。
- **子进程编码**：调用方显式声明编码；ANSI（winansi）只用于 netsh / schtasks / VBoxManage / vmrun 这类带本地化文本的输出，ping 等纯 ASCII 匹配已改用 UTF-8。
