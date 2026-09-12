import { describe, expect, it } from 'vitest'
import { localVoice, readableText } from './speech'

describe('read aloud privacy', () => {
  it('extracts prose through the Markdown AST, excluding code and diagrams', () => {
    expect(readableText('# Result\n\nA **stopped** target.\n\n```mermaid\ngraph TD; A-->B\n```\n\n`secret`')).toBe('Result\nA stopped target.')
  })
  it('never falls back to a remote or different-language voice', () => {
    const voices = [{ localService: false, lang: 'ja-JP' }, { localService: true, lang: 'en-US' }] as SpeechSynthesisVoice[]
    expect(localVoice(voices, 'ja-JP')).toBeUndefined()
    expect(localVoice(voices, 'en-US')).toBe(voices[1])
  })
})