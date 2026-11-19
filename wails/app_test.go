package main

import (
	"net"
	"strings"
	"testing"
)

func TestTestTargetValidation(t *testing.T) {
	svc := &AppService{}

	if r := svc.TestTarget("  ", 80); r.Ok || !strings.Contains(r.Message, "目标地址") {
		t.Errorf("空地址应提示填写目标地址，got %+v", r)
	}
	if r := svc.TestTarget("127.0.0.1", 0); r.Ok || !strings.Contains(r.Message, "1–65535") {
		t.Errorf("非法端口应被拦截，got %+v", r)
	}
	if r := svc.TestTarget("127.0.0.1", 70000); r.Ok || !strings.Contains(r.Message, "1–65535") {
		t.Errorf("超范围端口应被拦截，got %+v", r)
	}
}

func TestTestTargetConnect(t *testing.T) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer ln.Close()
	go func() {
		for {
			c, err := ln.Accept()
			if err != nil {
				return
			}
			_ = c.Close()
		}
	}()

	port := ln.Addr().(*net.TCPAddr).Port
	r := (&AppService{}).TestTarget("127.0.0.1", port)
	if !r.Ok {
		t.Errorf("本机监听端口应可达，got %+v", r)
	}
}
