// CLI 模式的控制台挂接（对应 C# Cli.cs EnsureConsole）：
// 生产 exe 是 windowsgui 子系统。行为对齐 C#：
//   · stdout 已重定向（管道/文件）→ 完全不 AttachConsole，用继承句柄
//     （先 attach 会把 std 句柄重置成 console，输出就进不了重定向目标）
//   · stdout 无效（cmd 直跑）→ AttachConsole(父控制台) + 重绑 CONOUT$

package cli

import (
	"os"

	"golang.org/x/sys/windows"
)

const (
	attachParentProcess = ^uintptr(0)  // ATTACH_PARENT_PROCESS
	stdOutputHandle     = ^uintptr(10) // STD_OUTPUT_HANDLE (-11)
	fileTypeChar        = 2
	fileTypeUnknown     = 0
)

var kernel32Cli = windows.NewLazySystemDLL("kernel32.dll")

// AttachParentConsole 按 C# EnsureConsole 的顺序挂接控制台。
func AttachParentConsole() {
	// 1. 先看继承的 stdout：管道/文件（已重定向）→ 不 attach，直接用
	hStd, _, _ := kernel32Cli.NewProc("GetStdHandle").Call(stdOutputHandle)
	if hStd != 0 && hStd != uintptr(windows.InvalidHandle) {
		ft, _, _ := kernel32Cli.NewProc("GetFileType").Call(hStd)
		switch ft & 0xffff {
		case fileTypeChar:
			// stdout 是 console：windowsgui 下一般拿不到有效句柄，
			// 走到这里说明 attach 前 console 已可用，直接用即可
			return
		case fileTypeUnknown:
			// 无效句柄（cmd 直跑 GUI exe）→ 继续走 attach
		default:
			// 管道/文件：已重定向，用继承句柄，绝不 attach
			return
		}
	}

	// 2. 无效句柄：挂父控制台并重绑标准输出
	attach := kernel32Cli.NewProc("AttachConsole")
	if r, _, _ := attach.Call(attachParentProcess); r == 0 {
		return // 没有父控制台（双击启动等），静默放弃
	}

	conout, err := windows.CreateFile(
		windows.StringToUTF16Ptr("CONOUT$"),
		windows.GENERIC_READ|windows.GENERIC_WRITE,
		windows.FILE_SHARE_READ|windows.FILE_SHARE_WRITE,
		nil, windows.OPEN_EXISTING, 0, 0)
	if err != nil {
		return
	}
	os.Stdout = os.NewFile(uintptr(conout), "CONOUT$")
	os.Stderr = os.NewFile(uintptr(conout), "CONERR$")
}
