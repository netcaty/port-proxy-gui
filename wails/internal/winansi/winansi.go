// Package winansi 移植自 C# Services/AnsiText.cs：
// 把子进程输出的原始字节按系统 ANSI 代码页（GetACP）解码。
// netsh / arp 等工具直接写 ANSI 字节，硬编码 936 只在中文系统正确；
// 直接调 Win32 MultiByteToWideChar 可以适配任意语言版本，行为与 C# 版完全一致。
package winansi

import (
	"sync"
	"unicode/utf16"
	"unsafe"

	"golang.org/x/sys/windows"
)

var (
	kernel32   = windows.NewLazySystemDLL("kernel32.dll")
	procGetACP = kernel32.NewProc("GetACP")
	procMB2WC  = kernel32.NewProc("MultiByteToWideChar")
)

var (
	acpOnce sync.Once
	acpVal  uint32
)

func codePage() uint32 {
	acpOnce.Do(func() {
		acpVal = 936 // 兜底：中文系统
		if r, _, _ := procGetACP.Call(); r != 0 {
			acpVal = uint32(r)
		}
	})
	return acpVal
}

// Decode ANSI 字节 → UTF-8 字符串。任何失败回退按 UTF-8 解。
func Decode(b []byte) string {
	if len(b) == 0 {
		return ""
	}
	cp := codePage()

	// 先算需要的 UTF-16 字符数
	n, _, _ := procMB2WC.Call(
		uintptr(cp), 0,
		uintptr(unsafe.Pointer(&b[0])), uintptr(len(b)),
		0, 0)
	if n == 0 {
		return string(b) // 解不了就当 UTF-8
	}

	buf := make([]uint16, n)
	m, _, _ := procMB2WC.Call(
		uintptr(cp), 0,
		uintptr(unsafe.Pointer(&b[0])), uintptr(len(b)),
		uintptr(unsafe.Pointer(&buf[0])), n)
	if m == 0 {
		return string(b)
	}
	return string(utf16.Decode(buf[:m]))
}
