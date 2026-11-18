package registryio

import "testing"

func TestTryParsePair(t *testing.T) {
	cases := []struct {
		in     string
		addr   string
		port   int
		wantOK bool
	}{
		{"0.0.0.0/8080", "0.0.0.0", 8080, true},
		{"127.0.0.1/80", "127.0.0.1", 80, true},
		{"*/1234", "*", 1234, true},                       // 遗留脏数据：字面量 *
		{"::/443", "::", 443, true},                       // IPv6 监听
		{"fe80::1/9000", "fe80::1", 9000, true},           // IPv6 地址里没有斜杠，按最后一个斜杠切
		{"fe80::1%eth0/9000", "fe80::1%eth0", 9000, true}, // scope id
		{"", "", 0, false},                                // 空串
		{"nonsense", "", 0, false},                        // 没有斜杠
		{"/8080", "", 0, false},                           // 地址为空
		{"0.0.0.0/", "", 0, false},                        // 端口为空
		{"0.0.0.0/0", "", 0, false},                       // 端口 0 非法
		{"0.0.0.0/65536", "", 0, false},                   // 端口越界
		{"0.0.0.0/x", "", 0, false},                       // 端口非数字
	}
	for _, c := range cases {
		addr, port, ok := TryParsePair(c.in)
		if ok != c.wantOK {
			t.Errorf("TryParsePair(%q) ok=%v, want %v", c.in, ok, c.wantOK)
			continue
		}
		if ok && (addr != c.addr || port != c.port) {
			t.Errorf("TryParsePair(%q) = (%q,%d), want (%q,%d)", c.in, addr, port, c.addr, c.port)
		}
	}
}
