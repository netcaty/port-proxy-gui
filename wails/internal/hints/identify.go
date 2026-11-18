package hints

// 整合客户机身份识别：IdentifyLocalGuests 编排 Hyper-V / VMware / VBox /
// 沙盒启发式，并做 ICMP 去重。对应 C# IdentifyLocalGuestsAsync + AddSandboxHints
// + KeepLatestSandboxIpAsync + Deduplicate。

import (
	"sort"
	"strings"
	"sync"

	"portproxymanager/internal/logging"
)

// IdentifyLocalGuests 识别本地客户机（沙盒/Hyper-V/VMware/VirtualBox）。
// 超时以各子探测自身的 Math.Min(timeoutMs, Xxx) 为准。
func IdentifyLocalGuests(baseHints []AddressHint, timeoutMs int) []AddressHint {
	// 并发跑 Hyper-V / CLI 虚拟机探测 / 沙盒确认+CIM 命名
	//（C# 版 sandboxNameTask 与 vmTask 是并发的，串行会白等 1-4s）
	var vmHints []AddressHint
	var cliHints []AddressHint
	sandboxRunning := false
	var sandboxName *string
	var wg sync.WaitGroup
	wg.Add(3)
	go func() { defer wg.Done(); vmHints = ProbeHyperV(baseHints, minInt(timeoutMs, HyperVMS)) }()
	go func() { defer wg.Done(); cliHints = ProbeVMNames(baseHints, minInt(timeoutMs, VmCliMS)) }()
	go func() {
		defer wg.Done()
		sandboxRunning = IsWindowsSandboxRunning()
		if sandboxRunning {
			sandboxName = getWindowsSandboxName()
		}
	}()
	wg.Wait()

	identified := append([]AddressHint{}, cliHints...)

	// Hyper-V 里名为 Sandbox 的虚拟机由沙盒进程配置，补上 .wsb 文件名
	for _, hint := range vmHints {
		if hint.Kind != nil && *hint.Kind == "wsb" && sandboxName != nil && notBlank(sandboxName) {
			hint.ID = sandboxName
		}
		identified = append(identified, hint)
	}

	// 沙盒运行由进程窗口确认，IP 走 ARP + Hyper-V 启发式
	if sandboxRunning {
		identified = append(identified, addSandboxHints(baseHints, vmHints, sandboxName)...)
	}

	// 沙盒重启后 Default Switch 会同时残留旧/新永久邻居；只保留当前可达的
	identified = keepLatestSandboxIP(identified)

	return deduplicate(identified)
}

// addSandboxHints 以 Default Switch 上未被 Hyper-V 虚拟机认领的微软 OUI
// 邻居为锚点，给沙盒补上候选 IP（对应 C# AddSandboxHints）。
func addSandboxHints(baseHints, vmHints []AddressHint, sandboxName *string) []AddressHint {
	claimedIPs := map[string]bool{}
	claimedMACs := map[string]bool{}
	for _, h := range vmHints {
		claimedIPs[strings.ToLower(h.Address)] = true
		if h.Mac != nil {
			if n := NormalizeMacPtr(h.Mac); n != nil {
				claimedMACs[strings.ToLower(*n)] = true
			}
		}
	}

	// 锚点：未被认领 + 看起来是 Default Switch + 微软虚拟网卡 OUI
	seenAnchor := map[string]bool{}
	var anchors []AddressHint
	for _, h := range baseHints {
		if h.Mac == nil || claimedIPs[strings.ToLower(h.Address)] {
			continue
		}
		n := NormalizeMacPtr(h.Mac)
		if n == nil || claimedMACs[strings.ToLower(*n)] {
			continue
		}
		if !looksLikeDefaultSwitch(h) {
			continue
		}
		vendor := MacVendor(n)
		if vendor == nil || *vendor != "微软虚拟网卡" {
			continue
		}
		key := strings.ToLower(*n)
		if seenAnchor[key] {
			continue
		}
		seenAnchor[key] = true
		anchors = append(anchors, h)
	}

	var out []AddressHint
	for _, anchor := range anchors {
		mac := NormalizeMacPtr(anchor.Mac)
		if mac == nil {
			continue
		}
		// 同一 MAC 可能有多块网卡（Default Switch / MyApps vSwitch），合并优先 Default Switch
		group := []AddressHint{}
		for _, h := range baseHints {
			if h.Mac == nil || claimedIPs[strings.ToLower(h.Address)] {
				continue
			}
			if SameMac(h.Mac, mac) {
				group = append(group, h)
			}
		}
		sort.SliceStable(group, func(i, j int) bool {
			iDS, jDS := looksLikeDefaultSwitch(group[i]), looksLikeDefaultSwitch(group[j])
			if iDS != jDS {
				return iDS
			}
			return group[i].Address < group[j].Address
		})
		if len(group) == 0 {
			continue
		}
		primary := group[0]
		for _, h := range group {
			if looksLikeDefaultSwitch(h) {
				primary = h
				break
			}
		}

		// 备用 IP（最多 2 个）写进 Detail
		seenAlt := map[string]bool{}
		var alternates []string
		for _, h := range group {
			if strings.EqualFold(h.Address, primary.Address) || seenAlt[strings.ToLower(h.Address)] {
				continue
			}
			seenAlt[strings.ToLower(h.Address)] = true
			if h.Source != nil {
				alternates = append(alternates, h.Address+"（"+*h.Source+"）")
			} else {
				alternates = append(alternates, h.Address)
			}
			if len(alternates) >= 2 {
				break
			}
		}

		detailParts := []string{}
		if sandboxName != nil && primary.Source != nil {
			detailParts = append(detailParts, *primary.Source)
		}
		detailParts = append(detailParts, "WSB 正在运行")
		if len(alternates) > 0 {
			detailParts = append(detailParts, "另有 "+strings.Join(alternates, "、"))
		}

		id := "WindowsSandbox"
		if sandboxName != nil {
			id = *sandboxName
		}
		source := primary.Source
		if sandboxName != nil {
			source = sandboxName
		}
		out = append(out, primary.With(func(h *AddressHint) {
			h.Category = "疑似 Windows 沙盒"
			h.Source = source
			h.Detail = strPtr(strings.Join(detailParts, " · "))
			h.Kind = strPtr("wsb")
			h.ID = strPtr(id)
			h.Confirmed = true
		}))
	}
	return out
}

// looksLikeDefaultSwitch 候选是否来自 Default Switch 网卡。
func looksLikeDefaultSwitch(h AddressHint) bool {
	if h.Source != nil && strings.Contains(strings.ToLower(*h.Source), "default switch") {
		return true
	}
	return strings.Contains(h.Category, "沙盒")
}

// keepLatestSandboxIP 同一 .wsb 出现多个 IP 时，只用 ICMP 可达的（最近的）。
// 全部不可达时保留原列表，避免误删（对应 C# KeepLatestSandboxIpAsync）。
func keepLatestSandboxIP(hints []AddressHint) []AddressHint {
	// 按 id 分组，只看 kind=wsb 且同 id 多于一个的
	groups := map[string][]AddressHint{}
	for _, h := range hints {
		if h.Kind == nil || *h.Kind != "wsb" {
			continue
		}
		id := ""
		if h.ID != nil {
			id = *h.ID
		} else {
			id = h.Address
		}
		groups[strings.ToLower(id)] = append(groups[strings.ToLower(id)], h)
	}

	var duplicateIPs []string
	seenDup := map[string]bool{}
	for _, group := range groups {
		if len(group) <= 1 {
			continue
		}
		for _, h := range group {
			if !seenDup[strings.ToLower(h.Address)] {
				seenDup[strings.ToLower(h.Address)] = true
				duplicateIPs = append(duplicateIPs, h.Address)
			}
		}
	}
	if len(duplicateIPs) == 0 {
		return hints
	}

	// 并发 ICMP（ goroutine + WaitGroup）
	reachable := map[string]bool{}
	var mu sync.Mutex
	var wg sync.WaitGroup
	for _, ip := range duplicateIPs {
		wg.Add(1)
		go func(ip string) {
			defer wg.Done()
			if sandboxIPReachable(ip) {
				mu.Lock()
				reachable[strings.ToLower(ip)] = true
				mu.Unlock()
			}
		}(ip)
	}
	wg.Wait()

	if len(reachable) == 0 {
		if log := logging.Current(); log != nil {
			log.Info("沙盒存在多个 IP，但 ICMP 均不可达，保留全部候选。")
		}
		return hints
	}

	out := make([]AddressHint, 0, len(hints))
	for _, h := range hints {
		if h.Kind == nil || *h.Kind != "wsb" || !seenDup[strings.ToLower(h.Address)] {
			out = append(out, h)
			continue
		}
		if reachable[strings.ToLower(h.Address)] {
			out = append(out, h)
		}
	}
	return out
}

// deduplicate 按地址去重。
func deduplicate(hints []AddressHint) []AddressHint {
	seen := map[string]bool{}
	out := make([]AddressHint, 0, len(hints))
	for _, h := range hints {
		if seen[strings.ToLower(h.Address)] {
			continue
		}
		seen[strings.ToLower(h.Address)] = true
		out = append(out, h)
	}
	return out
}

func minInt(a, b int) int {
	if a < b {
		return a
	}
	return b
}
