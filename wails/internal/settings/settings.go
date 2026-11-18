// Package settings 移植自 C# Services/SettingsStore.cs：
// 界面偏好，存在数据目录的 settings.json（字段名 camelCase，与 C# 版兼容）。
package settings

import (
	"portproxymanager/internal/jsonutil"
	"portproxymanager/internal/logging"
)

// AppSettings 界面偏好。
type AppSettings struct {
	Version         int            `json:"version"`
	WindowWidth     int            `json:"windowWidth"`
	WindowHeight    int            `json:"windowHeight"`
	WindowX         int            `json:"windowX"`
	WindowY         int            `json:"windowY"`
	Maximized       bool           `json:"maximized"`
	CloseToTray     bool           `json:"closeToTray"`
	TrayTipShown    bool           `json:"trayTipShown"`
	LastGroup       string         `json:"lastGroup"`
	LastFilter      string         `json:"lastFilter"`
	ColumnWidths    map[string]int `json:"columnWidths"`
	BackupKeepCount int            `json:"backupKeepCount"`
	BackupKeepDays  int            `json:"backupKeepDays"`
}

// Default 默认值（与 C# 属性初始化器一致）。
func Default() AppSettings {
	return AppSettings{
		Version:         1,
		WindowWidth:     1120,
		WindowHeight:    660,
		WindowX:         minInt32,
		WindowY:         minInt32,
		CloseToTray:     true,
		BackupKeepCount: 50,
		BackupKeepDays:  30,
	}
}

const minInt32 = -2147483648

// Store settings 存储。
type Store struct {
	file  string
	Value AppSettings
}

// Load 读取；解析失败用默认设置。
func Load(file string) *Store {
	s := &Store{file: file, Value: Default()}
	var v AppSettings
	if err := jsonutil.Read(file, &v); err != nil {
		if log := logging.Current(); log != nil {
			log.Warn("settings.json 解析失败，使用默认设置：" + err.Error())
		}
		return s
	}
	// 与 C# 反序列化语义对齐：缺省字段保留默认值（Go 零值 vs 默认值）
	merged := Default()
	if v.Version != 0 {
		merged.Version = v.Version
	}
	if v.WindowWidth != 0 {
		merged.WindowWidth = v.WindowWidth
	}
	if v.WindowHeight != 0 {
		merged.WindowHeight = v.WindowHeight
	}
	if v.WindowX != 0 {
		merged.WindowX = v.WindowX
	}
	if v.WindowY != 0 {
		merged.WindowY = v.WindowY
	}
	merged.Maximized = v.Maximized
	merged.CloseToTray = v.CloseToTray
	merged.TrayTipShown = v.TrayTipShown
	if v.LastGroup != "" {
		merged.LastGroup = v.LastGroup
	}
	if v.LastFilter != "" {
		merged.LastFilter = v.LastFilter
	}
	if v.ColumnWidths != nil {
		merged.ColumnWidths = v.ColumnWidths
	}
	if v.BackupKeepCount != 0 {
		merged.BackupKeepCount = v.BackupKeepCount
	}
	if v.BackupKeepDays != 0 {
		merged.BackupKeepDays = v.BackupKeepDays
	}
	s.Value = merged
	return s
}

// Save 落盘。失败只记日志，不打断用户操作。
func (s *Store) Save() error {
	if err := jsonutil.Write(s.file, s.Value); err != nil {
		if log := logging.Current(); log != nil {
			log.Warn("保存设置失败：" + err.Error())
		}
		return err
	}
	return nil
}
