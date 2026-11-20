package hints

import (
	"encoding/binary"
	"testing"
	"unsafe"
)

// rowOff 是 mibIpNetTable2 里 Table 数组的偏移（8 字节对齐）。
var offsetProbe mibIpNetTable2

const rowOff = int(unsafe.Offsetof(offsetProbe.Table))

func TestMibStructuresLayout(t *testing.T) {
	if got := unsafe.Sizeof(mibIpNetRow2{}); got != mibIpNetRow2Size {
		t.Fatalf("MIB_IPNET_ROW2 大小应为 %d，实际 %d", mibIpNetRow2Size, got)
	}
	if rowOff != mibIpNetTableAlign {
		t.Fatalf("Table 偏移应为 %d，实际 %d", mibIpNetTableAlign, rowOff)
	}
}

// 按 MIB_IPNET_ROW2 的 x64 偏移写一行。
func putNeighborRow(buf []byte, off int, ip [4]byte, family uint16, mac []byte, ifIndex uint32, state int32, macLen uint32) {
	binary.LittleEndian.PutUint16(buf[off+0:], family)
	copy(buf[off+4:off+8], ip[:])
	binary.LittleEndian.PutUint32(buf[off+28:], ifIndex)
	// LUID 在 32..40，留空
	copy(buf[off+40:off+72], mac)
	binary.LittleEndian.PutUint32(buf[off+72:], macLen)
	binary.LittleEndian.PutUint32(buf[off+76:], uint32(state))
}

func TestParseIpNetTable2(t *testing.T) {
	const rows = 5
	buf := make([]byte, rowOff+rows*mibIpNetRow2Size)
	binary.LittleEndian.PutUint32(buf[0:], rows)

	// 正常 IPv4 行
	putNeighborRow(buf, rowOff+0*mibIpNetRow2Size,
		[4]byte{192, 168, 1, 5}, afInet, []byte{0x74, 0x8F, 0x1B, 0x88, 0xEC, 0xAD}, 12, 5, 6)
	// 同接口的另一个地址，排在后面但应排在前一个之后
	putNeighborRow(buf, rowOff+1*mibIpNetRow2Size,
		[4]byte{192, 168, 1, 202}, afInet, []byte{0x00, 0x26, 0x40, 0x00, 0x0A, 0x0C}, 12, 5, 6)
	// Incomplete：无 MAC，跳过
	putNeighborRow(buf, rowOff+2*mibIpNetRow2Size,
		[4]byte{192, 168, 1, 99}, afInet, nil, 12, nlnsIncomplete, 0)
	// 非 6 字节物理地址（InfiniBand），跳过
	putNeighborRow(buf, rowOff+3*mibIpNetRow2Size,
		[4]byte{192, 168, 1, 100}, afInet, []byte{1, 2, 3, 4, 5, 6, 7, 8}, 12, 5, 8)
	// IPv6 行，跳过
	putNeighborRow(buf, rowOff+4*mibIpNetRow2Size,
		[4]byte{0, 0, 0, 0}, 23, []byte{0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF}, 12, 5, 6)

	table := (*mibIpNetTable2)(unsafe.Pointer(&buf[0]))
	got := parseIpNetTable2(table)
	if len(got) != 2 {
		t.Fatalf("应解析出 2 条邻居，实际 %d 条：%+v", len(got), got)
	}
	if got[0].Address != "192.168.1.5" || got[0].Mac != "748F1B88ECAD" || got[0].IfIndex != 12 {
		t.Fatalf("第一行解析错误：%+v", got[0])
	}
	if got[1].Address != "192.168.1.202" || got[1].Mac != "002640000A0C" {
		t.Fatalf("第二行解析错误：%+v", got[1])
	}
}

func TestParseIpNetTable2Zero(t *testing.T) {
	buf := make([]byte, rowOff)
	binary.LittleEndian.PutUint32(buf[0:], 0)
	table := (*mibIpNetTable2)(unsafe.Pointer(&buf[0]))
	if got := parseIpNetTable2(table); got != nil {
		t.Fatalf("空表应返回 nil，实际 %+v", got)
	}
}
