// UI 预览用 mock：拦截 Wails runtime 的 HTTP 调用（POST /wails/runtime），
// 按绑定方法 ID 返回固定样例数据。仅用于 `npm run dev` + mocked.html。
// 方法 ID 与 bindings/portproxymanager/appservice.ts 保持一致。

type Handler = (args: any[]) => unknown

const now = '2026-11-20 08:27'

function mkRow(p: Record<string, any>): any {
  return {
    protocol: 'tcp',
    name: null, group: null, note: null, createdAt: now,
    followKind: null, followId: null, followMac: null, followLabel: null,
    followState: '',
    hasFirewallRule: false,
    ...p,
  }
}

let rows: any[] = [
  mkRow({
    key: 'v4tov4/tcp/0.0.0.0/3389', family: 'v4tov4',
    listenAddress: '0.0.0.0', listenPort: 3389,
    connectAddress: '172.28.16.3', connectPort: 3389,
    listenText: '0.0.0.0:3389', connectText: '172.28.16.3:3389',
    netshCmd: 'netsh interface portproxy add v4tov4 tcp listenaddress=0.0.0.0 listenport=3389 connectaddress=172.28.16.3 connectport=3389',
    name: '开发跳板', group: '开发环境',
    followKind: 'wsl', followId: 'Ubuntu-22.04', followLabel: 'WSL Ubuntu-22.04',
    followState: '已更新', hasFirewallRule: true,
  }),
  mkRow({
    key: 'v4tov4/tcp/0.0.0.0/3306', family: 'v4tov4',
    listenAddress: '0.0.0.0', listenPort: 3306,
    connectAddress: '192.168.1.20', connectPort: 3306,
    listenText: '0.0.0.0:3306', connectText: '192.168.1.20:3306',
    netshCmd: 'netsh interface portproxy add v4tov4 tcp listenaddress=0.0.0.0 listenport=3306 connectaddress=192.168.1.20 connectport=3306',
    name: 'MySQL 直连', group: '数据库', note: '测试库', hasFirewallRule: true,
  }),
  mkRow({
    key: 'v4tov4/tcp/0.0.0.0/8080', family: 'v4tov4',
    listenAddress: '0.0.0.0', listenPort: 8080,
    connectAddress: '172.20.4.9', connectPort: 80,
    listenText: '0.0.0.0:8080', connectText: '172.20.4.9:80',
    netshCmd: 'netsh interface portproxy add v4tov4 tcp listenaddress=0.0.0.0 listenport=8080 connectaddress=172.20.4.9 connectport=80',
    name: '沙盒 Web', group: '沙盒',
    followKind: 'wsb', followId: 'demo.wsb', followLabel: '沙盒 demo.wsb',
    followState: '已更新',
  }),
  mkRow({
    key: 'v4tov4/tcp/0.0.0.0/9022', family: 'v4tov4',
    listenAddress: '0.0.0.0', listenPort: 9022,
    connectAddress: '192.168.40.5', connectPort: 22,
    listenText: '0.0.0.0:9022', connectText: '192.168.40.5:22',
    netshCmd: 'netsh interface portproxy add v4tov4 tcp listenaddress=0.0.0.0 listenport=9022 connectaddress=192.168.40.5 connectport=22',
    name: '构建机 SSH', group: '虚拟机', note: 'Hyper-V Ubuntu',
    followKind: 'hyperv', followId: 'build-01', followLabel: 'Hyper-V build-01',
    followState: '无变化', hasFirewallRule: true,
  }),
  mkRow({
    key: 'v6tov6/tcp/::/8443', family: 'v6tov6',
    listenAddress: '::', listenPort: 8443,
    connectAddress: 'fd00::5', connectPort: 8443,
    listenText: '[::]:8443', connectText: '[fd00::5]:8443',
    netshCmd: 'netsh interface portproxy add v6tov6 tcp listenaddress=:: listenport=8443 connectaddress=fd00::5 connectport=8443',
    name: 'IPv6 网关',
  }),
  mkRow({
    key: 'v4tov4/tcp/0.0.0.0/9000', family: 'v4tov4',
    listenAddress: '0.0.0.0', listenPort: 9000,
    connectAddress: '10.0.0.2', connectPort: 80,
    listenText: '0.0.0.0:9000', connectText: '10.0.0.2:80',
    netshCmd: 'netsh interface portproxy add v4tov4 tcp listenaddress=0.0.0.0 listenport=9000 connectaddress=10.0.0.2 connectport=80',
    name: '老路由转发', group: '开发环境', note: '内网老设备 Web 管理页', hasFirewallRule: true,
  }),
]

const candidates = [
  { kind: 'wsl', address: '172.28.16.3', id: 'Ubuntu-22.04', display: '172.28.16.3 - WSL:Ubuntu-22.04' },
  { kind: 'wsb', address: '172.20.4.9', id: 'demo.wsb', display: '172.20.4.9 - WindowsSandbox:demo.wsb' },
  { kind: 'hyperv', address: '192.168.40.5', id: 'build-01', mac: '00155d0a1b02', display: '192.168.40.5 - HyperV:build-01' },
  { kind: 'vm', address: '192.168.40.9', id: 'vmware-dev', mac: '005056c00008', display: '192.168.40.9 - VMware:vmware-dev' },
  { kind: 'gateway', address: '192.168.1.1', display: '192.168.1.1 - 网关:default' },
  { kind: 'lan', address: '192.168.1.50', display: '192.168.1.50 - 局域网设备:printer' },
]

let autorun = true

const handlers: Record<number, Handler> = {
  // ListEntries
  3071081926: () => rows,
  // ListGroups
  856412414: () => [...new Set(rows.map(r => r.group).filter(Boolean))],
  // GetEnv
  1987093081: () => ({
    dataDir: 'C:\\ProgramData\\PortProxyManager',
    sourceLabel: '%ProgramData%\\PortProxyManager',
    elevated: true,
    user: 'netcat',
    appVersion: '2.1.0',
  }),
  // IsAutorunEnabled
  2442277387: () => autorun,
  // EnableAutorun / DisableAutorun
  3538867827: () => { autorun = true; return { ok: true, message: '' } },
  704698498: () => { autorun = false; return { ok: true, message: '' } },
  // InferFamily
  668632086: () => 'v4tov4',
  // ListCandidates
  4220849212: () => candidates,
  // TestTarget
  3645264907: () => ({ ok: true, message: 'TCP 连接成功（12 ms）' }),
  // SyncFollow
  549847948: () => ({
    updatedCount: 1, failedCount: 0, unresolvedCount: 0, needsElevationCount: 0,
    items: rows.map(r => ({ key: r.key })).slice(0, 4),
  }),
  // ValidateEntry
  272864264: () => ({ ok: true, error: null, warning: null }),
  // AddEntry
  2515791711: (args) => {
    const e = args[0]
    rows = [...rows, mkRow({
      key: `${e.family}/${e.protocol}/${e.listenAddress}/${e.listenPort}`,
      family: e.family, protocol: e.protocol, listenAddress: e.listenAddress, listenPort: e.listenPort,
      connectAddress: e.connectAddress, connectPort: e.connectPort,
      listenText: `${e.listenAddress === '*' ? '0.0.0.0' : e.listenAddress}:${e.listenPort}`,
      connectText: `${e.connectAddress}:${e.connectPort}`,
      netshCmd: 'netsh interface portproxy add …',
      name: args[1]?.name, group: args[1]?.group, note: args[1]?.note,
      hasFirewallRule: !!args[1]?.addFirewallRule,
    })]
    return { ok: true, error: null, entry: { key: rows[rows.length - 1].key } }
  },
  // UpdateEntry
  2493502397: () => ({ ok: true, error: null, entry: {} }),
  // DeleteEntry
  3258593699: (args) => {
    const e = args[0]
    rows = rows.filter(r => !(r.family === e.family && r.protocol === e.protocol
      && r.listenAddress === e.listenAddress && r.listenPort === e.listenPort))
    return { ok: true, error: null }
  },
  // SetFollowTarget
  2132722906: () => ({ ok: true, error: null }),
  // CreateSnapshot
  2836472162: () => ({ ok: true, error: null, message: 'backups/backup-20261120-090000-手动.json' }),
  // OpenDataDir
  3774760905: () => ({ ok: true, error: null }),
  // Diagnose
  734433986: () => ({
    passCount: 4, warnCount: 1, failCount: 1, skipCount: 1,
    checks: [
      { status: 0, statusLabel: '通过', name: '注册表条目', detail: '注册表中存在该映射，参数与列表一致。', action: 0 },
      { status: 0, statusLabel: '通过', name: 'IP Helper 服务', detail: 'iphlpsvc 正在运行。', action: 0 },
      { status: 0, statusLabel: '通过', name: '监听端口', detail: '0.0.0.0:3389 已在监听。', action: 0 },
      { status: 1, statusLabel: '警告', name: '端口占用检测', detail: '监听端口同时被其他进程以 UDP 绑定，TCP 转发不受影响。', action: 0 },
      { status: 2, statusLabel: '失败', name: '防火墙规则', detail: '没有找到与监听端口匹配的入站放行规则。', action: 2, actionLabel: '添加防火墙规则' },
      { status: 0, statusLabel: '通过', name: '目标连通性', detail: '目标 172.28.16.3:3389 TCP 可达（12 ms）。', action: 0 },
      { status: 3, statusLabel: '跳过', name: '客户机跟随', detail: '跳过：该映射未启用目标跟随。', action: 0 },
    ],
  }),
  // FixDiagnostic
  2936153142: () => ({ ok: true, message: '已添加防火墙入站放行规则。' }),
  // ListLogs
  1220588237: () => ([
    { name: 'portproxy-20261120.log', size: 4096, modTime: '2026-11-20 08:27' },
    { name: 'portproxy-20261119.log', size: 8192, modTime: '2026-11-19 21:03' },
  ]),
  // ReadLog
  2650266520: () => [
    '2026-11-20 08:27:11 [INFO] 启动 PortProxyManager v2.1.0（管理员）',
    '2026-11-20 08:27:11 [INFO] 数据目录 C:\\ProgramData\\PortProxyManager',
    '2026-11-20 08:27:12 [INFO] 读取注册表映射 6 条',
    '2026-11-20 08:27:40 [INFO] 跟随同步：更新 1，无变化 3',
    '2026-11-20 08:31:02 [INFO] netsh add v4tov4 tcp listenaddress=0.0.0.0 listenport=8080 connectaddress=172.20.4.9 connectport=80',
    '2026-11-20 08:31:02 [INFO] 写后核对通过',
  ].join('\r\n'),
  // PreviewImport
  3827841547: () => ({
    error: null,
    diff: [
      { kind: 0, entry: { family: 'v4tov4', protocol: 'tcp', listenAddress: '0.0.0.0', listenPort: 15432, connectAddress: '192.168.1.20', connectPort: 5432 } },
      { kind: 1, entry: { family: 'v4tov4', protocol: 'tcp', listenAddress: '0.0.0.0', listenPort: 3306, connectAddress: '192.168.1.21', connectPort: 3306 } },
      { kind: 2, entry: { family: 'v4tov4', protocol: 'tcp', listenAddress: '0.0.0.0', listenPort: 9000, connectAddress: '10.0.0.2', connectPort: 80 } },
    ],
  }),
  // LoadImportDocument
  147579594: () => ({ error: null, diff: [] }),
  // ApplyImport
  1803268863: () => ({ ok: true, applied: 3, failed: 0, errors: [] }),
  // ExportEntries
  498746392: () => ({ ok: true, error: null, message: 'mappings.json' }),
  // PreviewNetsh
  1600621554: () => 'netsh interface portproxy add v4tov4 tcp …',
}

// 拦截 Wails runtime 的 HTTP 传输层。
// 请求体形状：{ object, method, args: { "call-id", methodID, args: [...] } }
const origFetch = window.fetch.bind(window)
window.fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
  const url = typeof input === 'string' ? input : (input instanceof URL ? input.href : input.url)
  if (url.includes('/wails/runtime') && init?.method === 'POST' && typeof init.body === 'string') {
    let body: any = {}
    try { body = JSON.parse(init.body) } catch { /* 非 JSON 交给原 fetch */ }
    const payload = body?.args ?? {}
    const methodId = payload?.methodID
    const handler = typeof methodId === 'number' ? handlers[methodId] : undefined
    if (handler) {
      const result = handler(payload.args ?? [])
      return new Response(JSON.stringify(result), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }
  }
  return origFetch(input, init)
}
