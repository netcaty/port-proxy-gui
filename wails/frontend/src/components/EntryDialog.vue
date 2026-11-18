<script setup lang="ts">
// 新增 / 编辑映射对话框（对应 C# UI/EntryDialog.cs）：
// 地址端口表单 + 实时 netsh 预览 + 服务端校验 + 目标候选下拉（含 WSL / 沙盒 / 虚拟机跟随）。
import { computed, onMounted, ref, watch } from 'vue'
import * as AppService from '../../bindings/portproxymanager/appservice'
import type { EntryRow } from '../../bindings/portproxymanager/models'

const props = defineProps<{ editing: EntryRow | null }>()
const emit = defineEmits<{ (e: 'close'): void; (e: 'saved'): void }>()

// 表单字段
const name = ref(props.editing?.name ?? '')
const group = ref(props.editing?.group ?? '')
const note = ref(props.editing?.note ?? '')
const listenAddress = ref(props.editing?.listenAddress ?? '')
const listenPort = ref(props.editing?.listenPort ?? 0)
const connectAddress = ref(props.editing?.connectAddress ?? '')
const connectPort = ref(props.editing?.connectPort ?? 0)

const family = ref('v4tov4')
const error = ref('')
const warning = ref('')
const submitting = ref(false)

// 目标候选 / 跟随
const candidates = ref<any[]>([])
const selectedCandidate = ref('') // 候选地址（纯 IP）
const followKind = ref(props.editing?.followKind ?? '')
const followId = ref(props.editing?.followId ?? '')
const followMac = ref(props.editing?.followMac ?? '')
const followLabel = ref(props.editing?.followLabel ?? '')

const listenDisplay = computed(() => listenAddress.value.trim() || '*')
const isEdit = computed(() => props.editing !== null)

watch([listenAddress, connectAddress], async () => {
  try {
    family.value = await AppService.InferFamily(listenDisplay.value, connectAddress.value.trim())
  } catch { /* 推断失败保持原值 */ }
}, { immediate: true })

const netshPreview = computed(() => {
  const listen = listenDisplay.value === '*'
    ? (family.value.startsWith('v4') ? '0.0.0.0' : '::')
    : listenDisplay.value
  const connect = connectAddress.value.trim() || '（目标地址）'
  return `netsh interface portproxy add ${family.value} tcp ` +
    `listenaddress=${listen} listenport=${listenPort.value || '<端口>'} ` +
    `connectaddress=${connect} connectport=${connectPort.value || '<端口>'}`
})

// 与后端 follow.IsFollowable 一致的跟随启用判断
function looksLikeMac(id: string): boolean {
  return /^[0-9a-fA-F]{12}$/.test(id.replace(/[-:]/g, ''))
}

function isFollowable(c: any): boolean {
  const kind = c.kind ?? ''
  if (kind === 'wsl') return !!c.id && !looksLikeMac(c.id)
  return (kind === 'wsb' || kind === 'hyperv' || kind === 'vm')
    && (!!c.mac || !!c.id)
}

function optionTip(c: any): string {
  return isFollowable(c)
    ? '选择后自动填充目标地址并启用跟随：客户机 IP 变化时自动重建映射'
    : '网关 / 局域网设备：仅填充目标地址，不支持跟随'
}

// 下拉整体的悬停提示：说明当前跟随状态
const candidateTip = computed(() => {
  if (followKind.value) {
    return `已启用跟随：${followLabel.value || followKind.value}（切换为"手动填写 IP"可清除跟随）`
  }
  return '选择客户机候选（wsl / wsb / hyperv / vm）可启用目标跟随；网关和局域网设备仅填充地址'
})

function onCandidateChange() {
  const c = candidates.value.find(x => x.address === selectedCandidate.value)
  if (!c) {
    // 选了第一项"手动填写 IP"：等同清除跟随
    followKind.value = ''
    followId.value = ''
    followMac.value = ''
    followLabel.value = ''
    return
  }
  connectAddress.value = c.address
  if (isFollowable(c)) {
    // 选中可跟随候选即建立跟随规则（Kind/Id/Mac 只用于本地匹配，不进命令行）
    followKind.value = c.kind
    followId.value = c.id ?? ''
    followMac.value = c.mac ?? ''
    followLabel.value = c.display ?? ''
  } else {
    followKind.value = ''
    followId.value = ''
    followMac.value = ''
    followLabel.value = ''
  }
}

onMounted(async () => {
  try {
    candidates.value = (await AppService.ListCandidates(false)) as any[]
  } catch { candidates.value = [] }
  // 编辑时回显已保存的跟随目标
  if (props.editing?.followKind) {
    followKind.value = props.editing.followKind
    const match = candidates.value.find(c =>
      c.kind === followKind.value && (c.id === followId.value || c.address === props.editing?.connectAddress))
    if (match) selectedCandidate.value = match.address
  }
})

async function submit() {
  error.value = ''
  warning.value = ''
  submitting.value = true
  try {
    const entry = {
      family: family.value,
      protocol: 'tcp',
      listenAddress: listenDisplay.value,
      listenPort: Number(listenPort.value),
      connectAddress: connectAddress.value.trim(),
      connectPort: Number(connectPort.value),
    }
    const metaIn = { name: name.value || null, group: group.value || null, note: note.value || null }

    const validation = await AppService.ValidateEntry(entry, props.editing?.key ?? '')
    if (!validation.ok && validation.error) {
      error.value = validation.error
      return
    }
    if (validation.warning) warning.value = validation.warning

    const old = props.editing
    const oldEntry = old ? {
      family: old.family, protocol: old.protocol,
      listenAddress: old.listenAddress, listenPort: old.listenPort,
      connectAddress: old.connectAddress, connectPort: old.connectPort,
    } : entry

    const res = isEdit.value
      ? await AppService.UpdateEntry(oldEntry, entry, metaIn)
      : await AppService.AddEntry(entry, metaIn)

    if (!res.ok && res.error) {
      error.value = res.error
      return
    }

    // 保存跟随规则到 meta（新增时用新条目的 key）
    const key = isEdit.value && old ? old.key : (res.entry?.key ?? '')
    if (key) {
      await AppService.SetFollowTarget(key, followKind.value, followId.value, followMac.value, followLabel.value)
    }
    emit('saved')
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <Teleport to="body">
    <div class="modal-mask" @click.self="emit('close')">
      <div class="modal">
        <div class="modal-header">{{ isEdit ? '编辑映射' : '新增映射' }}</div>
        <div class="modal-body">
          <div class="form-grid">
            <label>名称</label>
            <input v-model="name" placeholder="用于显示（不会进入 netsh 命令）" />
            <label>分组</label>
            <input v-model="group" placeholder="可选，用于过滤" />
            <label>目标候选</label>
            <select :value="selectedCandidate" :title="candidateTip"
                    @change="selectedCandidate = ($event.target as HTMLSelectElement).value; onCandidateChange()">
              <option value="" title="手动填写目标地址，不启用跟随">— 手动填写 IP（不跟随）—</option>
              <option v-for="c in candidates" :key="c.address" :value="c.address" :title="optionTip(c)">
                {{ c.display }}
              </option>
            </select>
            <label>监听地址</label>
            <input v-model="listenAddress" class="mono" placeholder="留空或 * 表示所有地址" />
            <label>监听端口</label>
            <input v-model.number="listenPort" class="mono" type="number" min="1" max="65535" placeholder="1–65535" />
            <label>目标地址</label>
            <input v-model="connectAddress" class="mono" placeholder="具体 IP，例如 192.168.1.5 / 127.0.0.1" />
            <label>目标端口</label>
            <input v-model.number="connectPort" class="mono" type="number" min="1" max="65535" placeholder="1–65535" />
            <label>备注</label>
            <input v-model="note" placeholder="可选" />
          </div>

          <div v-if="followKind" class="form-warning" style="margin-top:6px">
            🔄 已启用目标跟随：{{ followLabel || followKind }}（客户机 IP 变化后自动重建映射）
          </div>

          <div class="netsh-preview">{{ netshPreview }}</div>
          <div v-if="error" class="form-error">{{ error }}</div>
          <div v-else-if="warning" class="form-warning">{{ warning }}</div>
        </div>
        <div class="modal-footer">
          <button @click="emit('close')">取消</button>
          <button class="primary" :disabled="submitting" @click="submit">
            {{ submitting ? '执行中…' : (isEdit ? '保存修改' : '创建') }}
          </button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
