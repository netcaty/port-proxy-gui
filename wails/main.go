package main

// main 是应用入口。启动顺序：
// CLI 解析 →（--dump 纯只读输出后退出）→ 数据目录 → 日志 → meta/settings
// → 单实例互斥 → 需要时 UAC 自提权重启 → 窗口 + 托盘 → 注册表监听 + 跟随定时器。

import (
	"embed"
	"fmt"
	"os"
	"os/exec"

	"github.com/wailsapp/wails/v3/pkg/application"
	"github.com/wailsapp/wails/v3/pkg/events"

	"portproxymanager/internal/autorun"
	"portproxymanager/internal/cli"
	"portproxymanager/internal/elevation"
	"portproxymanager/internal/elevationx"
	"portproxymanager/internal/logging"
	"portproxymanager/internal/meta"
	"portproxymanager/internal/paths"
	"portproxymanager/internal/settings"
	"portproxymanager/internal/singleinst"
)

//go:embed all:frontend/dist
var assets embed.FS

//go:embed build/windows/icon.ico
var trayIconData []byte

func main() {
	opts := cli.Parse(os.Args[1:])

	// CLI 只读模式：先挂父控制台（windowsgui 子系统默认不绑定 cmd 的控制台）
	if opts.Help || opts.Dump || opts.Hints {
		cli.AttachParentConsole()
	}

	if opts.Help {
		fmt.Print(cli.HelpText)
		return
	}

	// --dump / --hints 纯只读：不建目录、不提权、不启动 GUI
	if opts.Dump {
		os.Exit(cli.Dump(opts))
	}
	if opts.Hints {
		os.Exit(cli.Hints(opts))
	}

	pathsCtx, err := paths.Resolve(opts.DataDir)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	if err := pathsCtx.EnsureLayout(); err != nil {
		fmt.Fprintln(os.Stderr, "数据目录不可用：", err)
		os.Exit(1)
	}

	if _, err := logging.Init(pathsCtx.LogDir); err != nil {
		// 日志不可写不影响主流程
		fmt.Fprintln(os.Stderr, "日志目录不可用：", err)
	}

	settingsStore := settings.Load(pathsCtx.SettingsFile)
	metaStore, err := meta.Load(pathsCtx.MetaFile)
	if err != nil {
		fmt.Fprintln(os.Stderr, "meta 加载失败：", err)
		os.Exit(1)
	}

	// 单实例：已有实例在跑时激活它并退出。
	// 硬约束：互斥初始化真失败（句柄无效）时必须退出，绝不能多实例并行写数据。
	guard, isFirst, err := singleinst.Acquire()
	if err != nil {
		fmt.Fprintln(os.Stderr, "单实例互斥初始化失败：", err)
		os.Exit(1)
	}
	if !isFirst {
		// Acquire 内部已向旧实例发出激活信号
		guard.Release()
		return
	}
	defer guard.Release()

	// 提权策略（对应 C#）：非提权 + 未禁用提权 → UAC 自提权重启。
	// UAC 取消或提权失败则继续以只读模式运行。
	if !elevation.IsElevated() && !opts.NoElevate {
		started, rerr := elevationx.RelaunchElevated()
		if started {
			return
		}
		if rerr != nil {
			if log := logging.Current(); log != nil {
				log.Warn("自提权未成功，继续以只读模式运行：" + rerr.Error())
			}
		}
	}

	appService := NewAppService(pathsCtx, metaStore, settingsStore)

	app := application.New(application.Options{
		Name:        AppName,
		Description: "Windows TCP 端口转发（netsh portproxy）管理工具",
		Services: []application.Service{
			application.NewService(appService),
		},
		Assets: application.AssetOptions{
			Handler: application.AssetFileServerFS(assets),
		},
	})
	appService.SetApp(app)

	s := settingsStore.Value
	opts0 := application.WebviewWindowOptions{
		Title:     AppName,
		Width:     clamp(s.WindowWidth, 900, 100000),
		Height:    clamp(s.WindowHeight, 560, 100000),
		MinWidth:  900,
		MinHeight: 560,
		URL:       "/",
	}
	if s.Maximized {
		opts0.StartState = application.WindowStateMaximised
	}
	win := app.Window.NewWithOptions(opts0)

	// 托盘（对应 C# NotifyIcon + 右键菜单）
	setupSystemTray(app, win, appService, settingsStore, pathsCtx)

	// 关窗进托盘（settings.CloseToTray），托盘菜单"退出"才真正退出。
	// 必须用 RegisterHook + Cancel（对照官方 hide-window 示例）：
	// hook 先于事件监听器执行，Cancel 阻止 Wails 内部的关闭监听器（它会
	// unconditionallyClose + impl.close，OnWindowEvent 阶段窗口已经救不回来）。
	closeToTray := func() bool { return settingsStore.Value.CloseToTray }
	win.RegisterHook(events.Common.WindowClosing, func(ev *application.WindowEvent) {
		if closeToTray() {
			win.Hide()
			ev.Cancel()
			return
		}
		saveWindowBounds(win, settingsStore)
		appService.Stop()
	})

	appService.StartWatcher()

	// 已有实例被再次启动时（--dump/--hints 除外）：静默激活主窗口，
	// 不弹任何对话框。Focus 把窗口真正带到前台（仅 Show 时 Windows 可能
	// 只让任务栏闪烁而不切换）。
	if guard != nil {
		activateCh := guard.ActivateCh()
		go func() {
			for range activateCh {
				win.Show()
				win.UnMinimise()
				win.Focus()
			}
		}()
	}

	if opts.Tray {
		win.Hide()
	}

	if err := app.Run(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

// setupSystemTray 创建托盘图标与菜单。
func setupSystemTray(app *application.App, win application.Window, svc *AppService,
	settingsStore *settings.Store, pathsCtx *paths.Context) {
	tray := app.SystemTray.New()
	tray.SetIcon(trayIconData)
	tray.SetTooltip("PortProxyManager — netsh portproxy 管理器")

	menu := application.NewMenu()
	menu.Add("打开 PortProxyManager").OnClick(func(*application.Context) {
		win.Show()
		win.UnMinimise()
	})
	menu.Add("同步跟随目标").OnClick(func(*application.Context) {
		go svc.SyncFollow(false)
	})
	menu.Add("打开数据目录").OnClick(func(*application.Context) {
		// explorer 打开数据目录（路径来自固定解析逻辑，无自由文本）
		_ = exec.Command("explorer.exe", pathsCtx.DataDir).Start()
	})
	var autorunItem *application.MenuItem
	autorunItem = menu.AddCheckbox("开机自启", autorun.IsEnabled())
	autorunItem.OnClick(func(*application.Context) {
		var r autorun.Result
		if autorun.IsEnabled() {
			r = autorun.Disable()
		} else {
			r = autorun.Enable(pathsCtx.DataDir)
		}
		autorunItem.SetChecked(autorun.IsEnabled())
		if !r.Ok && r.Message != "" {
			// 失败信息写日志（托盘无气泡 API 的替代提示）
			if log := logging.Current(); log != nil {
				log.Warn("开机自启设置失败：" + r.Message)
			}
		}
	})
	var closeToTrayItem *application.MenuItem
	closeToTrayItem = menu.AddCheckbox("关窗进托盘", settingsStore.Value.CloseToTray)
	closeToTrayItem.OnClick(func(*application.Context) {
		sv := settingsStore.Value
		sv.CloseToTray = !sv.CloseToTray
		settingsStore.Value = sv
		_ = settingsStore.Save()
		closeToTrayItem.SetChecked(sv.CloseToTray)
	})
	menu.AddSeparator()
	menu.Add("退出").OnClick(func(*application.Context) {
		saveWindowBounds(win, settingsStore)
		svc.Stop()
		app.Quit()
	})
	tray.SetMenu(menu)

	tray.OnClick(func() {
		win.Show()
		win.UnMinimise()
	})
}

func saveWindowBounds(win application.Window, settingsStore *settings.Store) {
	w, h := win.Size()
	x, y := win.Position()
	sv := settingsStore.Value
	sv.WindowWidth = w
	sv.WindowHeight = h
	sv.WindowX = x
	sv.WindowY = y
	sv.Maximized = win.IsMaximised()
	settingsStore.Value = sv
	_ = settingsStore.Save()
}

func clamp(v, lo, hi int) int {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}
