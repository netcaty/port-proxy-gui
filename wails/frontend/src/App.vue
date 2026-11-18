<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { Events } from '@wailsio/runtime'
import * as AppService from '../bindings/portproxymanager/appservice'
import type { EntryRow, EnvInfo } from '../bindings/portproxymanager/models'
import EntryDialog from './components/EntryDialog.vue'
import LogViewer from './components/LogViewer.vue'
import DiagnosticsDialog from './components/DiagnosticsDialog.vue'
import ImportDialog from './components/ImportDialog.vue'

const rows = ref<EntryRow[]>([])
const groups = ref<string[]>([])
const env = ref<EnvInfo | null>(null)
const loading = ref(false)

// 表格状态
const selectedKey = ref('')
const groupFilter = ref('')
const searchText = ref('')
const sortKey = ref<'name' | 'group' | 'family' | 'listenPort' | 'connect'>('listenPort')
const sortDesc = ref(false)

// 对话框状态
const showEntryDialog = ref(false)
const editTarget = ref<EntryRow | null>(null)
const showLogViewer = ref(false)
const diagnoseTarget = ref<EntryRow | null>(null)
const showImportDialog = ref(false)

// 右键菜单
const ctxVisible = ref(false)
const ctxX = ref(0)
const ctxY = ref(0)

let unlisten: (() => void) | null = null

function rowByKey(key: string): EntryRow | undefined {
  return rows.value.find(r => r.key === key)
}

async function refresh() {
  loading.value = true
  try {
    rows.value = (await AppService.ListEntries()) as EntryRow[]
    groups.value = (await AppService.ListGroups()) as string[]
  } catch (e) {
    console.error('读取映射失败', e)
    rows.value = []
  } finally {
    loading.value = false
  }
}

async function loadEnv() {
  env.value = (await AppService.GetEnv()) as EnvInfo
}

const filteredRows = computed(() => {
  let list = rows.value
  if (groupFilter.value) list = list.filter(r => (r.group ?? '') === groupFilter.value)
  const kw = searchText.value.trim().toLowerCase()
  if (kw) {
    list = list.filter(r =>
      JSON.stringify([r.name, r.group, r.listenText, r.connectText, r.note]).toLowerCase().includes(kw))
  }
  const dir = sortDesc.value ? -1 : 1
  const sorted = [...list].sort((a, b) => {
    const va = String(sortValue(a) ?? '').toLowerCase()
    const vb = String(sortValue(b) ?? '').toLowerCase()
    if (va < vb) return -1 * dir
    if (va > vb) return 1 * dir
    return 0
  })
  return sorted
})

function sortValue(r: EntryRow): string | number {
  switch (sortKey.value) {
    case 'name': return r.name ?? ''
    case 'group': return r.group ?? ''
    case 'family': return r.family
    case 'listenPort': return r.listenPort
    case 'connect': return r.connectText
  }
}

function clickSort(key: typeof sortKey.value) {
  if (sortKey.value === key) {
    sortDesc.value = !sortDesc.value
  } else {
    sortKey.value = key
    sortDesc.value = false
  }
}

function openAdd() {
  editTarget.value = null
  showEntryDialog.value = true
}

// 双击表格空白处 = 新增映射；行内双击由行自身处理（编辑），此处排除
function onTableDblClick(ev: MouseEvent) {
  if (!env.value?.elevated) return
  const target = ev.target as HTMLElement
  if (target.closest('tr[data-key], thead, th')) return
  openAdd()
}

function openEdit(row?: EntryRow) {
  if (!row) return
  editTarget.value = row
  showEntryDialog.value = true
}

async function deleteRow(row?: EntryRow) {
  if (!row) return
  const label = `${row.listenText} → ${row.connectText}`
  if (!window.confirm(`确定删除这条映射？\n\n${row.family}/${row.protocol}  ${label}`)) return
  const res = await AppService.DeleteEntry({ family: row.family, protocol: row.protocol,
    listenAddress: row.listenAddress, listenPort: row.listenPort,
    connectAddress: row.connectAddress, connectPort: row.connectPort })
  if (!res.ok && res.error) {
    window.alert(`删除失败：\n${res.error}`)
  } else {
    await refresh()
  }
}

function copyText(text: string) {
  navigator.clipboard.writeText(text).catch(() => {
    // 剪贴板不可用时退回选中文本方案
    const ta = document.createElement('textarea')
    ta.value = text
    document.body.appendChild(ta)
    ta.select()
    document.execCommand('copy')
    document.body.removeChild(ta)
  })
}

function openDiagnose(row?: EntryRow) {
  if (!row) return
  diagnoseTarget.value = row
}

async function createSnapshot() {
  const res = await AppService.CreateSnapshot('手动')
  window.alert(res.ok ? '✅ 快照已创建（数据目录 backups 下）' : '❌ ' + (res.error ?? '创建失败'))
}

// ---- 状态栏 ----
const autorun = ref(false)

const shortDataDir = computed(() => {
  const dir = env.value?.dataDir ?? ''
  return dir.length > 46 ? dir.slice(0, 20) + '…' + dir.slice(-24) : dir
})

async function loadAutorun() {
  try {
    autorun.value = await AppService.IsAutorunEnabled()
  } catch { autorun.value = false }
}

function openDataDir() {
  void AppService.OpenDataDir()
}

async function toggleAutorun() {
  const res = autorun.value ? await AppService.DisableAutorun() : await AppService.EnableAutorun()
  if (!res.ok && res.message) {
    window.alert(res.message)
  }
  await loadAutorun()
}

async function syncFollow() {
  const report = await AppService.SyncFollow(true)
  const updated = report.updatedCount ?? 0
  const failed = report.failedCount ?? 0
  const unresolved = report.unresolvedCount ?? 0
  window.alert(`目标跟随同步完成：\n\n已更新 ${updated} · 无变化 ${(report.items ?? []).length - updated - failed - unresolved - (report.needsElevationCount ?? 0)}\n未检测到 ${unresolved} · 失败 ${failed}`)
  await refresh()
}

function openCtxMenu(ev: MouseEvent, row: EntryRow) {
  selectedKey.value = row.key
  ctxX.value = ev.clientX
  ctxY.value = ev.clientY
  ctxVisible.value = true
}

onMounted(async () => {
  await Promise.all([refresh(), loadEnv(), loadAutorun()])
  unlisten = Events.On('entries-changed', () => { void refresh() })
})

onUnmounted(() => {
  unlisten?.()
})
</script>

<template>
  <!-- 非管理员横幅：与 C# 版"只读可浏览、提权才能改"一致 -->
  <div v-if="env && !env.elevated" class="banner warn">
    ⚠️ 当前以普通权限运行：可以浏览映射，但新增 / 编辑 / 删除需要管理员权限。
  </div>

  <div class="toolbar">
    <button class="primary" :disabled="!env?.elevated" @click="openAdd">＋ 新增映射</button>
    <button :disabled="!selectedKey" @click="openEdit(rowByKey(selectedKey))">✏️ 编辑</button>
    <button :disabled="!selectedKey" @click="deleteRow(rowByKey(selectedKey))">🗑️ 删除</button>
    <button :disabled="!selectedKey" @click="copyText(rowByKey(selectedKey)?.netshCmd ?? '')">📋 复制命令</button>
    <button :disabled="!env?.elevated" @click="syncFollow">🔄 同步跟随</button>
    <div class="spacer" />
    <select v-model="groupFilter">
      <option value="">全部分组</option>
      <option v-for="g in groups" :key="g" :value="g">{{ g }}</option>
    </select>
    <input v-model="searchText" type="search" placeholder="搜索名称 / 地址 / 端口…" style="width: 200px" />
    <button @click="refresh" :title="loading ? '加载中…' : '刷新'">{{ loading ? '…' : '⟳' }}</button>
    <button @click="showImportDialog = true">导入/导出</button>
    <button @click="createSnapshot">快照</button>
    <button @click="showLogViewer = true">日志</button>
  </div>

  <div class="table-wrap" @click="ctxVisible = false" @dblclick="onTableDblClick">
    <table class="grid">
      <thead>
        <tr>
          <th style="width: 150px" @click="clickSort('name')">名称</th>
          <th style="width: 90px" @click="clickSort('group')">分组</th>
          <th style="width: 90px" @click="clickSort('family')">类型</th>
          <th style="width: 170px" @click="clickSort('listenPort')">监听</th>
          <th style="width: 170px" @click="clickSort('connect')">目标</th>
          <th style="width: 80px">跟随</th>
          <th style="width: 60px">防火墙</th>
          <th>备注</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="r in filteredRows" :key="r.key" data-key="1"
            :class="{ selected: r.key === selectedKey }"
            @click="selectedKey = r.key; ctxVisible = false"
            @dblclick="openEdit(r)"
            @contextmenu.prevent="openCtxMenu($event, r)">
          <td>{{ r.name || '（未命名）' }}</td>
          <td>{{ r.group ?? '' }}</td>
          <td class="dim">{{ r.family }}/{{ r.protocol }}</td>
          <td class="mono">{{ r.listenText }}</td>
          <td class="mono">{{ r.connectText }}</td>
          <td><span v-if="r.followKind" class="tag follow">{{ r.followKind }}</span></td>
          <td><span v-if="r.hasFirewallRule" class="tag fw">✓</span></td>
          <td class="dim" style="white-space: normal">{{ r.note ?? '' }}</td>
        </tr>
        <tr v-if="filteredRows.length === 0">
          <td colspan="8">
            <div class="empty-tip">
              {{ rows.length === 0 ? '还没有任何映射：双击空白处或点上方"新增映射"创建第一条。' : '没有符合过滤条件的映射。' }}
            </div>
          </td>
        </tr>
      </tbody>
    </table>
  </div>

  <div class="statusbar">
    <span class="item clickable" :title="env?.dataDir ?? ''" @click="openDataDir"
          style="max-width: 380px; overflow: hidden; text-overflow: ellipsis">
      📁 {{ shortDataDir }}
    </span>
    <span class="item">{{ env?.elevated ? '🔑 管理员' : '👁️ 只读' }}</span>
    <span class="item clickable" title="点击切换开机自启（计划任务，需要管理员权限）" @click="toggleAutorun">
      🚀 自启：{{ autorun ? '开' : '关' }}
    </span>
    <span class="item">{{ env?.user }}</span>
    <span class="item">v{{ env?.appVersion }}</span>
  </div>

  <!-- 右键菜单 -->
  <Teleport to="body">
    <div v-if="ctxVisible" class="ctx-menu" :style="{ left: ctxX + 'px', top: ctxY + 'px' }">
      <div class="mi" @click="ctxVisible = false; openEdit(rowByKey(selectedKey))">编辑</div>
      <div class="mi" @click="ctxVisible = false; deleteRow(rowByKey(selectedKey))">删除</div>
      <div class="mi" @click="ctxVisible = false; copyText(rowByKey(selectedKey)?.netshCmd ?? '')">复制 netsh 命令</div>
      <div class="mi" @click="ctxVisible = false; openDiagnose(rowByKey(selectedKey))">诊断…</div>
    </div>
  </Teleport>

  <!-- 新增 / 编辑对话框 -->
  <EntryDialog v-if="showEntryDialog" :editing="editTarget"
               @close="showEntryDialog = false" @saved="showEntryDialog = false; refresh()" />

  <!-- 日志查看 -->
  <LogViewer v-if="showLogViewer" @close="showLogViewer = false" />

  <!-- 诊断 -->
  <DiagnosticsDialog v-if="diagnoseTarget" :entry="diagnoseTarget" @close="diagnoseTarget = null" />

  <!-- 导入 / 导出 -->
  <ImportDialog v-if="showImportDialog" @close="showImportDialog = false" @imported="showImportDialog = false; refresh()" />
</template>
