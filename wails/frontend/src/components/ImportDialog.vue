<script setup lang="ts">
// 导入 / 导出（对应 C# UI/ImportPreviewDialog.cs）
import { ref } from 'vue'
import * as AppService from '../../bindings/portproxymanager/appservice'
import Icon from '../ui/Icon.vue'
import { toast } from '../ui/toast'

const emit = defineEmits<{ (e: 'close'): void; (e: 'imported'): void }>()

const tab = ref<'import' | 'export'>('import')
const filePath = ref('')
const preview = ref<any>(null)
const selected = ref<number[]>([])
const message = ref('')
const messageOk = ref(false)
const busy = ref(false)

function setMessage(text: string, ok = false) {
  message.value = text
  messageOk.value = ok
}

async function loadPreview() {
  setMessage('')
  if (!filePath.value.trim()) {
    setMessage('请输入文件路径。')
    return
  }
  busy.value = true
  try {
    preview.value = await AppService.PreviewImport(filePath.value.trim())
    if (preview.value.error) {
      setMessage('读取失败：' + preview.value.error)
      preview.value = null
      return
    }
    // 默认全选
    selected.value = preview.value.diff.map((_: any, i: number) => i)
  } catch (e) {
    setMessage('读取失败：' + String(e))
  } finally {
    busy.value = false
  }
}

async function applyImport() {
  if (!preview.value) return
  busy.value = true
  setMessage('')
  try {
    const items = selected.value.map(i => preview.value.diff[i]).filter(Boolean)
    const res = await AppService.ApplyImport(items)
    setMessage(`已执行 ${res.applied} 项${res.failed ? `，失败 ${res.failed} 项` : ''}` +
      (res.errors?.length ? '\n' + res.errors.join('\n') : ''), res.ok)
    if (res.ok) emit('imported')
  } catch (e) {
    setMessage('导入失败：' + String(e))
  } finally {
    busy.value = false
  }
}

async function doExport() {
  setMessage('')
  if (!filePath.value.trim()) {
    setMessage('请输入导出文件路径。')
    return
  }
  busy.value = true
  try {
    const res = await AppService.ExportEntries(filePath.value.trim())
    if (res.ok) {
      setMessage('导出成功', true)
      toast.success('映射已导出')
    } else {
      setMessage('导出失败：' + (res.error ?? ''))
    }
  } catch (e) {
    setMessage('导出失败：' + String(e))
  } finally {
    busy.value = false
  }
}

function kindLabel(kind: number): string {
  return ['新增', '覆盖', '删除', '无变化'][kind] ?? '?'
}

function kindCls(kind: number): string {
  return ['kind-add', 'kind-over', 'kind-del', 'kind-same'][kind] ?? ''
}
</script>

<template>
  <Teleport to="body">
    <div class="modal-mask" @click.self="emit('close')">
      <div class="modal" style="width: 780px">
        <div class="modal-header">导入 / 导出映射</div>
        <div class="modal-body">
          <div style="margin-bottom: 12px">
            <div class="seg">
              <button :class="{ on: tab === 'import' }" @click="tab = 'import'">导入</button>
              <button :class="{ on: tab === 'export' }" @click="tab = 'export'">导出</button>
            </div>
          </div>

          <div class="form-grid" style="grid-template-columns: 70px 1fr">
            <label>文件路径</label>
            <input v-model="filePath" placeholder="例如 C:\Users\me\Desktop\mappings.json" />
          </div>

          <template v-if="tab === 'import'">
            <div style="margin-top: 10px">
              <button class="btn" @click="loadPreview" :disabled="busy">加载并预览差异</button>
            </div>
            <div v-if="preview?.diff?.length" style="margin-top: 12px; max-height: 46vh; overflow: auto">
              <table class="grid" style="width: 100%">
                <thead><tr><th style="width: 40px"></th><th style="width: 60px">操作</th><th>监听</th><th>目标</th></tr></thead>
                <tbody>
                  <tr v-for="(d, i) in preview.diff" :key="i">
                    <td><input type="checkbox" v-model="selected" :value="i" /></td>
                    <td :class="kindCls(d.kind)">{{ kindLabel(d.kind) }}</td>
                    <td class="mono">{{ d.entry.listenAddress }}:{{ d.entry.listenPort }}</td>
                    <td class="mono">{{ d.entry.connectAddress }}:{{ d.entry.connectPort }}</td>
                  </tr>
                </tbody>
              </table>
            </div>
            <div v-else-if="preview" class="empty-tip">没有差异。</div>
          </template>

          <div v-if="message" :class="messageOk ? 'form-ok' : 'form-error'"
               style="margin-top: 10px; white-space: pre-wrap">{{ message }}</div>
        </div>
        <div class="modal-footer">
          <button class="btn" @click="emit('close')">取消</button>
          <button v-if="tab === 'import'" class="btn primary" :disabled="busy || !preview?.diff?.length" @click="applyImport">执行导入</button>
          <button v-else class="btn primary" :disabled="busy" @click="doExport">
            <Icon name="transfer" :size="14" />导出
          </button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
