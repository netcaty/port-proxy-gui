// Package follow 移植自 C# Services/TargetFollowService.cs：
// WSL / 虚拟机目标跟随。把"跟谁"保存在 meta 里，网络变化或定时器触发时重新探测，
// 地址变了才走 netsh 重建映射（经 PortProxyService.Update + 注册表核对）。

package follow

import (
	"net"
	"strings"

	"portproxymanager/internal/hints"
	"portproxymanager/internal/logging"
	"portproxymanager/internal/models"
	"portproxymanager/internal/netsh"
	"portproxymanager/internal/registryio"
)

// Target 一条目标跟随规则（Kind 取 wsl/hyperv/wsb/vm；Mac 用于改名后认回）。
type Target struct {
	Kind  string
	ID    *string
	Mac   *string
	Label *string
}

func (t Target) KindKey() string { return strings.ToLower(strings.TrimSpace(t.Kind)) }

// Display 界面展示文本。
func (t Target) Display() string {
	if notBlank(t.Label) {
		return *t.Label
	}
	id := clean(t.ID)
	suffix := id
	if id == nil || looksLikeMacID(*id) {
		suffix = shortMac(t.Mac)
	}
	switch t.KindKey() {
	case "wsl":
		if suffix == nil || strings.EqualFold(*suffix, "WSL") {
			return "WSL"
		}
		return "WSL · " + *suffix
	case "hyperv":
		return "Hyper-V · " + orDefault(suffix, "未命名")
	case "wsb":
		return "沙盒 · " + orDefault(suffix, "未命名")
	case "vm":
		return "虚拟机 · " + orDefault(suffix, "未命名")
	default:
		return t.KindKey() + " · " + orDefault(suffix, "未命名")
	}
}

// TargetFromMeta 从 meta 构造跟随规则；没启用跟随返回 nil。
func TargetFromMeta(m *models.EntryMeta) *Target {
	if m == nil || !m.IsFollowing() || m.FollowKind == nil {
		return nil
	}
	return &Target{
		Kind:  strings.ToLower(strings.TrimSpace(*m.FollowKind)),
		ID:    clean(m.FollowId),
		Mac:   normalizeMacPtr(m.FollowMac),
		Label: clean(m.FollowLabel),
	}
}

// TargetFromHint 从候选地址构造跟随规则。
func TargetFromHint(h hints.AddressHint) *Target {
	if !IsFollowable(h) {
		return nil
	}
	return &Target{
		Kind:  strings.ToLower(strings.TrimSpace(deref(h.Kind))),
		ID:    clean(h.ID),
		Mac:   normalizeMacPtr(h.Mac),
		Label: strPtr(BuildLabel(h)),
	}
}

// IsFollowable 只有 WSL/Hyper-V/沙盒/VMware/VirtualBox 客户机才值得跟随。
func IsFollowable(h hints.AddressHint) bool {
	kind := deref(h.Kind)
	if kind != "wsl" && kind != "hyperv" && kind != "wsb" && kind != "vm" {
		return false
	}
	if kind == "wsl" {
		return notBlank(h.ID) && !looksLikeMacID(*h.ID)
	}
	return notBlank(h.Mac) || notBlank(h.ID)
}

// Matches 判断探测结果是否还是同一条跟随规则。
// 虚拟机优先比 MAC（改名也不怕）；两边都有 MAC 时不一致绝不按名字误匹配；WSL 比发行版名。
func Matches(h hints.AddressHint, t *Target) bool {
	if !IsFollowable(h) {
		return false
	}
	hintKind, targetKind := strings.ToLower(strings.TrimSpace(deref(h.Kind))), t.KindKey()

	if hintKind == "wsl" || targetKind == "wsl" {
		if hintKind != "wsl" || targetKind != "wsl" {
			return false
		}
		hintID, targetID := clean(h.ID), clean(t.ID)
		if hintID == nil || looksLikeMacID(*hintID) {
			return false
		}
		// 早期探测拿不到发行版名时 Id 是通用的 "WSL"，接受任意 WSL 结果
		if targetID == nil || strings.EqualFold(*targetID, "WSL") {
			return true
		}
		return strings.EqualFold(*targetID, *hintID)
	}

	targetMac, hintMac := normalizeMacPtr(t.Mac), normalizeMacPtr(h.Mac)
	if targetMac != nil && hintMac != nil {
		return *targetMac == *hintMac
	}

	targetName, hintName := clean(t.ID), clean(h.ID)
	targetNameMac, hintNameMac := normalizeMacPtr(targetName), normalizeMacPtr(hintName)
	if targetNameMac != nil && hintNameMac != nil {
		return *targetNameMac == *hintNameMac
	}
	return targetName != nil && hintName != nil && strings.EqualFold(*targetName, *hintName)
}

// TargetEquals 界面里判断两个跟随规则是不是同一个。
func TargetEquals(a, b *Target) bool {
	if a == nil || b == nil {
		return a == nil && b == nil
	}
	aMac, bMac := normalizeMacPtr(a.Mac), normalizeMacPtr(b.Mac)
	if aMac != nil && bMac != nil {
		return *aMac == *bMac
	}
	if a.KindKey() != b.KindKey() {
		return false
	}
	return strings.EqualFold(orDefault(clean(a.ID), ""), orDefault(clean(b.ID), ""))
}

// BuildLabel 跟随目标的展示标签。
func BuildLabel(h hints.AddressHint) string {
	id := clean(h.ID)
	suffix := id
	if id == nil || looksLikeMacID(*id) {
		suffix = shortMac(h.Mac)
	}
	switch strings.ToLower(strings.TrimSpace(deref(h.Kind))) {
	case "wsl":
		if suffix == nil || strings.EqualFold(*suffix, "WSL") {
			return "WSL"
		}
		return "WSL · " + *suffix
	case "hyperv":
		return "Hyper-V · " + orDefault(suffix, "未命名")
	case "wsb":
		return "沙盒 · " + orDefault(suffix, "未命名")
	case "vm":
		return "虚拟机 · " + orDefault(suffix, "未命名")
	default:
		return deref(h.Kind) + " · " + orDefault(suffix, h.Address)
	}
}

// SyncStatus 同步状态。
type SyncStatus int

const (
	StatusUpdated SyncStatus = iota
	StatusUpToDate
	StatusUnresolved
	StatusSkipped
	StatusNeedsElevation
	StatusFailed
)

func (s SyncStatus) Label() string {
	switch s {
	case StatusUpdated:
		return "已更新"
	case StatusUpToDate:
		return "无变化"
	case StatusUnresolved:
		return "未检测到"
	case StatusSkipped:
		return "跳过"
	case StatusNeedsElevation:
		return "待提权"
	default:
		return "失败"
	}
}

type SyncItem struct {
	Entry       models.PortProxyEntry `json:"entry"`
	Target      *Target               `json:"target"`
	NewAddress  *string               `json:"newAddress"`
	Status      SyncStatus            `json:"status"`
	StatusLabel string                `json:"statusLabel"`
	Message     string                `json:"message"`
}

type SyncReport struct {
	Items               []SyncItem `json:"items"`
	UpdatedCount        int        `json:"updatedCount"`
	FailedCount         int        `json:"failedCount"`
	NeedsElevationCount int        `json:"needsElevationCount"`
	UnresolvedCount     int        `json:"unresolvedCount"`
}

// Sync 执行一次跟随同步。entries 是映射 + meta 的列表；elevated 决定是否能写。
func Sync(entries []models.PortProxyEntry, getMeta func(key string) *models.EntryMeta,
	elevated bool, forceRefresh bool) SyncReport {
	report := SyncReport{Items: []SyncItem{}}

	type pendingItem struct {
		entry  models.PortProxyEntry
		target *Target
	}
	var targets []pendingItem
	for _, e := range entries {
		t := TargetFromMeta(getMeta(e.Key()))
		if t != nil {
			targets = append(targets, pendingItem{e, t})
		}
	}
	if len(targets) == 0 {
		return report
	}

	// 映射可能已被外部删除；跟随不能把它悄悄重建回来
	registry, readErr := registryio.ReadAll()
	if readErr != nil {
		for _, t := range targets {
			report.Items = append(report.Items, SyncItem{
				Entry: t.entry, Target: t.target, Status: StatusFailed,
				StatusLabel: StatusFailed.Label(), Message: "读取注册表失败：" + readErr.Error(),
			})
		}
		return finalize(report)
	}
	currentByKey := map[string]models.PortProxyEntry{}
	for _, e := range registry {
		key := strings.ToLower(e.Key())
		if _, ok := currentByKey[key]; !ok {
			currentByKey[key] = e
		}
	}

	var pending []pendingItem
	for _, t := range targets {
		if _, ok := currentByKey[strings.ToLower(t.entry.Key())]; !ok {
			report.Items = append(report.Items, SyncItem{
				Entry: t.entry, Target: t.target, Status: StatusSkipped,
				StatusLabel: StatusSkipped.Label(),
				Message:     "注册表里已经没有这条映射，跳过跟随（不会自动重建）。",
			})
			continue
		}
		pending = append(pending, t)
	}
	if len(pending) == 0 {
		return finalize(report)
	}

	// 收集候选：缓存强刷 + 已确认的 ARP 邻居
	hintsList := collectHints(forceRefresh)

	for _, p := range pending {
		if !p.entry.ConnectIsV4() {
			report.Items = append(report.Items, SyncItem{
				Entry: p.entry, Target: p.target, Status: StatusSkipped,
				StatusLabel: StatusSkipped.Label(),
				Message:     "目标侧不是 IPv4，无法跟随 IPv4 客户机地址。",
			})
			continue
		}

		var matched *hints.AddressHint
		for i := range hintsList {
			if Matches(hintsList[i], p.target) {
				matched = &hintsList[i]
				break
			}
		}
		if matched == nil {
			report.Items = append(report.Items, SyncItem{
				Entry: p.entry, Target: p.target, Status: StatusUnresolved,
				StatusLabel: StatusUnresolved.Label(),
				Message:     "当前没有检测到 " + p.target.Display() + "，保留原目标 " + p.entry.ConnectText() + "。",
			})
			continue
		}

		ip := net.ParseIP(matched.Address)
		if ip == nil || ip.To4() == nil {
			report.Items = append(report.Items, SyncItem{
				Entry: p.entry, Target: p.target, Status: StatusSkipped,
				StatusLabel: StatusSkipped.Label(),
				Message:     "探测到的地址「" + matched.Address + "」不是有效的 IPv4。",
			})
			continue
		}
		address := ip.String()
		current := currentByKey[strings.ToLower(p.entry.Key())]
		currentTarget := p.entry.NormalizedConnect()
		if current.ConnectAddress != "" {
			currentTarget = current.NormalizedConnect()
		}
		if strings.EqualFold(address, currentTarget) {
			report.Items = append(report.Items, SyncItem{
				Entry: p.entry, Target: p.target, NewAddress: strPtr(address),
				Status: StatusUpToDate, StatusLabel: StatusUpToDate.Label(),
				Message: "目标未变化（" + address + "）。",
			})
			continue
		}
		if !elevated {
			report.Items = append(report.Items, SyncItem{
				Entry: p.entry, Target: p.target, NewAddress: strPtr(address),
				Status: StatusNeedsElevation, StatusLabel: StatusNeedsElevation.Label(),
				Message: "目标已变为 " + address + "，但写入映射需要管理员权限。",
			})
			continue
		}

		oldEntry := p.entry
		if current.ConnectAddress != "" {
			oldEntry = current
		}
		updated := p.entry.WithConnection(address, p.entry.ConnectPort)
		if _, err := netsh.Update(oldEntry, updated); err != nil {
			if log := logging.Current(); log != nil {
				log.Error("跟随 " + p.target.Display() + " 更新失败：" + err.Error())
			}
			report.Items = append(report.Items, SyncItem{
				Entry: p.entry, Target: p.target, NewAddress: strPtr(address),
				Status: StatusFailed, StatusLabel: StatusFailed.Label(),
				Message: "更新失败：" + err.Error(),
			})
			continue
		}
		if log := logging.Current(); log != nil {
			log.Info("跟随 " + p.target.Display() + "：" + p.entry.ListenText() +
				" 目标 " + currentTarget + " → " + address)
		}
		report.Items = append(report.Items, SyncItem{
			Entry: p.entry, Target: p.target, NewAddress: strPtr(address),
			Status: StatusUpdated, StatusLabel: StatusUpdated.Label(),
			Message: "目标已更新：" + currentTarget + " → " + address,
		})
	}
	return finalize(report)
}

func collectHints(forceRefresh bool) []hints.AddressHint {
	cached := hints.RefreshNow(forceRefresh)
	base := hints.FilterUnconfirmedGuests(hints.GetHints(true))
	seen := map[string]bool{}
	merged := []hints.AddressHint{}
	for _, h := range append(cached, base...) {
		if !IsFollowable(h) || seen[strings.ToLower(h.Address)] {
			continue
		}
		seen[strings.ToLower(h.Address)] = true
		merged = append(merged, h)
	}
	return merged
}

func finalize(r SyncReport) SyncReport {
	for _, item := range r.Items {
		switch item.Status {
		case StatusUpdated:
			r.UpdatedCount++
		case StatusFailed:
			r.FailedCount++
		case StatusNeedsElevation:
			r.NeedsElevationCount++
		case StatusUnresolved:
			r.UnresolvedCount++
		}
	}
	return r
}

func normalizeMacPtr(s *string) *string {
	if s == nil {
		return nil
	}
	return hints.NormalizeMac(*s)
}

func looksLikeMacID(id string) bool { return hints.NormalizeMac(id) != nil }

func shortMac(mac *string) *string {
	n := normalizeMacPtr(mac)
	if n == nil {
		return nil
	}
	s := "MAC " + (*n)[len(*n)-4:]
	return &s
}

func clean(s *string) *string {
	if s == nil {
		return nil
	}
	t := strings.TrimSpace(*s)
	if t == "" {
		return nil
	}
	return &t
}

func deref(s *string) string {
	if s == nil {
		return ""
	}
	return *s
}

func notBlank(s *string) bool { return s != nil && strings.TrimSpace(*s) != "" }

func strPtr(s string) *string { return &s }

func orDefault(s *string, fallback string) string {
	if s == nil {
		return fallback
	}
	return *s
}
