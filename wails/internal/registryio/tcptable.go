package registryio

// GetExtendedTcpTable 枚举本机 TCP 监听表（对应 .NET 的 GetActiveTcpListeners）。
// 表结构：MIB_TCPTABLE_OWNER_PID { DWORD dwNumEntries; MIB_TCPROW_OWNER_PID table[]; }
// 每行 24 字节：state, localAddr, localPort, remoteAddr, remotePort, pid（全是 DWORD）。

import (
	"encoding/binary"
	"fmt"
	"net"
	"unsafe"

	"golang.org/x/sys/windows"
)

const (
	afInet                   = 2
	tcpTableOwnerPidListener = 3 // TCP_TABLE_OWNER_PID_LISTENER
)

var (
	iphlpapi                = windows.NewLazySystemDLL("iphlpapi.dll")
	procGetExtendedTcpTable = iphlpapi.NewProc("GetExtendedTcpTable")
)

func tcpListeners() ([]IPEndPointInfo, error) {
	var size uint32
	// 第一次调用取缓冲区大小（返回 ERROR_INSUFFICIENT_BUFFER = 122）
	procGetExtendedTcpTable.Call(
		0, uintptr(unsafe.Pointer(&size)), 0,
		afInet, tcpTableOwnerPidListener, 0)
	if size == 0 {
		return nil, fmt.Errorf("GetExtendedTcpTable 未返回缓冲区大小")
	}

	buf := make([]byte, size)
	ret, _, _ := procGetExtendedTcpTable.Call(
		uintptr(unsafe.Pointer(&buf[0])), uintptr(unsafe.Pointer(&size)), 0,
		afInet, tcpTableOwnerPidListener, 0)
	if ret != 0 {
		return nil, fmt.Errorf("GetExtendedTcpTable 失败：code %d", ret)
	}

	return parseTcpTable(buf), nil
}

func parseTcpTable(buf []byte) []IPEndPointInfo {
	if len(buf) < 4 {
		return nil
	}
	count := binary.LittleEndian.Uint32(buf[0:4])
	const rowSize = 24
	need := 4 + int(count)*rowSize
	if int(count) > 1_000_000 || len(buf) < need {
		return nil
	}

	result := make([]IPEndPointInfo, 0, count)
	for i := 0; i < int(count); i++ {
		off := 4 + i*rowSize
		row := buf[off : off+rowSize]
		// localAddr 是网络字节序的 4 字节，直接当 IP 字节用
		ip := net.IP(row[4:8])
		// localPort 是"网络字节序端口"存成 DWORD（LE 读出后字节交换）
		portRaw := binary.LittleEndian.Uint16(row[8:10])
		port := (portRaw >> 8) | (portRaw << 8)
		result = append(result, IPEndPointInfo{
			Address: normalizeAddressForListener(ip),
			Port:    int(port & 0xffff),
		})
	}
	return result
}
