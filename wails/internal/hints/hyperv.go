package hints

// Hyper-V 探测（对应 C# ProbeHyperVAsync）：用 PowerShell Get-VMNetworkAdapter / Get-VM
// 拿运行中的虚拟机 IP、MAC、名称；只保留 Get-VM 明确报告 Running 的。
// 沙盒识别也复用这里的 MAC/交换机信息（AddSandboxHints）。

import (
	"encoding/json"
	"net"
	"os"
	"strings"

	"portproxymanager/internal/proc"
	"portproxymanager/internal/windir"
)

// ProbeHyperV 查询 Hyper-V 运行中虚拟机的候选地址。
func ProbeHyperV(baseHints []AddressHint, timeoutMs int) []AddressHint {
	// SystemDir() 已指向 System32，不要再加一层
	module := windir.SystemDir() + string(osPathSeparator) + "WindowsPowerShell" +
		string(osPathSeparator) + "v1.0" + string(osPathSeparator) + "Modules" +
		string(osPathSeparator) + "Hyper-V"
	if _, err := os.Stat(module); err != nil {
		return []AddressHint{}
	}
	ps := powershellExe()
	if ps == "" {
		return []AddressHint{}
	}

	const script = `
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false);
$ErrorActionPreference = 'SilentlyContinue';
$nics = @(Get-VMNetworkAdapter -All | Where-Object { $_.MacAddress -and -not $_.IsManagementOs } | Select-Object VMName,SwitchName,MacAddress,IPAddresses);
$vms = @(Get-VM | Select-Object Name,@{n='State';e={[string]$_.State}});
[pscustomobject]@{ nics = $nics; vms = $vms } | ConvertTo-Json -Depth 5 -Compress`
	res := proc.RunQuiet(ps, []string{
		"-NoProfile", "-NonInteractive", "-NoLogo", "-ExecutionPolicy", "Bypass", "-Command", script,
	}, proc.WithTimeoutMs(timeoutMs), proc.WithEncoding(proc.Utf8))
	if !res.Ok() {
		return []AddressHint{}
	}

	jsonText := strings.TrimSpace(res.StdOut)
	start := strings.Index(jsonText, "{")
	if start < 0 {
		return []AddressHint{}
	}
	var root map[string]any
	if err := json.Unmarshal([]byte(jsonText[start:]), &root); err != nil {
		return []AddressHint{}
	}

	// VM 名 → 状态，只留 Running
	states := map[string]string{}
	for _, item := range asSlice(root["vms"]) {
		obj, ok := item.(map[string]any)
		if !ok {
			continue
		}
		name, _ := obj["Name"].(string)
		state, _ := obj["State"].(string)
		if strings.TrimSpace(name) != "" {
			states[strings.ToLower(strings.TrimSpace(name))] = state
		}
	}

	localIPs := map[string]bool{}
	for _, a := range EnumerateLocalNetwork().Adapters {
		localIPs[strings.ToLower(a.Address)] = true
	}

	hints := []AddressHint{}
	usedIPs := map[string]bool{}
	for _, item := range asSlice(root["nics"]) {
		obj, ok := item.(map[string]any)
		if !ok {
			continue
		}
		vmName := strings.TrimSpace(jsonString(obj, "VMName"))
		if vmName == "" || strings.EqualFold(vmName, machineName()) {
			continue
		}
		state := states[strings.ToLower(vmName)]
		if !strings.EqualFold(state, "Running") {
			continue // 只保留 Get-VM 明确 Running 的
		}

		mac := NormalizeMac(jsonString(obj, "MacAddress"))
		ips := []string{}
		if arr, ok := obj["IPAddresses"].([]any); ok {
			for _, raw := range arr {
				text, _ := raw.(string)
				ip := net.ParseIP(text)
				if ip == nil || ip.To4() == nil || IsUseless(ip) {
					continue
				}
				if localIPs[strings.ToLower(ip.String())] {
					continue
				}
				ips = append(ips, ip.String())
			}
		}
		// 用 MAC 把 ARP 里识别到的地址认回来
		if mac != nil {
			for _, known := range baseHints {
				if SameMac(known.Mac, mac) && !localIPs[strings.ToLower(known.Address)] {
					ips = append(ips, known.Address)
				}
			}
		}

		switchName := strings.TrimSpace(jsonString(obj, "SwitchName"))
		isSandbox := strings.Contains(strings.ToLower(vmName), "sandbox")
		category := "Hyper-V 虚拟机"
		kind := "hyperv"
		if isSandbox {
			category = "Windows 沙盒"
			kind = "wsb"
		}
		detail := "「" + vmName + "」" + localizeVMState(state)

		for _, ip := range dedupFold(ips) {
			if usedIPs[strings.ToLower(ip)] {
				continue
			}
			usedIPs[strings.ToLower(ip)] = true
			hints = append(hints, AddressHint{
				Address: ip, Category: category,
				Source: strPtr(switchName), Detail: strPtr(detail), Mac: mac,
				Kind: strPtr(kind), ID: strPtr(vmName), Confirmed: true,
			})
			if len(hints) >= 3*len(asSlice(root["nics"]))+3 {
				break // 每个 nic 最多 3 个，粗略上限
			}
		}
	}
	return hints
}

func jsonString(obj map[string]any, key string) string {
	if v, ok := obj[key].(string); ok {
		return v
	}
	return ""
}

func machineName() string {
	if h, err := os.Hostname(); err == nil {
		return h
	}
	return ""
}

func localizeVMState(state string) string {
	switch strings.ToLower(strings.TrimSpace(state)) {
	case "running":
		return "运行中"
	case "off":
		return "已关闭"
	case "saved":
		return "已保存"
	case "paused":
		return "已暂停"
	case "starting":
		return "启动中"
	case "stopping":
		return "停止中"
	default:
		return strings.TrimSpace(state)
	}
}
