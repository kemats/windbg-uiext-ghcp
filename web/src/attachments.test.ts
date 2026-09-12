import { describe, expect, it } from 'vitest'
import { readAttachments } from './attachments'
import { pricePerMillion } from './metadata'

describe('model prices and selected files', () => {
  it('uses the SDK billing batch rather than assuming a million tokens', () => {
    expect(pricePerMillion(0.1, 1000)).toBe('100')
    expect(pricePerMillion(0, 1000)).toBe('0')
    expect(pricePerMillion(null, 1000)).toBe('Not reported')
    expect(pricePerMillion(2, 0)).toBe('Not reported')
    expect(pricePerMillion(-1, 1000)).toBe('Not reported')
  })
  it('encodes explicitly selected files without retaining a filesystem path', async () => {
    const [file] = await readAttachments([new File(['example'], 'trace.log')], [])
    expect(file.name).toBe('trace.log')
    expect(file.mimeType).toBe('text/plain')
    expect(atob(file.data)).toBe('example')
    expect(file).not.toHaveProperty('path')
    await expect(readAttachments([new File(['binary'], 'dump.dmp')], [])).rejects.toThrow('Unsupported file')
    await expect(readAttachments(Array.from({ length: 6 }, () => new File(['x'], 'log.txt')), [])).rejects.toThrow('at most 5')
    await expect(readAttachments([new File([new Uint8Array(256 * 1024 + 1)], 'large.log')], [])).rejects.toThrow('256 KiB')
    await expect(readAttachments([new File(['x'], 'drawing.svg', { type: 'image/svg+xml' })], [])).rejects.toThrow('Unsupported file')
    await expect(readAttachments([new File(['x'], 'bad\nname.txt')], [])).rejects.toThrow('Invalid attachment name')
  })
})