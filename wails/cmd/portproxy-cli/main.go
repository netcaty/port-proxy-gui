// portproxymanager-cli：console 子系统的 CLI 伴生程序。
//
// 为什么需要它：主程序 portproxymanager.exe 是 windowsgui 子系统（GUI 不弹黑窗），
// 交互式 cmd 启动 GUI 子系统程序时【不等待】退出——提示符立即返回，
// 探测结果 1-2 秒后才打到屏幕上。这个伴生 exe 是 console 子系统，
// cmd 会正常等待，CLI 体验与常规命令行工具一致。
//
// 构建在 wails/ 目录：go build -o bin/portproxymanager-cli.exe ./cmd/portproxy-cli
package main

import (
	"fmt"
	"os"

	"portproxymanager/internal/cli"
)

func main() {
	opts := cli.Parse(os.Args[1:])

	// console 子系统下继承的 stdout 就是控制台本身；
	// AttachParentConsole 在重定向场景不动作，保持管道/文件行为一致
	cli.AttachParentConsole()

	if opts.Help {
		fmt.Print(cli.HelpText)
		return
	}
	if opts.Dump {
		os.Exit(cli.Dump(opts))
	}
	if opts.Hints {
		os.Exit(cli.Hints(opts))
	}
	if opts.Sync {
		os.Exit(cli.Sync(opts))
	}
	if opts.InstallSync {
		os.Exit(cli.InstallSync(opts))
	}
	if opts.RemoveSync {
		os.Exit(cli.RemoveSync())
	}

	// console 子系统的伴生程序不承载 GUI（双击会带黑窗），
	// 没有 CLI 命令时给出指引用 exit 2 区分
	fmt.Print(cli.HelpText)
	fmt.Println("\n未指定 CLI 命令（--dump / --hints / --sync / --install-sync / --remove-sync）。图形界面请使用同目录的 portproxymanager.exe。")
	os.Exit(2)
}
