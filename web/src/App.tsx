import { useEffect, useRef, useState } from 'react'
import type { MouseEvent as ReactMouseEvent } from 'react'
import { ArrowUp, BookOpen, Check, Copy, ExternalLink, FileText, Github, History, Image, Info, LoaderCircle, LogIn, Paperclip, Plug, Plus, ShieldCheck, Square, Terminal, Volume2, X } from 'lucide-react'
import { demo, initial, send, subscribe } from './bridge'
import type { Mode, Snapshot } from './bridge'
import { Markdown } from './Markdown'
import { useSpeech } from './useSpeech'
import { credits, number, percent, time } from './metadata'
import { ModelPicker } from './ModelPicker'
import { ToolPicker } from './ToolPicker'
import { Activity, Banner, ImagePreview, SessionHistory, SessionName } from './ChatExtras'
import { fileAccept, fileSize, readAttachments } from './attachments'
import type { DraftAttachment } from './attachments'
import './App.css'

export default function App() {
  const [state, setState] = useState(initial)
  const [draft, setDraft] = useState('')
  const [modelPending, setModelPending] = useState(false)
  const [historyOpen, setHistoryOpen] = useState(false)
  const [accountOpen, setAccountOpen] = useState(false)
  const [sessionPending, setSessionPending] = useState(false)
  const [inputHeight, setInputHeight] = useState(() => {
    try { return Math.max(76, Math.min(360, Number(localStorage.getItem('composerHeight')) || 90)) } catch { return 90 }
  })
  const resizeStart = useRef<{ y: number; height: number } | null>(null)
  const [attachments, setAttachments] = useState<DraftAttachment[]>([])
  const [readingFiles, setReadingFiles] = useState(false)
  const [sending, setSending] = useState(false)
  const pendingSend = useRef<{ sessionId: string; lastUserId?: string } | null>(null)
  const reading = useRef(false)
  const draftGeneration = useRef(0)
  const fileInput = useRef<HTMLInputElement>(null)
  const [error, setError] = useState<string | null>(null)
  const [connecting, setConnecting] = useState(false)
  const autoConnectRequested = useRef(false)
  const [theme, setTheme] = useState(matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
  const [copied, setCopied] = useState<string | null>(null)
  const [voiceMenu, setVoiceMenu] = useState<{ x: number; y: number } | null>(null)
  const voiceMenuElement = useRef<HTMLDivElement>(null)
  const [sessionOpen, setSessionOpen] = useState(false)
  const sessionPanel = useRef<HTMLDivElement>(null)
  const scroll = useRef<HTMLDivElement>(null)
  const nearBottom = useRef(true)
  const composer = useRef<HTMLTextAreaElement>(null)
  const speech = useSpeech(state.sessionId)
  useEffect(() => {
    let sequence = -1
    let identity = ''
    const unsubscribe = subscribe(event => {
      if (event.version !== 1 || event.sequence <= sequence) return
      sequence = event.sequence
      switch (event.type) {
        case 'snapshot': {
          const snapshot = event.payload as Snapshot
          if (identity && identity !== snapshot.sessionId) {
            setDraft(''); setAttachments([]); draftGeneration.current++; setHistoryOpen(false); setSessionPending(false)
            pendingSend.current = null; setSending(false); setError(null)
          }
          identity = snapshot.sessionId
          setState(snapshot); setConnecting(snapshot.status === 'Connecting')
          const pending = pendingSend.current
          const lastUser = snapshot.messages.filter(message => message.role === 'user').at(-1)
          if (pending && pending.sessionId === snapshot.sessionId && lastUser && lastUser.id !== pending.lastUserId) {
            pendingSend.current = null; setSending(false); setDraft(''); setAttachments([])
          }
          break
        }
        case 'theme': setTheme((event.payload as { theme: string }).theme); break
        case 'error': setError((event.payload as { message: string }).message); setConnecting(false); setModelPending(false); setSessionPending(false); setSending(false); pendingSend.current = null; break
        case 'sessionChanged': setSessionPending(false); break
        case 'signInCancelled': setConnecting(false); break
        case 'modelChanged': case 'toolsChanged': setModelPending(false); break
        case 'connecting': setConnecting(true); break
        case 'disconnected':
          setConnecting(false); setState(initial); setSessionPending(false); setModelPending(false)
          setDraft(''); setAttachments([]); draftGeneration.current++; identity = ''; break
      }
    })
    send('ready')
    if (!demo && !autoConnectRequested.current) {
      autoConnectRequested.current = true
      send('connect')
    }
    return unsubscribe
  }, [])
  useEffect(() => { document.documentElement.dataset.theme = theme }, [theme])
  useEffect(() => {
    if (!sessionOpen) return
    function dismiss(event: PointerEvent) {
      if (event.target instanceof Node && !sessionPanel.current?.contains(event.target)) setSessionOpen(false)
    }
    document.addEventListener('pointerdown', dismiss)
    return () => document.removeEventListener('pointerdown', dismiss)
  }, [sessionOpen])
  useEffect(() => {
    if (!voiceMenu) return
    const dismiss = (event: PointerEvent) => {
      if (event.target instanceof Node && !voiceMenuElement.current?.contains(event.target)) setVoiceMenu(null)
    }
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') setVoiceMenu(null) }
    document.addEventListener('pointerdown', dismiss)
    document.addEventListener('keydown', escape)
    return () => { document.removeEventListener('pointerdown', dismiss); document.removeEventListener('keydown', escape) }
  }, [voiceMenu])
  useEffect(() => {
    if (nearBottom.current && scroll.current) scroll.current.scrollTop = scroll.current.scrollHeight
  }, [state.messages, state.approvals])
  function request(type: string, fields = {}) { send(type, { sessionId: state.sessionId, ...fields }) }
  function resize(height: number) {
    const value = Math.max(76, Math.min(360, height))
    setInputHeight(value)
    try { localStorage.setItem('composerHeight', String(value)) } catch { }
  }
  function openLink(url: string) {
    if (url.startsWith('windbg-command:')) {
      if (state.busy || sessionPending || modelPending) { setError('Wait for the current operation before running a command.'); return }
      try { request('command', { text: decodeURIComponent(url.slice('windbg-command:'.length)) }) }
      catch { setError('Invalid debugger command link.') }
    } else if (/^https?:\/\//i.test(url)) {
      if (demo) window.open(url, '_blank', 'noopener,noreferrer')
      else request('openUrl', { text: url })
    }
  }
  function openVoiceMenu(event: ReactMouseEvent<HTMLButtonElement>) {
    event.preventDefault()
    const bounds = event.currentTarget.getBoundingClientRect()
    const x = event.clientX || bounds.left
    const y = event.clientY || bounds.bottom
    setVoiceMenu({ x: Math.max(8, Math.min(x, innerWidth - 288)), y: Math.max(8, Math.min(y, innerHeight - 328)) })
  }
  function openReport(event: ReactMouseEvent<HTMLButtonElement>, text: string, immersiveReader = false) {
    const diagrams = Array.from(event.currentTarget.closest('article')?.querySelectorAll('.diagram svg') || [])
      .map(diagram => diagram.outerHTML)
    request(immersiveReader ? 'openReportReader' : 'openReport', { text, diagrams })
  }
  function submit() {
    if ((!draft.trim() && !attachments.length) || state.busy || connecting || sessionPending || modelPending || sending || reading.current || !state.sessionId) return
    if (attachments.some(file => file.mimeType.startsWith('image/')) && state.models.find(model => model.id === state.model)?.vision === false) {
      setError('The selected model does not support images. Choose a vision model or remove the image.'); return
    }
    setError(null)
    pendingSend.current = { sessionId: state.sessionId, lastUserId: state.messages.filter(message => message.role === 'user').at(-1)?.id }
    setSending(true)
    request('send', { text: draft.trim(), attachments: attachments.map(({ name, mimeType, data }) => ({ name, mimeType, data })) })
    nearBottom.current = true
    composer.current?.focus()
  }
  async function addFiles(files: File[]) {
    if (!files.length) return
    if (reading.current || state.busy || sending || modelPending) { setError('Wait for the current operation before adding attachments.'); return }
    reading.current = true; setReadingFiles(true); setError(null)
    const generation = draftGeneration.current
    try {
      const added = await readAttachments(files, attachments)
      if (generation === draftGeneration.current) setAttachments(current => [...current, ...added])
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Could not read the attachment.') }
    finally { reading.current = false; setReadingFiles(false) }
  }
  async function copy(id: string, text: string) {
    try { await navigator.clipboard.writeText(text); setCopied(id) }
    catch { setError('Clipboard access is unavailable.') }
  }
  const contextPercent = percent(state.info?.currentTokens, state.info?.tokenLimit)
  const login = state.account?.authenticated ? state.account.login ? `@${state.account.login}` : 'Account unavailable'
    : state.account ? 'Not signed in' : state.sessionId ? 'Account unavailable' : 'Not connected'
  const sessionInfo = <div className="session-info" ref={sessionPanel} onKeyDown={event => { if (event.key === 'Escape') { setSessionOpen(false); sessionPanel.current?.querySelector('button')?.focus() } }}>
        <button className="icon session-trigger" title="Session info" aria-label="Session info" aria-expanded={sessionOpen} aria-controls="session-details" onClick={() => setSessionOpen(!sessionOpen)}>
          <Info size={15} />
        </button>
        {sessionOpen && <section id="session-details" className="session-popover" aria-label="Session info">
          <div className="popover-heading"><h2>Session Info</h2><button className="icon" title="Close session info" aria-label="Close session info" onClick={() => setSessionOpen(false)}><X size={15} /></button></div>
          <dl>
            <dt>Reported cost</dt><dd>{credits(state.info?.nanoAiu, state.info?.unreportedCostCalls)}</dd>
            <dt>Started</dt><dd>{time(state.info?.startedAt)}</dd>
            <dt>Turns</dt><dd>{state.turns?.length ?? state.messages.filter(message => message.role === 'user').length}</dd>
            <dt>GitHub</dt><dd>{login}</dd>
          </dl>
          <h3>Context Window</h3>
          <div className="context-caption"><span>{number(state.info?.currentTokens)} / {number(state.info?.tokenLimit)} tokens</span><span>{contextPercent == null ? '' : `${Math.round(contextPercent)}%`}</span></div>
          {contextPercent != null && <progress aria-label="Context window usage" value={contextPercent} max={100} />}
          <dl>
            <dt>System instructions</dt><dd>{number(state.info?.systemTokens)}</dd>
            <dt>Tool definitions</dt><dd>{number(state.info?.toolDefinitionsTokens)}</dd>
            <dt>Conversation</dt><dd>{number(state.info?.conversationTokens)}</dd>
            <dt>Context messages</dt><dd>{number(state.info?.messagesLength)}</dd>
          </dl>
          <h3>Session ID</h3><div className="session-id">{state.sessionId || 'Not connected'}</div>
        </section>}
      </div>
  return <main className="chat-shell" aria-label="Copilot Chat" onDragOver={event => {
    if (event.dataTransfer.types.includes('Files')) { event.preventDefault(); event.dataTransfer.dropEffect = 'copy' }
  }} onDrop={event => {
    if (event.dataTransfer.types.includes('Files')) { event.preventDefault(); void addFiles(Array.from(event.dataTransfer.files)) }
  }}>
    <header className="topbar">
      <button type="button" className="account" aria-label="GitHub account" title={`${login} (${state.account?.host || 'GitHub'})`} aria-expanded={accountOpen} disabled={connecting || state.busy || sessionPending || modelPending || sending || readingFiles} onClick={() => setAccountOpen(!accountOpen)}><Github size={15} /><span>{login}</span></button>
      {state.sessionId && <SessionName key={state.sessionId} title={state.sessionTitle || state.sessions?.find(entry => entry.id === state.sessionId)?.title || 'New chat'}
        busy={state.busy || sessionPending || modelPending || sending || readingFiles}
        onRename={text => request('rename', { historyId: state.sessionId, text })} />}
      {demo && <span className="demo-label">Browser demo</span>}
      <button className="icon history-trigger" title="Session history" aria-label="Session history" aria-expanded={historyOpen} disabled={!state.sessionId} onClick={() => setHistoryOpen(!historyOpen)}><History size={18} /></button>
      <button className="icon" title="New chat" aria-label="New chat" disabled={!state.sessionId || state.busy || sessionPending || modelPending || sending || readingFiles}
        onClick={() => { speech.stop(); setError(null); setSessionPending(true); request('new', { model: state.model || undefined }) }}><Plus size={19} /></button>
    </header>
    {accountOpen && <section className="account-menu" aria-label="Account actions" onKeyDown={event => { if (event.key === 'Escape') setAccountOpen(false) }}>
      <button type="button" onClick={() => { setAccountOpen(false); setHistoryOpen(false); setError(null); speech.stop(); setConnecting(true); request('signIn') }}><LogIn size={16} />{state.account?.authenticated ? 'Switch account' : 'Sign in'}</button>
      <button type="button" className="icon" aria-label="Close account menu" title="Close account menu" onClick={() => setAccountOpen(false)}><X size={15} /></button>
    </section>}
    {historyOpen && <SessionHistory sessions={state.sessions || []} selected={state.sessionId} busy={state.busy || sessionPending || modelPending || sending || readingFiles} onClose={() => setHistoryOpen(false)}
      onResume={historyId => { speech.stop(); setSessionPending(true); request('resume', { historyId }) }}
      onRename={(historyId, text) => request('rename', { historyId, text })} onDelete={historyId => request('delete', { historyId })} />}
    <div className="transcript" ref={scroll} onScroll={() => {
      const element = scroll.current!
      nearBottom.current = element.scrollHeight - element.scrollTop - element.clientHeight < 90
    }}>
      {!state.messages.length && <div className="empty">
        <Terminal size={34} strokeWidth={1.3} />
        <h2>{state.sessionId ? 'Current-target conversation' : 'GitHub Copilot'}</h2>
        {!state.sessionId && <button className="connect" disabled={connecting} onClick={() => { setError(null); send('connect') }}>
          {connecting ? <LoaderCircle size={16} className="spin" /> : <Plug size={16} />}Connect
        </button>}
      </div>}
      {state.messages.filter(message => message.role === 'user' || message.text.trim()).map(message => {
        if (message.role !== 'user' && message.role !== 'assistant') return <Activity key={`${message.id}-${message.complete}`} message={message} theme={theme}
          available={speech.available(message.text)} configurable={speech.configurable(message.text)} playing={speech.playing === message.id}
          onRead={() => speech.toggle(message.id, message.text)} onVoiceMenu={openVoiceMenu}
          onCopy={() => void copy(message.id, message.text)} onLink={openLink} />
        const turn = state.turns?.find(item => item.id === message.turnId)
        return <article key={message.id} className={`message ${message.role}`} tabIndex={message.role === 'assistant' ? 0 : undefined} aria-label={message.role === 'assistant' ? 'Copilot response' : 'Your message'}>
        <div className="message-body">{message.role === 'user' ? <p className="user-text">{message.text}</p> : <Markdown text={message.text} complete={message.complete} theme={theme} onLink={openLink} />}</div>
        {!!message.attachments?.length && <ul className="sent-attachments" aria-label="Sent attachments">{message.attachments.map((file, index) => <li key={index} title={file.name}>
          <ImagePreview file={file} /><Paperclip size={13} /><span>{file.name}</span><small>{fileSize(file.size)}</small>
        </li>)}</ul>}
        {message.role === 'assistant' && <div className="turn-details" aria-label="Response details">
          <div className="turn-metadata">
            <time dateTime={turn?.startedAt || message.timestamp} title="Turn started">{time(turn?.startedAt || message.timestamp)}</time>
            <span title="Models used in this turn">{turn?.models.length ? turn.models.join(', ') : message.model || 'Model not reported'}</span>
            <span title="Reported cost for the entire turn">{credits(turn?.nanoAiu, turn?.unreportedCostCalls)}</span>
          </div>
          {message.complete && <div className="message-actions">
            <button className="icon" title={copied === message.id ? 'Copied' : 'Copy response'} aria-label="Copy response" onClick={() => void copy(message.id, message.text)}>{copied === message.id ? <Check size={14} /> : <Copy size={14} />}</button>
            <button className="icon" title="Open response in browser" aria-label="Open response in browser" onClick={event => openReport(event, message.text)}><ExternalLink size={14} /></button>
            <button className="icon" title="Open response in Immersive Reader" aria-label="Open response in Immersive Reader" onClick={event => openReport(event, message.text, true)}><BookOpen size={14} /></button>
            <button className="icon" disabled={!speech.configurable(message.text)}
              title={!speech.available(message.text) ? 'Right-click to choose a voice' : speech.playing === message.id ? 'Stop reading' : 'Read aloud (right-click to choose voice)'}
              aria-label={speech.playing === message.id ? 'Stop reading' : 'Read aloud'} onClick={() => speech.toggle(message.id, message.text)} onContextMenu={openVoiceMenu}>
              {speech.playing === message.id ? <Square size={14} /> : <Volume2 size={14} />}
            </button>
          </div>}
        </div>}
      </article>})}
      {state.approvals.map(approval => <section className="approval" key={approval.id} aria-label={approval.kind === 'share' ? 'Share output approval' : approval.kind === 'execute' ? 'Execute command approval' : 'Tool permission approval'}>
        <div className="approval-heading"><ShieldCheck size={17} /><h2>{approval.kind === 'share' ? 'Share output with Copilot?' : approval.kind === 'execute' ? 'Run debugger command?' : 'Allow tool access and result sharing?'}</h2></div>
        <pre>{approval.text || '(No output)'}</pre>
        <div className="approval-actions">
          <button onClick={() => request('approval', { approvalId: approval.id, approved: false })}><X size={14} />Deny</button>
          <button className="primary" onClick={() => request('approval', { approvalId: approval.id, approved: true })}><Check size={14} />{approval.kind === 'share' ? 'Share output' : approval.kind === 'execute' ? 'Run command' : 'Allow'}</button>
        </div>
      </section>)}
    </div>
    {voiceMenu && <div ref={voiceMenuElement} className="voice-menu" role="menu" aria-label="Read aloud voice" style={{ left: voiceMenu.x, top: voiceMenu.y }}>
      <button role="menuitemradio" aria-checked={!speech.voiceUri} onClick={() => { speech.selectVoice(null); setVoiceMenu(null) }}>
        <span>Automatic</span><small>Matching local voice</small>{!speech.voiceUri && <Check size={14} />}
      </button>
      {speech.voices.map(voice => <button key={voice.voiceURI} role="menuitemradio" aria-checked={speech.voiceUri === voice.voiceURI}
        title={voice.localService ? `${voice.name} (${voice.lang})` : `${voice.name} (${voice.lang}) may send text to the voice provider`}
        onClick={() => { speech.selectVoice(voice.voiceURI); setVoiceMenu(null) }}>
        <span>{voice.name}</span><small>{voice.lang} · {voice.localService ? 'Local' : 'Online'}</small>{speech.voiceUri === voice.voiceURI && <Check size={14} />}
      </button>)}
    </div>}
    {(error || state.error || speech.error) && <Banner key={state.sessionId + (error || state.error || speech.error)} text={error || state.error || speech.error || ''} kind="error" />}
    {state.mode === 'ApproveAll' && <Banner key={state.sessionId + '-auto'} text="Approve all: commands and result sharing are automatically approved, including enabled built-in and MCP tools. Tools may access files, contact services or execute code." kind="warning" />}
    <footer>
      <form className="composer" onSubmit={event => { event.preventDefault(); submit() }} onPaste={event => {
        const files = Array.from(event.clipboardData.files)
        if (files.length) { event.preventDefault(); void addFiles(files) }
      }}>
        <div className="composer-resize" role="separator" aria-label="Input height" aria-orientation="horizontal" aria-valuemin={76} aria-valuemax={360} aria-valuenow={Math.round(inputHeight)} tabIndex={0}
          onPointerDown={event => { resizeStart.current = { y: event.clientY, height: inputHeight }; event.currentTarget.setPointerCapture(event.pointerId) }}
          onPointerMove={event => { if (resizeStart.current) resize(resizeStart.current.height + resizeStart.current.y - event.clientY) }}
          onPointerUp={() => { resizeStart.current = null }} onPointerCancel={() => { resizeStart.current = null }}
          onKeyDown={event => { if (event.key === 'ArrowUp' || event.key === 'ArrowDown') { event.preventDefault(); resize(inputHeight + (event.key === 'ArrowUp' ? 20 : -20)) } }} />
        <input ref={fileInput} type="file" multiple accept={fileAccept} aria-label="Attach files" className="file-input" onChange={event => {
          void addFiles(Array.from(event.target.files || [])); event.target.value = ''
        }} />
        {!!attachments.length && <ul className="draft-attachments" aria-label="Attachments to send">{attachments.map(file => <li key={file.id} title={file.name}>
          <ImagePreview file={file} />
          {file.mimeType.startsWith('image/') ? <Image size={15} /> : <FileText size={15} />}<span>{file.name}</span><small>{fileSize(file.size)}</small>
          <button type="button" className="icon" aria-label={`Remove ${file.name}`} title={`Remove ${file.name}`} disabled={sending || readingFiles} onClick={() => setAttachments(current => current.filter(item => item.id !== file.id))}><X size={13} /></button>
        </li>)}</ul>}
        <textarea ref={composer} aria-label="Message" placeholder="Ask about the current target..." rows={3} maxLength={32000} value={draft}
          disabled={sending || sessionPending} style={{ height: `min(${inputHeight}px, 35dvh)` }}
          onChange={event => setDraft(event.target.value)} onKeyDown={event => {
            if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing && event.keyCode !== 229) { event.preventDefault(); submit() }
          }} />
        <div className="composer-bottom">
          <div className="composer-settings">
            <button type="button" className="icon" aria-label="Add attachments" title="Add attachments" disabled={state.busy || sending || modelPending || readingFiles} onClick={() => fileInput.current?.click()}>
              {readingFiles ? <LoaderCircle size={15} className="spin" /> : <Paperclip size={15} />}
            </button>
            <ModelPicker models={state.models} selected={state.model} disabled={!state.sessionId || state.busy || sessionPending || modelPending || sending || readingFiles} onSelect={model => {
              setError(null); setModelPending(true); request('model', { model })
            }} />
            <ToolPicker settings={state.toolSettings} connected={!!state.sessionId} disabled={state.busy || sessionPending || modelPending || sending || readingFiles}
              onSelect={(tools, servers) => { setError(null); setModelPending(true); request('tools', { tools, servers }) }}
              onLoad={() => { setError(null); setModelPending(true); request('loadMcp') }}
              onAuthenticate={(server, forceReauth) => { setError(null); setModelPending(true); request(forceReauth ? 'reauthenticateMcp' : 'authenticateMcp', { text: server }) }}
              onReload={() => { setError(null); setModelPending(true); request('reloadTools') }} />
            <div className="mode-switch" role="group" aria-label="Execution approval mode">
              {(['AskEveryTime', 'ApproveAll'] as Mode[]).map(mode => <button type="button" key={mode} aria-pressed={state.mode === mode} disabled={!state.sessionId}
                onClick={() => request('mode', { mode })}>{mode === 'AskEveryTime' ? 'Ask every time' : 'Approve all'}</button>)}
            </div>
          </div>
          {state.busy ? <button type="button" className="icon stop" title="Cancel response" aria-label="Cancel response" disabled={modelPending || state.status === 'Switching model' || state.status === 'Updating tools'} onClick={() => request('cancel')}><Square size={15} /></button>
            : <button type="submit" className="icon primary" title={attachments.length ? 'Send message and attachments to Copilot' : 'Send message'} aria-label="Send message" disabled={(!draft.trim() && !attachments.length) || connecting || sessionPending || modelPending || sending || readingFiles || !state.sessionId}><ArrowUp size={19} /></button>}
        </div>
      </form>
      <div className="footer-meta">
        <span className={`status ${state.busy ? 'working' : ''}`} role="status">{(state.busy || connecting) && <LoaderCircle size={12} className="spin" />}{connecting ? 'Connecting' : state.status === 'Ready' || state.status === 'Disconnected' ? '' : state.status}</span>
        {sessionInfo}
      </div>
    </footer>
  </main>
}