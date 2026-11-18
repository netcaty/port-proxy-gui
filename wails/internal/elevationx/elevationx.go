// Package elevationx 移植自 C# Cli.cs 的自提权重启：
// manifest 用 asInvoker，GUI 时按需 ShellExecute "runas" 重启，
// 保留只读免提权路径。UAC 取消（错误码 1223）要能区分。

package elevationx

import (
	"errors"
	"fmt"
	"os"
	"strings"
	"unsafe"

	"golang.org/x/sys/windows"
)

const errorCancelled = 1223 // ERROR_CANCELLED

// shellExecuteInfo 对应 Win32 SHELLEXECUTEINFW（x64，112 字节）。
// x/sys/windows 没有封装 ShellExecuteEx，直接手写结构体调 shell32.dll。
// 注意：hIcon/hMonitor 在 C 里是 union（8 字节），这里用单个 uintptr 占位保证布局。
type shellExecuteInfo struct {
	Size       uint32
	Mask       uint32
	Hwnd       windows.HWND
	Verb       *uint16
	File       *uint16
	Parameters *uint16
	Directory  *uint16
	Show       int32
	InstApp    windows.Handle
	IDList     uintptr
	Class      uintptr
	ClassKey   windows.Handle
	HotKey     uint32
	_          uint32  // 对齐填充
	Union      uintptr // hIcon / hMonitor union
	HProcess   windows.Handle
}

const shellExecuteInfoSize = 112 // sizeof(SHELLEXECUTEINFOW) on x64

const (
	seeMaskFlagNoUI = 0x00000400
	swShownormal    = 1
)

var (
	shell32  = windows.NewLazySystemDLL("shell32.dll")
	procSEEx = shell32.NewProc("ShellExecuteExW")
)

// RelaunchElevated 以管理员权限重启当前程序，附带原命令行参数（去掉 --no-elevate）。
// 返回 (started, error)；UAC 被取消时 started=false。
func RelaunchElevated() (bool, error) {
	exe, err := os.Executable()
	if err != nil {
		return false, err
	}

	// 收集参数，去掉 --no-elevate（避免无限提权循环）
	args := []string{}
	for _, a := range os.Args[1:] {
		if strings.EqualFold(a, "--no-elevate") {
			continue
		}
		args = append(args, a)
	}
	params := quoteArgs(args)

	ei := &shellExecuteInfo{
		Size:       shellExecuteInfoSize,
		Mask:       seeMaskFlagNoUI,
		File:       windows.StringToUTF16Ptr(exe),
		Verb:       windows.StringToUTF16Ptr("runas"),
		Parameters: windows.StringToUTF16Ptr(params),
		Show:       swShownormal,
	}

	ret, _, callErr := procSEEx.Call(uintptr(unsafe.Pointer(ei)))
	if ret == 0 {
		if errno, ok := callErr.(windows.Errno); ok {
			if uint32(errno) == errorCancelled {
				return false, nil // 用户取消 UAC，不算错误
			}
			return false, fmt.Errorf("提权启动失败（错误码 %d）", uint32(errno))
		}
		return false, errors.New("提权启动失败：" + callErr.Error())
	}
	return true, nil
}

// quoteArgs 简单引号封装（Windows 命令行约定）。
func quoteArgs(args []string) string {
	var b strings.Builder
	for i, a := range args {
		if i > 0 {
			b.WriteByte(' ')
		}
		if strings.ContainsAny(a, " \t\"") {
			b.WriteByte('"')
			b.WriteString(strings.ReplaceAll(a, `"`, `\"`))
			b.WriteByte('"')
		} else {
			b.WriteString(a)
		}
	}
	return b.String()
}
