// Package netwatch 监听本机 IP 接口变化，对应 C# 的
// NetworkChange.NetworkAddressChanged + 3 秒防抖。
//
// 用 NotifyIpInterfaceChange（iphlpapi）而不是轮询：接口 up/down、地址增删、
// DHCP 续租都会回调；回调里只做非阻塞信号，合并成一次 onChange。
package netwatch

import (
	"sync"
	"time"

	"golang.org/x/sys/windows"
)

// Debounce 与 C# ProbeTimeouts.NetworkDebounceMs 保持一致（3 秒）。
const Debounce = 3 * time.Second

// Watcher 网络变化监听器。
type Watcher struct {
	onChange func()
	debounce time.Duration
	handle   windows.Handle
	callback uintptr
	changed  chan struct{}
	quit     chan struct{}
	done     chan struct{}
	started  bool
	stopOnce sync.Once
}

// New 创建监听器。onChange 在后台 goroutine 串行执行。
func New(onChange func()) *Watcher {
	if onChange == nil {
		onChange = func() {}
	}
	return &Watcher{
		onChange: onChange,
		debounce: Debounce,
		changed:  make(chan struct{}, 1),
		quit:     make(chan struct{}),
		done:     make(chan struct{}),
	}
}

// Start 注册通知并启动防抖循环。返回错误表示系统不支持或注册失败，
// 调用方应退化为原来的定时同步（不影响程序其余功能）。
func (w *Watcher) Start() error { return w.start(false) }

// start 的 initialNotification 仅供测试用来强制触发一次回调，验证回调 ABI。
func (w *Watcher) start(initialNotification bool) error {
	// 回调签名：IpInterfaceChangeCallback(context, row, notificationType)。
	// 只投递一个合并信号，绝不阻塞系统通知线程。
	w.callback = windows.NewCallback(func(_, _, _ uintptr) uintptr {
		select {
		case w.changed <- struct{}{}:
		default:
		}
		return 0
	})

	// AF_UNSPEC=0：IPv4/IPv6 接口变化都通知。
	// 生产路径 initialNotification=false：启动时不立刻触发（启动同步已有单独路径）。
	var handle windows.Handle
	if err := windows.NotifyIpInterfaceChange(0, w.callback, nil, initialNotification, &handle); err != nil {
		return err
	}
	w.handle = handle

	w.started = true
	go func() {
		defer close(w.done)
		run(w.changed, w.quit, w.debounce, w.onChange)
	}()
	return nil
}

// Stop 取消订阅并等待循环退出。可重复调用。
func (w *Watcher) Stop() {
	w.stopOnce.Do(func() {
		if w.handle != 0 {
			// Cancel 会等待正在执行的回调返回；回调只做非阻塞发送，不会卡住。
			_ = windows.CancelMibChangeNotify2(w.handle)
			w.handle = 0
		}
		close(w.quit)
		if w.started {
			<-w.done
		}
	})
}

// run 防抖循环：收到变化后等一段静默期再触发；期间继续变化则重新计时。
func run(changed, quit <-chan struct{}, debounce time.Duration, onChange func()) {
	for {
		select {
		case <-quit:
			return
		case <-changed:
		}
		if !waitQuiet(changed, quit, debounce) {
			return
		}
		onChange()
	}
}

// waitQuiet 等待 debounce 内没有新的变化。收到退出信号返回 false。
func waitQuiet(changed, quit <-chan struct{}, debounce time.Duration) bool {
	timer := time.NewTimer(debounce)
	defer timer.Stop()
	for {
		select {
		case <-quit:
			return false
		case <-changed:
			if !timer.Stop() {
				select {
				case <-timer.C:
				default:
				}
			}
			timer.Reset(debounce)
		case <-timer.C:
			return true
		}
	}
}
