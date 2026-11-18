// Package singleinst 移植自 C# Program.cs 的单实例逻辑：
// 命名 Mutex + 激活事件——已有实例运行时，新实例激活旧窗口后退出。
// 注意 AbandonedMutexException 要当作"拿到锁"处理（旧实例崩溃留下的）。

package singleinst

import (
	"time"

	"golang.org/x/sys/windows"
)

// 命名带 .Wails 后缀与 C# 版区分：两版共用数据目录但不能共用单实例锁——
// 否则 Go 版启动时会把 C# 版的窗口激活（同名 Activate 事件互通）。
// 注意：名字独立 ≠ 可并发，两版同时运行仍会并发写 meta.json，请勿同时开着。
const (
	mutexName    = `Local\PortProxyManager.Wails.SingleInstance`
	activateName = `Local\PortProxyManager.Wails.Activate`
)

// Instance 单实例守卫。
type Instance struct {
	mutex    windows.Handle
	activate windows.Handle
	owned    bool
}

// Acquire 尝试获取单实例锁。
// waitTimeout=0 表示立即返回（普通启动）；非 0 用于托盘"重启"场景——
// 新实例等待旧实例退出释放锁后再接管（对应 C# mutex.WaitOne(restartWait ? 5000 : 0)）。
// 返回值：(guard, isFirst, error)。isFirst=false 表示已有实例在运行，
// 调用方应 SignalActivate() 激活旧窗口后退出。
func Acquire(waitTimeout time.Duration) (*Instance, bool, error) {
	// 注意 x/sys 语义：mutex 已存在时返回【有效句柄 + err=ERROR_ALREADY_EXISTS】，
	// 这是正常路径（锁被旧实例持有），绝不是失败——失败仅指句柄无效。
	mutex, err := windows.CreateMutex(nil, false, windows.StringToUTF16Ptr(mutexName))
	if err != nil && mutex == 0 {
		return nil, false, err
	}

	wait, _ := windows.WaitForSingleObject(mutex, uint32(waitTimeout.Milliseconds()))
	// Win32 返回值：WAIT_OBJECT_0=0（拿到锁）、WAIT_ABANDONED=0x80（接管被遗弃的锁）、
	// WAIT_TIMEOUT=0x102（已被其他实例持有）。
	owned := wait == windows.WAIT_OBJECT_0 || wait == windows.WAIT_ABANDONED

	g := &Instance{mutex: mutex}
	if !owned {
		// 已有实例：打开激活事件并通知它
		activate, err := windows.OpenEvent(windows.EVENT_MODIFY_STATE, false, windows.StringToUTF16Ptr(activateName))
		if err == nil {
			g.activate = activate
			_ = windows.SetEvent(activate)
		}
		return g, false, nil
	}

	g.owned = true
	activate, err := windows.CreateEvent(nil, 0, 0, windows.StringToUTF16Ptr(activateName))
	if err != nil {
		_ = windows.CloseHandle(mutex)
		return nil, false, err
	}
	g.activate = activate
	return g, true, nil
}

// ActivateCh 启动一个 goroutine 监听"激活"事件（旧实例被再次启动时收到）。
// 收到信号时向通道发送一次。
func (g *Instance) ActivateCh() <-chan struct{} {
	ch := make(chan struct{}, 1)
	if !g.owned || g.activate == 0 {
		return ch
	}
	go func() {
		for {
			wait, _ := windows.WaitForSingleObject(g.activate, windows.INFINITE)
			if wait == windows.WAIT_OBJECT_0 {
				select {
				case ch <- struct{}{}:
				default:
				}
			}
			time.Sleep(50 * time.Millisecond)
		}
	}()
	return ch
}

// SignalActivate 通知已有实例激活窗口。
func (g *Instance) SignalActivate() {
	if g != nil && g.activate != 0 {
		_ = windows.SetEvent(g.activate)
	}
}

// Release 释放锁（退出时调用）。
func (g *Instance) Release() {
	if g == nil {
		return
	}
	if g.owned {
		_ = windows.ReleaseMutex(g.mutex)
	}
	if g.mutex != 0 {
		_ = windows.CloseHandle(g.mutex)
	}
	if g.activate != 0 {
		_ = windows.CloseHandle(g.activate)
	}
}
