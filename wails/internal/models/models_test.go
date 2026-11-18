package models

import "testing"

func TestNormalizeAddress(t *testing.T) {
	for _, in := range []string{"", " ", "0.0.0.0", "::", "[::]", "*"} {
		if got := NormalizeAddress(in); got != "*" {
			t.Errorf("NormalizeAddress(%q) = %q, want *", in, got)
		}
	}
	if got := NormalizeAddress("127.0.0.1"); got != "127.0.0.1" {
		t.Errorf("NormalizeAddress(127.0.0.1) = %q", got)
	}
}

func TestEntryKeyAndPreview(t *testing.T) {
	e := PortProxyEntry{Family: "v4tov4", Protocol: "tcp", ListenAddress: "*", ListenPort: 8080,
		ConnectAddress: "192.168.1.5", ConnectPort: 80}
	if e.Key() != "v4tov4|tcp|*|8080" {
		t.Errorf("Key = %q", e.Key())
	}
	if e.NetshListenAddress() != "0.0.0.0" {
		t.Errorf("NetshListenAddress = %q, want 0.0.0.0（通配符必须展开）", e.NetshListenAddress())
	}
	want := "netsh interface portproxy add v4tov4 tcp listenaddress=0.0.0.0 listenport=8080 connectaddress=192.168.1.5 connectport=80"
	if e.NetshPreview() != want {
		t.Errorf("NetshPreview =\n%q\nwant\n%q", e.NetshPreview(), want)
	}
}

func TestFamilyV6Expansion(t *testing.T) {
	e := PortProxyEntry{Family: "v6tov4", Protocol: "tcp", ListenAddress: "::", ListenPort: 8080,
		ConnectAddress: "10.0.0.2", ConnectPort: 80}
	if e.NetshListenAddress() != "::" {
		t.Errorf("v6 监听通配应展开为 ::，got %q", e.NetshListenAddress())
	}
	if !e.ListenIsV4() == false {
		t.Errorf("v6tov4 监听侧应为 IPv6")
	}
}

func TestSameTarget(t *testing.T) {
	a := PortProxyEntry{Family: "v4tov4", Protocol: "tcp", ListenAddress: "0.0.0.0", ListenPort: 80,
		ConnectAddress: "10.0.0.1", ConnectPort: 8080}
	b := PortProxyEntry{Family: "v4tov4", Protocol: "tcp", ListenAddress: "*", ListenPort: 80,
		ConnectAddress: "10.0.0.1", ConnectPort: 8080}
	if !a.SameTarget(b) {
		t.Errorf("0.0.0.0 与 * 应视为同一监听")
	}
	c := b
	c.ConnectPort = 8081
	if a.SameTarget(c) {
		t.Errorf("目标端口不同不应视为同一目标")
	}
}
