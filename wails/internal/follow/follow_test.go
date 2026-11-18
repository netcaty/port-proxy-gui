package follow

import (
	"testing"

	"portproxymanager/internal/hints"
)

func str(s string) *string { return &s }

func TestMatchesVMByMacEvenIfRenamed(t *testing.T) {
	h := hints.AddressHint{
		Address: "192.168.1.50", Category: "Hyper-V 虚拟机", Kind: str("vm"),
		ID: str("新名字"), Mac: str("00-15-5D-AA-BB-CC"), Confirmed: true,
	}
	target := &Target{Kind: "vm", ID: str("旧名字"), Mac: str("00155DAABBCC")}
	if !Matches(h, target) {
		t.Errorf("两边都有 MAC 时应按 MAC 匹配，改名字不影响")
	}

	// MAC 不一致时绝不能按名字误配
	targetSameNameDifferentMac := &Target{Kind: "vm", ID: str("新名字"), Mac: str("00-0C-29-11-22-33")}
	if Matches(h, targetSameNameDifferentMac) {
		t.Errorf("MAC 不一致但名字相同时不应匹配（防止误配到别的虚拟机）")
	}
}

func TestMatchesWSLByDistroName(t *testing.T) {
	h := hints.AddressHint{
		Address: "172.28.128.1", Kind: str("wsl"), ID: str("Ubuntu-22.04"), Confirmed: true,
	}
	if !Matches(h, &Target{Kind: "wsl", ID: str("ubuntu-22.04")}) {
		t.Errorf("WSL 按发行版名忽略大小写匹配")
	}
	// 只有 MAC 的 ARP 邻居不能当成 WSL 发行版
	hMAC := hints.AddressHint{Address: "172.28.128.2", Kind: str("wsl"), ID: str("00-15-5D-01-02-03")}
	if Matches(hMAC, &Target{Kind: "wsl", ID: str("00-15-5D-01-02-03")}) {
		t.Errorf("不能把 MAC 当发行版名匹配 WSL")
	}
	// 早期探测的通用 "WSL" id 接受任意 WSL 结果
	if !Matches(h, &Target{Kind: "wsl", ID: str("WSL")}) {
		t.Errorf("通用 WSL 规则应接受任意 WSL 候选")
	}
}

func TestIsFollowable(t *testing.T) {
	gw := hints.AddressHint{Address: "192.168.1.1", Kind: str("gw")}
	if IsFollowable(gw) {
		t.Errorf("网关不可跟随")
	}
	vmNoID := hints.AddressHint{Address: "192.168.1.2", Kind: str("vm")}
	if IsFollowable(vmNoID) {
		t.Errorf("没有 MAC 也没有 ID 的 vm 不可跟随")
	}
	vmOK := hints.AddressHint{Address: "192.168.1.3", Kind: str("vm"), Mac: str("00155DAABBCC")}
	if !IsFollowable(vmOK) {
		t.Errorf("有 MAC 的 vm 可跟随")
	}
}

func TestTargetDisplay(t *testing.T) {
	if got := (&Target{Kind: "wsl", ID: str("Ubuntu")}).Display(); got != "WSL · Ubuntu" {
		t.Errorf("Display = %q", got)
	}
	if got := (&Target{Kind: "wsb", ID: str("微信沙盒.wsb")}).Display(); got != "沙盒 · 微信沙盒.wsb" {
		t.Errorf("Display = %q", got)
	}
	if got := (&Target{Kind: "vm", Mac: str("00155DAABBCC")}).Display(); got != "虚拟机 · MAC BBCC" {
		t.Errorf("Display = %q", got)
	}
}
