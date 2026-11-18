// Package windir 提供系统目录定位（替代 C# Environment.SpecialFolder.System）。
package windir

import (
	"os"
	"path/filepath"
)

// SystemDir 返回 System32 目录。SystemRoot 在所有 Windows 进程中都已设置；
// 万一缺失，退回 C:\Windows\System32。
func SystemDir() string {
	if root := os.Getenv("SystemRoot"); root != "" {
		return filepath.Join(root, "System32")
	}
	if root := os.Getenv("windir"); root != "" {
		return filepath.Join(root, "System32")
	}
	return `C:\Windows\System32`
}
