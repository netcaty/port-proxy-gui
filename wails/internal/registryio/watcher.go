// Package registryio 移植自 C# Services/RegistryWatcher.cs：
// 用 RegNotifyChangeKeyValue 监听 PortProxy 键，别人在 cmd 里改映射时界面自动刷新，
// 不需要定时轮询。键暂时不存在时退化为 2 秒一次的打开重试。
package registryio

import (
	"runtime"
	"time"

	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"

	"portproxymanager/internal/logging"
)

const (
	regNotifyChangeName    = 0x1
	regNotifyChangeLastSet = 0x4
)

// Watcher 注册表变更监听器。Changed 在后台 goroutine 触发，接收方自行调度。
type Watcher struct {
	stopEvent windows.Handle
	quit      chan struct{}
	onChanged func()
	started   bool
}

// NewWatcher 创建监听器。onChanged 在后台 goroutine 里被调用。
func NewWatcher(onChanged func()) *Watcher {
	// 自动复位事件（对应 C# AutoResetEvent）
	stop, _ := windows.CreateEvent(nil, 0, 0, nil)
	return &Watcher{stopEvent: stop, quit: make(chan struct{}), onChanged: onChanged}
}

// Start 启动后台监听 goroutine。
func (w *Watcher) Start() {
	if w.started {
		return
	}
	w.started = true
	go w.loop()
}

// Stop 停止监听。
func (w *Watcher) Stop() {
	select {
	case <-w.quit:
		return
	default:
	}
	close(w.quit)
	if w.stopEvent != 0 {
		_ = windows.SetEvent(w.stopEvent)
	}
}

func (w *Watcher) loop() {
	runtime.LockOSThread()
	stopCh := make(chan struct{})
	go func() {
		<-w.quit
		close(stopCh)
	}()

	for {
		select {
		case <-stopCh:
			return
		default:
		}

		// 需要 ReadKey + Notify 权限；registry.READ 含 KEY_NOTIFY
		key, err := registry.OpenKey(registry.LOCAL_MACHINE, RootPath, registry.READ)
		if err != nil {
			// 键还不存在（从未配置过）。睡一会儿再试，顺便相当于低频轮询。
			if sleepOrQuit(stopCh, 2*time.Second) {
				return
			}
			continue
		}

		changed, _ := windows.CreateEvent(nil, 0, 0, nil)
		if changed == 0 {
			key.Close()
			if sleepOrQuit(stopCh, time.Second) {
				return
			}
			continue
		}

		rc, _, _ := procRegNotifyChangeKeyValue.Call(
			uintptr(key),
			1, // watchSubtree
			uintptr(regNotifyChangeName|regNotifyChangeLastSet),
			uintptr(changed),
			1, // asynchronous
		)

		if rc != 0 {
			windows.CloseHandle(changed)
			key.Close()
			if sleepOrQuit(stopCh, time.Second) {
				return
			}
			continue
		}

		// 等变更事件 / 停止信号 / 5 秒超时（超时只是重新订阅）
		res := waitTwo(changed, w.stopEvent, 5*time.Second)
		windows.CloseHandle(changed)
		key.Close()

		if res == errStop {
			return
		}
		if res == errTimeout {
			continue
		}

		// 一次 netsh 写操作可能触发多次通知，合并一下
		if sleepOrQuit(stopCh, 250*time.Millisecond) {
			return
		}

		if w.onChanged != nil {
			callChanged(w.onChanged)
		}
	}
}

// callChanged 回调异常不能拖垮监听线程。
func callChanged(fn func()) {
	defer func() {
		if r := recover(); r != nil {
			if log := logging.Current(); log != nil {
				log.Error("注册表变更回调异常")
			}
		}
	}()
	fn()
}

type waitResult int

const (
	errStop    waitResult = waitResult(1)
	errTimeout waitResult = waitResult(2)
	errChanged waitResult = waitResult(0)
)

// waitTwo 等两个事件之一或超时（对应 C# WaitHandle.WaitAny）。
// WaitForMultipleObjects 成功返回 WAIT_OBJECT_0 + index（WAIT_OBJECT_0 = 0x00000000），
// WAIT_TIMEOUT = 0x00000102。
func waitTwo(a, b windows.Handle, timeout time.Duration) waitResult {
	const (
		waitObject0  = 0x00000000
		waitTimeoutV = 0x00000102
	)
	event, _ := windows.WaitForMultipleObjects([]windows.Handle{a, b}, false, uint32(timeout.Milliseconds()))
	switch event {
	case waitObject0:
		return errChanged
	case waitObject0 + 1:
		return errStop
	case waitTimeoutV:
		return errTimeout
	}
	return errTimeout
}

// sleepOrQuit 睡 d；期间收到退出信号返回 true。
func sleepOrQuit(stopCh <-chan struct{}, d time.Duration) bool {
	select {
	case <-stopCh:
		return true
	case <-time.After(d):
		return false
	}
}

var procRegNotifyChangeKeyValue = func() *windows.LazyProc {
	advapi32 := windows.NewLazySystemDLL("advapi32.dll")
	return advapi32.NewProc("RegNotifyChangeKeyValue")
}()
