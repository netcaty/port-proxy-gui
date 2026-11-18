package hints

import (
	"net"
	"strings"
)

// NormalizeMac 规范化 MAC 为 12 位大写十六进制；非法返回 nil。
func NormalizeMac(mac string) *string {
	if strings.TrimSpace(mac) == "" {
		return nil
	}
	var sb strings.Builder
	for _, c := range mac {
		if (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') {
			sb.WriteRune(c)
		}
	}
	hex := strings.ToUpper(sb.String())
	if len(hex) != 12 {
		return nil
	}
	return &hex
}

// FormatMacId 把规范化 MAC 显示成 00-15-5D-01-02-03，作为 id 兜底。
func FormatMacId(mac *string) string {
	hex := mac
	if hex == nil {
		n := NormalizeMac("")
		hex = n
	}
	if hex == nil {
		return "unknown"
	}
	parts := make([]string, 6)
	for i := 0; i < 6; i++ {
		parts[i] = (*hex)[i*2 : i*2+2]
	}
	return strings.Join(parts, "-")
}

// MacVendor 按 OUI 前 6 位给出厂商说明（与 C# 版厂商表一致）。
func MacVendor(mac *string) *string {
	hex := NormalizeMacPtr(mac)
	if hex == nil || len(*hex) < 6 {
		return nil
	}
	var vendor string
	switch (*hex)[:6] {
	case "00155D":
		vendor = "微软虚拟网卡"
	case "005056", "000C29", "000569", "001C14":
		vendor = "VMware 虚拟网卡"
	case "080027", "0A0027":
		vendor = "VirtualBox 虚拟网卡"
	case "525400":
		vendor = "QEMU/KVM 虚拟网卡"
	case "00163E":
		vendor = "Xen 虚拟网卡"
	case "0003FF":
		vendor = "Hyper-V/虚拟 PC 网卡"
	case "001C42":
		vendor = "Parallels 虚拟网卡"
	default:
		return nil
	}
	return &vendor
}

// NormalizeMacPtr 对已是字符串的 MAC 再做一次规范化（兼容不同来源）。
func NormalizeMacPtr(mac *string) *string {
	if mac == nil {
		return nil
	}
	return NormalizeMac(*mac)
}

// SameMac 两个 MAC 是否相同（都规范化后比较）。
func SameMac(a, b *string) bool {
	left, right := NormalizeMacPtr(a), NormalizeMacPtr(b)
	return left != nil && right != nil && *left == *right
}

// IsUseless 广播 / 组播 / 链路本地 / 未指定地址不展示。
func IsUseless(ip net.IP) bool {
	if ip == nil || ip.To4() == nil {
		return true // 这里只收集 IPv4
	}
	if ip.IsUnspecified() || ip.IsLoopback() || ip.IsMulticast() || ip.IsLinkLocalUnicast() {
		return true
	}
	b := ip.To4()
	if b[0] >= 224 {
		return true
	}
	if b[0] == 169 && b[1] == 254 {
		return true
	}
	if b[0] == 0 || b[0] == 255 {
		return true
	}
	return false
}

// InSubnet 判断 address 是否落在 networkAddress/prefixLength 子网内。
func InSubnet(address, networkAddress string, prefixLength int) bool {
	a := net.ParseIP(address)
	n := net.ParseIP(networkAddress)
	if a == nil || n == nil {
		return false
	}
	a4, n4 := a.To4(), n.To4()
	if a4 == nil || n4 == nil {
		return false
	}
	if prefixLength < 0 {
		prefixLength = 0
	}
	if prefixLength > 32 {
		prefixLength = 32
	}
	full := prefixLength / 8
	for i := 0; i < full; i++ {
		if a4[i] != n4[i] {
			return false
		}
	}
	rem := prefixLength % 8
	if rem == 0 {
		return true
	}
	mask := byte(0xff << (8 - rem))
	return (a4[full] & mask) == (n4[full] & mask)
}

func notBlank(s *string) bool {
	return s != nil && strings.TrimSpace(*s) != ""
}

func strPtr(s string) *string { return &s }
