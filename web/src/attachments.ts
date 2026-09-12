import type { AttachmentInfo, ChatAttachment } from './bridge'

export interface DraftAttachment extends ChatAttachment, Omit<AttachmentInfo, 'data'> { id: string }
export const fileAccept = 'image/png,image/jpeg,image/gif,image/webp,text/*,.log,.json,.jsonl,.xml,.yaml,.yml,.csv,.md,.cs,.cpp,.c,.h,.hpp,.js,.ts,.tsx,.jsx,.py,.ps1,.sql,.ini,.config,.toml,.sh,.rs,.go,.java,.diff,.patch'
const textExtension = /\.(txt|log|jsonl?|xml|ya?ml|csv|md|cs|cpp|c|h|hpp|[jt]sx?|py|ps1|sql|ini|config|toml|sh|rs|go|java|diff|patch)$/i
const imageTypes: Record<string, string> = { png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', gif: 'image/gif', webp: 'image/webp' }

export async function readAttachments(files: File[], current: DraftAttachment[]): Promise<DraftAttachment[]> {
  if (files.length + current.length > 5) throw new Error('Attach at most 5 files.')
  if (files.reduce((total, file) => total + file.size, current.reduce((total, file) => total + file.size, 0)) > 8 * 1024 * 1024)
    throw new Error('Attachments exceed the 8 MiB total limit.')
  const classified = files.map(file => {
    const extension = file.name.split('.').at(-1)?.toLowerCase() || ''
    const mimeType = imageTypes[extension] || (Object.values(imageTypes).includes(file.type) ? file.type :
      !file.type.startsWith('image/') && (file.type.startsWith('text/') || textExtension.test(file.name)) ? 'text/plain' : null)
    if (!mimeType) throw new Error(`Unsupported file: ${file.name}. Attach text, source code, logs, PNG, JPEG, GIF or WebP.`)
    if (file.name.length > 200 || Array.from(file.name).some(character => {
      const code = character.charCodeAt(0)
      return code < 32 || (code >= 127 && code <= 159) || character === '/' || character === '\\'
    })) throw new Error('Invalid attachment name.')
    if (file.size > (mimeType === 'text/plain' ? 256 * 1024 : 4 * 1024 * 1024))
      throw new Error(`${file.name} exceeds the ${mimeType === 'text/plain' ? '256 KiB text' : '4 MiB image'} limit.`)
    return { file, mimeType }
  })
  return Promise.all(classified.map(async ({ file, mimeType }) => {
    const bytes = new Uint8Array(await file.arrayBuffer())
    let binary = ''
    for (let offset = 0; offset < bytes.length; offset += 32768) binary += String.fromCharCode(...bytes.subarray(offset, offset + 32768))
    return { id: crypto.randomUUID(), name: file.name, size: file.size, mimeType, data: btoa(binary) }
  }))
}

export function fileSize(size: number) {
  return size < 1024 ? `${size} B` : size < 1024 * 1024 ? `${(size / 1024).toFixed(1)} KiB` : `${(size / (1024 * 1024)).toFixed(1)} MiB`
}