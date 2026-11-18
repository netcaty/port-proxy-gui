// Package hints 移植自 C# Services/NetworkHintService.cs：
// 收集可作为 portproxy 目标的候选地址，并尽量说明"这个 IP 是谁"。
//
// 下拉只列默认网关、运行中的虚拟机和邻居设备；本机网卡 / 虚拟网卡地址不展示。
// 沙盒是否在运行只由进程 + 可见窗口确认；IP 统一走 ARP + Hyper-V 启发式；
// 同一沙盒残留多个 IP 时用 ICMP 可达性只保留最近一个。
// 注意：C# 版里基于 wsb.exe CLI 的链路（list/ip、包内路径解析、提权令牌注入）
// 在本重写中按需求整体去掉，不移植。
package hints

// AddressHint 下拉里的一条地址建议。
type AddressHint struct {
	Address   string  `json:"address"`
	Category  string  `json:"category"`
	Source    *string `json:"source"`
	Detail    *string `json:"detail"`
	Mac       *string `json:"mac"`
	Kind      *string `json:"kind"`
	ID        *string `json:"id"`
	Confirmed bool    `json:"confirmed"`
}

// Display 下拉里显示的文本，统一为 "ip - type:id"。
// type 取 wsl / wsb / vm / hyperv；网关和普通局域网设备用 gw / lan。
func (h AddressHint) Display() string {
	kind := "lan"
	if notBlank(h.Kind) {
		kind = *h.Kind
	}
	id := ""
	if notBlank(h.ID) {
		id = *h.ID
	} else if h.Source != nil {
		id = *h.Source
	} else {
		id = h.Category
	}
	return h.Address + " - " + kind + ":" + id
}

func (h AddressHint) With(changes func(*AddressHint)) AddressHint {
	c := h
	changes(&c)
	return c
}

// GuestKinds 这些 kind 必须被对应探测确认运行中才允许展示。
var GuestKinds = map[string]bool{"wsl": true, "wsb": true, "vm": true, "hyperv": true}

// FilterUnconfirmedGuests 网关和普通局域网设备走 ARP/DNS，可以直接展示；
// wsl / wsb / vm / hyperv 必须由对应探测确认正在运行后才允许展示，
// 避免把虚拟交换机上的残留邻居当成可代理目标。
func FilterUnconfirmedGuests(hints []AddressHint) []AddressHint {
	out := make([]AddressHint, 0, len(hints))
	for _, h := range hints {
		if h.Confirmed || h.Kind == nil || !GuestKinds[*h.Kind] {
			out = append(out, h)
		}
	}
	return out
}

var _ = notBlank
