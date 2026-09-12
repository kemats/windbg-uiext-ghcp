import { useEffect, useId, useState } from 'react'
import ReactMarkdown, { defaultUrlTransform } from 'react-markdown'
import remarkGfm from 'remark-gfm'
import rehypeHighlight from 'rehype-highlight'
import DOMPurify from 'dompurify'

let renderQueue = Promise.resolve()
function Diagram({ source, theme }: { source: string; theme: string }) {
  const id = useId().replace(/[^a-zA-Z0-9]/g, '')
  const [svg, setSvg] = useState('')
  const [error, setError] = useState(false)
  useEffect(() => {
    let disposed = false
    setSvg(''); setError(false)
    if (source.length > 12_000) { setError(true); return }
    renderQueue = renderQueue.then(async () => {
      if (disposed) return
      const container = document.createElement('div')
      container.className = 'mermaid-staging'
      document.body.append(container)
      try {
        const { default: mermaid } = await import('mermaid')
        mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: 'base', htmlLabels: false,
          themeVariables: { darkMode: theme === 'dark', primaryColor: theme === 'dark' ? '#234339' : '#e5f3ed',
            primaryBorderColor: '#398775', primaryTextColor: theme === 'dark' ? '#ededee' : '#24292e',
            lineColor: '#626c70', fontFamily: 'Source Sans 3 Variable' },
          maxTextSize: 12_000, maxEdges: 150, flowchart: { htmlLabels: false }, suppressErrorRendering: true })
        await mermaid.parse(source)
        const result = await mermaid.render('diagram' + id, source, container)
        if (!disposed) setSvg(DOMPurify.sanitize(result.svg, { USE_PROFILES: { svg: true, svgFilters: true }, FORBID_TAGS: ['foreignObject', 'a', 'image'] }))
      } catch { if (!disposed) setError(true) }
      finally { container.remove() }
    })
    return () => { disposed = true }
  }, [source, theme, id])
  if (error) return <pre><code>{source}</code></pre>
  if (!svg) return <pre aria-label="Diagram source"><code>{source}</code></pre>
  return <div className="diagram" aria-label="Mermaid diagram" dangerouslySetInnerHTML={{ __html: svg }} />
}

export function Markdown({ text, complete, theme, onLink }: { text: string; complete: boolean; theme: string; onLink?: (url: string) => void }) {
  return <ReactMarkdown remarkPlugins={[remarkGfm]} rehypePlugins={[[rehypeHighlight, { detect: false }]]} skipHtml
    urlTransform={url => url.startsWith('windbg-command:') ? url : defaultUrlTransform(url)}
    components={{
      img: () => null,
      a: ({ children, href }) => href && /^(https?:\/\/|windbg-command:)/i.test(href) && onLink
        ? <a className="reference" href={href} title={href} onClick={event => { event.preventDefault(); onLink(href) }}>{children}</a>
        : <span className="reference" title={href}>{children}</span>,
      pre: ({ children }) => <div className="code-block">{children}</div>,
      code: ({ className, children }) => {
        if (className?.includes('language-mermaid') && complete)
          return <Diagram source={String(children).trim()} theme={theme} />
        return <code className={className}>{children}</code>
      },
    }}>{text}</ReactMarkdown>
}