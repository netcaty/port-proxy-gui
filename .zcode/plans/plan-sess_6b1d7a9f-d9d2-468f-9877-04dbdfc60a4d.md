## Context

编辑对话框（`UI/EntryDialog.cs`）目前用两个独立控件表达「目标」：「目标地址」可手输/选候选 IP，「目标跟随」用下拉选择跟随对象，二者通过禁用/回填地址文本联动，容易混淆。需求：合并为**一个目标输入框**，旁边加一组单选按钮（固定 IP / 跟随目标）切换模式。下游契约不变：`Result`（PortProxyEntry）+ `ResultMeta.Follow*` 供 `TargetFollowService`/`MainForm` 使用。

## 改动内容（仅 `UI/EntryDialog.cs` + README 一处描述）

### 1. BuildUi：合并为单行「目标」
- 删除「目标跟随」行、`_followBox`、`_followRefreshButton`（`RefreshFollowChoicesAsync`、`RefreshFollowChoicesWhenReadyAsync` 相应改名/简化，无按钮后不再需要 busy 禁用）。
- 「目标」行改为：col1 放一个 `Panel`，内部水平排列两个 RadioButton（`_followNo`="固定 IP"、`_followYes`="跟随目标"，同容器自动成组，默认选固定 IP）+ `_connectAddress`（占满剩余宽度）；col2 仍为 `_connectPort`。
- 保留 `DropDown`/`DropDownClosed`/`SelectionChangeCommitted`/`TextChanged` 事件，但处理函数按模式分支。

### 2. 模式切换（新增 `SetFollowMode(bool)`，由两个 RadioButton 的 CheckedChanged 调用）
- 切换前保存固定 IP 文本到字段 `_fixedAddressText`（经 `TryExtractAddress` 归一）。
- **跟随模式**：`_connectAddress.DropDownStyle = DropDownList`，调用现有 `PopulateFollowChoices()` 填充 `FollowChoice`（去掉首个"不跟随"项——单选按钮已表达；保留"当前未检测到，保留原地址"离线兜底项）；选中项的 `Hint.Address` 用于预览/保存。
- **固定 IP 模式**：`DropDownStyle = DropDown`，调用现有 `PopulateAddressHints()` 填候选，恢复 `_fixedAddressText`（编辑已有跟随条目时回退到 `_existing.NormalizedConnect`）。
- 复用现有守卫：`_populatingFollow`、`_populatingHints`、`_populateVersion/_selectionVersion`、`DropDownClosed` 归一化（仅固定模式有 pending）。

### 3. 统一"当前目标地址"来源（新增 `GetCurrentConnectAddress()`）
- 跟随模式：`CurrentFollowChoice()?.Hint?.Address ?? _existing?.NormalizedConnect ?? ""`（离线跟随保留原地址）。
- 固定模式：`_connectAddress.Text`。
- `BuildEntry()`、`UpdatePreview()`、`TestConnectAsync()` 全部改用该方法；`OnConfirm` 中 `follow = IsFollowMode ? CurrentFollowTarget() : null` 写入 `ResultMeta.Follow*`（固定 IP 时为 null，`EntryMeta.IsEmpty` 语义保持——meta 会被正确清理）。
- 校验：跟随模式下未选中任何目标 → `_errorLabel` 提示"请选择跟随目标"；离线跟随（Hint 为 null）且无 `_existing` → 报错。

### 4. LoadValues / 构造顺序
- `LoadValues` 不变（地址文本、`_initialFollowTarget`）；构造顺序改为 `BuildUi → LoadValues → 按 _initialFollowTarget 勾选单选 → 按模式填充一次 → UpdatePreview → GuestHintCache.RefreshIfStale`，保证编辑跟随条目时下拉能显示匹配项/离线兜底项。

### 5. 其他
- `ResizeAddressDropDown` 泛化为按字符串测宽，固定模式传 `hints.Select(h=>h.Display)`，跟随模式传 choice 的 Display。
- Tooltip 更新；`Shown` 预热逻辑保留（改名为按当前模式补填）。
- README「目标跟随」描述同步为单选按钮交互。
- 不改 `TargetFollowService` / `EntryMeta` / `MainForm`。

### 不在范围内（明确不做）
- ComboBox 状态机（版本计数、延迟归一化）保留——它们是手输 IP 防覆盖的兜底；深度重构 UI 控件不在本次范围。

## 验证
1. `dotnet build -c Debug` 0 警告 0 错误。
2. 启动 `dist/fx`（发布新构建后运行）：
   - 新增：默认固定 IP，可手输/选候选，保存后无跟随 meta；
   - 切「跟随目标」：下拉只出已确认运行中的 WSL/虚拟机/沙盒，选中后预览 netsh 显示其当前 IP，保存后列表「跟随」列正常、5 分钟自动跟随生效；
   - 编辑离线跟随条目：单选在「跟随目标」，显示"当前未检测到"兜底项，保存不丢配置；
   - 跟随→固定 IP 切换：地址文本恢复为上次固定 IP，保存后 meta 被清理。