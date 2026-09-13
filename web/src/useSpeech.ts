import { useEffect, useRef, useState } from 'react'
import { preferredVoice, readableText } from './speech'

export function useSpeech(sessionId: string) {
  const [voices, setVoices] = useState<SpeechSynthesisVoice[]>([])
  const [voiceUri, setVoiceUriState] = useState<string | null>(() => {
    try { return localStorage.getItem('speechVoice') } catch { return null }
  })
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
  function available(text: string) { return !!preferredVoice(voices, language(text), voiceUri) && !!readableText(text) }
  function configurable(text: string) { return voices.length > 0 && !!readableText(text) }
  function selectVoice(value: string | null) {
    stop()
    setVoiceUriState(value)
    try {
      if (value) localStorage.setItem('speechVoice', value)
      else localStorage.removeItem('speechVoice')
    } catch { }
  }
  function toggle(id: string, text: string) {
    if (playing === id) { stop(); return }
    stop()
    const voice = preferredVoice(voices, language(text), voiceUri)
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
  return { playing, available, configurable, toggle, stop, error, voices, voiceUri, selectVoice }
}