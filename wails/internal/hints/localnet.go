package hints

// 本地网卡枚举 + ARP 邻居收集，对应 C# EnumerateLocalNetwork / AddNeighbors。
// Go 的 net.Interfaces() 能拿到网卡名与 IPv4；网关用 PowerShell 的
// Get-NetIPConfiguration 补齐（.NET 的 GetIPProperties().GatewayAddresses 无直接等价）。

import (
	"net"
	"os/exec"
	"regexp"
	"strings"

	"portproxymanager/internal/logging"
	"portproxymanager/internal/proc"
	"portproxymanager/internal/windir"
)

// AdapterKind 网卡分类。
type AdapterKind int

const (
	AdapterPhysical AdapterKind = iota
	AdapterWsl
	AdapterHyperV
	AdapterDefaultSwitch
	AdapterSandbox
	AdapterVMware
	AdapterVirtualBox
	AdapterTunnel
)

type AdapterInfo struct {
	Kind  AdapterKind
	Label string
}

type LocalAdapter struct {
	Address string
	Prefix  int
	Kind    AdapterKind
	Label   string
}

type LocalNetwork struct {
	Adapters   []LocalAdapter
	Gateways   []Gateway
	Broadcasts map[string]bool
}

type Gateway struct {
	Address string
	Label   string
}

var (
	arpRowRe  = regexp.MustCompile(`^\s*(\d{1,3}(?:\.\d{1,3}){3})\s+([0-9a-fA-F]{2}(?:-[0-9a-fA-F]{2}){5})`)
	arpHeadRe = regexp.MustCompile(`(\d{1,3}(?:\.\d{1,3}){3})[^0-9]*---`)
)

// EnumerateLocalNetwork 枚举本机网卡、网关、广播地址。
func EnumerateLocalNetwork() LocalNetwork {
	netw := LocalNetwork{Broadcasts: map[string]bool{}}

	ifaces, err := net.Interfaces()
	if err != nil {
		if log := logging.Current(); log != nil {
			log.Warn("枚举网卡失败：" + err.Error())
		}
		return netw
	}

	for _, nic := range ifaces {
		if nic.Flags&net.FlagLoopback != 0 || nic.Flags&net.FlagUp == 0 {
			continue
		}
		info := Classify(nic.Name, nic.HardwareAddr.String())

		addrs, _ := nic.Addrs()
		for _, a := range addrs {
			ipNet, ok := a.(*net.IPNet)
			if !ok {
				continue
			}
			ip4 := ipNet.IP.To4()
			if ip4 == nil || IsUseless(ipNet.IP) {
				continue
			}
			prefix, _ := ipNet.Mask.Size()
			netw.Adapters = append(netw.Adapters, LocalAdapter{
				Address: ip4.String(), Prefix: prefix, Kind: info.Kind, Label: info.Label,
			})

			// 计算广播地址
			mask := net.IP(ipNet.Mask).To4()
			if len(mask) == 4 {
				bcast := make([]byte, 4)
				for i := 0; i < 4; i++ {
					bcast[i] = ip4[i] | ^mask[i]
				}
				netw.Broadcasts[net.IP(bcast).String()] = true
			}
		}
	}

	// 网关：PowerShell Get-NetIPConfiguration（IPv4 默认路由）
	netw.Gateways = queryGateways()
	return netw
}

// Classify 按网卡名/描述分类（对应 C# Classify + CleanAdapterName）。
func Classify(name, description string) AdapterInfo {
	both := name + " " + description
	label := cleanAdapterName(name, description)

	switch {
	case containsFold(both, "WSL"):
		return AdapterInfo{Kind: AdapterWsl, Label: "WSL"}
	case containsFold(both, "Sandbox"):
		return AdapterInfo{Kind: AdapterSandbox, Label: label}
	case containsFold(both, "VMware") || containsFold(both, "VMnet"):
		return AdapterInfo{Kind: AdapterVMware, Label: label}
	case containsFold(both, "VirtualBox"):
		return AdapterInfo{Kind: AdapterVirtualBox, Label: label}
	case containsFold(both, "vEthernet") || containsFold(both, "Hyper-V"):
		kind := AdapterHyperV
		if containsFold(label, "Default Switch") {
			kind = AdapterDefaultSwitch
		}
		return AdapterInfo{Kind: kind, Label: label}
	case containsFold(both, "Tunnel"):
		return AdapterInfo{Kind: AdapterTunnel, Label: label}
	default:
		return AdapterInfo{Kind: AdapterPhysical, Label: label}
	}
}

func cleanAdapterName(name, description string) string {
	text := strings.TrimSpace(name)
	if strings.HasPrefix(strings.ToLower(text), "vethernet") {
		open, close := strings.Index(text, "("), strings.LastIndex(text, ")")
		if open >= 0 && close > open {
			text = strings.TrimSpace(text[open+1 : close])
		}
	}
	for _, prefix := range []string{"VMware Network Adapter ", "VirtualBox "} {
		if len(text) >= len(prefix) && strings.EqualFold(text[:len(prefix)], prefix) {
			text = strings.TrimSpace(text[len(prefix):])
		}
	}
	if containsFold(text, "WSL") {
		text = "WSL"
	}
	if text == "" {
		text = strings.TrimSpace(description)
	}
	return text
}

// queryGateways 用 PowerShell 拿默认网关。失败返回空列表。
func queryGateways() []Gateway {
	ps := powershellExe()
	if ps == "" {
		return nil
	}
	// 注意：Go raw string 里不能出现反引号，PowerShell 的 tab 用 [char]9 表示
	script := `[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);` +
		`Get-NetIPConfiguration | Where-Object {$_.IPv4DefaultGateway -ne $null} | ` +
		`ForEach-Object { ($_.IPv4DefaultGateway.NextHop, $_.NetAdapter.Name) -join [char]9 }`
	res := proc.RunQuiet(ps, []string{
		"-NoProfile", "-NonInteractive", "-NoLogo", "-ExecutionPolicy", "Bypass", "-Command", script,
	}, proc.WithTimeoutMs(4000), proc.WithEncoding(proc.Utf8))
	if !res.Ok() {
		return nil
	}
	var out []Gateway
	for _, line := range strings.Split(res.StdOut, "\n") {
		parts := strings.Split(strings.TrimSpace(line), "\t")
		if len(parts) != 2 || net.ParseIP(parts[0]) == nil {
			continue
		}
		out = append(out, Gateway{Address: parts[0], Label: cleanAdapterName(parts[1], "")})
	}
	return out
}

// AddNeighbors 解析 arp.exe -a 输出，加入候选（只保留优先级最高的地址）。
func AddNeighbors(add func(address string, priority int, category string, source *string, mac *string, kind *string, id *string), network LocalNetwork) {
	arp := windir.SystemDir() + string(osPathSeparator) + "arp.exe"
	res := proc.RunQuiet(arp, []string{"-a"}, proc.WithTimeoutMs(ArpMS), proc.WithEncoding(proc.Ansi))
	if !res.Ok() {
		return
	}

	byInterface := map[string]LocalAdapter{}
	for _, a := range network.Adapters {
		if _, ok := byInterface[a.Address]; !ok {
			byInterface[a.Address] = a
		}
	}

	currentInterfaceIP := ""
	count := 0
	for _, raw := range strings.Split(res.StdOut, "\n") {
		line := strings.TrimSpace(raw)
		if line == "" {
			continue
		}
		if strings.Contains(line, "---") {
			if m := arpHeadRe.FindStringSubmatch(line); m != nil {
				currentInterfaceIP = m[1]
			}
			continue
		}
		m := arpRowRe.FindStringSubmatch(line)
		if m == nil {
			continue
		}
		address := m[1]
		mac := NormalizeMac(m[2])
		if mac == nil || isBroadcastOrMulticast(address, mac, network) {
			continue
		}
		if count >= 30 {
			break
		}
		adapter, _ := byInterface[currentInterfaceIP]
		category, source, kind := describeNeighbor(adapter)
		add(address, 4, category, source, mac, kind, strPtr(FormatMacId(mac)))
		count++
	}
}

func isBroadcastOrMulticast(address string, mac *string, network LocalNetwork) bool {
	if network.Broadcasts[address] {
		return true
	}
	if mac == nil {
		return true
	}
	switch *mac {
	case "FFFFFFFFFFFF", "000000000000":
		return true
	}
	if strings.HasPrefix(*mac, "01005E") {
		return true
	}
	ip := net.ParseIP(address)
	return ip == nil || IsUseless(ip)
}

func describeNeighbor(adapter LocalAdapter) (category string, source *string, kind *string) {
	if adapter.Label == "" {
		return "局域网设备", nil, strPtr("lan")
	}
	switch adapter.Kind {
	case AdapterWsl:
		return "WSL 虚拟机", strPtr(adapter.Label), strPtr("wsl")
	case AdapterDefaultSwitch:
		return "Hyper-V/沙盒虚拟机", strPtr(adapter.Label), strPtr("vm")
	case AdapterHyperV:
		return "Hyper-V 虚拟机", strPtr(adapter.Label), strPtr("hyperv")
	case AdapterSandbox:
		return "Windows 沙盒", strPtr(adapter.Label), strPtr("wsb")
	case AdapterVMware:
		return "VMware 虚拟机", strPtr(adapter.Label), strPtr("vm")
	case AdapterVirtualBox:
		return "VirtualBox 虚拟机", strPtr(adapter.Label), strPtr("vm")
	default:
		return "局域网设备", strPtr(adapter.Label), strPtr("lan")
	}
}

func containsFold(s, sub string) bool {
	return strings.Contains(strings.ToLower(s), strings.ToLower(sub))
}

const osPathSeparator = '\\'

var _ = exec.Command
