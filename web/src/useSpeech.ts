import { useEffect, useRef, useState } from 'react'
import { preferredVoice, readableText } from './speech'

export function useSpeech(sessionId: string) {
  const [voices, setVoices] = useState<SpeechSynthesisVoice[]>([])
  const [voiceUri, setVoiceUriState] = useState<string | null>(() => {
    try { return localStorage.getItem('speechVoice') } catch { return null }
  })
  const [playingState, setPlayingState] = useState<{ sessionId: string; id: string } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const generation = useRef({ value: 0 })
  const playing = playingState?.sessionId === sessionId ? playingState.id : null
  function stop() {
    generation.current.value++
    window.speechSynthesis?.cancel()
    setPlayingState(null)
  }
  useEffect(() => {
    const synthesis = window.speechSynthesis
    if (!synthesis) return
    const generationState = generation.current
    const update = () => setVoices(synthesis.getVoices())
    update()
    synthesis.addEventListener('voiceschanged', update)
    const hide = () => { if (document.hidden) stop() }
    document.addEventListener('visibilitychange', hide)
    return () => {
      generationState.value++
      synthesis.cancel()
      synthesis.removeEventListener('voiceschanged', update)
      document.removeEventListener('visibilitychange', hide)
    }
  }, [])
  useEffect(() => {
    generation.current.value++
    window.speechSynthesis?.cancel()
  }, [sessionId])
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
    const identity = generation.current.value
    const utterance = new SpeechSynthesisUtterance(readableText(text))
    utterance.voice = voice
    utterance.lang = voice.lang
    utterance.onend = () => { if (identity === generation.current.value) setPlayingState(null) }
    utterance.onerror = () => {
      if (identity === generation.current.value) { setPlayingState(null); setError('Read aloud is unavailable in this runtime.') }
    }
    setPlayingState({ sessionId, id })
    window.speechSynthesis.speak(utterance)
  }
  return { playing, available, configurable, toggle, stop, error, voices, voiceUri, selectVoice }
}