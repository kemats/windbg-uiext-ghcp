import { describe, expect, it } from 'vitest'
import { credits, number, percent, time } from './metadata'

describe('reported metadata', () => {
  it('distinguishes missing usage from zero and partial billing', () => {
    expect(credits(null)).toBe('Not reported')
    expect(credits(-1)).toBe('Not reported')
    expect(credits(Number.NaN)).toBe('Not reported')
    expect(credits(0)).toBe('0 credits')
    expect(credits(125_000_000)).toBe('0.125 credits')
    expect(credits(125_000_000, 1)).toBe('0.125 credits (partial)')
    expect(number(undefined)).toBe('Not reported')
    expect(number(0)).toBe('0')
  })
  it('bounds context meters and handles unknown timestamps', () => {
    expect(percent(1360, 8000)).toBe(17)
    expect(percent(9000, 8000)).toBe(100)
    expect(percent(null, 8000)).toBeNull()
    expect(percent(100, 0)).toBeNull()
    expect(time('invalid')).toBe('Not reported')
    expect(time(undefined)).toBe('Not reported')
  })
})