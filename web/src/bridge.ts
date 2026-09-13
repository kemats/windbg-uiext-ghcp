export type Mode = 'AskEveryTime' | 'ApproveAll'
export interface AttachmentInfo { name: string; mimeType: string; size: number; data?: string | null }
export interface ChatAttachment { name: string; mimeType: string; data: string }
export interface ModelPrices { batchSize: number | null; input: number | null; output: number | null; cacheRead: number | null; cacheWrite: number | null }
export interface ModelOption {
  id: string; name: string; contextTokens?: number | null; maxPromptTokens?: number | null; vision?: boolean | null
  reasoningEfforts?: string[] | null; defaultReasoningEffort?: string | null; multiplier?: number | null; policy?: string | null; prices?: ModelPrices | null
}
export interface Message { id: string; role: 'user' | 'assistant' | 'reasoning' | 'thinking' | 'tool'; text: string; complete: boolean; turnId?: string; timestamp?: string; model?: string | null; attachments?: AttachmentInfo[] | null; title?: string | null; activityStatus?: string | null }
export interface SessionEntry { id: string; title: string; updatedAt: string }
export interface TurnInfo {
  id: string; startedAt: string; completedAt: string | null; models: string[]; nanoAiu: number | null
  inputTokens: number | null; outputTokens: number | null; usageCalls: number; unreportedCostCalls: number
}
export interface SessionInfo {
  startedAt: string; nanoAiu: number | null; unreportedCostCalls: number; currentTokens: number | null
  tokenLimit: number | null; systemTokens: number | null; toolDefinitionsTokens: number | null
  conversationTokens: number | null; messagesLength: number | null
}
export interface ToolOption { id: string; name: string; group: string; description: string | null; selected: boolean }
export interface McpOption { name: string; enabled: boolean; status: string; canAuthenticate?: boolean; canReauthenticate?: boolean }
export interface ToolSettings { tools: ToolOption[]; servers: McpOption[]; configPath: string | null; error?: string | null }
export interface Approval { id: string; kind: 'execute' | 'share' | 'tool' | 'permission'; text: string }
export interface Snapshot {
  sessionId: string; mode: Mode; busy: boolean; status: string; error: string | null; model: string | null
  target: { available: boolean }
  messages: Message[]; approvals: Approval[]; models: ModelOption[]
  account?: { login: string | null; host: string | null; authenticated: boolean } | null
  info?: SessionInfo | null; turns?: TurnInfo[] | null
  sessions?: SessionEntry[] | null; sessionTitle?: string | null
  toolSettings?: ToolSettings | null
}
export interface Envelope { version: number; sequence: number; type: string; payload: unknown }
export interface Request {
  version: number; type: string; requestId: string; sessionId?: string
  text?: string; model?: string; mode?: Mode; approvalId?: string; approved?: boolean
  attachments?: ChatAttachment[]
  historyId?: string
  tools?: string[]; servers?: string[]
  diagrams?: string[]
}
interface WebView {
  postMessage(request: Request): void
  addEventListener(type: 'message', handler: (event: MessageEvent<Envelope>) => void): void
  removeEventListener(type: 'message', handler: (event: MessageEvent<Envelope>) => void): void
}
declare global { interface Window { chrome?: { webview?: WebView } } }
export const demo = !window.chrome?.webview
export const initial: Snapshot = {
  sessionId: '', mode: 'AskEveryTime', busy: false, status: 'Disconnected', error: null, model: null,
  target: { available: false }, messages: [], approvals: [], models: [],
}
function demoInfo(): SessionInfo {
  return { startedAt: new Date().toISOString(), nanoAiu: null, unreportedCostCalls: 0, currentTokens: null,
    tokenLimit: null, systemTokens: null, toolDefinitionsTokens: null, conversationTokens: null, messagesLength: null }
}
const demoElevated = new URLSearchParams(location.search).get('elevated') === '1'
let mock: Snapshot = { ...initial, model: 'auto', sessionId: crypto.randomUUID(), status: 'Ready', target: { available: true }, models: [
  { id: 'demo', name: 'Demo model', contextTokens: 8000, maxPromptTokens: 6000, vision: true, reasoningEfforts: ['low', 'medium', 'high'], defaultReasoningEffort: 'medium', prices: { batchSize: 1000, input: 0.1, output: 0.4, cacheRead: 0.01, cacheWrite: 0 } },
  { id: 'demo-text', name: 'Demo text model', contextTokens: 16000, vision: false },
  { id: 'auto', name: 'Auto' },
],
  account: { login: 'demo-user', host: 'github.com', authenticated: true }, info: demoInfo(), turns: [],
  toolSettings: { configPath: 'mcp.json', servers: [{ name: 'example-mcp', enabled: false, status: 'disabled' }], tools: [
    { id: 'debugger_command', name: 'debugger_command', group: 'WinDbg', description: 'Execute a command on the current target.', selected: true },
    { id: 'debugger_target', name: 'debugger_target', group: 'WinDbg', description: 'Read current target details.', selected: true },
    { id: 'view', name: 'view', group: 'Built-In', description: 'Read a file.', selected: false },
    { id: 'powershell', name: 'powershell', group: 'Built-In', description: 'Execute a PowerShell command.', selected: false },
  ] } }
const listeners = new Set<(event: Envelope) => void>()
const history = new Map<string, Snapshot>()
function remember() {
  const previous = mock.sessions?.find(entry => entry.id === mock.sessionId)
  const entry = { id: mock.sessionId, title: previous?.title && previous.title !== 'New chat' ? previous.title : mock.messages.find(message => message.role === 'user')?.text.slice(0, 80) || 'New chat', updatedAt: new Date().toISOString() }
  mock.sessionTitle = entry.title
  mock.sessions = [entry, ...(mock.sessions || []).filter(item => item.id !== entry.id)]
  history.set(mock.sessionId, structuredClone(mock))
}
let sequence = 0
let timer: ReturnType<typeof setInterval> | undefined
let executed = false
function emit() {
  const envelope = { version: 1, sequence: ++sequence, type: 'snapshot', payload: structuredClone(mock) }
  listeners.forEach(listener => listener(envelope))
}
function finish(text: string) {
  const id = crypto.randomUUID()
  mock.approvals = []
  mock.messages.forEach(message => { message.complete = true })
  if (executed) mock.messages.push({ id: crypto.randomUUID(), role: 'tool', title: 'debugger_command', text: 'k\n\nDEMO OUTPUT\nexample!Worker\nexample!main', complete: true })
  const modelName = mock.models.find(model => model.id === mock.model)?.name || 'Demo model'
  mock.messages.push({ id, role: 'assistant', text: '', complete: false, turnId: mock.turns?.at(-1)?.id, timestamp: new Date().toISOString(), model: modelName })
  let offset = 0
  timer = setInterval(() => {
    const message = mock.messages.find(item => item.id === id)
    if (!message) { clearInterval(timer); return }
    offset += 32
    message.text = text.slice(0, offset)
    if (offset >= text.length) {
      clearInterval(timer); message.complete = true; mock.busy = false; mock.status = 'Ready'
      const turn = mock.turns?.at(-1)
      if (turn) Object.assign(turn, { completedAt: new Date().toISOString(), models: [modelName], nanoAiu: 125_000_000, inputTokens: 1200, outputTokens: 160, usageCalls: 1 })
      mock.info = { ...mock.info!, nanoAiu: (mock.info?.nanoAiu ?? 0) + 125_000_000, currentTokens: 1360, tokenLimit: 8000,
        systemTokens: 400, toolDefinitionsTokens: 300, conversationTokens: 660, messagesLength: mock.messages.length }
    }
    emit()
  }, 30)
  emit()
}
function mockRequest(request: Request) {
  switch (request.type) {
    case 'ready':
      listeners.forEach(listener => listener({ version: 1, sequence: ++sequence, type: 'host', payload: { elevated: demoElevated } }))
      remember(); emit(); break
    case 'connect': remember(); emit(); break
    case 'signIn':
      remember()
      mock = { ...mock, sessionId: crypto.randomUUID(), sessionTitle: 'New chat', busy: false, status: 'Ready', mode: 'AskEveryTime', messages: [], approvals: [], info: demoInfo(), turns: [], account: { login: 'other-demo-user', host: 'github.com', authenticated: true } }
      remember(); emit(); break
    case 'new':
      clearInterval(timer)
      remember()
      mock = { ...mock, sessionId: crypto.randomUUID(), sessionTitle: 'New chat', model: request.model || null, busy: false, status: 'Ready', mode: 'AskEveryTime', messages: [], approvals: [], info: demoInfo(), turns: [] }
      remember()
      emit(); break
    case 'resume': {
      remember()
      const saved = history.get(request.historyId || '')
      if (saved) mock = { ...structuredClone(saved), toolSettings: mock.toolSettings, sessions: mock.sessions, busy: false, mode: 'AskEveryTime', approvals: [] }
      emit(); listeners.forEach(listener => listener({ version: 1, sequence: ++sequence, type: 'sessionChanged', payload: {} })); break
    }
    case 'rename': {
      mock.sessions = mock.sessions?.map(entry => entry.id === request.historyId ? { ...entry, title: request.text || entry.title } : entry)
      if (request.historyId === mock.sessionId) mock.sessionTitle = request.text || mock.sessionTitle
      const saved = history.get(request.historyId || '')
      if (saved) saved.sessionTitle = request.text || saved.sessionTitle
      emit(); break
    }
    case 'delete':
      history.delete(request.historyId || '')
      mock.sessions = mock.sessions?.filter(entry => entry.id !== request.historyId)
      emit(); break
    case 'command':
      mock.status = 'Ready'
      emit(); break
    case 'openReport': case 'openReportReader': break
    case 'tools':
      if (mock.busy || !mock.toolSettings) return
      mock.toolSettings.servers = mock.toolSettings.servers.map(server => ({ ...server, enabled: !!request.servers?.includes(server.name), canAuthenticate: false, status: request.servers?.includes(server.name) ? 'connected' : 'disabled' }))
      mock.toolSettings.tools = mock.toolSettings.tools.filter(tool => tool.group !== 'example-mcp')
      if (request.servers?.includes('example-mcp')) mock.toolSettings.tools.push({ id: 'example-mcp-read', name: 'read', group: 'example-mcp', description: 'Read example data.', selected: false })
      mock.toolSettings.tools = mock.toolSettings.tools.map(tool => ({ ...tool, selected: !!request.tools?.includes(tool.id) }))
      emit(); listeners.forEach(listener => listener({ version: 1, sequence: ++sequence, type: 'toolsChanged', payload: {} })); break
    case 'reloadTools': case 'loadMcp': case 'authenticateMcp': case 'reauthenticateMcp':
      emit(); listeners.forEach(listener => listener({ version: 1, sequence: ++sequence, type: 'toolsChanged', payload: {} })); break
    case 'model':
      if (mock.busy) return
      mock.model = request.model || null; emit()
      listeners.forEach(listener => listener({ version: 1, sequence: ++sequence, type: 'modelChanged', payload: { sessionId: mock.sessionId } }))
      break
    case 'mode':
      mock.mode = request.mode!
      if (mock.mode === 'ApproveAll' && mock.approvals.length && mock.status !== 'Running command') {
        executed = true; finish('Synthetic demo output. The command and output were automatically approved.')
      } else emit()
      break
    case 'cancel':
      clearInterval(timer); mock.busy = false; mock.status = 'Cancelled'; mock.approvals = []
      mock.messages.forEach(message => { message.complete = true }); emit(); break
    case 'send':
      if (mock.busy) return
      executed = false
      mock.busy = true; mock.status = 'Analyzing'
      {
        const id = crypto.randomUUID(), timestamp = new Date().toISOString()
        mock.messages.push({ id, role: 'user', text: request.text ?? '', complete: true, turnId: id, timestamp,
          attachments: request.attachments?.map(file => ({ name: file.name, mimeType: file.mimeType, size: atob(file.data).length, data: file.mimeType.startsWith('image/') ? file.data : undefined })) })
        mock.turns?.push({ id, startedAt: timestamp, completedAt: null, models: [], nanoAiu: null, inputTokens: null, outputTokens: null, usageCalls: 0, unreportedCostCalls: 0 })
        mock.messages.push({ id: crypto.randomUUID(), role: 'reasoning', title: 'Reasoning', text: 'Synthetic reasoning event: inspect the current stack before drawing a conclusion.', complete: false, turnId: id })
      }
      remember()
      if (mock.mode === 'ApproveAll') { executed = true; finish('Synthetic demo output. The command and output were automatically approved.'); break }
      mock.approvals = [{ id: crypto.randomUUID(), kind: 'execute', text: 'k' }]
      emit(); break
    case 'approval': {
      const pending = mock.approvals.find(item => item.id === request.approvalId)
      if (!pending) return
      if (mock.status === 'Running command') {
        mock.messages.push({ id: crypto.randomUUID(), role: 'tool', title: 'User command: ' + pending.text, text: request.approved ? 'Synthetic local output' : 'Command execution denied.', complete: true })
        mock.busy = false; mock.status = 'Ready'; mock.approvals = []; emit(); break
      }
      if (!request.approved) { finish('The request was denied. No further debugger action was taken.'); return }
      if (pending.kind === 'execute') {
        executed = true
        mock.approvals = [{ ...pending, id: crypto.randomUUID(), kind: 'share', text: 'DEMO OUTPUT\nexample!Worker\nexample!main' }]
        emit()
      } else finish('## Stack summary\n\nThis is **synthetic demo output**, not a live target. The worker was called by `main`.\n\n```text\nexample!Worker\nexample!main\n```\n\n```mermaid\nflowchart TD\n  Main[main] --> Worker[Worker]\n```\n\nNo fault can be established from this sample stack alone.')
      break
    }
  }
}
export function send(type: string, fields: Omit<Partial<Request>, 'type' | 'version' | 'requestId'> = {}) {
  const request = { version: 1, requestId: crypto.randomUUID(), type, ...fields }
  if (window.chrome?.webview) window.chrome.webview.postMessage(request)
  else mockRequest(request)
}
export function subscribe(handler: (event: Envelope) => void) {
  const native = window.chrome?.webview
  const receive = (event: MessageEvent<Envelope>) => handler(event.data)
  if (native) native.addEventListener('message', receive)
  else listeners.add(handler)
  return () => {
    native?.removeEventListener('message', receive)
    listeners.delete(handler)
  }
}