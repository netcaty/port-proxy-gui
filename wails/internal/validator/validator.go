// Package validator 移植自 C# Services/Validator.cs。
package validator

import (
	"net"
	"strconv"
	"strings"

	"portproxymanager/internal/models"
	"portproxymanager/internal/registryio"
	"portproxymanager/internal/reservation"
)

// Result 校验结果。
type Result struct {
	Ok      bool    `json:"ok"`
	Error   *string `json:"error"`
	Warning *string `json:"warning"`
}

func success(warning string) Result {
	r := Result{Ok: true}
	if warning != "" {
		r.Warning = &warning
	}
	return r
}

func fail(err string) Result { return Result{Ok: false, Error: &err} }

// IsWildcard 空串 / * / 0.0.0.0 / :: 都算作"所有地址"。
func IsWildcard(address string) bool {
	t := strings.TrimSpace(address)
	return t == "" || t == "*" || t == "0.0.0.0" || t == "::" || t == "[::]"
}

// TryNormalizeAddress 校验并归一化单个地址。ipv6 指定该侧应该是什么版本。
// 通配符统一返回 *。
func TryNormalizeAddress(input string, ipv6 bool) (normalized string, err string) {
	normalized = "*"
	text := strings.TrimSpace(input)
	if text == "" {
		return
	}
	if IsWildcard(text) {
		return
	}

	ip := net.ParseIP(text)
	if ip == nil {
		return "*", "「" + text + "」不是合法的 IP 地址。"
	}

	isV6 := strings.Contains(text, ":")
	if isV6 != ipv6 {
		if ipv6 {
			return "*", "「" + text + "」是 IPv4 地址，这一侧要求 IPv6。"
		}
		return "*", "「" + text + "」是 IPv6 地址，这一侧要求 IPv4。"
	}

	normalized = text
	return
}

// InferFamily 根据两侧地址推断 family。通配符侧按 IPv4 处理（最常见）。
func InferFamily(listenAddress, connectAddress string) string {
	isV6 := func(a string) bool {
		if IsWildcard(a) {
			return false
		}
		return strings.Contains(a, ":")
	}

	listenV6, connectV6 := isV6(listenAddress), isV6(connectAddress)
	switch {
	case !listenV6 && !connectV6:
		return "v4tov4"
	case !listenV6 && connectV6:
		return "v4tov6"
	case listenV6 && !connectV6:
		return "v6tov4"
	default:
		return "v6tov6"
	}
}

// Validate 完整校验。editingKey 是编辑前的 Key，用于把它自己从重复检查里排除。
func Validate(entry models.PortProxyEntry, existing []models.PortProxyEntry, editingKey string) Result {
	if entry.ListenPort < 1 || entry.ListenPort > 65535 {
		return fail("监听端口必须在 1–65535 之间。")
	}
	if entry.ConnectPort < 1 || entry.ConnectPort > 65535 {
		return fail("目标端口必须在 1–65535 之间。")
	}

	if !containsFold(registryio.Families, entry.Family) {
		return fail("未知的类型：" + entry.Family + "。")
	}

	if _, err := TryNormalizeAddress(entry.ListenAddress, !entry.ListenIsV4()); err != "" {
		return fail("监听地址：" + err)
	}
	if _, err := TryNormalizeAddress(entry.ConnectAddress, !entry.ConnectIsV4()); err != "" {
		return fail("目标地址：" + err)
	}

	if entry.NormalizedConnect() == "*" {
		return fail("目标地址不能是「所有地址」，请填写具体的 IP（例如 127.0.0.1）。")
	}

	for _, e := range existing {
		if strings.EqualFold(e.Key(), editingKey) || !strings.EqualFold(e.Key(), entry.Key()) {
			continue
		}
		return fail("已存在相同的监听：" + e.Family + "/" + e.Protocol + " " + e.ListenText() +
			" → " + e.ConnectText() + "。同一个监听地址和端口只能有一条映射。")
	}

	// 落在系统保留区间的端口谁都绑不上，直接拦下比创建一条死映射好
	if reserved := reservation.FindContaining(entry.ListenPort); reserved != nil {
		return fail("监听端口 " + itoa(entry.ListenPort) + " 落在系统保留区间 " +
			itoa(reserved.Start) + "–" + itoa(reserved.End) +
			"（由 Hyper-V / WSL / WinNAT 占用）。该端口任何程序都无法监听，portproxy 也不会生效，" +
			"请换一个端口（例如加 10000）。")
	}

	return success(CheckPortOccupancy(entry, existing))
}

// CheckPortOccupancy 监听端口是否已被占用。已被现有 portproxy 占用的情况会在
// 重复检查里先被拦下，所以这里的提示主要针对"其他程序占着这个端口"。
func CheckPortOccupancy(entry models.PortProxyEntry, existing []models.PortProxyEntry) string {
	for _, e := range existing {
		if e.ListenPort == entry.ListenPort && !strings.EqualFold(e.Key(), entry.Key()) {
			return "端口 " + itoa(entry.ListenPort) + " 已被另一条 portproxy 映射监听（不同地址），可能冲突。"
		}
	}

	listeners := registryio.ActiveTcpListeners()
	for _, l := range listeners {
		if l.Port == entry.ListenPort {
			return "端口 " + itoa(entry.ListenPort) + " 当前已被监听（" + l.Address +
				"），如果那是其他程序，创建后会冲突。"
		}
	}
	return ""
}

func containsFold(list []string, s string) bool {
	for _, x := range list {
		if strings.EqualFold(x, s) {
			return true
		}
	}
	return false
}

func itoa(n int) string { return strconv.Itoa(n) }
