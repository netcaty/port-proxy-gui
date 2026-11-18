package hints

// 反向解析局域网设备名（对应 C# ResolveDeviceNamesAsync）：
// Windows 解析器会同时尝试 DNS / mDNS 等，解析不到保持无名称。
// 结果带缓存（命中 2 分钟 / 未命中 60 秒负缓存），避免每次刷新都发查询。

import (
	"context"
	"net"
	"os"
	"strings"
	"sync"
	"time"
)

var (
	nameGate  sync.Mutex
	nameCache = map[string]nameCacheEntry{}
)

const (
	nameHitTTL  = 2 * time.Minute
	nameMissTTL = 60 * time.Second
)

type nameCacheEntry struct {
	name  *string
	atUTC time.Time
}

// IsNameCandidate 只给局域网设备和网关解析名称；虚拟机/沙盒已有更准确的
// 名称来源，反向解析出来的 GUID.mshome.net 之类反而没有意义。
func IsNameCandidate(h AddressHint) bool {
	return h.Category == "局域网设备" || h.Category == "默认网关"
}

// ResolveDeviceNames 反查设备名；解析成功的条目以新的 ID/Detail 发布。
func ResolveDeviceNames(baseHints []AddressHint) []AddressHint {
	// 去重取前 12 个候选
	seen := map[string]bool{}
	var targets []AddressHint
	for _, h := range baseHints {
		if !IsNameCandidate(h) || seen[strings.ToLower(h.Address)] {
			continue
		}
		seen[strings.ToLower(h.Address)] = true
		targets = append(targets, h)
		if len(targets) >= 12 {
			break
		}
	}
	if len(targets) == 0 {
		return []AddressHint{}
	}

	type result struct {
		hint AddressHint
		name *string
	}
	results := make([]result, len(targets))
	var wg sync.WaitGroup
	for i, h := range targets {
		wg.Add(1)
		go func(i int, h AddressHint) {
			defer wg.Done()
			results[i] = result{hint: h, name: resolveOneName(h)}
		}(i, h)
	}

	// 单个解析有超时，这里再兜一层总超时，避免某个系统解析卡住整体刷新
	done := make(chan struct{})
	go func() { wg.Wait(); close(done) }()
	select {
	case <-done:
	case <-time.After(time.Duration(DnsLookupMS+DnsBatchSlackMS) * time.Millisecond):
		// 超时：只收集已完成的（results 里 name 尚为 nil 的会因 racy 而不精确，
		// 但 name=nil 本来就不发布，安全）
	}

	var out []AddressHint
	for _, r := range results {
		if r.name != nil {
			out = append(out, r.hint.With(func(h *AddressHint) {
				d := *r.name
				h.Detail = &d
				h.ID = &d
			}))
		}
	}
	return out
}

// resolveOneName 带缓存的单地址反查。
func resolveOneName(h AddressHint) *string {
	if name, ok := getCachedName(h.Address); ok {
		return name
	}
	name := lookupHostName(h.Address, time.Duration(DnsLookupMS)*time.Millisecond)
	storeName(h.Address, name)
	return name
}

func getCachedName(address string) (*string, bool) {
	nameGate.Lock()
	defer nameGate.Unlock()
	entry, ok := nameCache[strings.ToLower(address)]
	if !ok {
		return nil, false
	}
	ttl := nameMissTTL
	if entry.name != nil {
		ttl = nameHitTTL
	}
	if time.Since(entry.atUTC) >= ttl {
		return nil, false
	}
	return entry.name, true
}

func storeName(address string, name *string) {
	nameGate.Lock()
	defer nameGate.Unlock()
	nameCache[strings.ToLower(address)] = nameCacheEntry{name: name, atUTC: time.Now()}
}

// lookupHostName 反查主机名，超时/失败返回 nil。
func lookupHostName(address string, timeout time.Duration) *string {
	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()

	var resolver net.Resolver
	hosts, err := resolver.LookupAddr(ctx, address)
	if err != nil || len(hosts) == 0 {
		return nil
	}
	name := sanitizeHostName(hosts[0], address)
	if name == "" {
		return nil
	}
	return &name
}

// sanitizeHostName 清洗反查结果（对应 C# SanitizeHostName）。
func sanitizeHostName(name, address string) string {
	text := strings.TrimSuffix(strings.TrimSpace(name), ".")
	if text == "" {
		return ""
	}
	if net.ParseIP(text) != nil {
		return ""
	}
	if strings.EqualFold(text, address) {
		return ""
	}
	if strings.EqualFold(text, "localhost") {
		return ""
	}
	if host, _ := os.Hostname(); host != "" && strings.EqualFold(text, host) {
		return ""
	}
	return text
}
