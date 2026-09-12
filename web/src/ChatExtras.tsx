import { useEffect, useRef, useState } from 'react'
import { Brain, Check, ChevronDown, Copy, Pencil, Square, Terminal, Trash2, Volume2, X } from 'lucide-react'
import type { AttachmentInfo, Message, SessionEntry } from './bridge'
import { Markdown } from './Markdown'

export function ImagePreview({ file }: { file: AttachmentInfo }) {
  const [open, setOpen] = useState(false)
  const dialog = useRef<HTMLDialogElement>(null)
  useEffect(() => { if (open) dialog.current?.showModal() }, [open])
  if (!file.data || !['image/png', 'image/jpeg', 'image/gif', 'image/webp'].includes(file.mimeType)) return null
  const source = `data:${file.mimeType};base64,${file.data}`
  return <>
    <button type="button" className="preview-thumbnail" title={`Preview ${file.name}`} aria-label={`Preview ${file.name}`} onClick={() => setOpen(true)}><img src={source} alt={file.name} /></button>
    {open && <dialog ref={dialog} className="image-preview" aria-label={`Image preview: ${file.name}`} onClose={() => setOpen(false)} onClick={event => { if (event.target === event.currentTarget) dialog.current?.close() }}>
      <div className="popover-heading"><span>{file.name}</span><button type="button" className="icon" title="Close preview" aria-label="Close preview" onClick={() => dialog.current?.close()}><X size={18} /></button></div>
      <img src={source} alt={file.name} />
    </dialog>}
  </>
}

export function Activity({ message, theme, available, playing, onRead, onCopy, onLink }: {
  message: Message; theme: string; available: boolean; playing: boolean; onRead(): void; onCopy(): void; onLink(url: string): void
}) {
  const [open, setOpen] = useState(!message.complete)
  return <section className="activity" aria-label={message.title || message.role}>
    <div className="activity-heading">
      <button className="activity-toggle" aria-expanded={open} onClick={() => setOpen(!open)}><ChevronDown size={14} className={open ? '' : 'collapsed'} />{message.role === 'tool' ? <Terminal size={15} data-activity-icon="tool" /> : <Brain size={15} data-activity-icon="reasoning" />}<span>{message.title || message.role}</span><small>{message.activityStatus || (message.complete ? 'Completed' : 'In progress')}</small></button>
      <button className="icon" title="Copy activity" aria-label="Copy activity" onClick={onCopy}><Copy size={14} /></button>
      {message.role !== 'tool' && <button className="icon" disabled={!available} title={playing ? 'Stop reading' : 'Read activity aloud'} aria-label={playing ? 'Stop reading' : 'Read activity aloud'} onClick={onRead}>{playing ? <Square size={14} /> : <Volume2 size={14} />}</button>}
    </div>
    {open && <div className="activity-body message-body">{message.role === 'tool' ? <pre>{message.text}</pre> : <Markdown text={message.text} complete={message.complete} theme={theme} onLink={onLink} />}</div>}
  </section>
}

export function Banner({ text, kind }: { text: string; kind: 'error' | 'warning' }) {
  const [dismissed, setDismissed] = useState(false)
  if (dismissed) return null
  return <div role={kind === 'error' ? 'alert' : 'status'} className={`${kind} notification`}><span>{text}</span><button type="button" className="icon" title="Dismiss notification" aria-label="Dismiss notification" onClick={() => setDismissed(true)}><X size={14} /></button></div>
}

export function SessionName({ title, busy, onRename }: { title: string; busy: boolean; onRename(title: string): void }) {
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState(title)
  return <div className="session-heading" aria-label="Current session">
    {editing ? <form onSubmit={event => { event.preventDefault(); if (!busy && draft.trim()) { onRename(draft.trim()); setEditing(false) } }} onKeyDown={event => { if (event.key === 'Escape') setEditing(false) }}>
      <input autoFocus aria-label="Current session name" maxLength={120} value={draft} disabled={busy} onChange={event => setDraft(event.target.value)} />
      <button type="submit" className="icon" title="Save session name" aria-label="Save session name" disabled={busy || !draft.trim()}><Check size={15} /></button>
      <button type="button" className="icon" title="Cancel session rename" aria-label="Cancel session rename" onClick={() => setEditing(false)}><X size={15} /></button>
    </form> : <>
      <h1 title={title}>{title}</h1>
      <button type="button" className="icon" title="Rename current session" aria-label="Rename current session" disabled={busy} onClick={() => { setDraft(title); setEditing(true) }}><Pencil size={14} /></button>
    </>}
  </div>
}

export function SessionHistory({ sessions, selected, busy, onClose, onResume, onRename, onDelete }: {
  sessions: SessionEntry[]; selected: string; busy: boolean; onClose(): void; onResume(id: string): void; onRename(id: string, title: string): void; onDelete(id: string): void
}) {
  const [query, setQuery] = useState('')
  const [editing, setEditing] = useState<string | null>(null)
  const [title, setTitle] = useState('')
  const [deleting, setDeleting] = useState<string | null>(null)
  return <section className="history-panel" aria-label="Session history" onKeyDown={event => { if (event.key === 'Escape') onClose() }}>
    <div className="popover-heading"><h2>Sessions</h2><button className="icon" title="Close history" aria-label="Close history" onClick={onClose}><X size={17} /></button></div>
    <input aria-label="Search sessions" placeholder="Search sessions" value={query} onChange={event => setQuery(event.target.value)} />
    <ul>{sessions.filter(entry => entry.title.toLocaleLowerCase().includes(query.toLocaleLowerCase())).map(entry => <li key={entry.id}>
      {editing === entry.id ? <form onSubmit={event => { event.preventDefault(); onRename(entry.id, title); setEditing(null) }}>
        <input autoFocus aria-label="Session name" value={title} maxLength={120} onChange={event => setTitle(event.target.value)} />
        <button type="submit" className="icon" title="Save name" aria-label="Save name" disabled={busy || !title.trim()}><Check size={15} /></button>
        <button type="button" className="icon" title="Cancel rename" aria-label="Cancel rename" onClick={() => setEditing(null)}><X size={15} /></button>
      </form> : <div className="history-row">
        <button className="history-select" aria-current={entry.id === selected ? 'true' : undefined} disabled={busy} onClick={() => { if (entry.id !== selected) onResume(entry.id); else onClose() }}><span>{entry.title}</span><small>{new Date(entry.updatedAt).toLocaleString()}</small></button>
        <button className="icon" title={`Rename ${entry.title}`} aria-label={`Rename ${entry.title}`} disabled={busy} onClick={() => { setEditing(entry.id); setTitle(entry.title) }}><Pencil size={14} /></button>
        <button className="icon" title={entry.id === selected ? 'Open another session to delete this session' : `Delete ${entry.title}`} aria-label={`Delete ${entry.title}`} disabled={busy || entry.id === selected} onClick={() => setDeleting(entry.id)}><Trash2 size={14} /></button>
      </div>}
      {deleting === entry.id && <div className="delete-confirm"><span>Delete this session permanently?</span><button disabled={busy} onClick={() => { onDelete(entry.id); setDeleting(null) }}>Delete</button><button onClick={() => setDeleting(null)}>Cancel</button></div>}
    </li>)}</ul>
  </section>
}