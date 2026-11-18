package hints

// VMware / VirtualBox 虚拟机名称识别（对应 C# ProbeVmNamesAsync）：
// 用各自 CLI 把运行中虚拟机的 MAC 映射回名称。工具不存在时保留 MAC 兜底。

import (
	"os"
	"path/filepath"
	"regexp"
	"strings"

	"portproxymanager/internal/proc"
)

var (
	vboxRowRe = regexp.MustCompile(`^\s*"([^"]+)"\s+\{([^}]+)\}\s*$`)
	vmxMacRe  = regexp.MustCompile(`(?i)ethernet\d+\.generatedmacaddress\s*=\s*"?([\w:-]+)"?`)
)

// ProbeVMNames 把候选里 kind=vm 且带 MAC 的地址，用 CLI 识别出的名称补上 ID。
func ProbeVMNames(baseHints []AddressHint, timeoutMs int) []AddressHint {
	candidates := []AddressHint{}
	for _, h := range baseHints {
		if h.Kind != nil && *h.Kind == "vm" && h.Mac != nil {
			candidates = append(candidates, h)
		}
	}
	if len(candidates) == 0 {
		return []AddressHint{}
	}

	byMAC := map[string]string{}
	needVBox, needVMware := false, false
	for _, h := range candidates {
		if strings.Contains(strings.ToLower(h.Category), "virtualbox") {
			needVBox = true
		}
		if strings.Contains(strings.ToLower(h.Category), "vmware") {
			needVMware = true
		}
	}
	if needVBox {
		for mac, name := range probeVirtualBoxNames(timeoutMs) {
			byMAC[mac] = name
		}
	}
	if needVMware {
		for mac, name := range probeVMwareNames(timeoutMs) {
			byMAC[mac] = name
		}
	}

	out := []AddressHint{}
	for _, h := range candidates {
		if h.Mac == nil {
			continue
		}
		n := NormalizeMacPtr(h.Mac)
		if n == nil {
			continue
		}
		if name, ok := byMAC[strings.ToLower(*n)]; ok {
			h.Kind = strPtr("vm")
			h.ID = strPtr(name)
			h.Confirmed = true
			out = append(out, h)
		}
	}
	return out
}

func probeVirtualBoxNames(timeoutMs int) map[string]string {
	names := map[string]string{}
	exe := findTool("VBoxManage.exe",
		filepath.Join(programFiles(), "Oracle", "VirtualBox", "VBoxManage.exe"),
		filepath.Join(programFilesX86(), "Oracle", "VirtualBox", "VBoxManage.exe"))
	if exe == "" {
		return names
	}
	list := proc.RunQuiet(exe, []string{"list", "runningvms"}, proc.WithTimeoutMs(minInt(timeoutMs, VBoxListMS)))
	if !list.Ok() {
		return names
	}
	count := 0
	for _, line := range strings.Split(list.StdOut, "\n") {
		m := vboxRowRe.FindStringSubmatch(strings.TrimSpace(line))
		if m == nil || count >= 8 {
			continue
		}
		count++
		name, uuid := m[1], m[2]
		info := proc.RunQuiet(exe, []string{"showvminfo", uuid, "--machinereadable"},
			proc.WithTimeoutMs(minInt(timeoutMs, VBoxInfoMS)))
		if !info.Ok() {
			continue
		}
		vmName, macs := parseVBoxMachineInfo(info.StdOut)
		if vmName == "" {
			vmName = name
		}
		for _, mac := range macs {
			names[mac] = vmName
		}
	}
	return names
}

func probeVMwareNames(timeoutMs int) map[string]string {
	names := map[string]string{}
	exe := findTool("vmrun.exe",
		filepath.Join(programFiles(), "VMware", "VMware Workstation", "vmrun.exe"),
		filepath.Join(programFilesX86(), "VMware", "VMware Workstation", "vmrun.exe"),
		filepath.Join(programFiles(), "VMware", "VMware Player", "vmrun.exe"),
		filepath.Join(programFilesX86(), "VMware", "VMware Player", "vmrun.exe"))
	if exe == "" {
		return names
	}
	list := proc.RunQuiet(exe, []string{"list"}, proc.WithTimeoutMs(minInt(timeoutMs, VMwareListMS)))
	if !list.Ok() {
		return names
	}
	count := 0
	for _, line := range strings.Split(list.StdOut, "\n") {
		vmx := strings.TrimSpace(strings.Trim(line, `"`))
		if !strings.HasSuffix(strings.ToLower(vmx), ".vmx") || count >= 8 {
			continue
		}
		count++
		name, macs := readVMXInfo(vmx)
		if name == "" {
			name = strings.TrimSuffix(filepath.Base(vmx), filepath.Ext(vmx))
		}
		for _, mac := range macs {
			names[mac] = name
		}
	}
	return names
}

func parseVBoxMachineInfo(text string) (name string, macs []string) {
	for _, line := range strings.Split(text, "\n") {
		line = strings.TrimSpace(line)
		i := strings.IndexByte(line, '=')
		if i <= 0 {
			continue
		}
		key := strings.ToLower(strings.TrimSpace(line[:i]))
		value := strings.Trim(strings.TrimSpace(line[i+1:]), `"`)
		switch {
		case key == "name":
			if value != "" {
				name = value
			}
		case strings.HasPrefix(key, "macaddress"):
			if mac := NormalizeMac(value); mac != nil {
				macs = append(macs, strings.ToLower(*mac))
			}
		}
	}
	return
}

func readVMXInfo(vmxPath string) (name string, macs []string) {
	data, err := os.ReadFile(vmxPath)
	if err != nil {
		return
	}
	for _, line := range strings.Split(string(data), "\n") {
		line = strings.TrimSpace(line)
		i := strings.IndexByte(line, '=')
		if i <= 0 {
			continue
		}
		key := strings.ToLower(strings.TrimSpace(line[:i]))
		value := strings.Trim(strings.TrimSpace(line[i+1:]), `"`)
		if key == "displayname" || key == "name" {
			if value != "" {
				name = value
			}
		}
	}
	for _, m := range vmxMacRe.FindAllStringSubmatch(string(data), -1) {
		if mac := NormalizeMac(m[1]); mac != nil {
			macs = append(macs, strings.ToLower(*mac))
		}
	}
	return
}

func findTool(fileName string, candidates ...string) string {
	for _, c := range candidates {
		if c != "" {
			if _, err := os.Stat(c); err == nil {
				return c
			}
		}
	}
	path := os.Getenv("PATH")
	for _, dir := range filepath.SplitList(path) {
		c := filepath.Join(dir, fileName)
		if _, err := os.Stat(c); err == nil {
			return c
		}
	}
	return ""
}

func programFiles() string {
	if p := os.Getenv("ProgramFiles"); p != "" {
		return p
	}
	return `C:\Program Files`
}

func programFilesX86() string {
	if p := os.Getenv("ProgramFiles(x86)"); p != "" {
		return p
	}
	return `C:\Program Files (x86)`
}
