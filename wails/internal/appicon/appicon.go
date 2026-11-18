// Package appicon 提供程序图标（与 C# 版 UI/AppIcon.cs 生成的图形一致：
// 深蓝圆角方块 + 白色 ⇄ 双向转发箭头）。
// 图标为静态资源（icon.png，由生成工具导出后入库），运行时直接 embed 读取。
package appicon

import (
	_ "embed"
)

//go:embed icon.png
var iconPNG []byte

// TrayIconPNG 托盘图标（PNG 字节，64×64）。
func TrayIconPNG() []byte { return iconPNG }
