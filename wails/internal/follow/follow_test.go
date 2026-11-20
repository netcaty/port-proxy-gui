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

func TestMatchesWSBByWsbFileName(t *testing.T) {
	// 沙盒每次启动 MAC 都会重新分配；同名 .wsb + 新 MAC 必须认回来
	newBoot := hints.AddressHint{
		Address: "172.18.63.220", Kind: str("wsb"),
		ID: str("微信沙盒.wsb"), Mac: str("00-15-5D-B1-F7-B6"), Confirmed: true,
	}
	oldTarget := &Target{Kind: "wsb", ID: str("微信沙盒.wsb"), Mac: str("00155DB1FE3A"), Label: str("172.18.55.100 - wsb:微信沙盒.wsb")}
	if !Matches(newBoot, oldTarget) {
		t.Errorf("重启后 MAC 变了，按 .wsb 文件名应认回同一沙盒")
	}

	// 其他 .wsb 文件名不误配
	if Matches(newBoot, &Target{Kind: "wsb", ID: str("别的沙盒.wsb"), Mac: str("00155DB1FE3A")}) {
		t.Errorf("不同 .wsb 文件名不应匹配")
	}

	// 早期探测没拿到文件名时，存的是通用 ID：接受任意沙盒结果
	if !Matches(newBoot, &Target{Kind: "wsb", ID: str("WindowsSandbox")}) {
		t.Errorf("通用 ID 的沙盒规则应接受任意沙盒候选")
	}
	if !Matches(newBoot, &Target{Kind: "wsb"}) {
		t.Errorf("没存 ID 的沙盒规则应接受任意沙盒候选")
	}

	// 这轮探测没拿到文件名时不能确认是同一个沙盒，宁可未检测到
	nameless := hints.AddressHint{
		Address: "172.18.63.220", Kind: str("wsb"),
		ID: str("WindowsSandbox"), Mac: str("00-15-5D-B1-F7-B6"), Confirmed: true,
	}
	if Matches(nameless, oldTarget) {
		t.Errorf("候选缺 .wsb 文件名时不应按 MAC/任意方式匹配")
	}

	// TargetEquals 回显同样按文件名认
	if !TargetEquals(&Target{Kind: "wsb", ID: str("微信沙盒.wsb"), Mac: str("00155DB1FE3A")},
		&Target{Kind: "wsb", ID: str("微信沙盒.wsb"), Mac: str("00155DB1F7B6")}) {
		t.Errorf("TargetEquals 对 wsb 应按 .wsb 文件名比较")
	}
	if TargetEquals(&Target{Kind: "wsb", ID: str("微信沙盒.wsb")},
		&Target{Kind: "wsb", ID: str("别的沙盒.wsb")}) {
		t.Errorf("TargetEquals 对不同 .wsb 文件名不应相等")
	}
}

func TestIsFollowable(t *testing.T) {
	gw := hints.AddressHint{Address: "192.168.1.1", Kind: str("gateway")}
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
