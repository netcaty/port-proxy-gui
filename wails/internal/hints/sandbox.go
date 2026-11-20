package hints

// 沙盒确认（本次重写的重点）：
//   - 是否运行：只靠 WindowsSandbox* 进程 + 可见窗口确认（hasVisibleWindow），
//     C# 版基于 wsb.exe CLI 的链路（list/ip、包内路径、提权令牌注入）整体去掉。
//   - .wsb 文件名：仍从进程命令行用 PowerShell CIM 提取（属"进程"范畴，不是 wsb CLI）。
//   - IP：走 ARP + Hyper-V 启发式（AddSandboxHints），不用 wsb ip。
//   - 多 IP：ICMP 可达性去重（KeepLatestSandboxIp）。

import (
	"encoding/json"
	"regexp"
	"strconv"
	"strings"
	"unsafe"

	"golang.org/x/sys/windows"

	"portproxymanager/internal/proc"
	"portproxymanager/internal/windir"
)

// IsWindowsSandboxRunning 沙盒是否在运行。
// 旧版是 WindowsSandbox.exe；新版窗口进程是 WindowsSandboxRemoteSession.exe。
// 用可见窗口排除只在后台常驻、没打开沙盒的进程。
func IsWindowsSandboxRunning() bool {
	return hasVisibleWindow("WindowsSandbox") ||
		hasVisibleWindow("WindowsSandboxClient") ||
		hasVisibleWindow("WindowsSandboxRemoteSession")
}

// hasVisibleWindow 进程存在且有可见主窗口（对应 C# Process.MainWindowHandle != 0）。
// Go 没有 MainWindowHandle 的直接等价物，用 EnumWindows + GetWindowThreadProcessId
// 收集有可见窗口的进程 PID，再与按名枚举到的进程 PID 求交。
func hasVisibleWindow(processName string) bool {
	pids, err := pidsByName(processName)
	if err != nil || len(pids) == 0 {
		return false
	}
	visible := visibleWindowPIDs()
	for pid := range pids {
		if visible[pid] {
			return true
		}
	}
	return false
}

// pidsByName 用 CreateToolhelp32Snapshot 按进程名收集 PID。
func pidsByName(name string) (map[uint32]bool, error) {
	snapshot, err := windows.CreateToolhelp32Snapshot(windows.TH32CS_SNAPPROCESS, 0)
	if err != nil {
		return nil, err
	}
	defer windows.CloseHandle(snapshot)

	var entry windows.ProcessEntry32
	entry.Size = uint32(unsafe.Sizeof(entry))
	if err := windows.Process32First(snapshot, &entry); err != nil {
		return nil, err
	}

	wanted := strings.ToLower(strings.TrimSuffix(name, ".exe"))
	pids := map[uint32]bool{}
	for {
		procName := strings.ToLower(windows.UTF16ToString(entry.ExeFile[:]))
		if procName == wanted || strings.TrimSuffix(procName, ".exe") == wanted {
			pids[entry.ProcessID] = true
		}
		if err := windows.Process32Next(snapshot, &entry); err != nil {
			break
		}
	}
	return pids, nil
}

// visibleWindowPIDs 枚举所有可见顶层窗口，返回其所属 PID 集合。
func visibleWindowPIDs() map[uint32]bool {
	result := map[uint32]bool{}
	user32 := windows.NewLazySystemDLL("user32.dll")
	enumProc := user32.NewProc("EnumWindows")
	getPIDProc := user32.NewProc("GetWindowThreadProcessId")
	isVisibleProc := user32.NewProc("IsWindowVisible")

	cb := windows.NewCallback(func(hwnd windows.HWND, _ uintptr) uintptr {
		visible, _, _ := isVisibleProc.Call(uintptr(hwnd))
		if visible == 0 {
			return 1 // continue
		}
		var pid uint32
		getPIDProc.Call(uintptr(hwnd), uintptr(unsafe.Pointer(&pid)))
		if pid != 0 {
			result[pid] = true
		}
		return 1
	})
	enumProc.Call(cb, 0)
	return result
}

// getWindowsSandboxName 从 WindowsSandbox* 进程命令行提取 .wsb 文件名（对应 CIM 查询）。
// 找不到 / 超时 / 出错都返回 nil，不影响主流程。
func getWindowsSandboxName() *string {
	ps := powershellExe()
	if ps == "" {
		return nil
	}
	const script = `
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false);
$ErrorActionPreference = 'SilentlyContinue';
Get-CimInstance Win32_Process -Filter "Name LIKE 'WindowsSandbox%'" |
  Select-Object Name,CommandLine | ConvertTo-Json -Compress`
	res := proc.RunQuiet(ps, []string{
		"-NoProfile", "-NonInteractive", "-NoLogo", "-ExecutionPolicy", "Bypass", "-Command", script,
	}, proc.WithTimeoutMs(SandboxNameMS), proc.WithEncoding(proc.Utf8))
	if !res.Ok() {
		return nil
	}

	jsonText := strings.TrimSpace(res.StdOut)
	start := strings.Index(jsonText, "[")
	if start < 0 {
		start = strings.Index(jsonText, "{")
	}
	if start < 0 {
		return nil
	}

	var root any
	if err := json.Unmarshal([]byte(jsonText[start:]), &root); err != nil {
		return nil
	}

	names := []string{}
	for _, item := range asSlice(root) {
		obj, ok := item.(map[string]any)
		if !ok {
			continue
		}
		cmdLine, _ := obj["CommandLine"].(string)
		if name := extractWsbName(cmdLine); name != "" {
			names = append(names, name)
		}
	}
	if len(names) == 0 {
		return nil
	}
	distinct := dedupFold(names)
	text := strings.Join(firstN(distinct, 2), "、")
	if len(distinct) > 2 {
		text += " 等"
	}
	return strPtr(text)
}

var wsbQuotedRe = regexp.MustCompile(`"([^"]+\.wsb)"`)
var wsbBareRe = regexp.MustCompile(`([^\s"]+\.wsb)`)

func extractWsbName(commandLine string) string {
	if strings.TrimSpace(commandLine) == "" {
		return ""
	}
	path := ""
	if m := wsbQuotedRe.FindStringSubmatch(commandLine); m != nil {
		path = m[1]
	} else if m := wsbBareRe.FindStringSubmatch(commandLine); m != nil {
		path = m[1]
	}
	if path == "" {
		return ""
	}
	// Path.GetFileName，兼容正反斜杠
	if i := strings.LastIndexAny(path, `/\`); i >= 0 {
		return path[i+1:]
	}
	return path
}

func asSlice(v any) []any {
	switch x := v.(type) {
	case []any:
		return x
	case map[string]any:
		return []any{x}
	default:
		return nil
	}
}

func dedupFold(items []string) []string {
	seen := map[string]bool{}
	out := []string{}
	for _, s := range items {
		k := strings.ToLower(s)
		if !seen[k] {
			seen[k] = true
			out = append(out, s)
		}
	}
	return out
}

func firstN(items []string, n int) []string {
	if len(items) <= n {
		return items
	}
	return items[:n]
}

// sandboxIPReachable 用系统 ping 探测一个沙盒 IP 是否可达（600ms）。
// 用 ping.exe 而非原始 ICMP socket：后者在 Windows 上需要管理员权限，
// 而 C# 的 Ping 类走系统 API 无需提权，ping.exe 行为与之等价。
// 只匹配 ASCII 的 "ttl="，因此按 UTF-8 解码即可——ASCII 与 ANSI 字节一致，
// 不需要再走 winansi（ANSI 解码只留给 netsh/schtasks 这类本地化输出）。
func sandboxIPReachable(address string) bool {
	ping := windir.SystemDir() + string(osPathSeparator) + "ping.exe"
	res := proc.RunQuiet(ping, []string{
		"-n", "1", "-w", strconv.Itoa(SandboxPingMS), address,
	}, proc.WithTimeoutMs(SandboxPingMS+1200), proc.WithEncoding(proc.Utf8))
	return res.Ok() && strings.Contains(strings.ToLower(res.StdOut), "ttl=")
}
