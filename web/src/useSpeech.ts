import { useEffect, useRef, useState } from 'react'
import { localVoice, readableText } from './speech'

export function useSpeech(sessionId: string) {
  const [voices, setVoices] = useState<SpeechSynthesisVoice[]>([])
  const [playing, setPlaying] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const generation = useRef(0)
  function stop() {
    generation.current++
    window.speechSynthesis?.cancel()
    setPlaying(null)
  }
  useEffect(() => {
    const synthesis = window.speechSynthesis
    if (!synthesis) return
    const update = () => setVoices(synthesis.getVoices())
    update()
    synthesis.addEventListener('voiceschanged', update)
    const hide = () => { if (document.hidden) stop() }
    document.addEventListener('visibilitychange', hide)
    return () => {
      generation.current++
      synthesis.cancel()
      synthesis.removeEventListener('voiceschanged', update)
      document.removeEventListener('visibilitychange', hide)
    }
  }, [])
  useEffect(() => { stop() }, [sessionId])
  function language(text: string) { return /[\u3040-\u30ff\u3400-\u9fff]/u.test(text) ? 'ja-JP' : 'en-US' }
  function available(text: string) { return !!localVoice(voices, language(text)) && !!readableText(text) }
  function toggle(id: string, text: string) {
    if (playing === id) { stop(); return }
    stop()
    const voice = localVoice(voices, language(text))
    if (!voice) return
    setError(null)
    const identity = generation.current
    const utterance = new SpeechSynthesisUtterance(readableText(text))
    utterance.voice = voice
    utterance.lang = voice.lang
    utterance.onend = () => { if (identity === generation.current) setPlaying(null) }
    utterance.onerror = () => {
      if (identity === generation.current) { setPlaying(null); setError('Read aloud is unavailable in this runtime.') }
    }
    setPlaying(id)
    window.speechSynthesis.speak(utterance)
  }
  return { playing, available, toggle, stop, error }
}