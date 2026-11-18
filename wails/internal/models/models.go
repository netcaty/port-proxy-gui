// Package models 定义映射条目与其补充元数据，与 C# 版 Models/ 一一对应。
package models

import (
	"fmt"
	"strings"
	"time"
)

// PortProxyEntry 一条 portproxy 映射。唯一标识是 family + protocol + 监听地址 + 监听端口，
// 因为 netsh 允许同一个监听端口在不同 family 下各有一条。
type PortProxyEntry struct {
	Family         string `json:"family"`
	Protocol       string `json:"protocol"`
	ListenAddress  string `json:"listenAddress"`
	ListenPort     int    `json:"listenPort"`
	ConnectAddress string `json:"connectAddress"`
	ConnectPort    int    `json:"connectPort"`
}

// NormalizeAddress 注册表里 0.0.0.0 / :: / 空串 都表示"所有地址"，统一归一化为 *。
func NormalizeAddress(address string) string {
	a := strings.TrimSpace(address)
	if a == "" || a == "0.0.0.0" || a == "::" || a == "[::]" {
		return "*"
	}
	return a
}

func (e PortProxyEntry) NormalizedListen() string  { return NormalizeAddress(e.ListenAddress) }
func (e PortProxyEntry) NormalizedConnect() string { return NormalizeAddress(e.ConnectAddress) }

// ListenIsV4 监听侧是否为 IPv4（决定通配符该如何展开成 netsh 参数）。
func (e PortProxyEntry) ListenIsV4() bool {
	return strings.HasPrefix(strings.ToLower(e.Family), "v4")
}

// ConnectIsV4 目标侧是否为 IPv4。family 形如 v4tov6，第 4-5 位是目标侧版本。
func (e PortProxyEntry) ConnectIsV4() bool {
	return len(e.Family) >= 6 && strings.EqualFold(e.Family[4:6], "v4")
}

// NetshListenAddress 传给 netsh 的监听地址：通配符要按 family 展开，* 未必被 netsh 接受。
func (e PortProxyEntry) NetshListenAddress() string {
	if e.NormalizedListen() == "*" {
		if e.ListenIsV4() {
			return "0.0.0.0"
		}
		return "::"
	}
	return e.NormalizedListen()
}

func (e PortProxyEntry) NetshConnectAddress() string {
	if e.NormalizedConnect() == "*" {
		if e.ConnectIsV4() {
			return "0.0.0.0"
		}
		return "::"
	}
	return e.NormalizedConnect()
}

func (e PortProxyEntry) Key() string {
	return fmt.Sprintf("%s|%s|%s|%d", e.Family, strings.ToLower(e.Protocol), e.NormalizedListen(), e.ListenPort)
}

func (e PortProxyEntry) ListenText() string {
	return fmt.Sprintf("%s:%d", e.NormalizedListen(), e.ListenPort)
}
func (e PortProxyEntry) ConnectText() string {
	return fmt.Sprintf("%s:%d", e.NormalizedConnect(), e.ConnectPort)
}

// SameTarget 两块是否指向同一目标（用于判断"覆盖"还是"相同"）。
func (e PortProxyEntry) SameTarget(other PortProxyEntry) bool {
	return strings.EqualFold(e.Key(), other.Key()) &&
		strings.EqualFold(e.NormalizedConnect(), other.NormalizedConnect()) &&
		e.ConnectPort == other.ConnectPort
}

// NetshPreview 界面上展示的等价 netsh 命令（不含执行）。
func (e PortProxyEntry) NetshPreview() string {
	return fmt.Sprintf("netsh interface portproxy add %s %s listenaddress=%s listenport=%d connectaddress=%s connectport=%d",
		e.Family, e.Protocol, e.NetshListenAddress(), e.ListenPort, e.NetshConnectAddress(), e.ConnectPort)
}

func (e PortProxyEntry) String() string {
	return fmt.Sprintf("%s/%s %s -> %s", e.Family, e.Protocol, e.ListenText(), e.ConnectText())
}

// WithConnection 返回一个替换了目标侧的副本。
func (e PortProxyEntry) WithConnection(connectAddress string, connectPort int) PortProxyEntry {
	c := e
	c.ConnectAddress = connectAddress
	c.ConnectPort = connectPort
	return c
}

// EntryMeta netsh 不支持的补充信息。按 PortProxyEntry.Key 关联，永远只用于展示，
// 绝不拼进任何命令行（安全边界）。
type EntryMeta struct {
	Name            *string    `json:"name,omitempty"`
	Group           *string    `json:"group,omitempty"`
	Note            *string    `json:"note,omitempty"`
	HasFirewallRule bool       `json:"hasFirewallRule"`
	CreatedAt       *time.Time `json:"createdAt,omitempty"`
	FollowKind      *string    `json:"followKind,omitempty"`
	FollowId        *string    `json:"followId,omitempty"`
	FollowMac       *string    `json:"followMac,omitempty"`
	FollowLabel     *string    `json:"followLabel,omitempty"`
}

// IsFollowing 是否启用了目标跟随。Kind 取 wsl / hyperv / wsb / vm。
func (m *EntryMeta) IsFollowing() bool {
	return m != nil && m.FollowKind != nil && strings.TrimSpace(*m.FollowKind) != ""
}

// IsEmpty 是否没有任何有效内容（空 meta 不必落盘）。
func (m *EntryMeta) IsEmpty() bool {
	if m == nil {
		return true
	}
	return strEmpty(m.Name) && strEmpty(m.Group) && strEmpty(m.Note) &&
		!m.HasFirewallRule && !m.IsFollowing()
}

// Clone 深拷贝（指针字段各持一份）。
func (m *EntryMeta) Clone() *EntryMeta {
	if m == nil {
		return nil
	}
	c := *m
	c.Name = cloneStr(m.Name)
	c.Group = cloneStr(m.Group)
	c.Note = cloneStr(m.Note)
	c.CreatedAt = m.CreatedAt // time.Time 值语义，共享无妨
	c.FollowKind = cloneStr(m.FollowKind)
	c.FollowId = cloneStr(m.FollowId)
	c.FollowMac = cloneStr(m.FollowMac)
	c.FollowLabel = cloneStr(m.FollowLabel)
	return &c
}

func strEmpty(s *string) bool { return s == nil || strings.TrimSpace(*s) == "" }

func cloneStr(s *string) *string {
	if s == nil {
		return nil
	}
	c := *s
	return &c
}
