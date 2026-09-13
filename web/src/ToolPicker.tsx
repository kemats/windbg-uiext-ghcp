import { useEffect, useRef, useState } from 'react'
import { ChevronDown, FilePenLine, Info, LogIn, Plug, RefreshCw, Search, UserRoundCog, Wrench, X } from 'lucide-react'
import type { ToolSettings } from './bridge'

function GroupCheck({ name, count, total, disabled, onChange }: { name: string; count: number; total: number; disabled: boolean; onChange: () => void }) {
  const input = useRef<HTMLInputElement>(null)
  useEffect(() => { if (input.current) input.current.indeterminate = count > 0 && count < total }, [count, total])
  return <input ref={input} type="checkbox" aria-label={`Select all ${name} tools`} checked={total > 0 && count === total} disabled={disabled || !total} onChange={onChange} />
}

export function ToolPicker({ settings, disabled, connected, onSelect, onLoad, onReload, onAuthenticate }: {
  settings?: ToolSettings | null; disabled: boolean; connected: boolean
  onSelect: (tools: string[], servers: string[]) => void; onLoad: () => void; onReload: () => void
  onAuthenticate: (server: string, forceReauth?: boolean) => void
}) {
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [collapsed, setCollapsed] = useState<string[]>([])
  const [height, setHeight] = useState(300)
  const root = useRef<HTMLDivElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const search = useRef<HTMLInputElement>(null)
  const tools = settings?.tools ?? []
  const servers = settings?.servers ?? []
  const selected = tools.filter(tool => tool.selected).map(tool => tool.id)
  const enabled = servers.filter(server => server.enabled).map(server => server.name)
  const groups = [...new Set(['WinDbg', 'Built-In', ...servers.map(server => server.name), ...tools.map(tool => tool.group)])]
  useEffect(() => {
    if (!open) return
    search.current?.focus()
    const composer = root.current?.closest('.composer')
    const measure = () => setHeight(Math.max(80, (composer?.getBoundingClientRect().top ?? 320) - 12))
    measure()
    const observer = new ResizeObserver(measure)
    if (composer) observer.observe(composer)
    function dismiss(event: PointerEvent) {
      if (event.target instanceof Node && !root.current?.contains(event.target)) setOpen(false)
    }
    document.addEventListener('pointerdown', dismiss)
    window.addEventListener('resize', measure)
    return () => { observer.disconnect(); document.removeEventListener('pointerdown', dismiss); window.removeEventListener('resize', measure) }
  }, [open])
  function close() { setOpen(false); trigger.current?.focus() }
  function toggle(ids: string[], checked: boolean) {
    onSelect(checked ? [...new Set([...selected, ...ids])] : selected.filter(id => !ids.includes(id)), enabled)
  }
  return <div ref={root} className="tool-picker" onKeyDown={event => {
    if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); close() }
    if (event.key === 'Enter' && event.target instanceof HTMLInputElement) event.preventDefault()
  }}>
    <button ref={trigger} type="button" className="icon" title="Configure tools" aria-label="Configure tools" aria-expanded={open} aria-controls="tool-picker-panel" aria-haspopup="dialog" disabled={!connected} onClick={() => setOpen(!open)}><Wrench size={16} /></button>
    {open && <section className="tool-popover" id="tool-picker-panel" role="dialog" aria-label="Choose tools" style={{ maxHeight: height }} aria-busy={disabled}>
      <div className="popover-heading"><h2>Tools <small>{selected.length} selected</small></h2><div className="tool-actions">
        <button type="button" className="icon" title="Open mcp.json" aria-label="Open mcp.json" disabled={disabled} onClick={onLoad}><FilePenLine size={15} /></button>
        <button type="button" className="icon" title="Reload tools" aria-label="Reload tools" disabled={disabled} onClick={onReload}><RefreshCw size={15} /></button>
        <button type="button" className="icon" title="Close tool picker" aria-label="Close tool picker" onClick={close}><X size={15} /></button>
      </div></div>
      <label className="model-search"><Search size={14} /><input ref={search} type="search" aria-label="Search tools" value={query} onChange={event => setQuery(event.target.value)} /></label>
      {settings?.configPath && <div className="tool-path" title={settings.configPath}>{settings.configPath}</div>}
      {settings?.error && <div className="error" role="alert">{settings.error}</div>}
      <div className="tool-groups">
        {groups.map(group => {
          const groupTools = tools.filter(tool => tool.group === group)
          const filtered = groupTools.filter(tool => `${group} ${tool.name} ${tool.description ?? ''}`.toLowerCase().includes(query.toLowerCase()))
          const server = servers.find(item => item.name === group)
          if (!filtered.length && !group.toLowerCase().includes(query.toLowerCase())) return null
          const expanded = !!query || !collapsed.includes(group)
          return <section className="tool-group" key={group} aria-label={group}>
            <div className="tool-group-heading">
              <button type="button" className="icon" title={`${expanded ? 'Collapse' : 'Expand'} ${group}`} aria-label={`${expanded ? 'Collapse' : 'Expand'} ${group}`} aria-expanded={expanded}
                onClick={() => setCollapsed(expanded ? [...collapsed, group] : collapsed.filter(item => item !== group))}><ChevronDown size={14} className={expanded ? '' : 'collapsed'} /></button>
              <GroupCheck name={group} count={groupTools.filter(tool => tool.selected).length} total={groupTools.length} disabled={disabled} onChange={() => toggle(groupTools.map(tool => tool.id), !groupTools.every(tool => tool.selected))} />
              <strong>{group}</strong>
              {server && <label className="server-toggle" title={`Connect ${group}`}><Plug size={13} /><input type="checkbox" aria-label={`Connect ${group}`} checked={server.enabled} disabled={disabled}
                onChange={event => onSelect(selected, event.target.checked ? [...enabled, group] : enabled.filter(name => name !== group))} /></label>}
            </div>
            {expanded && <div className="tool-children">
              {server && <div className="tool-server-status" role="status">{server.status}</div>}
              {server?.enabled && server.canAuthenticate && <button type="button" className="icon" title={`Sign in to ${group}`} aria-label={`Sign in to ${group}`} disabled={disabled}
                onClick={() => onAuthenticate(group)}><LogIn size={15} /></button>}
              {server?.enabled && server.canReauthenticate && <button type="button" className="icon" title={`Sign in again to ${group}`} aria-label={`Sign in again to ${group}`} disabled={disabled}
                onClick={() => onAuthenticate(group, true)}><UserRoundCog size={15} /></button>}
              {filtered.map(tool => <div className="tool-entry" key={tool.id}><label className="tool-row">
                <input type="checkbox" aria-label={tool.name} checked={tool.selected} disabled={disabled} onChange={event => toggle([tool.id], event.target.checked)} />
                <span><span>{tool.name}</span>{tool.description && <small>{tool.description}</small>}</span>
              </label>{tool.description && <details className="tool-description">
                <summary title={`Details for ${tool.name}`} aria-label={`Details for ${tool.name}`}><Info size={14} /></summary>
                <div className="tool-description-text" tabIndex={0}>{tool.description}</div>
              </details>}</div>)}
              {!filtered.length && <div className="tool-server-status">No tools</div>}
            </div>}
          </section>
        })}
        {!!query && !tools.some(tool => `${tool.group} ${tool.name} ${tool.description ?? ''}`.toLowerCase().includes(query.toLowerCase())) && !servers.some(server => server.name.toLowerCase().includes(query.toLowerCase())) && <div className="model-empty">No matching tools</div>}
      </div>
    </section>}
  </div>
}