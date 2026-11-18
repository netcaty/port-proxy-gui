package hints

import (
	"net"
	"sort"
	"strings"
	"sync"
	"time"

	"portproxymanager/internal/windir"
)

var (
	hintsGate  sync.Mutex
	hintsCache []AddressHint
	hintsAt    time.Time
	hintsTTL   = 15 * time.Second
)

// powershellExe System32 下的 Windows PowerShell 1.0 路径。
func powershellExe() string {
	// SystemDir() 已指向 System32，不要再加一层
	return windir.SystemDir() + string(osPathSeparator) + "WindowsPowerShell" + string(osPathSeparator) + "v1.0" + string(osPathSeparator) + "powershell.exe"
}

// GetHints 下拉候选：默认网关 + ARP 邻居（本机网卡/虚拟网卡地址不再列出）。带 15 秒缓存。
func GetHints(includeNeighbors bool) []AddressHint {
	if includeNeighbors {
		hintsGate.Lock()
		if time.Since(hintsAt) < hintsTTL && len(hintsCache) > 0 {
			out := hintsCache
			hintsGate.Unlock()
			return out
		}
		hintsGate.Unlock()
	}

	hints := collect(includeNeighbors)

	if includeNeighbors {
		hintsGate.Lock()
		hintsCache = hints
		hintsAt = time.Now()
		hintsGate.Unlock()
	}
	return hints
}

func collect(includeNeighbors bool) []AddressHint {
	type ranked struct {
		priority int
		hint     AddressHint
	}
	byAddress := map[string]ranked{}

	add := func(address string, priority int, category string, source *string, detail *string, mac *string, kind *string, id *string) {
		if strings.TrimSpace(address) == "" {
			return
		}
		ip := net.ParseIP(address)
		if ip == nil || ip.To4() == nil || IsUseless(ip) {
			return
		}
		text := ip.String()
		if existing, ok := byAddress[text]; ok && existing.priority <= priority {
			return
		}
		byAddress[text] = ranked{priority, AddressHint{
			Address: text, Category: category, Source: source, Detail: detail,
			Mac: mac, Kind: kind, ID: id,
		}}
	}

	network := EnumerateLocalNetwork()
	for _, gw := range network.Gateways {
		add(gw.Address, 0, "默认网关", strPtr(gw.Label), nil, nil, strPtr("gw"), strPtr(gw.Label))
	}
	if includeNeighbors {
		AddNeighbors(func(address string, priority int, category string, source *string, mac *string, kind *string, id *string) {
			add(address, priority, category, source, nil, mac, kind, id)
		}, network)
	}

	rankedList := make([]ranked, 0, len(byAddress))
	for _, r := range byAddress {
		rankedList = append(rankedList, r)
	}
	sort.SliceStable(rankedList, func(i, j int) bool {
		if rankedList[i].priority != rankedList[j].priority {
			return rankedList[i].priority < rankedList[j].priority
		}
		return rankedList[i].hint.Address < rankedList[j].hint.Address
	})
	out := make([]AddressHint, 0, len(rankedList))
	for _, r := range rankedList {
		out = append(out, r.hint)
	}
	return out
}
