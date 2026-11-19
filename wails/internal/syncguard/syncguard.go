// Package syncguard 提供跨进程的目标跟随同步互斥锁。
//
// GUI 的 5 分钟定时同步与「无人值守跟随同步」计划任务可能同时触发，
// 两边都会经 netsh 先删后加；必须串行化，避免互相覆盖回滚。
//
// 用命名 Mutex（不是信号量）：持锁进程崩溃时 WaitForSingleObject 返回
// WAIT_ABANDONED，后来者可以接管，不会永久卡死。Win32 Mutex 是线程持有的，
// 而 Go 的 goroutine 可能切换线程，所以整个持锁期间用 runtime.LockOSThread
// 固定当前线程。

package syncguard

import (
	"runtime"

	"golang.org/x/sys/windows"
)

// Local\ 即可：GUI 与计划任务都运行在同一个交互式用户会话里。
const mutexName = `Local\PortProxyManager.FollowSync`

// Guard 已获取的同步锁。Release 必须由获取它的同一个 goroutine 调用。
type Guard struct {
	handle windows.Handle
}

// TryAcquire 尝试立即拿锁；已被其他进程/协程占用时返回 (nil, false)。
// 注意 x/sys 语义：互斥体已存在时返回有效句柄 + err=ERROR_ALREADY_EXISTS，
// 这不是失败——真正失败只指句柄无效。
func TryAcquire() (*Guard, bool) {
	runtime.LockOSThread()

	h, err := windows.CreateMutex(nil, false, windows.StringToUTF16Ptr(mutexName))
	if err != nil && h == 0 {
		runtime.UnlockOSThread()
		return nil, false
	}

	wait, _ := windows.WaitForSingleObject(h, 0)
	// WAIT_OBJECT_0=0（拿到锁）、WAIT_ABANDONED=0x80（接管崩溃进程遗留的锁）。
	if wait != windows.WAIT_OBJECT_0 && wait != windows.WAIT_ABANDONED {
		_ = windows.CloseHandle(h)
		runtime.UnlockOSThread()
		return nil, false
	}

	return &Guard{handle: h}, true
}

// Release 释放锁并解除线程固定。
func (g *Guard) Release() {
	if g == nil {
		return
	}
	if g.handle != 0 {
		_ = windows.ReleaseMutex(g.handle)
		_ = windows.CloseHandle(g.handle)
		g.handle = 0
	}
	runtime.UnlockOSThread()
}
