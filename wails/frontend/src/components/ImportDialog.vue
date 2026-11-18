<script setup lang="ts">
// 导入 / 导出（对应 C# UI/ImportPreviewDialog.cs）
import { ref } from 'vue'
import * as AppService from '../../bindings/portproxymanager/appservice'

const emit = defineEmits<{ (e: 'close'): void; (e: 'imported'): void }>()

const tab = ref<'import' | 'export'>('import')
const filePath = ref('')
const preview = ref<any>(null)
const selected = ref<number[]>([])
const message = ref('')
const busy = ref(false)

async function loadPreview() {
  message.value = ''
  if (!filePath.value.trim()) {
    message.value = '请输入文件路径。'
    return
  }
  busy.value = true
  try {
    preview.value = await AppService.PreviewImport(filePath.value.trim())
    if (preview.value.error) {
      message.value = '读取失败：' + preview.value.error
      preview.value = null
      return
    }
    // 默认全选
    selected.value = preview.value.diff.map((_: any, i: number) => i)
  } catch (e) {
    message.value = '读取失败：' + String(e)
  } finally {
    busy.value = false
  }
}

async function applyImport() {
  if (!preview.value) return
  busy.value = true
  message.value = ''
  try {
    const items = selected.value.map(i => preview.value.diff[i]).filter(Boolean)
    const res = await AppService.ApplyImport(items)
    message.value = `已执行 ${res.applied} 项${res.failed ? `，失败 ${res.failed} 项` : ''}` +
      (res.errors?.length ? '\n' + res.errors.join('\n') : '')
    if (res.ok) emit('imported')
  } catch (e) {
    message.value = '导入失败：' + String(e)
  } finally {
    busy.value = false
  }
}

async function doExport() {
  message.value = ''
  if (!filePath.value.trim()) {
    message.value = '请输入导出文件路径。'
    return
  }
  busy.value = true
  try {
    const res = await AppService.ExportEntries(filePath.value.trim())
    message.value = res.ok ? '✅ 导出成功' : '❌ ' + (res.error ?? '导出失败')
  } catch (e) {
    message.value = '导出失败：' + String(e)
  } finally {
    busy.value = false
  }
}

function kindLabel(kind: number): string {
  return ['新增', '覆盖', '删除', '无变化'][kind] ?? '?'
}
</script>

<template>
  <Teleport to="body">
    <div class="modal-mask" @click.self="emit('close')">
      <div class="modal" style="min-width: 700px">
        <div class="modal-header">导入 / 导出映射</div>
        <div class="modal-body">
          <div style="display:flex; gap:8px; margin-bottom: 12px">
            <button :style="{ fontWeight: tab === 'import' ? 700 : 400 }" @click="tab = 'import'">导入</button>
            <button :style="{ fontWeight: tab === 'export' ? 700 : 400 }" @click="tab = 'export'">导出</button>
          </div>

          <div class="form-grid" style="grid-template-columns: 70px 1fr">
            <label>文件路径</label>
            <input v-model="filePath" placeholder="例如 C:\Users\me\Desktop\mappings.json" />
          </div>

          <template v-if="tab === 'import'">
            <div style="margin-top: 10px">
              <button @click="loadPreview" :disabled="busy">加载并预览差异</button>
            </div>
            <div v-if="preview?.diff?.length" style="margin-top: 12px; max-height: 46vh; overflow: auto">
              <table class="grid" style="width: 100%">
                <thead><tr><th style="width: 40px"></th><th style="width: 60px">操作</th><th>监听</th><th>目标</th></tr></thead>
                <tbody>
                  <tr v-for="(d, i) in preview.diff" :key="i">
                    <td><input type="checkbox" v-model="selected" :value="i" /></td>
                    <td :style="{ color: d.kind === 2 ? '#dc2626' : d.kind === 1 ? '#ca8a04' : '#16a34a' }">{{ kindLabel(d.kind) }}</td>
                    <td class="mono">{{ d.entry.listenAddress }}:{{ d.entry.listenPort }}</td>
                    <td class="mono">{{ d.entry.connectAddress }}:{{ d.entry.connectPort }}</td>
                  </tr>
                </tbody>
              </table>
            </div>
            <div v-else-if="preview" class="empty-tip">没有差异。</div>
          </template>

          <div v-if="message" class="form-warning" style="margin-top: 10px; white-space: pre-wrap">{{ message }}</div>
        </div>
        <div class="modal-footer">
          <button @click="emit('close')">取消</button>
          <button v-if="tab === 'import'" class="primary" :disabled="busy || !preview?.diff?.length" @click="applyImport">执行导入</button>
          <button v-else class="primary" :disabled="busy" @click="doExport">导出</button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
