<script setup lang="ts">
// 主窗口：映射列表（跳线面板视觉）。一行映射 = 一条跳线，
// 跟随中的映射是有源链路（行首 LED 常亮 + 粗线箭头）。
// 写操作全部走 AppService（后端校验 + 核对 + 回滚），本层只做展示与交互。
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { Events } from '@wailsio/runtime'
import * as AppService from '../bindings/portproxymanager/appservice'
import type { EntryRow, EnvInfo } from '../bindings/portproxymanager/models'
import EntryDialog from './components/EntryDialog.vue'
import LogViewer from './components/LogViewer.vue'
import DiagnosticsDialog from './components/DiagnosticsDialog.vue'
import ImportDialog from './components/ImportDialog.vue'
import Icon from './ui/Icon.vue'
import ToastStack from './ui/ToastStack.vue'
import ConfirmDialog from './ui/ConfirmDialog.vue'
import { toast } from './ui/toast'
import { confirmState, confirmDialog } from './ui/confirm'

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
const searchInput = ref<HTMLInputElement | null>(null)

// 对话框状态
const showEntryDialog = ref(false)
const editTarget = ref<EntryRow | null>(null)
const showLogViewer = ref(false)
const diagnoseTarget = ref<EntryRow | null>(null)
const showImportDialog = ref(false)

// 右键菜单 / 溢出菜单
const ctxVisible = ref(false)
const ctxX = ref(0)
const ctxY = ref(0)
const ovrVisible = ref(false)
const ovrX = ref(0)
const ovrY = ref(0)

let unlisten: (() => void) | null = null

function rowByKey(key: string): EntryRow | undefined {
  return rows.value.find(r => r.key === key)
}

function closeMenus() {
  ctxVisible.value = false
  ovrVisible.value = false
}

// ---- 展示辅助：系统术语 → 用户语言 ----
const KIND_LABELS: Record<string, string> = {
  wsl: 'WSL', wsb: 'Windows 沙盒', hyperv: 'Hyper-V', vm: '虚拟机',
}
function kindLabel(kind?: string | null): string {
  return kind ? (KIND_LABELS[kind] ?? kind) : ''
}

const FAMILY_LABELS: Record<string, string> = {
  v4tov4: 'v4→v4', v6tov6: 'v6→v6', v6tov4: 'v6→v4', v4tov6: 'v4→v6',
}
function familyLabel(f: string): string {
  return FAMILY_LABELS[f] ?? f
}

async function refresh() {
  loading.value = true
  try {
    rows.value = (await AppService.ListEntries()) as EntryRow[]
    groups.value = (await AppService.ListGroups()) as string[]
  } catch (e) {
    console.error('读取映射失败', e)
    toast.error('读取映射失败：' + String(e))
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
    const va = sortValue(a)
    const vb = sortValue(b)
    if (typeof va === 'number' && typeof vb === 'number') return (va - vb) * dir
    const sa = String(va ?? '').toLowerCase()
    const sb = String(vb ?? '').toLowerCase()
    if (sa < sb) return -1 * dir
    if (sa > sb) return 1 * dir
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

function thCls(key: typeof sortKey.value) {
  return { on: sortKey.value === key, desc: sortKey.value === key && sortDesc.value }
}

function clearFilters() {
  searchText.value = ''
  groupFilter.value = ''
}

function openAdd() {
  if (!env.value?.elevated) {
    toast.warn('需要管理员权限才能新增映射')
    return
  }
  closeMenus()
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
  closeMenus()
  editTarget.value = row
  showEntryDialog.value = true
}

async function deleteRow(row?: EntryRow) {
  if (!row) return
  const label = `${row.listenText} → ${row.connectText}`
  const ok = await confirmDialog({
    title: '删除映射',
    body: `${row.family}/${row.protocol}\n${label}\n\n将删除注册表映射并清除补充信息（自动先备份）。`,
    confirmLabel: '删除',
    danger: true,
  })
  if (!ok) return
  const res = await AppService.DeleteEntry({ family: row.family, protocol: row.protocol,
    listenAddress: row.listenAddress, listenPort: row.listenPort,
    connectAddress: row.connectAddress, connectPort: row.connectPort })
  if (!res.ok && res.error) {
    toast.error(`删除失败：\n${res.error}`)
  } else {
    toast.success(`已删除 ${label}`)
    if (selectedKey.value === row.key) selectedKey.value = ''
    await refresh()
  }
}

function copyText(text: string) {
  if (!text) {
    toast.warn('没有可复制的内容')
    return
  }
  navigator.clipboard.writeText(text).catch(() => {
    // 剪贴板不可用时退回选中文本方案
    const ta = document.createElement('textarea')
    ta.value = text
    document.body.appendChild(ta)
    ta.select()
    document.execCommand('copy')
    document.body.removeChild(ta)
  })
  toast.success('已复制 netsh 命令')
}

function copyCmd() {
  copyText(rowByKey(selectedKey.value)?.netshCmd ?? '')
}

function openDiagnose(row?: EntryRow) {
  if (!row) return
  closeMenus()
  diagnoseTarget.value = row
}

async function createSnapshot() {
  const res = await AppService.CreateSnapshot('手动')
  if (res.ok) toast.success('快照已创建（数据目录 backups 下）')
  else toast.error('创建快照失败：' + (res.error ?? ''))
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
    toast.error(res.message)
  } else {
    toast.success(autorun.value ? '已关闭开机自启' : '已开启开机自启（计划任务）')
  }
  await loadAutorun()
}

async function syncFollow() {
  const report = await AppService.SyncFollow(true)
  const items = report.items ?? []
  const updated = report.updatedCount ?? 0
  const failed = report.failedCount ?? 0
  const unresolved = report.unresolvedCount ?? 0
  const needsElev = report.needsElevationCount ?? 0
  const unchanged = Math.max(0, items.length - updated - failed - unresolved - needsElev)
  const parts = [`更新 ${updated}`, `无变化 ${unchanged}`]
  if (unresolved) parts.push(`未检测到 ${unresolved}`)
  if (failed) parts.push(`失败 ${failed}`)
  if (needsElev) parts.push(`需提权 ${needsElev}`)
  const summary = `目标跟随同步完成：${parts.join(' · ')}`
  if (failed || needsElev) toast.warn(summary)
  else toast.success(summary)
  await refresh()
}

function openCtxMenu(ev: MouseEvent, row: EntryRow) {
  selectedKey.value = row.key
  closeMenus()
  // 贴边时向内收，避免菜单溢出窗口
  ctxX.value = Math.min(ev.clientX, window.innerWidth - 200)
  ctxY.value = Math.min(ev.clientY, window.innerHeight - 180)
  ctxVisible.value = true
}

function toggleOvr(ev: MouseEvent) {
  const r = (ev.currentTarget as HTMLElement).getBoundingClientRect()
  ovrX.value = Math.max(8, r.right - 190)
  ovrY.value = r.bottom + 4
  ovrVisible.value = !ovrVisible.value
}

async function probeTarget(row?: EntryRow) {
  if (!row) return
  try {
    const r = await AppService.TestTarget(row.connectAddress, row.connectPort)
    const head = `${row.name || row.listenText} → ${row.connectText}`
    const msg = `${r.ok ? '可达' : '不可达'}：${r.message}`
    if (r.ok) toast.success(`${head}\n${msg}`)
    else toast.error(`${head}\n${msg}`)
  } catch (e) {
    toast.error('测试失败：' + String(e))
  }
}

// ---- 键盘快捷键：Ctrl+N 新增 · Del 删除 · / 搜索 · F5 刷新 ----
function isTyping(t: EventTarget | null): boolean {
  const el = t as HTMLElement
  return el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT' || el.isContentEditable
}

function anyModalOpen(): boolean {
  return showEntryDialog.value || showLogViewer.value || showImportDialog.value
    || !!diagnoseTarget.value || confirmState.visible || ctxVisible.value || ovrVisible.value
}

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape') {
    closeMenus()
    return
  }
  if (anyModalOpen() || isTyping(e.target)) return
  const k = e.key.toLowerCase()
  if (e.key === 'F5') {
    e.preventDefault()
    void refresh()
  } else if ((e.ctrlKey || e.metaKey) && k === 'n') {
    e.preventDefault()
    openAdd()
  } else if (k === '/') {
    e.preventDefault()
    searchInput.value?.focus()
  } else if (e.key === 'Delete' && selectedKey.value) {
    void deleteRow(rowByKey(selectedKey.value))
  }
}

onMounted(async () => {
  await Promise.all([refresh(), loadEnv(), loadAutorun()])
  unlisten = Events.On('entries-changed', () => { void refresh() })
  window.addEventListener('keydown', onKeydown)
  document.addEventListener('click', closeMenus)
})

onUnmounted(() => {
  unlisten?.()
  window.removeEventListener('keydown', onKeydown)
  document.removeEventListener('click', closeMenus)
})
</script>

<template>
  <!-- 非管理员横幅：与 C# 版"只读可浏览、提权才能改"一致 -->
  <div v-if="env && !env.elevated" class="banner warn">
    <Icon name="warning" :size="15" />
    <span>当前以普通权限运行：可以浏览映射，但新增、编辑、删除需要管理员权限。</span>
  </div>

  <header class="titleband">
    <div class="brand">
      <svg class="brand-mark" viewBox="0 0 24 24" width="18" height="18" fill="none"
           stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true">
        <rect x="2" y="7" width="7" height="10" rx="1.5" />
        <rect x="15" y="7" width="7" height="10" rx="1.5" />
        <path d="M9 12h6" />
      </svg>
      <span class="brand-name">PortProxy 管理器</span>
      <span class="brand-mech">netsh interface portproxy</span>
    </div>
    <div class="grow" />
    <button class="btn primary" :disabled="!env?.elevated" @click="openAdd">
      <Icon name="plus" :size="15" />新增映射
    </button>
  </header>

  <div class="toolbar">
    <button class="btn" :disabled="!selectedKey" @click="openEdit(rowByKey(selectedKey))"
            title="编辑所选（或双击行）">
      <Icon name="edit" :size="14" />编辑
    </button>
    <button class="btn" :disabled="!selectedKey" @click="deleteRow(rowByKey(selectedKey))"
            title="删除所选（Delete）">
      <Icon name="trash" :size="14" />删除
    </button>
    <button class="btn" :disabled="!selectedKey" @click="copyCmd"
            title="复制所选映射的 netsh 命令">
      <Icon name="copy" :size="14" />复制命令
    </button>
    <span class="tsep" />
    <button class="btn" @click="syncFollow"
            title="按跟随规则重新探测客户机 IP，并同步重建映射">
      <Icon name="locate" :size="14" />同步跟随
    </button>
    <div class="grow" />
    <select v-model="groupFilter" class="fld" title="按分组过滤">
      <option value="">全部分组</option>
      <option v-for="g in groups" :key="g" :value="g">{{ g }}</option>
    </select>
    <span class="search">
      <Icon name="search" :size="13" />
      <input ref="searchInput" v-model="searchText" type="search" style="width: 210px"
             placeholder="搜索名称 / 地址 / 端口" title="按 / 聚焦搜索框" />
    </span>
    <button class="btn icon" :disabled="loading" @click="refresh" title="刷新（F5）">
      <Icon name="refresh" :size="14" :class="{ spin: loading }" />
    </button>
    <button class="btn icon" @click.stop="toggleOvr" title="更多：导入导出 / 快照 / 日志">
      <Icon name="more" :size="15" />
    </button>
  </div>

  <div class="table-wrap" @click="closeMenus" @dblclick="onTableDblClick">
    <table class="grid">
      <thead>
        <tr>
          <th style="width: 150px" :class="thCls('name')" @click="clickSort('name')">名称</th>
          <th style="width: 84px" :class="thCls('group')" @click="clickSort('group')">分组</th>
          <th style="width: 76px" :class="thCls('family')" @click="clickSort('family')">类型</th>
          <th :class="thCls('listenPort')" @click="clickSort('listenPort')">转发链路</th>
          <th style="width: 104px" class="non-sort">跟随</th>
          <th style="width: 56px" class="non-sort">防火墙</th>
          <th class="non-sort">备注</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="r in filteredRows" :key="r.key" data-key="1"
            :class="{ selected: r.key === selectedKey }"
            @click="selectedKey = r.key; closeMenus()"
            @dblclick="openEdit(r)"
            @contextmenu.prevent="openCtxMenu($event, r)">
          <td>
            <span class="name-cell">
              <span class="led" :class="{ on: !!r.followKind }"
                    :title="r.followKind ? '跟随 ' + kindLabel(r.followKind) : '无跟随'" />
              {{ r.name || '（未命名）' }}
            </span>
          </td>
          <td class="dim">{{ r.group ?? '' }}</td>
          <td class="dim">{{ familyLabel(r.family) }}</td>
          <td>
            <span class="link-cell" :title="r.netshCmd">
              <span class="mono lp">{{ r.listenText }}</span>
              <svg class="flow" :class="{ live: !!r.followKind }" viewBox="0 0 44 10"
                   width="44" height="10" aria-hidden="true">
                <line x1="1" y1="5" x2="35" y2="5" />
                <path d="M35 1.5 42.5 5 35 8.5 Z" />
              </svg>
              <span class="mono tp">{{ r.connectText }}</span>
            </span>
          </td>
          <td>
            <span v-if="r.followKind" class="tag follow"
                  :title="r.followState ? `跟随状态：${r.followState}` : kindLabel(r.followKind)">
              {{ kindLabel(r.followKind) }}
            </span>
            <span v-else class="dash">–</span>
          </td>
          <td>
            <span v-if="r.hasFirewallRule" class="fw-cell" title="已创建入站放行规则（TCP）">
              <Icon name="shield-check" :size="14" />
            </span>
            <span v-else class="dash" title="无防火墙放行规则">–</span>
          </td>
          <td class="dim note">{{ r.note ?? '' }}</td>
        </tr>
        <tr v-if="filteredRows.length === 0">
          <td colspan="7">
            <div class="empty">
              <template v-if="rows.length === 0">
                <Icon name="link" :size="34" />
                <p>还没有任何映射。</p>
                <p class="hint">一条映射就是一根跳线：本机监听端口 → 目标机器端口。</p>
                <button class="btn primary" :disabled="!env?.elevated" @click="openAdd">
                  <Icon name="plus" :size="14" />新增第一条映射
                </button>
              </template>
              <template v-else>
                <Icon name="search" :size="30" />
                <p>没有符合筛选条件的映射。</p>
                <button class="btn" @click="clearFilters">清除筛选</button>
              </template>
            </div>
          </td>
        </tr>
      </tbody>
    </table>
  </div>

  <footer class="statusbar">
    <button class="sbtn" :title="env?.dataDir ?? ''" @click="openDataDir">
      <Icon name="folder" :size="13" />{{ shortDataDir }}
    </button>
    <span class="sbtn" :class="env?.elevated ? 'ok' : 'warn'"
          :title="env?.elevated ? '以管理员运行：可增删改映射' : '普通权限运行：只能浏览'">
      <Icon name="shield" :size="13" />{{ env?.elevated ? '管理员' : '只读' }}
    </span>
    <button class="sbtn" title="开机自启（计划任务，需要管理员权限）" @click="toggleAutorun">
      <Icon name="power" :size="13" />自启 {{ autorun ? '开' : '关' }}
    </button>
    <div class="grow" />
    <span class="sbtn">{{ env?.user }}</span>
    <span class="sbtn">v{{ env?.appVersion }}</span>
  </footer>

  <!-- 右键菜单 -->
  <Teleport to="body">
    <div v-if="ctxVisible" class="ctx-menu" :style="{ left: ctxX + 'px', top: ctxY + 'px' }" @click.stop>
      <div class="mi" @click="closeMenus(); openEdit(rowByKey(selectedKey))"><Icon name="edit" :size="14" />编辑</div>
      <div class="mi" @click="closeMenus(); deleteRow(rowByKey(selectedKey))"><Icon name="trash" :size="14" />删除</div>
      <div class="mi" @click="closeMenus(); copyCmd()"><Icon name="copy" :size="14" />复制 netsh 命令</div>
      <div class="mi" @click="closeMenus(); probeTarget(rowByKey(selectedKey))"><Icon name="plug" :size="14" />测试目标连通性…</div>
      <div class="mi" @click="closeMenus(); openDiagnose(rowByKey(selectedKey))"><Icon name="activity" :size="14" />诊断…</div>
    </div>
  </Teleport>

  <!-- 更多操作菜单 -->
  <Teleport to="body">
    <div v-if="ovrVisible" class="ctx-menu" :style="{ left: ovrX + 'px', top: ovrY + 'px' }" @click.stop>
      <div class="mi" @click="closeMenus(); showImportDialog = true"><Icon name="transfer" :size="14" />导入 / 导出…</div>
      <div class="mi" @click="closeMenus(); createSnapshot()"><Icon name="camera" :size="14" />创建快照</div>
      <div class="mi" @click="closeMenus(); showLogViewer = true"><Icon name="terminal" :size="14" />操作日志</div>
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

  <!-- 全局反馈 -->
  <ToastStack />
  <ConfirmDialog />
</template>
