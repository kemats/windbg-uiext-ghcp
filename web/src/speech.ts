import { unified } from 'unified'
import remarkParse from 'remark-parse'

export function readableText(markdown: string): string {
  const tree = unified().use(remarkParse).parse(markdown)
  function walk(node: { type: string; value?: string; children?: typeof tree.children }): string {
    if (['code', 'inlineCode', 'html', 'image'].includes(node.type)) return ''
    if (node.type === 'text') return node.value ?? ''
    const text = node.children?.map(walk).join('') ?? ''
    return ['paragraph', 'heading', 'listItem'].includes(node.type) ? text + '\n' : text
  }
  return walk(tree).trim()
}

export function preferredVoice(voices: SpeechSynthesisVoice[], language: string, voiceUri: string | null) {
  if (voiceUri) {
    const selected = voices.find(voice => voice.voiceURI === voiceUri)
    if (selected) return selected
  }
  return voices.find(voice => voice.localService && voice.lang.toLowerCase() === language.toLowerCase())
    ?? voices.find(voice => voice.localService && voice.lang.split('-')[0] === language.split('-')[0])
}