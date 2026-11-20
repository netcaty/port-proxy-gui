// Package netsh 移植自 C# Services/NetshRunner.cs + PortProxyService.cs。
// netsh 是所有映射写入的唯一通道；退出码 0 不代表真的生效，
// 调用方必须再读注册表核对（见 Service.Add/VerifyPresent）。
package netsh

import (
	"fmt"
	"path/filepath"
	"strings"
	"time"

	"portproxymanager/internal/proc"
	"portproxymanager/internal/windir"
)

// NetshPath System32 下的 netsh.exe。
func NetshPath() string {
	return filepath.Join(windir.SystemDir(), "netsh.exe")
}

// Run 执行 netsh 并记录操作日志。
// netsh 的报错文本是本地化的（中文系统为 ANSI 代码页），编码必须显式指定。
func Run(args []string) (proc.Result, error) {
	display := "netsh " + strings.Join(quoteForDisplayAll(args), " ")
	res, err := proc.RunLogged(display, NetshPath(), args, proc.WithEncoding(proc.Ansi))
	if err != nil {
		return proc.Result{ExitCode: -1, StdErr: err.Error()}, fmt.Errorf("无法执行 netsh：%v", err)
	}
	return res, nil
}

// RunQuiet 只读查询：不记录错误日志。用于"规则不存在"这类正常结果，
// 避免探测行为污染操作日志。
func RunQuiet(args []string, timeoutMs int) proc.Result {
	opts := []proc.RunOption{proc.WithEncoding(proc.Ansi)}
	if timeoutMs > 0 {
		opts = append(opts, proc.WithTimeout(time.Duration(timeoutMs)*time.Millisecond))
	}
	res, err := proc.Run(NetshPath(), args, opts...)
	if err != nil {
		return proc.Result{ExitCode: -1, StdErr: err.Error()}
	}
	return res
}

func quoteForDisplay(arg string) string {
	if strings.ContainsAny(arg, " \t") {
		return `"` + arg + `"`
	}
	return arg
}

func quoteForDisplayAll(args []string) []string {
	out := make([]string, len(args))
	for i, a := range args {
		out[i] = quoteForDisplay(a)
	}
	return out
}

// Friendly 把 netsh 的原始报错翻成用户能看懂的话，并保留原文方便排查。
// netsh 消息随系统语言变化，所以中英关键词都匹配。
func Friendly(result proc.Result) string {
	raw := strings.TrimSpace(result.Combined())

	if raw == "" {
		return fmt.Sprintf("netsh 执行失败（退出码 %d），没有返回任何信息。", result.ExitCode)
	}

	var hint string
	switch {
	case containsAny(raw, "已存在", "already exists", "对象已存在", "object already exists"):
		hint = "该监听地址和端口已经有一条映射了。"
	case containsAny(raw, "拒绝访问", "Access is denied", "请求的操作需要提升", "requires elevation"):
		hint = "权限不足，需要以管理员身份运行。"
	case containsAny(raw, "另一个程序", "in use", "被占用", "占用"):
		hint = "该端口正被其他程序占用。"
	case containsAny(raw, "参数", "parameter", "invalid", "不正确"):
		hint = "参数无效，请检查地址与端口。"
	case containsAny(raw, "没有与指定", "not found", "找不到", "不存在"):
		hint = "找不到指定的映射（可能已被其他程序删除）。"
	}

	if hint == "" {
		return raw
	}
	return hint + "\r\n（原始信息：" + raw + "）"
}

func containsAny(text string, keywords ...string) bool {
	lower := strings.ToLower(text)
	for _, k := range keywords {
		if strings.Contains(lower, strings.ToLower(k)) {
			return true
		}
	}
	return false
}
