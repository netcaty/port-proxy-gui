package hints

import "testing"

func TestExtractWsbName(t *testing.T) {
	cases := []struct {
		cmdLine string
		want    string
	}{
		{`WindowsSandbox.exe "C:\Users\me\Desktop\微信沙盒.wsb"`, "微信沙盒.wsb"},
		{`WindowsSandboxRemoteSession.exe --config C:\sandbox\test.wsb`, "test.wsb"},
		{`"D:\a b\带 空格 的名字.wsb"`, "带 空格 的名字.wsb"},
		{``, ""},
		{`WindowsSandbox.exe --no-config`, ""},
	}
	for _, c := range cases {
		if got := extractWsbName(c.cmdLine); got != c.want {
			t.Errorf("extractWsbName(%q) = %q, want %q", c.cmdLine, got, c.want)
		}
	}
}

func TestNormalizeMac(t *testing.T) {
	cases := []struct {
		in   string
		want string
	}{
		{"00-15-5D-01-02-03", "00155D010203"},
		{"00:15:5d:01:02:03", "00155D010203"},
		{"00155D010203", "00155D010203"},
		{"00-15-5D-01-02", ""}, // 长度不足
		{"zz-zz-zz-zz-zz-zz", ""},
	}
	for _, c := range cases {
		got := NormalizeMac(c.in)
		if c.want == "" {
			if got != nil {
				t.Errorf("NormalizeMac(%q) = %q, want nil", c.in, *got)
			}
			continue
		}
		if got == nil || *got != c.want {
			t.Errorf("NormalizeMac(%q) = %v, want %q", c.in, got, c.want)
		}
	}
}

func TestMacVendorSandboxAnchor(t *testing.T) {
	mac := NormalizeMac("00-15-5D-AA-BB-CC")
	if mac == nil {
		t.Fatal("normalize failed")
	}
	v := MacVendor(mac)
	if v == nil || *v != "微软虚拟网卡" {
		t.Errorf("00155D OUI 应识别为微软虚拟网卡（沙盒锚点判据），got %v", v)
	}
	vmware := NormalizeMac("00-0C-29-11-22-33")
	if v := MacVendor(vmware); v == nil || *v != "VMware 虚拟网卡" {
		t.Errorf("000C29 OUI 识别错误: %v", v)
	}
}

func TestDedupFold(t *testing.T) {
	got := dedupFold([]string{"Ubuntu", "ubuntu", "沙盒", "Ubuntu"})
	if len(got) != 2 || got[0] != "Ubuntu" || got[1] != "沙盒" {
		t.Errorf("dedupFold = %v", got)
	}
}
