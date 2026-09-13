import { describe, expect, it } from 'vitest'
import { preferredVoice, readableText } from './speech'

describe('read aloud privacy', () => {
  it('extracts prose through the Markdown AST, excluding code and diagrams', () => {
    expect(readableText('# Result\n\nA **stopped** target.\n\n```mermaid\ngraph TD; A-->B\n```\n\n`secret`')).toBe('Result\nA stopped target.')
  })
  it('uses a selected voice by URI, including an online voice', () => {
    const voices = [
      { localService: false, lang: 'ja-JP', voiceURI: 'online' },
      { localService: true, lang: 'ja-JP', voiceURI: 'local' },
    ] as SpeechSynthesisVoice[]
    expect(preferredVoice(voices, 'ja-JP', null)).toBe(voices[1])
    expect(preferredVoice(voices, 'ja-JP', 'online')).toBe(voices[0])
  })
  it('falls back to a matching local voice when a saved voice is unavailable', () => {
    const voices = [{ localService: false, lang: 'en-US' }, { localService: true, lang: 'ja-JP' }] as SpeechSynthesisVoice[]
    expect(preferredVoice(voices, 'ja-JP', 'missing')).toBe(voices[1])
    expect(preferredVoice(voices, 'fr-FR', null)).toBeUndefined()
  })
})