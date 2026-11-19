// Package paths 移植自 C# Services/Paths.cs：
// 数据目录解析。优先级：--data-dir → exe 同目录 → %ProgramData%。
// 显式指定失败时直接报错，绝不静默改道。
package paths

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"golang.org/x/sys/windows"
)

// Source 数据目录的来源。
type Source int

const (
	SourceCommandLine  Source = iota // --data-dir 显式指定
	SourceExeDirectory               // exe 同目录（默认，绿色便携）
	SourceProgramData                // 回退到 ProgramData
)

// Context 解析结果（与 C# PathContext 对应）。
type Context struct {
	DataDir        string
	Source         Source
	FallbackReason string

	MetaFile       string
	SettingsFile   string
	LogDir         string
	BackupDir      string
	SelfTestReport string
}

func newContext(dir string, src Source, fallbackReason string) *Context {
	return &Context{
		DataDir:        dir,
		Source:         src,
		FallbackReason: fallbackReason,
		MetaFile:       filepath.Join(dir, "meta.json"),
		SettingsFile:   filepath.Join(dir, "settings.json"),
		LogDir:         filepath.Join(dir, "logs"),
		BackupDir:      filepath.Join(dir, "backups"),
		SelfTestReport: filepath.Join(dir, "selftest-report.json"),
	}
}

// SourceEnum 英文枚举名（与 C# Source.ToString() 一致，供 --dump --json 兼容）。
func (c *Context) SourceEnum() string {
	switch c.Source {
	case SourceCommandLine:
		return "CommandLine"
	case SourceExeDirectory:
		return "ExeDirectory"
	case SourceProgramData:
		return "ProgramData"
	}
	return fmt.Sprintf("Source(%d)", int(c.Source))
}

func (c *Context) SourceLabel() string {
	switch c.Source {
	case SourceCommandLine:
		return "--data-dir 指定"
	case SourceExeDirectory:
		return "exe 同目录"
	case SourceProgramData:
		return "ProgramData（已降级）"
	}
	return fmt.Sprintf("Source(%d)", int(c.Source))
}

// EnsureLayout 建齐子目录。
func (c *Context) EnsureLayout() error {
	for _, d := range []string{c.DataDir, c.LogDir, c.BackupDir} {
		if err := os.MkdirAll(d, 0o755); err != nil {
			return err
		}
	}
	return nil
}

// Resolve 解析数据目录。失败时返回可直接展示给用户的中文原因。
func Resolve(requested string) (*Context, error) {
	if strings.TrimSpace(requested) != "" {
		// 相对路径按 exe 目录解析，避免依赖启动时的工作目录。
		// 绝对路径必须原样使用：Go 的 filepath.Join 不会像 C# Path.GetFullPath
		// 那样在第二个参数为绝对路径时丢弃前缀（计划任务传的就是绝对路径）。
		full, err := requestedPath(exeDir(), requested)
		if err != nil {
			return nil, fmt.Errorf("数据目录路径无效：%s\n%v", requested, err)
		}
		if reason := TryPrepare(full); reason != "" {
			return nil, fmt.Errorf("指定的数据目录不可用：%s\n原因：%s\n（显式指定的路径不会自动降级到其他位置，请检查权限或换一个路径）", full, reason)
		}
		return newContext(full, SourceCommandLine, ""), nil
	}

	exeReason := ""
	exeDirPath := exeDir()
	if isSystemProtectedPath(exeDirPath) {
		exeReason = "exe 位于系统保护目录（Program Files / Windows），不适合存放用户数据"
	} else if reason := TryPrepare(exeDirPath); reason == "" {
		return newContext(exeDirPath, SourceExeDirectory, ""), nil
	} else {
		exeReason = reason
	}

	programData := filepath.Join(commonData(), "PortProxyManager")
	if reason := TryPrepare(programData); reason == "" {
		return newContext(programData, SourceProgramData, exeReason), nil
	} else {
		return nil, fmt.Errorf("无法确定可用的数据目录。\n· exe 同目录：%s\n· ProgramData：%s\n请用 --data-dir 指定一个可写目录。", exeReason, reason)
	}
}

// ResolveForRead 只读场景（--dump）用：只算出路径，不建目录、不写探测文件。
func ResolveForRead(requested string) (*Context, error) {
	if strings.TrimSpace(requested) != "" {
		full, err := requestedPath(exeDir(), requested)
		if err != nil {
			return nil, fmt.Errorf("数据目录路径无效：%s（%v）", requested, err)
		}
		return newContext(full, SourceCommandLine, ""), nil
	}

	exeDirPath := exeDir()
	if !isSystemProtectedPath(exeDirPath) {
		return newContext(exeDirPath, SourceExeDirectory, ""), nil
	}
	return newContext(filepath.Join(commonData(), "PortProxyManager"), SourceProgramData, "exe 位于系统保护目录"), nil
}

// TryPrepare 建目录并实测可写（建临时文件再删），不靠 ACL 推断。
// 返回空串表示成功，否则返回中文原因。
func TryPrepare(dir string) string {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return mkdirReason(err)
	}
	probe := filepath.Join(dir, fmt.Sprintf(".write-test-%d.tmp", os.Getpid()))
	f, err := os.OpenFile(probe, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o644)
	if err != nil {
		return writeReason(err)
	}
	_, werr := f.WriteString("0")
	_ = f.Close()
	_ = os.Remove(probe)
	if werr != nil {
		return writeReason(werr)
	}
	return ""
}

func mkdirReason(err error) string {
	if os.IsPermission(err) {
		return "没有写入权限"
	}
	return "目录不存在且无法创建：" + err.Error()
}

func writeReason(err error) string {
	if os.IsPermission(err) {
		return "没有写入权限"
	}
	return "写入测试失败：" + err.Error()
}

func exeDir() string {
	exe, err := os.Executable()
	if err != nil {
		return "."
	}
	return filepath.Dir(exe)
}

// requestedPath 解析 --data-dir：绝对路径原样使用，相对路径基于 base。
// 去掉命令行/注册表里常见的包裹引号。
func requestedPath(base, requested string) (string, error) {
	p := strings.Trim(strings.TrimSpace(requested), `"`)
	if filepath.IsAbs(p) {
		return filepath.Abs(p)
	}
	return filepath.Abs(filepath.Join(base, p))
}

// commonData 取 %ProgramData%（CSIDL_COMMON_APPDATA）。
func commonData() string {
	p, err := windows.KnownFolderPath(windows.FOLDERID_ProgramData, 0)
	if err != nil {
		return `C:\ProgramData`
	}
	return p
}

// isSystemProtectedPath exe 位于 Program Files / Windows 下时不把数据放那儿。
func isSystemProtectedPath(dir string) bool {
	full := strings.TrimRight(strings.ToLower(dir), `\`)
	for _, env := range []string{"ProgramFiles", "ProgramFiles(x86)", "SystemRoot"} {
		root := strings.TrimRight(strings.ToLower(os.Getenv(env)), `\`)
		if root == "" {
			continue
		}
		if full == root || strings.HasPrefix(full, root+`\`) {
			return true
		}
	}
	// SystemRoot 没设时兜底检查 Windows 目录名（极罕见）
	if _, err := os.Stat(`C:\Windows`); err == nil {
		if full == `c:\windows` || strings.HasPrefix(full, `c:\windows\`) {
			return true
		}
	}
	return false
}
