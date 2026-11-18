package hints

// 默认网关查询（纯 API 版，替代 PowerShell Get-NetIPConfiguration）：
// GetIpForwardTable 取目的地址 0.0.0.0/0 的默认路由行，
// 网关 IP 在 dwForwardGateway（网络字节序），经 ifIndex 关联网卡名。
// C# 版 GatewayAddresses 是纯内存 API，这里保持同一量级（毫秒级）。

import (
	"encoding/binary"
	"fmt"
	"net"
	"unsafe"

	"golang.org/x/sys/windows"
)

const (
	afInetForward          = 2 // AF_INET
	mibIPForwardRowSize    = 56
	ipForwardMetRICDoneovo = 0
)

var procGetIpForwardTable = windows.NewLazySystemDLL("iphlpapi.dll").NewProc("GetIpForwardTable")

// queryGatewaysFast 纯 API 查默认网关。失败返回 nil（调用方回退 PowerShell 版）。
func queryGatewaysFast() []Gateway {
	var size uint32
	// 第一次调用取缓冲区大小（ERROR_INSUFFICIENT_BUFFER = 122）
	procGetIpForwardTable.Call(0, uintptr(unsafe.Pointer(&size)), 0)
	if size == 0 || size > 1<<20 {
		return nil
	}

	buf := make([]byte, size)
	ret, _, _ := procGetIpForwardTable.Call(
		uintptr(unsafe.Pointer(&buf[0])), uintptr(unsafe.Pointer(&size)), 0)
	if ret != 0 {
		return nil
	}

	if len(buf) < 4 {
		return nil
	}
	count := binary.LittleEndian.Uint32(buf[0:4])
	need := 4 + int(count)*mibIPForwardRowSize
	if int(count) > 100000 || len(buf) < need {
		return nil
	}

	// ifIndex → 网卡名（用于 label）
	byIndex := map[int]string{}
	ifaces, err := net.Interfaces()
	if err == nil {
		for _, nic := range ifaces {
			byIndex[nic.Index] = nic.Name
		}
	}

	var out []Gateway
	for i := 0; i < int(count); i++ {
		row := buf[4+i*mibIPForwardRowSize : 4+(i+1)*mibIPForwardRowSize]
		dest := binary.LittleEndian.Uint32(row[0:4])
		mask := binary.LittleEndian.Uint32(row[4:8])
		gw := binary.LittleEndian.Uint32(row[12:16])
		ifIndex := int(binary.LittleEndian.Uint32(row[16:20]))
		// 默认路由：目的 0.0.0.0、掩码 0.0.0.0
		if dest != 0 || mask != 0 || gw == 0 {
			continue
		}
		gwIP := net.IPv4(byte(gw), byte(gw>>8), byte(gw>>16), byte(gw>>24))
		if gwIP == nil || gwIP.To4() == nil || IsUseless(gwIP) {
			continue
		}
		label := ""
		if name, ok := byIndex[ifIndex]; ok {
			label = cleanAdapterName(name, "")
		}
		out = append(out, Gateway{Address: gwIP.String(), Label: label})
	}
	return out
}

var _ = fmt.Sprint
