// Package autorun 移植自 C# Services/AutorunService.cs：
// 登录自启用计划任务（RunLevel=HighestAvailable）而不是 HKCU Run 键：
// 后者会在登录时弹 UAC。通过 /xml 注册，避免 schtasks /tr 的引号地狱。

package autorun

import (
	"encoding/xml"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"golang.org/x/sys/windows"

	"portproxymanager/internal/elevation"
	"portproxymanager/internal/netsh"
	"portproxymanager/internal/proc"
	"portproxymanager/internal/windir"
)

const taskName = "PortProxyManager"

type authResult struct {
	Ok      bool   `json:"ok"`
	Message string `json:"message"`
}

// Result 动作结果（导出供绑定）。
type Result = authResult

func schtasks() string { return windir.SystemDir() + string(os.PathSeparator) + "schtasks.exe" }

// IsEnabled 查询任务是否存在。
func IsEnabled() bool {
	res := proc.RunQuiet(schtasks(), []string{"/query", "/tn", taskName}, proc.WithTimeoutMs(5000))
	return res.Ok()
}

// Enable 注册开机自启（dataDir 通过 --data-dir 传给新实例）。
func Enable(dataDir string) Result {
	if !elevation.IsElevated() {
		return Result{Message: "注册开机自启需要管理员权限。"}
	}
	exe, err := os.Executable()
	if err != nil {
		return Result{Message: "无法确定程序路径：" + err.Error()}
	}

	xmlText := buildXML(exe, dataDir)
	temp := filepath.Join(os.TempDir(), fmt.Sprintf("ppm-task-%d.xml", os.Getpid()))
	// schtasks 要求 XML 是 UTF-16 带 BOM
	if err := os.WriteFile(temp, append([]byte{0xFF, 0xFE}, utf16LE(xmlText)...), 0o600); err != nil {
		return Result{Message: "写临时 XML 失败：" + err.Error()}
	}
	defer os.Remove(temp)

	res, err := proc.Run(schtasks(), []string{"/create", "/tn", taskName, "/xml", temp, "/f"}, proc.WithTimeoutMs(10000))
	if err != nil || !res.Ok() {
		return Result{Message: netsh.Friendly(res)}
	}
	if !IsEnabled() {
		return Result{Message: "计划任务创建命令返回成功，但查询不到该任务，请检查「任务计划程序」。"}
	}
	return Result{Ok: true, Message: "已注册开机自启（计划任务「" + taskName + "」，管理员权限，不弹 UAC）。"}
}

// Disable 取消开机自启。
func Disable() Result {
	if !elevation.IsElevated() {
		return Result{Message: "删除开机自启需要管理员权限。"}
	}
	res, err := proc.Run(schtasks(), []string{"/delete", "/tn", taskName, "/f"}, proc.WithTimeoutMs(10000))
	if (err == nil && res.Ok()) || !IsEnabled() {
		return Result{Ok: true, Message: "已取消开机自启。"}
	}
	return Result{Message: netsh.Friendly(res)}
}

func buildXML(exePath, dataDir string) string {
	user := xmlEscape(elevation.CurrentUser())
	exe := xmlEscape(exePath)
	args := xmlEscape(`--tray --no-elevate --data-dir="` + dataDir + `"`)
	return fmt.Sprintf(`<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>PortProxy 管理器：登录后以管理员权限在托盘常驻。</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>%s</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>%s</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>%s</Command>
      <Arguments>%s</Arguments>
    </Exec>
  </Actions>
</Task>`, user, user, exe, args)
}

func xmlEscape(s string) string {
	var b strings.Builder
	_ = xml.EscapeText(&b, []byte(s))
	return b.String()
}

func utf16LE(s string) []byte {
	u := windows.StringToUTF16(s)
	out := make([]byte, len(u)*2)
	for i, v := range u {
		out[2*i] = byte(v)
		out[2*i+1] = byte(v >> 8)
	}
	return out
}
