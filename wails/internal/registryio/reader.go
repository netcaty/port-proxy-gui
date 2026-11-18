// Package registryio 移植自 C# Services/RegistryReader.cs：
// 映射的唯一真相源。注册表结构（netsh 的 portproxy 就是这么存的）：
//
//	HKLM\SYSTEM\CurrentControlSet\Services\PortProxy\<family>\tcp
//	  值名   = 监听地址/端口
//	  值数据 = 目标地址/端口
//
// 这里只读；任何写入都必须走 netsh，否则运行中的 IP Helper 不会重载。
package registryio

import (
	"net"
	"sort"
	"strconv"
	"strings"

	"golang.org/x/sys/windows/registry"

	"portproxymanager/internal/logging"
	"portproxymanager/internal/models"
)

// RootPath PortProxy 根键。
const RootPath = `SYSTEM\CurrentControlSet\Services\PortProxy`

// Families 支持的四个 family，顺序即展示顺序。
var Families = []string{"v4tov4", "v4tov6", "v6tov4", "v6tov6"}

// ReadAll 读取全部映射；出错时返回错误并记录日志。
func ReadAll() ([]models.PortProxyEntry, error) {
	var list []models.PortProxyEntry

	root, openErr := registry.OpenKey(registry.LOCAL_MACHINE, RootPath, registry.QUERY_VALUE)
	if openErr != nil {
		// 键不存在（从未配置过）是正常情况，返回空列表
		if openErr == registry.ErrNotExist {
			return []models.PortProxyEntry{}, nil
		}
		if log := logging.Current(); log != nil {
			log.Error("读取注册表失败：" + openErr.Error())
		}
		return []models.PortProxyEntry{}, openErr
	}
	defer root.Close()

	for _, family := range Families {
		protoKey, err := registry.OpenKey(root, family+`\tcp`, registry.QUERY_VALUE)
		if err != nil {
			continue
		}

		names, err := protoKey.ReadValueNames(0) // 0 = 全部值名
		if err == nil {
			for _, valueName := range names {
				listenAddress, listenPort, ok := TryParsePair(valueName)
				if !ok {
					continue
				}
				data, _, err := protoKey.GetStringValue(valueName)
				if err != nil {
					continue
				}
				connectAddress, connectPort, ok := TryParsePair(data)
				if !ok {
					continue
				}

				list = append(list, models.PortProxyEntry{
					Family:         family,
					Protocol:       "tcp",
					ListenAddress:  listenAddress,
					ListenPort:     listenPort,
					ConnectAddress: connectAddress,
					ConnectPort:    connectPort,
				})
			}
		}
		protoKey.Close()
	}

	// 按 family 顺序 → 监听端口 → 监听地址（忽略大小写）排序，与 C# 版一致
	sort.SliceStable(list, func(i, j int) bool {
		fi, fj := familyIndex(list[i].Family), familyIndex(list[j].Family)
		if fi != fj {
			return fi < fj
		}
		if list[i].ListenPort != list[j].ListenPort {
			return list[i].ListenPort < list[j].ListenPort
		}
		return strings.ToLower(list[i].NormalizedListen()) < strings.ToLower(list[j].NormalizedListen())
	})

	return list, nil
}

func familyIndex(family string) int {
	for i, f := range Families {
		if strings.EqualFold(f, family) {
			return i
		}
	}
	return len(Families)
}

// TryParsePair 把 "地址/端口" 拆开。IPv6 里没有斜杠，所以按最后一个斜杠切。
func TryParsePair(text string) (address string, port int, ok bool) {
	if strings.TrimSpace(text) == "" {
		return "", 0, false
	}
	raw := strings.TrimSpace(text)
	slash := strings.LastIndexByte(raw, '/')
	if slash <= 0 || slash == len(raw)-1 {
		return "", 0, false
	}
	address = strings.TrimSpace(raw[:slash])
	p, err := strconv.Atoi(strings.TrimSpace(raw[slash+1:]))
	if err != nil || p <= 0 || p > 65535 {
		return "", 0, false
	}
	return address, p, true
}

// IPEndPointInfo 一个本机 TCP 监听端点。
type IPEndPointInfo struct {
	Address string `json:"address"`
	Port    int    `json:"port"`
}

// ActiveTcpListeners 当前所有监听端口（用于"是否被占用"提示）。
// 拿不到就返回空列表，只是少一条提示。
func ActiveTcpListeners() []IPEndPointInfo {
	rows, err := tcpListeners()
	if err != nil {
		return nil
	}
	return rows
}

func normalizeAddressForListener(ip net.IP) string {
	if ip == nil {
		return ""
	}
	return ip.String()
}
