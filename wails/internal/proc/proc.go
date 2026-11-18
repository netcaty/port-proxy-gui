// Package proc 移植自 C# Services/ProcRunner.cs：
// 统一的子进程执行封装。默认按系统 ANSI 代码页解码，中文不乱码；
// 个别工具（PowerShell / wsl.exe）输出 UTF-8 或 UTF-16，可显式指定编码。
package proc

import (
	"errors"
	"fmt"
	"os/exec"
	"path/filepath"
	"strings"
	"time"

	"golang.org/x/sys/windows"

	"portproxymanager/internal/logging"
	"portproxymanager/internal/winansi"
	"portproxymanager/internal/windir"
)

// TextEncoding 子进程输出文本的编码（与 C# ProcTextEncoding 对应）。
type TextEncoding int

const (
	// Ansi 系统 ANSI 代码页，适合 netsh / arp 等 Win32 工具。
	Ansi TextEncoding = iota
	// Utf8 适合已显式设置输出编码的 PowerShell。
	Utf8
	// Utf16 适合 wsl.exe 自己的列表类输出（UTF-16LE 无 BOM）。
	Utf16
)

// Result 子进程执行结果（与 C# ProcResult 对应）。
type Result struct {
	ExitCode int    `json:"exitCode"`
	StdOut   string `json:"stdOut"`
	StdErr   string `json:"stdErr"`
}

func (r Result) Ok() bool { return r.ExitCode == 0 }

// Combined 非空的 stdout/stderr 合并（与 C# Combined 语义一致）。
func (r Result) Combined() string {
	var parts []string
	for _, s := range []string{r.StdOut, r.StdErr} {
		t := strings.TrimSpace(s)
		if t != "" {
			parts = append(parts, t)
		}
	}
	return strings.Join(parts, "\r\n")
}

const defaultTimeout = 30 * time.Second

// Run 同步执行外部命令，超时杀掉整个进程树并返回 TimeoutError。
func Run(fileName string, args []string, opts ...RunOption) (Result, error) {
	cfg := runConfig{timeout: defaultTimeout, encoding: Ansi}
	for _, o := range opts {
		o(&cfg)
	}

	cmd := exec.Command(fileName, args...)
	// 不弹黑窗（CREATE_NO_WINDOW），与 C# CreateNoWindow + Hidden 一致
	cmd.SysProcAttr = &windows.SysProcAttr{HideWindow: true, CreationFlags: 0x08000000}

	var outBuf, errBuf bytesAccumulator
	cmd.Stdout = &outBuf
	cmd.Stderr = &errBuf

	if err := cmd.Start(); err != nil {
		return Result{ExitCode: -1, StdErr: err.Error()}, err
	}

	done := make(chan error, 1)
	go func() { done <- cmd.Wait() }()

	var waitErr error
	timer := time.NewTimer(cfg.timeout)
	defer timer.Stop()

	select {
	case waitErr = <-done:
	case <-timer.C:
		killTree(cmd.Process.Pid)
		<-done // 回收僵尸
		return Result{ExitCode: -1}, timeoutError{
			name: filepath.Base(fileName), timeout: cfg.timeout,
		}
	}

	out := outBuf.bytes()
	errOut := errBuf.bytes()
	return Result{
		ExitCode: exitCodeOf(cmd, waitErr),
		StdOut:   decode(out, cfg.encoding),
		StdErr:   decode(errOut, cfg.encoding),
	}, nil
}

// RunQuiet 只读探测用：不记录日志，任何失败都折算成 Result 而不是 error。
func RunQuiet(fileName string, args []string, opts ...RunOption) Result {
	res, err := Run(fileName, args, opts...)
	if err != nil {
		var te timeoutError
		if errors.As(err, &te) {
			return Result{ExitCode: -1, StdErr: te.Error()}
		}
		return Result{ExitCode: -1, StdErr: err.Error()}
	}
	return res
}

// RunLogged 执行并按成败写操作日志（与 C# NetshRunner.Run 的日志行为对应）。
func RunLogged(display string, fileName string, args []string, opts ...RunOption) (Result, error) {
	res, err := Run(fileName, args, opts...)
	log := logging.Current()
	if err != nil {
		if log != nil {
			log.Error(fmt.Sprintf("%s 执行失败：%v", display, err))
		}
		return res, err
	}
	if log != nil {
		if res.Ok() {
			log.Info(fmt.Sprintf("%s -> exit 0", display))
		} else {
			log.Error(fmt.Sprintf("%s -> exit %d\r\n%s", display, res.ExitCode, res.Combined()))
		}
	}
	return res, nil
}

type runConfig struct {
	timeout  time.Duration
	encoding TextEncoding
}

// RunOption 可选参数。
type RunOption func(*runConfig)

// WithTimeout 覆盖默认 30 秒超时。
func WithTimeout(d time.Duration) RunOption { return func(c *runConfig) { c.timeout = d } }

// WithTimeoutMs 以毫秒覆盖默认 30 秒超时（便于照搬 C# 的 ProbeTimeouts 常量）。
func WithTimeoutMs(ms int) RunOption { return WithTimeout(time.Duration(ms) * time.Millisecond) }

// WithEncoding 指定输出编码。
func WithEncoding(e TextEncoding) RunOption { return func(c *runConfig) { c.encoding = e } }

// TimeoutError 超时（与 C# TimeoutException 对应）。
type timeoutError struct {
	name    string
	timeout time.Duration
}

func (e timeoutError) Error() string {
	return fmt.Sprintf("%s 在 %s 内没有结束。", e.name, e.timeout)
}

type bytesAccumulator struct{ b []byte }

func (a *bytesAccumulator) Write(p []byte) (int, error) {
	a.b = append(a.b, p...)
	return len(p), nil
}

func (a *bytesAccumulator) bytes() []byte { return a.b }

func exitCodeOf(cmd *exec.Cmd, waitErr error) int {
	if cmd.ProcessState == nil {
		return -1
	}
	return cmd.ProcessState.ExitCode()
}

func decode(b []byte, e TextEncoding) string {
	if len(b) == 0 {
		return ""
	}
	switch e {
	case Utf8:
		return string(b)
	case Utf16:
		start := 0
		if len(b) >= 2 && b[0] == 0xFF && b[1] == 0xFE {
			start = 2
		}
		return trimNull(utf16Decode(b[start:]))
	default:
		return winansi.Decode(b)
	}
}

func trimNull(s string) string {
	return strings.Trim(s, "\x00\uFEFF")
}

// killTree 杀掉整个进程树（对应 C# Kill(entireProcessTree: true)）。
// taskkill /T /F 是最可靠的方式；失败再退回单进程 Kill。
func killTree(pid int) {
	tk := filepath.Join(windir.SystemDir(), "taskkill.exe")
	_ = exec.Command(tk, "/PID", fmt.Sprint(pid), "/T", "/F").Start()
}

// utf16Decode UTF-16LE 字节切片 → 字符串。
func utf16Decode(b []byte) string {
	if len(b) < 2 {
		return ""
	}
	if len(b)%2 != 0 {
		b = b[:len(b)-1]
	}
	u := make([]uint16, len(b)/2)
	for i := range u {
		u[i] = uint16(b[2*i]) | uint16(b[2*i+1])<<8
	}
	return windows.UTF16ToString(u)
}
