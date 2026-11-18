// Package diagnoses 移植自 C# Services/DiagnosticsService.cs：
// 单条映射的转发链路诊断。按"配置 → 服务 → 监听 → 连通性 → 目标 → 防火墙"
// 顺序逐项检查，失败项尽量附带可直接执行的修复动作。

package diagnoses

import (
	"fmt"
	"net"
	"strings"
	"time"

	"portproxymanager/internal/elevation"
	"portproxymanager/internal/firewall"
	"portproxymanager/internal/iphelper"
	"portproxymanager/internal/models"
	"portproxymanager/internal/probe"
	"portproxymanager/internal/registryio"
	"portproxymanager/internal/reservation"
	"portproxymanager/internal/validator"
)

// Status 检查状态。
type Status int

const (
	StatusPass Status = iota
	StatusWarn
	StatusFail
	StatusSkip
)

func (s Status) Label() string {
	switch s {
	case StatusPass:
		return "通过"
	case StatusWarn:
		return "警告"
	case StatusFail:
		return "失败"
	default:
		return "跳过"
	}
}

// Action 修复动作。
type Action int

const (
	ActionNone Action = iota
	ActionStartIpHelper
	ActionRestartIpHelper
	ActionRebuildMapping
	ActionAddFirewallRule
)

// Check 单项检查。
type Check struct {
	Name        string `json:"name"`
	Status      Status `json:"status"`
	StatusLabel string `json:"statusLabel"`
	Detail      string `json:"detail"`
	Action      Action `json:"action"`
	ActionLabel string `json:"actionLabel"`
}

// Report 诊断报告。
type Report struct {
	Entry     models.PortProxyEntry `json:"entry"`
	At        string                `json:"at"`
	Checks    []Check               `json:"checks"`
	PassCount int                   `json:"passCount"`
	WarnCount int                   `json:"warnCount"`
	FailCount int                   `json:"failCount"`
	SkipCount int                   `json:"skipCount"`
}

func check(name string, status Status, detail string, action Action, actionLabel string) Check {
	return Check{Name: name, Status: status, StatusLabel: status.Label(), Detail: detail, Action: action, ActionLabel: actionLabel}
}

func pass(name, detail string) Check { return check(name, StatusPass, detail, ActionNone, "") }
func fail(name, detail string) Check { return check(name, StatusFail, detail, ActionNone, "") }
func skip(name, detail string) Check { return check(name, StatusSkip, detail, ActionNone, "") }
func warn(name, detail string) Check { return check(name, StatusWarn, detail, ActionNone, "") }

// Run 执行诊断。
func Run(entry models.PortProxyEntry, meta *models.EntryMeta, elevated bool) Report {
	report := Report{
		Entry:  entry,
		At:     time.Now().Format("2006-01-02 15:04:05"),
		Checks: []Check{},
	}

	// 1. 注册表里是否真的存在这条映射
	snapshot, readErr := registryio.ReadAll()
	var current *models.PortProxyEntry
	for i := range snapshot {
		if strings.EqualFold(snapshot[i].Key(), entry.Key()) {
			current = &snapshot[i]
			break
		}
	}
	switch {
	case readErr != nil:
		report.Checks = append(report.Checks, fail("注册表映射", "读取注册表失败："+readErr.Error()))
	case current == nil:
		report.Checks = append(report.Checks, fail("注册表映射", "注册表里没有这条映射，配置没有真正落地或被外部删除。"))
	case !current.SameTarget(entry):
		report.Checks = append(report.Checks, check("注册表映射", StatusWarn,
			"注册表里的目标是 "+current.ConnectText()+"，与界面显示不一致（界面可能未刷新）。", ActionNone, ""))
	default:
		report.Checks = append(report.Checks, pass("注册表映射", "条目存在，目标为 "+current.ConnectText()+"。"))
	}

	// 2. 地址/类型格式
	if formatError := checkFormat(entry); formatError != "" {
		report.Checks = append(report.Checks, fail("地址与类型", formatError))
	} else {
		report.Checks = append(report.Checks, pass("地址与类型",
			fmt.Sprintf("%s，监听 %s，目标 %s。", entry.Family, entry.ListenText(), entry.ConnectText())))
	}

	// 3. 监听地址写法（字面量 * 是历史遗留）
	if strings.TrimSpace(entry.ListenAddress) == "*" {
		if elevated {
			report.Checks = append(report.Checks, check("监听地址写法", StatusWarn,
				"注册表里监听地址是字面量 *（不是 netsh 标准的 0.0.0.0），IP Helper 可能无法加载这条映射。",
				ActionRebuildMapping, "重建为 0.0.0.0"))
		} else {
			report.Checks = append(report.Checks, warn("监听地址写法",
				"注册表里监听地址是字面量 *（不是 netsh 标准的 0.0.0.0），IP Helper 可能无法加载这条映射。"))
		}
	} else {
		report.Checks = append(report.Checks, pass("监听地址写法", "监听地址写法正常。"))
	}

	// 4. 监听地址是否属于本机
	if validator.IsWildcard(entry.ListenAddress) {
		report.Checks = append(report.Checks, pass("监听地址归属", "监听所有本机地址。"))
	} else {
		local := localAddresses()
		if local[strings.ToLower(stripBrackets(entry.NetshListenAddress()))] {
			report.Checks = append(report.Checks, pass("监听地址归属", entry.NetshListenAddress()+" 是本机已启用的地址。"))
		} else {
			report.Checks = append(report.Checks, fail("监听地址归属",
				entry.NetshListenAddress()+" 不在本机任何已启用的网卡上，IP Helper 无法绑定。"))
		}
	}

	// 5. 系统保留端口区间
	if reserved := reservation.FindContaining(entry.ListenPort); reserved != nil {
		report.Checks = append(report.Checks, fail("系统保留端口",
			fmt.Sprintf("监听端口 %d 落在系统保留区间 %d–%d（由 Hyper-V / WSL / WinNAT 占用），任何程序都无法监听它。",
				entry.ListenPort, reserved.Start, reserved.End)))
	} else {
		report.Checks = append(report.Checks, pass("系统保留端口", "不在系统保留区间内。"))
	}

	// 6. IP Helper 服务
	state := iphelper.GetState()
	helperRunning := state == iphelper.StateRunning
	switch state {
	case iphelper.StateRunning:
		report.Checks = append(report.Checks, pass("IP Helper 服务", "iphlpsvc 正在运行。"))
	case iphelper.StateStopped:
		if elevated {
			report.Checks = append(report.Checks, check("IP Helper 服务", StatusFail,
				"iphlpsvc 没有运行，portproxy 不会转发任何流量。", ActionStartIpHelper, "启动 IP Helper"))
		} else {
			report.Checks = append(report.Checks, fail("IP Helper 服务", "iphlpsvc 没有运行，portproxy 不会转发任何流量。"))
		}
	case iphelper.StateNotFound:
		report.Checks = append(report.Checks, fail("IP Helper 服务", "系统里找不到 iphlpsvc 服务，端口转发功能不可用。"))
	default:
		report.Checks = append(report.Checks, warn("IP Helper 服务", "无法查询 iphlpsvc 状态（权限或服务管理器异常）。"))
	}

	// 7. 监听器是否已经建立
	listenerFound := false
	if !helperRunning {
		report.Checks = append(report.Checks, skip("端口监听", "IP Helper 未运行，跳过监听器检查。"))
	} else {
		listeners := registryio.ActiveTcpListeners()
		hit := false
		for _, l := range listeners {
			if l.Port == entry.ListenPort && listenerAddressMatches(entry, l.Address) {
				hit = true
				break
			}
		}
		if hit {
			listenerFound = true
			report.Checks = append(report.Checks, pass("端口监听",
				fmt.Sprintf("已有监听器 %s:%d。", entry.NetshListenAddress(), entry.ListenPort)))
		} else {
			if elevated {
				report.Checks = append(report.Checks, check("端口监听", StatusFail,
					fmt.Sprintf("没有找到 %s:%d 的监听器；配置存在，但 IP Helper 没有加载它。", entry.NetshListenAddress(), entry.ListenPort),
					ActionRestartIpHelper, "重启 IP Helper"))
			} else {
				report.Checks = append(report.Checks, fail("端口监听",
					fmt.Sprintf("没有找到 %s:%d 的监听器；配置存在，但 IP Helper 没有加载它。", entry.NetshListenAddress(), entry.ListenPort)))
			}
		}
	}

	// 8. 本机连接监听端口
	if !listenerFound {
		report.Checks = append(report.Checks, skip("本机连通性", "监听器不存在，跳过本机连接测试。"))
	} else {
		probeHost := "127.0.0.1"
		if !entry.ListenIsV4() {
			probeHost = "::1"
		}
		if !validator.IsWildcard(entry.ListenAddress) {
			probeHost = stripBrackets(entry.NetshListenAddress())
		}
		ok, detail := probe.TestConnect(probeHost, entry.ListenPort, 1500*time.Millisecond)
		if ok {
			report.Checks = append(report.Checks, pass("本机连通性",
				fmt.Sprintf("连接 %s:%d 成功，监听侧可用。", probeHost, entry.ListenPort)))
		} else {
			if elevated {
				report.Checks = append(report.Checks, check("本机连通性", StatusFail,
					fmt.Sprintf("连接 %s:%d 失败：%s", probeHost, entry.ListenPort, detail), ActionRestartIpHelper, "重启 IP Helper"))
			} else {
				report.Checks = append(report.Checks, fail("本机连通性",
					fmt.Sprintf("连接 %s:%d 失败：%s", probeHost, entry.ListenPort, detail)))
			}
		}
	}

	// 9. 目标是否可达
	if entry.NormalizedConnect() == "*" {
		report.Checks = append(report.Checks, skip("目标连通性", "目标地址是所有地址，无法测试。"))
	} else {
		ok, detail := probe.TestConnect(stripBrackets(entry.NetshConnectAddress()), entry.ConnectPort, 2500*time.Millisecond)
		if ok {
			report.Checks = append(report.Checks, pass("目标连通性", entry.ConnectText()+" 可达。"))
		} else {
			report.Checks = append(report.Checks, fail("目标连通性",
				fmt.Sprintf("无法连接 %s：%s 映射本身仍然有效，但目标当前不可达。", entry.ConnectText(), detail)))
		}
	}

	// 10. 防火墙联动（可选）
	if meta != nil && meta.HasFirewallRule {
		if firewall.Exists(entry) {
			report.Checks = append(report.Checks, pass("防火墙规则",
				"入站放行规则「"+firewall.RuleName(entry)+"」存在。"))
		} else {
			if elevated {
				report.Checks = append(report.Checks, check("防火墙规则", StatusFail,
					"标记了防火墙联动，但找不到规则「"+firewall.RuleName(entry)+"」。", ActionAddFirewallRule, "补充防火墙规则"))
			} else {
				report.Checks = append(report.Checks, fail("防火墙规则",
					"标记了防火墙联动，但找不到规则「"+firewall.RuleName(entry)+"」。"))
			}
		}
	} else {
		report.Checks = append(report.Checks, skip("防火墙规则", "未启用防火墙联动（只影响跨机访问，本机访问不受影响）。"))
	}

	// 汇总
	for _, c := range report.Checks {
		switch c.Status {
		case StatusPass:
			report.PassCount++
		case StatusWarn:
			report.WarnCount++
		case StatusFail:
			report.FailCount++
		case StatusSkip:
			report.SkipCount++
		}
	}
	return report
}

// ToText 报告转文本（复制/保存用）。
func (r Report) ToText() string {
	lines := []string{
		"PortProxy 诊断报告",
		"时间：" + r.At,
		fmt.Sprintf("映射：%s/%s %s → %s", r.Entry.Family, r.Entry.Protocol, r.Entry.ListenText(), r.Entry.ConnectText()),
		fmt.Sprintf("结果：通过 %d，警告 %d，失败 %d，跳过 %d", r.PassCount, r.WarnCount, r.FailCount, r.SkipCount),
		"",
	}
	for _, c := range r.Checks {
		lines = append(lines, fmt.Sprintf("[%s] %s：%s", c.StatusLabel, c.Name, c.Detail))
	}
	return strings.Join(lines, "\r\n")
}

// FixResult 修复动作结果（供 Wails 绑定序列化）。
type FixResult struct {
	Ok      bool   `json:"ok"`
	Message string `json:"message"`
}

// ApplyFix 执行修复动作并返回可绑定的结果。
func ApplyFix(action Action, entry models.PortProxyEntry) FixResult {
	ok, msg := Fix(action, entry)
	return FixResult{Ok: ok, Message: msg}
}

// Fix 执行修复动作（对应 C# 诊断对话框的修复按钮）。
func Fix(action Action, entry models.PortProxyEntry) (bool, string) {
	if !elevation.IsElevated() {
		return false, "修复需要管理员权限，请以管理员身份重新启动本程序。"
	}
	switch action {
	case ActionStartIpHelper:
		r := iphelper.TryStart()
		return r.Ok, r.Message
	case ActionRestartIpHelper:
		r := iphelper.TryRestart()
		return r.Ok, r.Message
	case ActionAddFirewallRule:
		r := firewall.Add(entry)
		return r.Ok, r.Message
	default:
		return false, "没有可执行的修复动作。"
	}
}

func checkFormat(entry models.PortProxyEntry) string {
	validFamily := false
	for _, f := range registryio.Families {
		if strings.EqualFold(f, entry.Family) {
			validFamily = true
			break
		}
	}
	if !validFamily {
		return "未知的类型：" + entry.Family + "。"
	}
	if entry.ListenPort < 1 || entry.ListenPort > 65535 {
		return "监听端口不在 1–65535 之间。"
	}
	if entry.ConnectPort < 1 || entry.ConnectPort > 65535 {
		return "目标端口不在 1–65535 之间。"
	}
	if _, err := validator.TryNormalizeAddress(entry.ListenAddress, !entry.ListenIsV4()); err != "" {
		return "监听地址：" + err
	}
	if _, err := validator.TryNormalizeAddress(entry.ConnectAddress, !entry.ConnectIsV4()); err != "" {
		return "目标地址：" + err
	}
	if entry.NormalizedConnect() == "*" {
		return "目标地址不能是所有地址。"
	}
	return ""
}

func listenerAddressMatches(entry models.PortProxyEntry, listenerAddress string) bool {
	if validator.IsWildcard(entry.ListenAddress) {
		return addressEquals(listenerAddress, "0.0.0.0") || addressEquals(listenerAddress, "::")
	}
	return addressEquals(listenerAddress, entry.NetshListenAddress())
}

func localAddresses() map[string]bool {
	result := map[string]bool{"127.0.0.1": true, "::1": true}
	ifaces, err := net.Interfaces()
	if err != nil {
		return result
	}
	for _, nic := range ifaces {
		addrs, _ := nic.Addrs()
		for _, a := range addrs {
			if ipNet, ok := a.(*net.IPNet); ok {
				result[strings.ToLower(ipNet.IP.String())] = true
			}
		}
	}
	return result
}

func addressEquals(a, b string) bool {
	left, right := net.ParseIP(stripBrackets(a)), net.ParseIP(stripBrackets(b))
	if left != nil && right != nil {
		if left.To4() != nil {
			left = left.To4()
		}
		if right.To4() != nil {
			right = right.To4()
		}
		return left.Equal(right)
	}
	return strings.EqualFold(a, b)
}

func stripBrackets(s string) string {
	return strings.Trim(s, "[]")
}
