package netwatch

import (
	"sync/atomic"
	"testing"
	"time"
)

// 连续多次网络变化只应触发一次同步；静默期结束后再来变化应能再次触发。
func TestRunCoalescesBursts(t *testing.T) {
	changed := make(chan struct{}, 1)
	quit := make(chan struct{})
	done := make(chan struct{})
	var calls int32

	go func() {
		run(changed, quit, 40*time.Millisecond, func() {
			atomic.AddInt32(&calls, 1)
		})
		close(done)
	}()

	// 模拟一次网络切换引发的连串通知（短于防抖窗口）。
	for i := 0; i < 5; i++ {
		changed <- struct{}{}
		time.Sleep(5 * time.Millisecond)
	}
	time.Sleep(150 * time.Millisecond)
	if got := atomic.LoadInt32(&calls); got != 1 {
		t.Fatalf("一次突发应只触发 1 次，实际 %d 次", got)
	}

	// 静默期结束后再来一次，应再次触发。
	changed <- struct{}{}
	time.Sleep(120 * time.Millisecond)
	if got := atomic.LoadInt32(&calls); got != 2 {
		t.Fatalf("第二次变化应触发第 2 次，实际 %d 次", got)
	}

	close(quit)
	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("退出信号后 run 未返回")
	}
}

// 防抖等待期间收到退出信号应立即返回。
func TestWaitQuietExits(t *testing.T) {
	changed := make(chan struct{}, 1)
	quit := make(chan struct{})
	result := make(chan bool, 1)

	go func() { result <- waitQuiet(changed, quit, time.Hour) }()
	close(quit)

	select {
	case ok := <-result:
		if ok {
			t.Fatal("退出信号后应返回 false")
		}
	case <-time.After(2 * time.Second):
		t.Fatal("waitQuiet 未响应退出信号")
	}
}

// 用 initialNotification=true 强制系统立即回调一次，验证注册与回调 ABI 可用。
func TestInitialNotification(t *testing.T) {
	var calls int32
	w := New(func() { atomic.AddInt32(&calls, 1) })
	w.debounce = 20 * time.Millisecond

	if err := w.start(true); err != nil {
		t.Skipf("当前环境不支持 NotifyIpInterfaceChange：%v", err)
	}
	defer w.Stop()

	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) && atomic.LoadInt32(&calls) == 0 {
		time.Sleep(20 * time.Millisecond)
	}
	if atomic.LoadInt32(&calls) == 0 {
		t.Fatal("initialNotification 未触发回调")
	}
}
