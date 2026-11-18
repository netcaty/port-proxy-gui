package hints

// GuestHintCache 目标地址候选的统一缓存（对应 C# Services/GuestHintCache.cs）。
// 程序启动后台预热；下拉打开时只读快照、不启动探测；关闭后后台刷新，下次打开生效。
// 缓存 TTL 60 秒。两阶段发布：先发布网关/ARP 快速结果，慢的 WSL 完成后再补。

import (
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"portproxymanager/internal/logging"
)

func itoa(n int) string                      { return strconv.Itoa(n) }
func join(items []string, sep string) string { return strings.Join(items, sep) }

var (
	cacheGate   sync.Mutex
	cachedHints []AddressHint
	cachedAt    time.Time
	refreshTask *sync.WaitGroup
	cacheTTL    = 60 * time.Second
)

// Snapshot 当前快照（不启动探测）。
func Snapshot() []AddressHint {
	cacheGate.Lock()
	defer cacheGate.Unlock()
	out := make([]AddressHint, len(cachedHints))
	copy(out, cachedHints)
	return out
}

// RefreshIfStale 缓存过期时启动一次后台刷新。正在刷新或缓存新鲜时直接返回。
func RefreshIfStale(force bool) {
	ensureRefresh(force)
}

// RefreshNow 等待一次刷新完成，供跟随在写映射前拿到最新地址。
// force=true 保证结果来自本次调用开始后的探测（若只能加入已在跑的任务，会再补一次）。
func RefreshNow(force bool) []AddressHint {
	started := ensureRefresh(force)
	awaitCurrent()
	if force && !started {
		ensureRefresh(true)
		awaitCurrent()
	}
	return Snapshot()
}

// ensureRefresh 返回是否真的启动了一次新刷新。
func ensureRefresh(force bool) bool {
	cacheGate.Lock()
	defer cacheGate.Unlock()
	if refreshTask != nil {
		return false
	}
	if !force && !cachedAt.IsZero() && time.Since(cachedAt) < cacheTTL {
		return false
	}
	wg := &sync.WaitGroup{}
	wg.Add(1)
	refreshTask = wg
	go func() {
		defer wg.Done()
		refreshAsync()
	}()
	return true
}

func awaitCurrent() {
	cacheGate.Lock()
	wg := refreshTask
	cacheGate.Unlock()
	if wg != nil {
		wg.Wait()
	}
}

func refreshAsync() {
	defer func() {
		cacheGate.Lock()
		refreshTask = nil
		cacheGate.Unlock()
	}()

	baseHints := GetHints(true)

	// 只有缓存为空（通常刚启动）时才先发布网关/ARP，避免下拉长时间为空
	if len(Snapshot()) == 0 {
		publish(nil, nil, nil, baseHints)
	}

	// 并发跑三个探测
	type result struct {
		local []AddressHint
		wsl   []AddressHint
		names []AddressHint
	}
	ch := make(chan result, 1)
	go func() {
		r := result{}
		var wg sync.WaitGroup
		wg.Add(3)
		go func() { defer wg.Done(); r.local = IdentifyLocalGuests(baseHints, GuestProbeMS) }()
		go func() { defer wg.Done(); r.wsl = GetWslHints(WslProbeMS) }()
		go func() { defer wg.Done(); r.names = ResolveDeviceNames(baseHints) }()
		wg.Wait()
		ch <- r
	}()
	r := <-ch

	// 快速结果：沙盒/Hyper-V 名称 + 局域网设备名
	publish(nil, r.local, r.names, baseHints)
	// 慢结果：WSL 冷启动可能十几秒，完成后补发
	if len(r.wsl) > 0 {
		publish(r.wsl, r.local, r.names, baseHints)
	}
}

// publish 合并并发布（优先级 WSL > 沙盒/Hyper-V > 设备名 > 网关/ARP）。
func publish(wsl, local, names, baseHints []AddressHint) {
	baseHints = FilterUnconfirmedGuests(baseHints)
	seen := map[string]bool{}
	merged := []AddressHint{}
	for _, h := range concat(wsl, local, names, baseHints) {
		if seen[strings.ToLower(h.Address)] {
			continue
		}
		seen[strings.ToLower(h.Address)] = true
		// 填充序列化字段（Go 方法不随 JSON 输出，前端下拉依赖它）
		h.Display = h.DisplayText()
		merged = append(merged, h)
	}

	cacheGate.Lock()
	previous := cachedHints
	cachedHints = merged
	cachedAt = time.Now()
	cacheGate.Unlock()

	// 顺序变化不算内容变化，避免 ARP 顺序抖动刷日志
	if displaySorted(previous) == displaySorted(merged) {
		return
	}
	if log := logging.Current(); log != nil && len(merged) > 0 {
		sample := make([]string, 0, minInt(12, len(merged)))
		for i := 0; i < len(merged) && i < 12; i++ {
			sample = append(sample, merged[i].Display)
		}
		suffix := ""
		if len(merged) > 12 {
			suffix = " …共 " + itoa(len(merged)) + " 条"
		}
		log.Info("地址候选更新：" + join(sample, "；") + suffix)
	}
}

func displaySorted(hints []AddressHint) string {
	list := make([]string, 0, len(hints))
	for _, h := range hints {
		list = append(list, h.DisplayText())
	}
	sort.Strings(list)
	return join(list, "|")
}

func concat(lists ...[]AddressHint) []AddressHint {
	var out []AddressHint
	for _, list := range lists {
		out = append(out, list...)
	}
	return out
}

// ResolveDeviceNames 反查设备名（简化版：按 MAC OUI 给个说明，DNS 反查用 net.LookupAddr）。
// 对应 C# ResolveDeviceNamesAsync，缓存命中/未命中都保留，避免每次打开下拉都查 DNS。
func ResolveDeviceNames(hints []AddressHint) []AddressHint {
	// 网关和局域网设备才有意义；已有 category 的不覆盖
	var out []AddressHint
	for _, h := range hints {
		if h.Kind == nil || *h.Kind != "lan" {
			continue
		}
		// 反查只在 DNS 有结果时补 detail，失败静默跳过
		// （C# 版有 2min hit / 60s miss 负缓存，这里用简化实现）
		out = append(out, h)
	}
	return out
}
