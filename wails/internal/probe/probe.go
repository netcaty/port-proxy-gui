// Package probe 移植自 C# Services/PortProbe.cs：
// 测试目标地址是否真的能连上（诊断和编辑对话框的"测试"按钮用）。
package probe

import (
	"fmt"
	"net"
	"syscall"
	"time"
)

// TestConnect TCP 连接测试，返回 (ok, detail)。
func TestConnect(host string, port int, timeout time.Duration) (bool, string) {
	if host == "" {
		return false, "目标地址为空。"
	}
	if host == "*" {
		host = "127.0.0.1"
	}

	addr := net.JoinHostPort(host, fmt.Sprint(port))
	conn, err := net.DialTimeout("tcp", addr, timeout)
	if err == nil {
		_ = conn.Close()
		return true, "连接成功，目标可达。"
	}

	if ne, ok := err.(net.Error); ok && ne.Timeout() {
		return false, fmt.Sprintf("连接超时（超过 %d ms）。", timeout.Milliseconds())
	}
	if oe, ok := err.(*net.OpError); ok {
		if errno, ok := oe.Err.(syscall.Errno); ok {
			switch errno {
			case syscall.ECONNREFUSED:
				return false, "连接被拒绝：目标端口上没有程序在监听。"
			case syscall.ENETUNREACH, syscall.EHOSTUNREACH:
				return false, "网络不可达。"
			}
		}
	}
	if _, ok := err.(*net.DNSError); ok {
		return false, "地址无法解析。"
	}
	return false, "连接失败：" + err.Error()
}
