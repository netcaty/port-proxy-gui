// Package firewall 移植自 C# Services/FirewallService.cs：
// 可选：为映射建立入站放行规则。规则名固定由监听地址+端口生成，不使用用户自由文本。

package firewall

import (
	"fmt"

	"portproxymanager/internal/elevation"
	"portproxymanager/internal/models"
	"portproxymanager/internal/netsh"
)

// RuleName 规则名（只由 IP:端口 生成）。
func RuleName(entry models.PortProxyEntry) string {
	return fmt.Sprintf("PortProxy %s:%d", entry.NormalizedListen(), entry.ListenPort)
}

// Exists 规则是否存在（只读查询，不记错误日志）。
func Exists(entry models.PortProxyEntry) bool {
	res := netsh.RunQuiet([]string{"advfirewall", "firewall", "show", "rule", "name=" + RuleName(entry)}, 5000)
	return res.Ok()
}

// AddResult 动作结果。
type AddResult struct {
	Ok      bool   `json:"ok"`
	Message string `json:"message"`
}

// Add 添加入站放行规则。
func Add(entry models.PortProxyEntry) AddResult {
	if !elevation.IsElevated() {
		return AddResult{Message: "创建防火墙规则需要管理员权限。"}
	}
	res, err := netsh.Run([]string{
		"advfirewall", "firewall", "add", "rule",
		"name=" + RuleName(entry), "dir=in", "action=allow", "protocol=TCP",
		fmt.Sprintf("localport=%d", entry.ListenPort),
	})
	if err != nil || !res.Ok() {
		return AddResult{Message: netsh.Friendly(res)}
	}
	return AddResult{Ok: true, Message: fmt.Sprintf("已添加入站放行规则（TCP %d）。", entry.ListenPort)}
}

// Delete 删除规则（不存在也算成功，幂等）。
func Delete(entry models.PortProxyEntry) AddResult {
	if !elevation.IsElevated() {
		return AddResult{Message: "删除防火墙规则需要管理员权限。"}
	}
	res, err := netsh.Run([]string{"advfirewall", "firewall", "delete", "rule", "name=" + RuleName(entry)})
	if (err == nil && res.Ok()) || !Exists(entry) {
		return AddResult{Ok: true, Message: "防火墙规则已清理。"}
	}
	return AddResult{Message: netsh.Friendly(res)}
}
