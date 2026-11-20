package hints

// 默认网关查询（GetIpForwardTable2）：取目的前缀 0.0.0.0/0 的路由行，
// NextHop 即网关；用 InterfaceIndex 关联网卡名。
// 纯 API（毫秒级，与 C# 的 GatewayAddresses 同量级），不解析任何本地化文本。

import (
	"net"
	"unsafe"

	"golang.org/x/sys/windows"
)

// queryGatewaysFast 纯 API 查默认网关。失败返回 nil（调用方回退 PowerShell 版）。
func queryGatewaysFast() []Gateway {
	var table *windows.MibIpForwardTable2
	if err := windows.GetIpForwardTable2(afInet, &table); err != nil || table == nil {
		return nil
	}
	defer windows.FreeMibTable(unsafe.Pointer(table))

	if table.NumEntries == 0 || table.NumEntries > 100000 {
		return nil
	}
	rows := unsafe.Slice(&table.Table[0], int(table.NumEntries))

	// ifIndex → 网卡名（用于 label）
	byIndex := map[uint32]string{}
	if ifaces, err := net.Interfaces(); err == nil {
		for _, nic := range ifaces {
			byIndex[uint32(nic.Index)] = nic.Name
		}
	}
	return gatewaysFromRows(rows, byIndex)
}

// gatewaysFromRows 从路由表行里挑出 IPv4 默认路由的网关。
func gatewaysFromRows(rows []windows.MibIpForwardRow2, byIndex map[uint32]string) []Gateway {
	var out []Gateway
	for i := range rows {
		row := &rows[i]
		if row.DestinationPrefix.PrefixLength != 0 { // 只看默认路由
			continue
		}
		if row.NextHop.Family != windows.AF_INET { // IPv6 默认路由留给 IPv6 场景
			continue
		}
		addr := (*windows.RawSockaddrInet4)(unsafe.Pointer(&row.NextHop)).Addr[:]
		ip := net.IP(addr).To4()
		if ip == nil || IsUseless(ip) { // 跳过 0.0.0.0（on-link 默认路由）
			continue
		}
		label := ""
		if name, ok := byIndex[row.InterfaceIndex]; ok {
			label = cleanAdapterName(name, "")
		}
		out = append(out, Gateway{Address: ip.String(), Label: label})
	}
	return out
}
