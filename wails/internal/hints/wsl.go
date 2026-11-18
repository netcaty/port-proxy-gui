package hints

// WSL 客户机地址（对应 C# GetWslHintsAsync）：
// 只查正在运行的发行版，绝不为了探测而启动发行版。
// wsl -l 列表是 UTF-16LE；hostname -I 是 Linux 原始输出（UTF-8）。

import (
	"net"
	"os"
	"path/filepath"
	"strings"

	"portproxymanager/internal/proc"
	"portproxymanager/internal/windir"
)

// GetWslHints 查询 WSL 运行中发行版的 IP。
func GetWslHints(timeoutMs int) []AddressHint {
	wsl := filepath.Join(windir.SystemDir(), "wsl.exe")
	if _, err := os.Stat(wsl); err != nil {
		return []AddressHint{}
	}

	// 先查正在运行的发行版（-l --running 不会启动任何发行版）
	listResult := proc.RunQuiet(wsl, []string{"-l", "--running", "-q"},
		proc.WithTimeoutMs(minInt(timeoutMs, WslListMS)), proc.WithEncoding(proc.Utf16))
	if !listResult.Ok() {
		return []AddressHint{}
	}
	distros := parseWslDistros(listResult.StdOut)
	if len(distros) == 0 {
		return []AddressHint{}
	}

	// 指定已运行发行版执行 hostname -I，避免拉起默认发行版
	var ipResult *proc.Result
	probedDistro := ""
	for _, distro := range distros {
		probe, err := proc.Run(wsl, []string{"-d", distro, "hostname", "-I"},
			proc.WithTimeoutMs(timeoutMs), proc.WithEncoding(proc.Utf8))
		if err == nil && probe.Ok() {
			ipResult = &probe
			probedDistro = distro
			break
		}
	}
	if ipResult == nil {
		return []AddressHint{}
	}

	network := EnumerateLocalNetwork()
	localIPs := map[string]bool{}
	for _, a := range network.Adapters {
		localIPs[strings.ToLower(a.Address)] = true
	}

	reported := ParseIPv4List(ipResult.StdOut)
	var ips []string
	for _, ip := range reported {
		if !localIPs[strings.ToLower(ip)] {
			ips = append(ips, ip)
		}
	}

	// 镜像网络：WSL 与宿主机共用网络命名空间，返回的 IP 是宿主机地址
	mirrored := false
	for _, ip := range reported {
		if localIPs[strings.ToLower(ip)] {
			mirrored = true
			break
		}
	}
	if mirrored {
		detail := "与 Windows 共用 localhost，直接用 127.0.0.1"
		if probedDistro != "" {
			detail = probedDistro + " 与 Windows 共用 localhost"
		}
		id := probedDistro
		if id == "" {
			id = "WSL"
		}
		return []AddressHint{{
			Address: "127.0.0.1", Category: "WSL 客户机",
			Source: strPtr("镜像网络"), Detail: strPtr(detail),
			Kind: strPtr("wsl"), ID: strPtr(id), Confirmed: true,
		}}
	}

	if len(ips) == 0 {
		return []AddressHint{}
	}

	// NAT 模式：只保留落在 WSL 虚拟网卡子网内的地址（剔掉 docker0 等内部网桥）
	var wslNets []LocalAdapter
	for _, a := range network.Adapters {
		if a.Kind == AdapterWsl {
			wslNets = append(wslNets, a)
		}
	}
	candidates := ips
	if len(wslNets) > 0 {
		candidates = []string{}
		for _, ip := range ips {
			for _, n := range wslNets {
				if InSubnet(ip, n.Address, n.Prefix) {
					candidates = append(candidates, ip)
					break
				}
			}
		}
	}
	if len(candidates) == 0 {
		candidates = ips
	}

	source := probedDistro
	if source == "" {
		source = "WSL"
	}
	out := []AddressHint{}
	seen := map[string]bool{}
	for _, ip := range candidates {
		if seen[strings.ToLower(ip)] {
			continue
		}
		seen[strings.ToLower(ip)] = true
		out = append(out, AddressHint{
			Address: ip, Category: "WSL 客户机", Source: strPtr(source), Detail: strPtr("NAT 虚拟网络"),
			Kind: strPtr("wsl"), ID: strPtr(source), Confirmed: true,
		})
	}
	return out
}

// ParseIPv4List 从空格分隔文本里解析 IPv4 列表。
func ParseIPv4List(text string) []string {
	var result []string
	for _, token := range strings.Fields(text) {
		ip := net.ParseIP(token)
		if ip == nil || ip.To4() == nil || IsUseless(ip) {
			continue
		}
		result = append(result, ip.String())
	}
	return result
}

func parseWslDistros(text string) []string {
	var out []string
	for _, line := range strings.Split(text, "\n") {
		line = strings.Trim(strings.TrimSpace(line), "\uFEFF\x00")
		if line != "" {
			out = append(out, line)
		}
	}
	return out
}
