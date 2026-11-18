<script setup lang="ts">
// 单条映射的转发链路诊断（对应 C# UI/DiagnosticsDialog.cs）
import { onMounted, ref } from 'vue'
import * as AppService from '../../bindings/portproxymanager/appservice'
import type { EntryRow } from '../../bindings/portproxymanager/models'

const props = defineProps<{ entry: EntryRow }>()
const emit = defineEmits<{ (e: 'close'): void }>()

const report = ref<any>(null)
const loading = ref(true)
const fixing = ref(false)
const message = ref('')

const entryObj = {
  family: props.entry.family, protocol: props.entry.protocol,
  listenAddress: props.entry.listenAddress, listenPort: props.entry.listenPort,
  connectAddress: props.entry.connectAddress, connectPort: props.entry.connectPort,
}

async function run() {
  loading.value = true
  message.value = ''
  try {
    report.value = await AppService.Diagnose(entryObj)
  } catch (e) {
    message.value = '诊断失败：' + String(e)
  } finally {
    loading.value = false
  }
}

async function fix(action: number, label: string) {
  fixing.value = true
  message.value = ''
  try {
    const res = await AppService.FixDiagnostic(action, entryObj)
    message.value = (res.ok ? '✅ ' : '❌ ') + res.message
    await run() // 修复后重新诊断
  } catch (e) {
    message.value = '修复失败：' + String(e)
  } finally {
    fixing.value = false
  }
}

function copyReport() {
  if (!report.value) return
  const lines = [
    'PortProxy 诊断报告',
    `映射：${props.entry.family}/${props.entry.protocol} ${props.entry.listenText} → ${props.entry.connectText}`,
    `结果：通过 ${report.value.passCount}，警告 ${report.value.warnCount}，失败 ${report.value.failCount}，跳过 ${report.value.skipCount}`,
    '',
    ...report.value.checks.map((c: any) => `[${c.statusLabel}] ${c.name}：${c.detail}`),
  ]
  navigator.clipboard.writeText(lines.join('\r\n')).catch(() => {})
}

onMounted(run)
</script>

<template>
  <Teleport to="body">
    <div class="modal-mask" @click.self="emit('close')">
      <div class="modal" style="min-width: 640px">
        <div class="modal-header">
          诊断：{{ entry.name || entry.listenText }} → {{ entry.connectText }}
        </div>
        <div class="modal-body">
          <div v-if="loading" class="empty-tip">诊断中…</div>
          <template v-else-if="report">
            <div style="margin-bottom: 10px; font-size: 12px">
              <span style="color: #16a34a">通过 {{ report.passCount }}</span> ·
              <span style="color: #ca8a04">警告 {{ report.warnCount }}</span> ·
              <span style="color: #dc2626">失败 {{ report.failCount }}</span> ·
              跳过 {{ report.skipCount }}
            </div>
            <table class="grid" style="width: 100%">
              <thead><tr><th style="width: 60px">状态</th><th style="width: 110px">检查项</th><th>详情</th><th style="width: 110px">操作</th></tr></thead>
              <tbody>
                <tr v-for="(c, i) in report.checks" :key="i">
                  <td :style="{ color: c.status === 0 ? '#16a34a' : c.status === 1 ? '#ca8a04' : c.status === 2 ? '#dc2626' : '#9ca3af', fontWeight: 600 }">{{ c.statusLabel }}</td>
                  <td>{{ c.name }}</td>
                  <td style="white-space: normal">{{ c.detail }}</td>
                  <td>
                    <button v-if="c.action > 0 && c.actionLabel" :disabled="fixing"
                            @click="fix(c.action, c.actionLabel)">{{ c.actionLabel }}</button>
                  </td>
                </tr>
              </tbody>
            </table>
          </template>
          <div v-if="message" class="form-warning" style="margin-top: 10px">{{ message }}</div>
        </div>
        <div class="modal-footer">
          <button @click="copyReport">复制报告</button>
          <button @click="run" :disabled="loading || fixing">重新诊断</button>
          <button @click="emit('close')">关闭</button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
