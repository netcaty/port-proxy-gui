package hints

// IPv4 邻居表（ARP）读取：GetIpNetTable2 替代 arp.exe -a。
// 直接拿结构化的 IP/MAC/接口索引，不解析本地化文本、不受控制台代码页影响，
// 也没有 arp 输出按接口分段时的猜测逻辑。

import (
	"encoding/binary"
	"net"
	"sort"
	"unsafe"

	"golang.org/x/sys/windows"
)

const (
	afInet             = 2
	nlnsIncomplete     = 1 // NL_NEIGHBOR_STATE 里的 Incomplete
	maxNeighborCount   = 30
	mibIpNetRow2Size   = 88 // x64 下 sizeof(MIB_IPNET_ROW2)
	mibIpNetTableAlign = 8  // Table 数组因含 uint64 而 8 字节对齐
)

var (
	iphlpapiNeighbors  = windows.NewLazySystemDLL("iphlpapi.dll")
	procGetIpNetTable2 = iphlpapiNeighbors.NewProc("GetIpNetTable2")
)

// mibIpNetRow2 对应 MIB_IPNET_ROW2（x64 布局，测试里断言大小）。
type mibIpNetRow2 struct {
	Address               [28]byte // SOCKADDR_INET：IPv4 时 family@0、addr@4
	InterfaceIndex        uint32
	InterfaceLuid         uint64
	PhysicalAddress       [32]byte
	PhysicalAddressLength uint32
	State                 int32
	Flags                 uint8
	_                     [3]byte
	ReachabilityTime      uint32
}

// mibIpNetTable2 对应 MIB_IPNET_TABLE2：NumEntries 后按 8 字节对齐接行数组。
type mibIpNetTable2 struct {
	NumEntries uint32
	_          [mibIpNetTableAlign - 4]byte
	Table      [1]mibIpNetRow2
}

type neighbor struct {
	Address string
	Mac     string // 12 位大写十六进制，可直接喂 NormalizeMac
	IfIndex uint32
}

// ipv4Neighbors 读取本机 IPv4 邻居表。失败返回空列表（调用方只丢候选，不报错）。
func ipv4Neighbors() []neighbor {
	var table *mibIpNetTable2
	ret, _, _ := procGetIpNetTable2.Call(
		uintptr(afInet), uintptr(unsafe.Pointer(&table)))
	if ret != 0 || table == nil {
		return nil
	}
	defer windows.FreeMibTable(unsafe.Pointer(table))
	return parseIpNetTable2(table)
}

// parseIpNetTable2 解析表数据；结构布局与预期不符时返回空列表，宁可少候选也不误报。
func parseIpNetTable2(table *mibIpNetTable2) []neighbor {
	if table == nil || unsafe.Sizeof(mibIpNetRow2{}) != mibIpNetRow2Size {
		return nil
	}
	count := int(table.NumEntries)
	if count <= 0 || count > 65536 {
		return nil
	}

	rows := unsafe.Slice(&table.Table[0], count)
	out := make([]neighbor, 0, count)
	for i := range rows {
		row := &rows[i]
		if binary.LittleEndian.Uint16(row.Address[0:2]) != afInet {
			continue
		}
		// 未完成解析的行没有可用 MAC；非 6 字节地址（InfiniBand 等）与旧版 arp 行为一致地跳过。
		if row.State == nlnsIncomplete || row.PhysicalAddressLength != 6 {
			continue
		}
		ip := net.IP(row.Address[4:8]).To4()
		if ip == nil || IsUseless(ip) {
			continue
		}
		out = append(out, neighbor{
			Address: ip.String(),
			Mac:     formatMac6(row.PhysicalAddress[:6]),
			IfIndex: row.InterfaceIndex,
		})
	}

	// arp -a 按接口、地址排序；这里保持同样稳定的顺序，候选展示不抖动。
	sort.SliceStable(out, func(i, j int) bool {
		if out[i].IfIndex != out[j].IfIndex {
			return out[i].IfIndex < out[j].IfIndex
		}
		a := net.ParseIP(out[i].Address).To4()
		b := net.ParseIP(out[j].Address).To4()
		return string(a) < string(b)
	})
	return out
}

func formatMac6(b []byte) string {
	const hex = "0123456789ABCDEF"
	out := make([]byte, 12)
	for i := 0; i < 6; i++ {
		out[2*i] = hex[b[i]>>4]
		out[2*i+1] = hex[b[i]&0x0F]
	}
	return string(out)
}
