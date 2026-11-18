// Package backup 移植自 C# Services/BackupService.cs：
// 导出文档（导入/导出/自动备份共用）、差异预览、快照保留策略。
// 文档结构与 C# 版兼容（camelCase、version/exportedAt/host/entries）。

package backup

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"portproxymanager/internal/jsonutil"
	"portproxymanager/internal/logging"
	"portproxymanager/internal/models"
	"portproxymanager/internal/settings"
)

// ExportEntry 导出文档里的一条（映射 + meta 合并）。
type ExportEntry struct {
	Family          string  `json:"family"`
	Protocol        string  `json:"protocol"`
	ListenAddress   string  `json:"listenAddress"`
	ListenPort      int     `json:"listenPort"`
	ConnectAddress  string  `json:"connectAddress"`
	ConnectPort     int     `json:"connectPort"`
	Name            *string `json:"name"`
	Group           *string `json:"group"`
	Note            *string `json:"note"`
	HasFirewallRule bool    `json:"hasFirewallRule"`
	FollowKind      *string `json:"followKind"`
	FollowId        *string `json:"followId"`
	FollowMac       *string `json:"followMac"`
	FollowLabel     *string `json:"followLabel"`
}

// ToEntry 转成映射。
func (e ExportEntry) ToEntry() models.PortProxyEntry {
	protocol := e.Protocol
	if strings.TrimSpace(protocol) == "" {
		protocol = "tcp"
	}
	return models.PortProxyEntry{
		Family: e.Family, Protocol: protocol,
		ListenAddress: e.ListenAddress, ListenPort: e.ListenPort,
		ConnectAddress: e.ConnectAddress, ConnectPort: e.ConnectPort,
	}
}

// ToMeta 转成 meta（CreatedAt 用当前时间）。
func (e ExportEntry) ToMeta() *models.EntryMeta {
	now := time.Now()
	return &models.EntryMeta{
		Name: e.Name, Group: e.Group, Note: e.Note,
		HasFirewallRule: e.HasFirewallRule, CreatedAt: &now,
		FollowKind: e.FollowKind, FollowId: e.FollowId,
		FollowMac: e.FollowMac, FollowLabel: e.FollowLabel,
	}
}

// From 从映射 + meta 构造导出条目。
func From(entry models.PortProxyEntry, m *models.EntryMeta) ExportEntry {
	e := ExportEntry{
		Family: entry.Family, Protocol: entry.Protocol,
		ListenAddress: entry.NormalizedListen(), ListenPort: entry.ListenPort,
		ConnectAddress: entry.NormalizedConnect(), ConnectPort: entry.ConnectPort,
	}
	if m != nil {
		e.Name, e.Group, e.Note = m.Name, m.Group, m.Note
		e.HasFirewallRule = m.HasFirewallRule
		e.FollowKind, e.FollowId = m.FollowKind, m.FollowId
		e.FollowMac, e.FollowLabel = m.FollowMac, m.FollowLabel
	}
	return e
}

// Document 导出文档。
type Document struct {
	Version    int           `json:"version"`
	ExportedAt *string       `json:"exportedAt"`
	Host       *string       `json:"host"`
	Entries    []ExportEntry `json:"entries"`
}

// BuildDocument 把当前状态写成导出文档。
func BuildDocument(entries []models.PortProxyEntry, getMeta func(key string) *models.EntryMeta) Document {
	host, _ := os.Hostname()
	now := time.Now().Format("2006-01-02T15:04:05-07:00")
	doc := Document{Version: 1, ExportedAt: &now, Host: &host, Entries: []ExportEntry{}}
	for _, e := range entries {
		var m *models.EntryMeta
		if getMeta != nil {
			m = getMeta(e.Key())
		}
		doc.Entries = append(doc.Entries, From(e, m))
	}
	return doc
}

// Export 导出到文件。
func Export(file string, entries []models.PortProxyEntry, getMeta func(string) *models.EntryMeta) error {
	if err := jsonutil.Write(file, BuildDocument(entries, getMeta)); err != nil {
		return err
	}
	if log := logging.Current(); log != nil {
		log.Info(fmt.Sprintf("已导出 %d 条映射到 %s", len(entries), file))
	}
	return nil
}

// LoadDocument 读取导出文档。
func LoadDocument(file string) (Document, error) {
	var doc Document
	if err := jsonutil.Read(file, &doc); err != nil {
		return doc, err
	}
	if doc.Entries == nil {
		return doc, fmt.Errorf("文件里没有 entries 字段：%s", file)
	}
	return doc, nil
}

// WriteSnapshot 破坏性操作前的自动快照，返回文件路径。
func WriteSnapshot(backupDir string, entries []models.PortProxyEntry,
	getMeta func(string) *models.EntryMeta, tag string, cfg *settings.AppSettings) (string, error) {
	safe := strings.Map(func(r rune) rune {
		if strings.ContainsRune(`\/:*?"<>|`, r) {
			return -1
		}
		return r
	}, tag)
	file := filepath.Join(backupDir, fmt.Sprintf("backup-%s-%s.json", time.Now().Format("20060102-150405"), safe))
	if err := jsonutil.Write(file, BuildDocument(entries, getMeta)); err != nil {
		return "", err
	}
	if log := logging.Current(); log != nil {
		log.Info("已创建快照 " + file)
	}
	if cfg != nil {
		_, _ = Prune(backupDir, cfg.BackupKeepCount, cfg.BackupKeepDays)
	}
	return file, nil
}

// Prune 快照保留策略。只处理 backup-*.json，永远保留最新一份。
func Prune(backupDir string, keepCount, keepDays int) (deleted, kept int) {
	entries, err := os.ReadDir(backupDir)
	if err != nil {
		return 0, 0
	}

	type fileInfo struct {
		name    string
		modTime time.Time
	}
	var files []fileInfo
	for _, e := range entries {
		if e.IsDir() || !strings.HasPrefix(e.Name(), "backup-") || !strings.HasSuffix(e.Name(), ".json") {
			continue
		}
		info, err := e.Info()
		if err == nil {
			files = append(files, fileInfo{e.Name(), info.ModTime()})
		}
	}
	sort.Slice(files, func(i, j int) bool { return files[i].modTime.After(files[j].modTime) })
	if len(files) == 0 {
		return 0, 0
	}

	now := time.Now()
	for i, f := range files {
		if i == 0 { // 最新一份无论多旧都保留
			kept++
			continue
		}
		tooMany := keepCount > 0 && i >= keepCount
		tooOld := keepDays > 0 && now.Sub(f.modTime) > time.Duration(keepDays)*24*time.Hour
		if !tooMany && !tooOld {
			kept++
			continue
		}
		if err := os.Remove(filepath.Join(backupDir, f.name)); err != nil {
			if log := logging.Current(); log != nil {
				log.Warn("删除旧快照失败：" + f.name + "：" + err.Error())
			}
			kept++
			continue
		}
		deleted++
		if log := logging.Current(); log != nil {
			log.Info("备份保留策略删除旧快照：" + f.name)
		}
	}
	return deleted, kept
}

// DiffKind 差异类型。
type DiffKind int

const (
	DiffAdd DiffKind = iota
	DiffUpdate
	DiffDelete
	DiffSame
)

func (d DiffKind) Label() string {
	switch d {
	case DiffAdd:
		return "新增"
	case DiffUpdate:
		return "覆盖"
	case DiffDelete:
		return "删除"
	default:
		return "无变化"
	}
}

// DiffItem 差异项。
type DiffItem struct {
	Kind      DiffKind               `json:"kind"`
	KindLabel string                 `json:"kindLabel"`
	Entry     models.PortProxyEntry  `json:"entry"`
	Current   *models.PortProxyEntry `json:"current"`
	Meta      *models.EntryMeta      `json:"meta"`
}

// Diff 对比当前状态与导入文件。
func Diff(current []models.PortProxyEntry, imported Document) []DiffItem {
	currentMap := map[string]models.PortProxyEntry{}
	for _, e := range current {
		currentMap[strings.ToLower(e.Key())] = e
	}
	importedKeys := map[string]bool{}
	var items []DiffItem

	for _, raw := range imported.Entries {
		entry := raw.ToEntry()
		importedKeys[strings.ToLower(entry.Key())] = true
		meta := raw.ToMeta()

		existing, ok := currentMap[strings.ToLower(entry.Key())]
		switch {
		case !ok:
			items = append(items, DiffItem{Kind: DiffAdd, KindLabel: DiffAdd.Label(), Entry: entry, Meta: meta})
		case !existing.SameTarget(entry):
			cur := existing
			items = append(items, DiffItem{Kind: DiffUpdate, KindLabel: DiffUpdate.Label(), Entry: entry, Current: &cur, Meta: meta})
		default:
			cur := existing
			items = append(items, DiffItem{Kind: DiffSame, KindLabel: DiffSame.Label(), Entry: entry, Current: &cur, Meta: meta})
		}
	}
	for _, existing := range current {
		if !importedKeys[strings.ToLower(existing.Key())] {
			cur := existing
			items = append(items, DiffItem{Kind: DiffDelete, KindLabel: DiffDelete.Label(), Entry: existing, Current: &cur})
		}
	}

	order := map[DiffKind]int{DiffDelete: 0, DiffUpdate: 1, DiffAdd: 2, DiffSame: 3}
	sort.SliceStable(items, func(i, j int) bool {
		if order[items[i].Kind] != order[items[j].Kind] {
			return order[items[i].Kind] < order[items[j].Kind]
		}
		if items[i].Entry.Family != items[j].Entry.Family {
			return items[i].Entry.Family < items[j].Entry.Family
		}
		return items[i].Entry.ListenPort < items[j].Entry.ListenPort
	})
	return items
}
