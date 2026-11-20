package hints

import (
	"testing"
	"unsafe"

	"golang.org/x/sys/windows"
)

func setNextHop(row *windows.MibIpForwardRow2, ip [4]byte) {
	row.NextHop.Family = windows.AF_INET
	(*windows.RawSockaddrInet4)(unsafe.Pointer(&row.NextHop)).Addr = ip
}

func TestGatewaysFromRows(t *testing.T) {
	byIndex := map[uint32]string{12: "WLAN", 7: "vEthernet (Default Switch)"}

	rows := make([]windows.MibIpForwardRow2, 3)
	// 默认路由 → 应保留
	rows[0].DestinationPrefix.PrefixLength = 0
	rows[0].InterfaceIndex = 12
	setNextHop(&rows[0], [4]byte{192, 168, 1, 1})
	// 非默认路由 → 跳过
	rows[1].DestinationPrefix.PrefixLength = 24
	rows[1].InterfaceIndex = 12
	setNextHop(&rows[1], [4]byte{192, 168, 1, 1})
	// IPv6 默认路由 → 跳过
	rows[2].DestinationPrefix.PrefixLength = 0
	rows[2].InterfaceIndex = 7
	rows[2].NextHop.Family = windows.AF_INET6

	got := gatewaysFromRows(rows, byIndex)
	if len(got) != 1 {
		t.Fatalf("应只保留 1 个 IPv4 默认网关，实际 %+v", got)
	}
	if got[0].Address != "192.168.1.1" || got[0].Label != "WLAN" {
		t.Fatalf("网关解析错误：%+v", got[0])
	}
}

func TestGatewaysFromRowsSkipsOnLink(t *testing.T) {
	rows := make([]windows.MibIpForwardRow2, 1)
	rows[0].DestinationPrefix.PrefixLength = 0
	rows[0].InterfaceIndex = 7
	setNextHop(&rows[0], [4]byte{0, 0, 0, 0})

	if got := gatewaysFromRows(rows, map[uint32]string{7: "x"}); len(got) != 0 {
		t.Fatalf("0.0.0.0 的 on-link 默认路由不应作为网关，实际 %+v", got)
	}
}
