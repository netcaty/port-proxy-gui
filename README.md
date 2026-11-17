# PortProxy 管理器

`netsh interface portproxy` 的 Windows 图形界面封装。用来管理 TCP 端口转发：
图形化增删改、本地备注分组、导入导出、防火墙联动，绿色便携、可指定数据目录。

---

## 为什么需要它

命令行管理 portproxy 有几个固定痛点，这个工具就是围着它们做的：

| 命令行的问题 | 本工具的做法 |
|---|---|
| `netsh` 输出是系统 ANSI 编码（中文系统为 GBK），在终端里常是乱码 | 读取走注册表，不经控制台；必须调用 netsh 时按 `GetACP()` 解码 |
| 要记住 `listenaddress= listenport= connectaddress= connectport=` 一长串参数 | 表单填写，实时预览将要执行的等价命令 |
| 没有校验，端口写错、地址填反只能事后排查 | 地址族、端口范围、重复监听、端口占用全部前置校验 |
| 无法命名、无法备注，几条以上就分不清谁是谁 | 名称 / 分组 / 备注，按四元组关联，可过滤、可导出 |
| 增删改都要手动提权开 cmd | 启动时一次 UAC，之后都在界面里操作 |

---

## 快速开始

1. 双击 `PortProxyManager.exe`，在 UAC 提示里点「是」。
2. 主界面列出当前所有映射（直接读注册表，不是解析 netsh 输出）。
3. 「新增」→ 填监听地址/端口、目标地址/端口 → 创建。
4. 创建后工具会立刻重读注册表核对，确认映射真的落地了才报成功。

想先看看现状而不提权？用 `--dump`（见下），它只读注册表。

---

## 命令行参数

```
PortProxyManager.exe [选项]

--data-dir=<路径>   指定数据目录。相对路径按 exe 所在目录解析。
                    显式指定后不会自动降级，失败即报错退出。
--tray              启动后直接收进托盘，不开主窗口。
--dump              以文本输出当前全部映射后退出（只读，不需要管理员）。
--json              配合 --dump，输出 JSON。
--out=<文件>        配合 --dump，同时把结果写入文件（相对当前目录）。
--selftest          自检：读取、校验规则、配置读写、增删核对，输出 selftest-report.json。
                    未提权时会先尝试提权；取消 UAC 则只跑只读部分。
--no-elevate        不自动提权：界面只读，任何写入操作都会提示需要管理员权限。
-h, --help          显示帮助。
```

示例：

```bat
:: 查看当前映射（无 UAC）
PortProxyManager.exe --dump

:: 机器脚本里解析
PortProxyManager.exe --dump --json --out=%TEMP%\portproxy.json

:: 数据目录放在固定位置，方便多台机器同步配置
PortProxyManager.exe --data-dir=D:\tools\portproxy-data
```

---

## 数据存放

**映射本身不另存副本。** 唯一数据源是注册表：

```
HKLM\SYSTEM\CurrentControlSet\Services\PortProxy\<v4tov4|v4tov6|v6tov4|v6tov6>\tcp
    值名   = 监听地址/端口
    值数据 = 目标地址/端口
```

工具只读它；所有写入都经 `netsh interface portproxy`。**不直接改注册表**——绕过 netsh
写的键重启后才生效，运行中的 IP Helper 不会立即重载，会出现「配置改了、转发没变」的状态。

其余内容放在数据目录：

```
<数据目录>\
  meta.json     名称 / 分组 / 备注 / 防火墙标记，键为 family|proto|监听地址|端口
  settings.json 窗口位置、过滤条件、托盘行为
  logs\         每日一个日志文件，保留 7 天
  backups\      删除、修改、导入前的自动快照
```

数据目录按以下顺序确定：

| 优先级 | 来源 | 不可用时 |
|---|---|---|
| 1 | `--data-dir` 显式指定 | **报错退出（退出码 2），绝不静默改道** |
| 2 | exe 同目录（默认，绿色便携） | 自动降级到 `%ProgramData%\PortProxyManager\`，界面顶部横幅提示 |
| 3 | `%ProgramData%\PortProxyManager\` | 报错退出 |

exe 位于 `Program Files`、`%SystemRoot%` 等系统保护目录时，视为不可用，直接走降级分支。
配置跟随 exe 移动；显式指定过的 `--data-dir` 会被写进开机自启的计划任务，托盘自启时不用再传。

---

## 功能

- **列表**：名称、分组、类型、监听地址/端口、目标地址/端口、防火墙；点表头排序，文本框与分组下拉过滤
- **增删改**：编辑等于「先删后加」，失败自动回滚到原配置
- **校验**：地址族匹配（v4 侧不能填 IPv6）、端口 1–65535、重复监听拦截、端口占用提示、family 自动推断
- **IP Helper**：`iphlpsvc` 没运行时横幅告警并支持一键启动
- **目标地址候选**：下拉列出默认网关、WSL、Hyper-V/沙盒/VMware 虚拟机和 ARP 邻居设备；
  能反向解析出名称的局域网设备会直接显示设备名，不再显示 MAC 和本机网卡 / 虚拟网卡地址。
  探测结果缓存 60 秒，过期后在后台刷新，下拉先显示缓存；仍可直接手输任意 IP
- **防火墙联动**（可选）：创建映射时同步添加入站放行规则，删除时按需清理
- **导入导出**：单个 JSON 含映射与备注；导入先 diff 预览，逐条勾选，「删除」默认不勾选
- **日志**：完整 netsh 命令、退出码、原始报错，界面内可查
- **托盘**：关闭窗口进托盘；开机自启用计划任务（`/rl highest`），避免每次开机弹 UAC

---

## 已知限制

- **只支持 TCP。** `netsh portproxy` 本身不支持 UDP，这是系统限制，不是本工具的取舍。
  需要 UDP 转发得换 NetNat 或第三方方案。
- **每次启动要过一次 UAC。** 写入 portproxy 必须管理员权限。只读的 `--dump` 不需要。
- **删除不可逆。** 已用二次确认 + 自动备份兜底，备份在 `backups\` 里。
- **映射随重启自动恢复。** portproxy 由 IP Helper 在开机时从注册表重放，不需要守护进程；
  「开机自启」只是让这个图形界面常驻托盘。

---

## 安全边界

数据目录里的 JSON 可能位于低权限可写的位置，因此：

- `name` / `group` / `note` 只用于界面展示，**绝不会拼进任何命令行**；
- 防火墙规则名固定用 `{地址}:{端口}` 生成，不使用自由文本；
- 进入 netsh 命令行的只有经过严格校验的 IP、1–65535 的端口和四个枚举 family 值。

即使这些 JSON 被篡改，也无法造成命令注入。

---

## 从源码构建

需要 .NET 9 SDK。

```bat
:: 框架依赖（本机需装 .NET 9 Desktop Runtime），单文件约 296 KB
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist\fx

:: 自包含（免安装运行时），单文件约 48 MB
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true ^
  -p:EnableCompressionInSingleFile=true -o dist\selfcontained
```

工程只用 WinForms 与 BCL，**没有任何 NuGet 依赖**：系统 ANSI 代码页解码走
`GetACP()` + `MultiByteToWideChar`，服务状态走 `OpenSCManager` / `QueryServiceStatusEx`。

---

## 自检

```bat
PortProxyManager.exe --selftest
```

会依次检查：注册表解析、唯一键构造、校验规则（含端口 0/65536、非法地址、重复监听等用例）、
数据目录可用性、meta/settings 读写往返、IP Helper 状态，然后**新增一条高位端口的临时映射、
独立核对注册表、再删除、最后比对前后快照**，确认现有配置没被动过。

取消 UAC 时只跑只读部分，写入相关项标为跳过。结果写入 `selftest-report.json`，
全部通过时退出码为 0。

---

## 日志位置

`<数据目录>\logs\app-yyyyMMdd.log`，界面里「日志」按钮可直接查看，保留最近 7 天。
启动时会记录解析到的数据目录、来源、权限、exe 构建时间和窗口 DPI，排查「配置到底在哪」时先看这一行。

未处理异常会写成同目录下的 `crash-yyyyMMdd-HHmmss.log`（不可写时依次回退到 exe 目录、
ProgramData、LocalAppData、%TEMP%）。界面线程的异常会弹窗提示并保留进程，不会静默消失；
真遇到闪退，先到这个文件里看栈。
