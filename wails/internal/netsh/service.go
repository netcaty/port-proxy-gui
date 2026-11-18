package netsh

// Service 移植自 C# Services/PortProxyService.cs：
// 映射的增删改。写入一律经 netsh，并且每次写后重读注册表核对——
// netsh 退出码为 0 但映射并未真正落地（或落地到了别处）是真实存在的坑。

import (
	"fmt"
	"strings"

	"portproxymanager/internal/elevation"
	"portproxymanager/internal/logging"
	"portproxymanager/internal/models"
	"portproxymanager/internal/proc"
	"portproxymanager/internal/registryio"
)

// Error 业务错误，可携带原始进程结果（与 C# PortProxyException 对应）。
type Error struct {
	Message string
	Result  *proc.Result
}

func (e *Error) Error() string { return e.Message }

func newError(message string, result *proc.Result) *Error {
	return &Error{Message: message, Result: result}
}

// Snapshot 读取全部映射。
func Snapshot() ([]models.PortProxyEntry, error) { return registryio.ReadAll() }

// Add 新增一条映射（写后重读注册表核对）。
func Add(entry models.PortProxyEntry) (models.PortProxyEntry, error) {
	if err := ensureElevated(); err != nil {
		return models.PortProxyEntry{}, err
	}

	result, err := Run([]string{
		"interface", "portproxy", "add", entry.Family, entry.Protocol,
		fmt.Sprintf("listenaddress=%s", entry.NetshListenAddress()),
		fmt.Sprintf("listenport=%d", entry.ListenPort),
		fmt.Sprintf("connectaddress=%s", entry.NetshConnectAddress()),
		fmt.Sprintf("connectport=%d", entry.ConnectPort),
	})
	if err != nil || !result.Ok() {
		return models.PortProxyEntry{}, newError(Friendly(result), &result)
	}

	return verifyPresent(entry, result)
}

// Delete 删除一条映射。
//
// 遇到注册表里把通配监听地址存成字面量 *（非 netsh 标准写法）的遗留条目时，
// netsh 需要精确匹配键名，因此先试原始地址，再退回标准地址，两个都失败才报错。
func Delete(entry models.PortProxyEntry, useRawListenAddress bool) error {
	if err := ensureElevated(); err != nil {
		return err
	}

	raw := strings.TrimSpace(entry.ListenAddress)
	var candidates []string

	if useRawListenAddress && raw != "" && raw != entry.NetshListenAddress() {
		candidates = append(candidates, raw)
	}
	if !containsStr(candidates, entry.NetshListenAddress()) {
		candidates = append(candidates, entry.NetshListenAddress())
	}
	if raw != "" && !containsStr(candidates, raw) {
		candidates = append(candidates, raw)
	}

	var lastResult *proc.Result

	for _, listenAddress := range candidates {
		result, _ := Run([]string{
			"interface", "portproxy", "delete", entry.Family, entry.Protocol,
			fmt.Sprintf("listenaddress=%s", listenAddress),
			fmt.Sprintf("listenport=%d", entry.ListenPort),
		})
		r := result
		lastResult = &r

		after, _ := registryio.ReadAll()
		remaining := findByKey(after, entry.Key())
		if remaining == nil {
			return nil // 删掉了（或本来就不存在）
		}

		if !result.Ok() {
			if log := logging.Current(); log != nil {
				log.Warn(fmt.Sprintf("删除用 listenaddress=%s 未成功，换下一种写法", listenAddress))
			}
		}
	}

	stillThere := findByKey(mustReadAll(), entry.Key())
	if stillThere == nil {
		return nil
	}

	return newError(
		fmt.Sprintf("netsh 无法删除这条映射：%s/%s %s。\r\n"+
			"它很可能是直接写注册表创建的（监听地址存成了非标准形式），netsh 匹配不到。"+
			"\r\n\r\n手工清理：在 regedit 里删除\r\n"+
			"HKLM\\%s\\%s\\%s 下的值 %s/%d",
			stillThere.Family, stillThere.Protocol, stillThere.ListenText(),
			registryio.RootPath, stillThere.Family, stillThere.Protocol,
			stillThere.NormalizedListen(), stillThere.ListenPort),
		lastResult)
}

// Update 修改 = 先删后加。加失败时按原值重新加上，保证不会把用户已有的映射改丢。
// 返回 false 表示无需修改（目标相同）。
func Update(oldEntry, newEntry models.PortProxyEntry) (bool, error) {
	if err := ensureElevated(); err != nil {
		return false, err
	}

	if oldEntry.SameTarget(newEntry) {
		return false, nil
	}

	if err := Delete(oldEntry, false); err != nil {
		return false, err
	}

	if _, err := Add(newEntry); err != nil {
		updateEx := err
		if log := logging.Current(); log != nil {
			log.Warn(fmt.Sprintf("修改失败，开始回滚：%s -> %s：%v", oldEntry, newEntry, updateEx))
		}
		if _, rollbackErr := Add(oldEntry); rollbackErr != nil {
			if log := logging.Current(); log != nil {
				log.Error(fmt.Sprintf("回滚失败，旧映射可能已丢失：%v", rollbackErr))
			}
			return false, newError(
				fmt.Sprintf("修改失败：%s\r\n回滚也失败：%s\r\n原映射 %s 现在可能已不存在，请用 backups 目录里的快照恢复。",
					errText(updateEx), errText(rollbackErr), oldEntry.ListenText()),
				nil)
		}
		return false, newError(fmt.Sprintf("修改失败，已把原映射恢复：%s", errText(updateEx)), nil)
	}

	return true, nil
}

func verifyPresent(expected models.PortProxyEntry, result proc.Result) (models.PortProxyEntry, error) {
	entries, readErr := registryio.ReadAll()
	if readErr != nil {
		return models.PortProxyEntry{}, newError(
			fmt.Sprintf("映射已提交，但读取注册表核对时出错：%v", readErr), &result)
	}

	actual := findByKey(entries, expected.Key())
	if actual == nil {
		return models.PortProxyEntry{}, newError(
			"命令返回成功，但注册表里没有出现这条映射，改动未生效。"+
				"请确认 IP Helper（iphlpsvc）服务正在运行。", &result)
	}

	if !actual.SameTarget(expected) {
		return models.PortProxyEntry{}, newError(
			fmt.Sprintf("命令返回成功，但实际落地结果与预期不一致：\r\n"+
				"预期 %s -> %s\r\n实际 %s -> %s",
				expected.ListenText(), expected.ConnectText(),
				actual.ListenText(), actual.ConnectText()), &result)
	}

	return *actual, nil
}

func ensureElevated() error {
	if !elevation.IsElevated() {
		return newError("修改端口映射需要管理员权限。请以管理员身份重新启动本程序。", nil)
	}
	return nil
}

func findByKey(entries []models.PortProxyEntry, key string) *models.PortProxyEntry {
	for i := range entries {
		if strings.EqualFold(entries[i].Key(), key) {
			return &entries[i]
		}
	}
	return nil
}

func mustReadAll() []models.PortProxyEntry {
	entries, _ := registryio.ReadAll()
	return entries
}

func containsStr(list []string, s string) bool {
	for _, x := range list {
		if x == s {
			return true
		}
	}
	return false
}

func errText(err error) string {
	if err == nil {
		return ""
	}
	return err.Error()
}
