// Package singleinst 移植自 C# Program.cs 的单实例逻辑：
// 命名 Mutex + 激活事件——已有实例运行时，新实例激活旧窗口后退出。
// 注意 AbandonedMutexException 要当作"拿到锁"处理（旧实例崩溃留下的）。

package singleinst

import (
	"time"

	"golang.org/x/sys/windows"
)

const (
	mutexName    = `Local\PortProxyManager.SingleInstance`
	activateName = `Local\PortProxyManager.Activate`
)

// Instance 单实例守卫。
type Instance struct {
	mutex    windows.Handle
	activate windows.Handle
	owned    bool
}

// Acquire 尝试获取单实例锁。
// 返回值：(guard, isFirst, error)。isFirst=false 表示已有实例在运行，
// 调用方应 SignalActivate() 激活旧窗口后退出。
func Acquire() (*Instance, bool, error) {
	mutex, err := windows.CreateMutex(nil, false, windows.StringToUTF16Ptr(mutexName))
	if err != nil {
		return nil, false, err
	}

	wait, _ := windows.WaitForSingleObject(mutex, 0)
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
