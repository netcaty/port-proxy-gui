# PortProxyManager（Go + Wails 版）

`netsh interface portproxy` 的 GUI 管理工具 —— C# WinForms 版（仓库根目录）的 Go + Wails v3 重写。本目录独立成 module，不影响根目录的 C# 工程。

## 构建与运行

依赖：Go 1.24+、Node 18+、wails3 CLI（`go install github.com/wailsapp/wails/v3/cmd/wails3@latest`）。

```
wails3 dev     # 开发模式（前端热重载）
wails3 build   # 生产构建，产物 bin/portproxymanager.exe（单文件，约 11 MB）
```

## 功能对照（与 C# 版）

| 功能 | 状态 | 说明 |
|---|---|---|
| 映射增删改 + netsh 预览 | ✅ | 写后重读注册表核对；修改失败自动回滚 |
| 注册表监听自动刷新 | ✅ | RegNotifyChangeKeyValue 手写 syscall |
| 数据目录（meta/settings/backups/logs） | ✅ | 与 C# 版**共用格式、共用数据目录**，可原地互换 |
| 目标跟随（WSL/Hyper-V/VMware/VBox/沙盒） | ✅ | 5 分钟定时 + 手动同步；虚拟机优先按 MAC 认回 |
| 沙盒检测 | ✅ | 见下方"与 C# 版差异" |
| ARP/网关候选 + 设备识别 | ✅ | arp.exe 解析、MAC OUI、Default Switch 锚点 |
| 沙盒多 IP ICMP 去重 | ✅ | 600ms 并发 ping，全不可达时保留全部 |
| 一键诊断（十步检查+修复） | ✅ | 注册表→格式→写法→归属→保留端口→iphelper→监听→连通→目标→防火墙 |
| 防火墙联动 / IP Helper 启动重启 | ✅ | 规则名只由 IP:端口 生成 |
| 导入导出 JSON（差异预览）/ 自动快照 | ✅ | 文档结构与 C# 版互通 |
| 系统托盘 + 关窗进托盘 | ✅ | Wails v3 原生 tray，右键菜单含自启开关 |
| 单实例互斥（再次启动激活旧窗口） | ✅ | 命名 Mutex + 激活事件，容错 AbandonedMutex |
| UAC 自提权（UAC 取消退回只读） | ✅ | ShellExecuteExW "runas"，错误码 1223 区分 |
| 开机自启（计划任务，登录不弹 UAC） | ✅ | schtasks /xml，UTF-16 BOM，RunLevel=HighestAvailable |
| CLI 只读模式 | ✅ | `--dump [--json] [--out=] [--data-dir=]`，JSON 字段与 C# 版一致 |
| --selftest 自检 | ⬜ 未移植 | |

### 与 C# 版的有意差异（按需求裁剪）

- **去掉 `wsb.exe` CLI 链路**：不再调用 `wsb list`/`wsb ip`，也不再有包内路径解析与提权令牌注入（ShellProcessRunner）。
- **沙盒运行确认只靠进程 + 可见窗口**（`WindowsSandbox` / `WindowsSandboxClient` / `WindowsSandboxRemoteSession`）。
- **沙盒 IP 只走 ARP + Hyper-V 启发式 + ICMP 去重**；`.wsb` 文件名仍从进程命令行（PowerShell CIM）提取。

## 数据目录

优先级与 C# 版一致：`--data-dir` → exe 同目录 → `%ProgramData%\PortProxyManager`（实测可写才用）。

- 与 C# 版**不要同时运行**（避免并发写）。
- 评估期建议 `--data-dir` 指向 C# 版数据目录做互验。

## 布局

```
main.go / app.go        入口 + Wails 服务层（36 个绑定方法）
internal/               按 C# Services 一一对应：
  registryio  注册表读 + 变更监听    netsh      映射写通道（写后核对）
  hints       地址候选/沙盒确认/WSL/VM 探测    follow   目标跟随引擎
  diagnoses   十步诊断             firewall   防火墙联动
  iphelper    iphlpsvc 管理        backup     导入导出/快照
  autorun     计划任务自启         singleinst 单实例
  elevationx  UAC 自提权           cli        --dump 只读模式
  paths/logging/meta/settings/proc/winansi/... 基础设施
frontend/               Vue 3 + TS（主表格、Entry/Diagnostics/Import/Log 对话框）
```
