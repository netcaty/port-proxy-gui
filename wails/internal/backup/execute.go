package backup

// 导入执行与文件列表（对应 C# BackupService.Apply / 目录枚举）。

import (
	"os"
	"path/filepath"
	"sort"
	"strings"

	"portproxymanager/internal/logging"
	"portproxymanager/internal/meta"
	"portproxymanager/internal/models"
	"portproxymanager/internal/netsh"
)

// PreviewResult 导入预览结果。
type PreviewResult struct {
	Document *Document  `json:"document"`
	Diff     []DiffItem `json:"diff"`
	Error    *string    `json:"error"`
}

// ApplyResult 导入执行结果。
type ApplyResult struct {
	Ok      bool     `json:"ok"`
	Applied int      `json:"applied"`
	Failed  int      `json:"failed"`
	Errors  []string `json:"errors"`
}

// BackupFile 备份/导出文件列表项。
type BackupFile struct {
	Name    string `json:"name"`
	Size    int64  `json:"size"`
	ModTime string `json:"modTime"`
}

// ListFiles 列出备份目录里的 json 文件（新的在前）。
func ListFiles(dir string) []BackupFile {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return []BackupFile{}
	}
	var out []BackupFile
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		ext := strings.ToLower(filepath.Ext(e.Name()))
		if ext != ".json" {
			continue
		}
		info, err := e.Info()
		if err != nil {
			continue
		}
		out = append(out, BackupFile{
			Name: e.Name(), Size: info.Size(),
			ModTime: info.ModTime().Format("2006-01-02 15:04:05"),
		})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Name > out[j].Name })
	return out
}

// Apply 执行导入差异中选中的条目。onChanged 在每次写操作后调用（用于刷新界面）。
func Apply(items []DiffItem, current []models.PortProxyEntry, store *meta.Store, onChanged func(models.PortProxyEntry)) ApplyResult {
	result := ApplyResult{Errors: []string{}}

	// 按当前注册表重建索引，确保用真实的最新状态执行
	currentMap := map[string]models.PortProxyEntry{}
	for _, e := range current {
		currentMap[strings.ToLower(e.Key())] = e
	}

	for _, item := range items {
		switch item.Kind {
		case DiffSame:
			// 无变化也把备注同步过来
			if item.Meta != nil {
				store.Set(item.Entry.Key(), *item.Meta)
			}
			result.Applied++
			continue
		}

		switch item.Kind {
		case DiffAdd:
			if _, err := netsh.Add(item.Entry); err != nil {
				result.Errors = append(result.Errors, "新增 "+item.Entry.ListenText()+" → "+item.Entry.ConnectText()+"："+err.Error())
				result.Failed++
				continue
			}
			if item.Meta != nil {
				store.Set(item.Entry.Key(), *item.Meta)
			}
			onChanged(item.Entry)

		case DiffUpdate:
			old := item.Entry
			if item.Current != nil {
				old = *item.Current
			} else if cur, ok := currentMap[strings.ToLower(item.Entry.Key())]; ok {
				old = cur
			}
			if _, err := netsh.Update(old, item.Entry); err != nil {
				result.Errors = append(result.Errors, "覆盖 "+item.Entry.ListenText()+" → "+item.Entry.ConnectText()+"："+err.Error())
				result.Failed++
				continue
			}
			if item.Meta != nil {
				store.Remove(strings.ToLower(old.Key()))
				store.Set(item.Entry.Key(), *item.Meta)
			}
			onChanged(item.Entry)

		case DiffDelete:
			if err := netsh.Delete(item.Entry, false); err != nil {
				result.Errors = append(result.Errors, "删除 "+item.Entry.ListenText()+" → "+item.Entry.ConnectText()+"："+err.Error())
				result.Failed++
				continue
			}
			store.Remove(item.Entry.Key())
			onChanged(item.Entry)
		}
		result.Applied++
	}

	_ = store.Save()
	result.Ok = result.Failed == 0
	if log := logging.Current(); log != nil && len(result.Errors) > 0 {
		for _, e := range result.Errors {
			log.Error("导入执行失败：" + e)
		}
	}
	return result
}
