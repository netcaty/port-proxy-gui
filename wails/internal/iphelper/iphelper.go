// Package iphelper 移植自 C# Services/IpHelperService.cs：
// IP Helper（iphlpsvc）状态检测、启动、重启。portproxy 的转发由这个服务实现，
// 它只在启动时读一次配置——绕过 netsh 写注册表后必须重启才生效。

package iphelper

import (
	"fmt"
	"time"

	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/mgr"

	"portproxymanager/internal/elevation"
	"portproxymanager/internal/logging"
)

// State iphlpsvc 状态。
type State int

const (
	StateRunning State = iota
	StateStopped
	StateNotFound
	StateUnknown
)

func (s State) Label() string {
	switch s {
	case StateRunning:
		return "运行中"
	case StateStopped:
		return "已停止"
	case StateNotFound:
		return "未找到"
	default:
		return "未知"
	}
}

const serviceName = "iphlpsvc"

// GetState 查询服务状态（用 SCM API，不解析本地化文本）。
func GetState() State {
	m, err := mgr.Connect()
	if err != nil {
		return StateUnknown
	}
	defer m.Disconnect()

	s, err := m.OpenService(serviceName)
	if err != nil {
		if err == windows.ERROR_SERVICE_DOES_NOT_EXIST {
			return StateNotFound
		}
		return StateUnknown
	}
	defer s.Close()

	status, err := s.Query()
	if err != nil {
		return StateUnknown
	}
	if status.State == svc.Running {
		return StateRunning
	}
	return StateStopped
}

// ActionResult 动作结果（与 C# (bool Ok, string Message) 对应）。
type ActionResult struct {
	Ok      bool   `json:"ok"`
	Message string `json:"message"`
}

// TryStart 启动服务。
func TryStart() ActionResult {
	if !elevation.IsElevated() {
		return ActionResult{Message: "启动服务需要管理员权限，请以管理员身份重新启动本程序。"}
	}

	m, err := mgr.Connect()
	if err != nil {
		return ActionResult{Message: fmt.Sprintf("无法连接服务管理器（错误码 %d）。", errCode(err))}
	}
	defer m.Disconnect()

	s, err := m.OpenService(serviceName)
	if err != nil {
		if err == windows.ERROR_SERVICE_DOES_NOT_EXIST {
			return ActionResult{Message: "系统里找不到 IP Helper 服务（iphlpsvc），端口转发功能不可用。"}
		}
		return ActionResult{Message: fmt.Sprintf("无法打开 IP Helper 服务（错误码 %d）。", errCode(err))}
	}
	defer s.Close()

	if err := s.Start(); err == nil {
		return ActionResult{Ok: true, Message: "IP Helper 已启动。"}
	} else {
		switch errCode(err) {
		case 1056: // ERROR_SERVICE_ALREADY_RUNNING
			return ActionResult{Ok: true, Message: "IP Helper 本来就在运行。"}
		case 1058: // ERROR_SERVICE_DISABLED
			return ActionResult{Message: "IP Helper 服务已被禁用，请先在「服务」里把启动类型改为「手动」或「自动」。"}
		default:
			return ActionResult{Message: fmt.Sprintf("启动 IP Helper 失败（错误码 %d）。", errCode(err))}
		}
	}
}

// TryRestart 重启服务（停止后轮询最多 6 秒等服务真正停下，再启动）。
func TryRestart() ActionResult {
	if !elevation.IsElevated() {
		return ActionResult{Message: "重启 IP Helper 需要管理员权限。"}
	}

	m, err := mgr.Connect()
	if err != nil {
		return ActionResult{Message: fmt.Sprintf("无法连接服务管理器（错误码 %d）。", errCode(err))}
	}
	defer m.Disconnect()

	s, err := m.OpenService(serviceName)
	if err != nil {
		return ActionResult{Message: fmt.Sprintf("无法打开 IP Helper 服务（错误码 %d）。", errCode(err))}
	}
	defer s.Close()

	if _, err := s.Control(svc.Stop); err != nil {
		if errCode(err) != 1062 { // ERROR_SERVICE_NOT_ACTIVE：本来没在跑，继续启动
			return ActionResult{Message: fmt.Sprintf("停止 IP Helper 失败（错误码 %d）。", errCode(err))}
		}
	} else {
		// 等服务真正停下，最多 6 秒
		deadline := time.Now().Add(6 * time.Second)
		for time.Now().Before(deadline) {
			time.Sleep(250 * time.Millisecond)
			status, err := s.Query()
			if err == nil && status.State == svc.Stopped {
				break
			}
		}
	}

	if err := s.Start(); err != nil {
		if errCode(err) == 1056 {
			return ActionResult{Ok: true, Message: "IP Helper 已在运行。"}
		}
		return ActionResult{Message: fmt.Sprintf("启动 IP Helper 失败（错误码 %d）。", errCode(err))}
	}

	if log := logging.Current(); log != nil {
		log.Info("IP Helper 已重启，映射应当已重新加载。")
	}
	return ActionResult{Ok: true, Message: "IP Helper 已重启，映射应当已重新加载。"}
}

func errCode(err error) uint32 {
	if err == nil {
		return 0
	}
	if errno, ok := err.(windows.Errno); ok {
		return uint32(errno)
	}
	return 0
}
