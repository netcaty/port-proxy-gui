// Package elevation 移植自 C# Services/Elevation.cs。
package elevation

import (
	"os"
	"os/user"
	"runtime"

	"golang.org/x/sys/windows"
)

// IsElevated 当前进程是否以管理员令牌运行。
func IsElevated() bool {
	runtime.LockOSThread() // Go 布局固定后 GetCurrentProcessToken 才可靠
	defer runtime.UnlockOSThread()
	return windows.GetCurrentProcessToken().IsElevated()
}

// CurrentUser 当前用户名（域\用户）；拿不到就退回 USERNAME 环境变量。
func CurrentUser() string {
	if u, err := user.Current(); err == nil && u.Username != "" {
		return u.Username
	}
	return os.Getenv("USERNAME")
}
