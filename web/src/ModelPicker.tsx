import { useEffect, useRef, useState } from 'react'
import { Check, ChevronDown, Search, X } from 'lucide-react'
import type { ModelOption } from './bridge'
import { number, pricePerMillion } from './metadata'

export function ModelPicker({ models, selected, disabled, onSelect }: {
  models: ModelOption[]; selected: string | null; disabled: boolean; onSelect: (model: string) => void
}) {
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [highlighted, setHighlighted] = useState<string | null>(null)
  const [availableHeight, setAvailableHeight] = useState(300)
  const root = useRef<HTMLDivElement>(null)
  const search = useRef<HTMLInputElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const selectedName = !selected || selected === 'auto' ? 'Auto' : models.find(model => model.id === selected)?.name || selected
  const filtered = models.filter(model => `${model.name} ${model.id}`.toLowerCase().includes(query.toLowerCase()))
  const detail = filtered.find(model => model.id === highlighted) || filtered.find(model => model.id === selected) || filtered[0]
  useEffect(() => {
    if (!open) return
    search.current?.focus()
    const composer = root.current?.closest('.composer')
    const measure = () => setAvailableHeight(Math.max(80, (composer?.getBoundingClientRect().top ?? 312) - 12))
    measure()
    const observer = new ResizeObserver(measure)
    if (composer) observer.observe(composer)
    window.addEventListener('resize', measure)
    function dismiss(event: PointerEvent) {
      if (event.target instanceof Node && !root.current?.contains(event.target)) setOpen(false)
    }
    document.addEventListener('pointerdown', dismiss)
    return () => { document.removeEventListener('pointerdown', dismiss); window.removeEventListener('resize', measure); observer.disconnect() }
  }, [open])
  function close() { setOpen(false); trigger.current?.focus() }
  function choose(model: ModelOption) {
    if (disabled || model.policy === 'disabled') return
    close()
    if (model.id !== selected) onSelect(model.id)
  }
  return <div className="model-picker" ref={root} onKeyDown={event => {
    if (event.key === 'Escape') { event.preventDefault(); close() }
  }}>
    <button ref={trigger} type="button" className="model-trigger" aria-label="Model" title={selectedName}
      disabled={disabled} aria-expanded={open} aria-controls="model-picker-panel" aria-haspopup="dialog" onClick={() => setOpen(!open)}>
      <span>{selectedName}</span><ChevronDown size={13} />
    </button>
    {open && <section className="model-popover" id="model-picker-panel" role="dialog" aria-label="Choose model" style={{ height: Math.min(480, availableHeight), maxHeight: availableHeight }}>
      <div className="popover-heading"><strong>Model</strong><button type="button" className="icon" aria-label="Close model picker" title="Close model picker" onClick={close}><X size={15} /></button></div>
      <p className="model-notice">Changing models keeps history but may reset the prompt cache and increase cost.</p>
      <label className="model-search"><Search size={14} /><input ref={search} role="combobox" aria-label="Search models" aria-autocomplete="list" aria-expanded="true" aria-controls="model-options"
        aria-activedescendant={detail ? `model-option-${models.indexOf(detail)}` : undefined} value={query} onChange={event => { setQuery(event.target.value); setHighlighted(null) }}
        onKeyDown={event => {
          if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault()
            const index = filtered.findIndex(model => model.id === detail?.id)
            const next = filtered[(index + (event.key === 'ArrowDown' ? 1 : -1) + filtered.length) % filtered.length]
            if (next) { setHighlighted(next.id); document.getElementById(`model-option-${models.indexOf(next)}`)?.scrollIntoView({ block: 'nearest' }) }
          } else if (event.key === 'Enter') { event.preventDefault(); if (detail) choose(detail) }
        }} /></label>
      <div className="model-browser">
        <div className="model-options" id="model-options" role="listbox" aria-label="Available models">
          {filtered.map(model => <button type="button" role="option" id={`model-option-${models.indexOf(model)}`} key={model.id} tabIndex={-1}
            aria-selected={model.id === selected} aria-disabled={model.policy === 'disabled'} className={model.id === detail?.id ? 'highlighted' : ''}
            onMouseEnter={() => setHighlighted(model.id)} onFocus={() => setHighlighted(model.id)} onClick={() => choose(model)}>
            <Check size={13} style={{ visibility: model.id === selected ? 'visible' : 'hidden' }} /><span>{model.name}</span>
          </button>)}
          {!filtered.length && <p className="model-empty">No matching models</p>}
        </div>
        {detail && <div className="model-details" aria-label="Model details">
          <h3>{detail.name}</h3><div className="model-id">{detail.id}</div>
          <h4>Credits per 1M tokens</h4>
          <dl>{(['input', 'output', 'cacheRead', 'cacheWrite'] as const).map((key, index) => <div key={key}>
            <dt>{['Input', 'Output', 'Cache read', 'Cache write'][index]}</dt><dd>{pricePerMillion(detail.prices?.[key], detail.prices?.batchSize)}</dd>
          </div>)}</dl>
          <dl>
            <div><dt>Context window</dt><dd>{number(detail.contextTokens)}</dd></div>
            <div><dt>Max prompt</dt><dd>{number(detail.maxPromptTokens)}</dd></div>
            <div><dt>Images</dt><dd>{detail.vision == null ? 'Not reported' : detail.vision ? 'Supported' : 'Not supported'}</dd></div>
            <div><dt>Thinking effort</dt><dd>{detail.reasoningEfforts?.join(', ') || 'Not reported'}</dd></div>
            {detail.defaultReasoningEffort && <div><dt>Default effort</dt><dd>{detail.defaultReasoningEffort}</dd></div>}
            {detail.policy && <div><dt>Policy</dt><dd>{detail.policy}</dd></div>}
          </dl>
        </div>}
      </div>
    </section>}
  </div>
}