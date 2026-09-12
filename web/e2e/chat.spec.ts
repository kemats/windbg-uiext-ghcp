import { expect, test } from '@playwright/test'

test('startup connects once and header actions stay right aligned through retry', async ({ page }, testInfo) => {
  await page.addInitScript(() => {
    let handler: ((event: { data: unknown }) => void) | undefined
    let sequence = 0
    let connections = 0
    const snapshot = { sessionId: '', mode: 'AskEveryTime', busy: false, status: 'Disconnected', error: null, model: null,
      target: { available: false }, approvals: [], models: [], messages: [] }
    const emit = () => handler?.({ data: { version: 1, sequence: ++sequence, type: 'snapshot', payload: structuredClone(snapshot) } })
    Object.assign(window, { headerTest: {
      connections: () => connections,
      fail: () => handler?.({ data: { version: 1, sequence: ++sequence, type: 'error', payload: { message: 'Saved authentication unavailable' } } }),
      disconnect: () => { snapshot.sessionId = ''; snapshot.status = 'Disconnected'; emit() },
    } })
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_type: string, callback: typeof handler) => { handler = callback }, removeEventListener: () => {},
      postMessage: (request: { type: string }) => {
        if (request.type === 'connect') {
          connections++
          if (connections === 1) {
            handler?.({ data: { version: 1, sequence: ++sequence, type: 'connecting', payload: {} } })
            return
          }
          snapshot.sessionId = 'session'; snapshot.status = 'Ready'
        }
        if (request.type === 'ready') handler?.({ data: { version: 1, sequence: ++sequence, type: 'theme', payload: { theme: 'dark' } } })
        emit()
      },
    } } })
  })
  await page.goto('/')
  const history = page.getByRole('button', { name: 'Session history', exact: true })
  const newChat = page.getByRole('button', { name: 'New chat', exact: true })
  const assertRightAligned = async () => {
    const header = (await page.locator('.topbar').boundingBox())!
    const newBounds = (await newChat.boundingBox())!
    const historyBounds = (await history.boundingBox())!
    const padding = await page.locator('.topbar').evaluate(element => parseFloat(getComputedStyle(element).paddingRight))
    expect(header.x + header.width - newBounds.x - newBounds.width).toBeCloseTo(padding, 0)
    expect(newBounds.x - historyBounds.x - historyBounds.width).toBeCloseTo(8, 0)
    expect(historyBounds.y).toBe(newBounds.y)
    return newBounds.x
  }
  await expect(newChat).toBeDisabled()
  await expect(history).toBeDisabled()
  expect(await page.evaluate('window.headerTest.connections()')).toBe(1)
  await expect(page.getByRole('button', { name: 'Connect', exact: true })).toBeDisabled()
  const original = await assertRightAligned()
  await page.screenshot({ path: testInfo.outputPath('disconnected-header.png') })
  await page.evaluate('window.headerTest.fail()')
  await expect(page.getByRole('alert')).toContainText('Saved authentication unavailable')
  await expect(page.getByRole('button', { name: 'Connect', exact: true })).toBeEnabled()
  expect(await page.evaluate('window.headerTest.connections()')).toBe(1)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  expect(await page.evaluate('window.headerTest.connections()')).toBe(2)
  await expect(page.locator('.session-heading h1')).toHaveText('New chat')
  await expect(newChat).toBeEnabled()
  expect(await assertRightAligned()).toBe(original)
  await page.evaluate('window.headerTest.disconnect()')
  await expect(page.locator('.session-heading')).toHaveCount(0)
  expect(await page.evaluate('window.headerTest.connections()')).toBe(2)
  expect(await assertRightAligned()).toBe(original)
})

test('current session title can be renamed and new chat starts empty', async ({ page }, testInfo) => {
  await page.goto('/')
  const heading = page.locator('.topbar .session-heading')
  const assertHeaderLayout = async () => {
    const header = (await page.locator('.topbar').boundingBox())!
    expect(header.height).toBeLessThanOrEqual(44)
    const controls = page.locator('.topbar').locator('button:visible, input:visible, h1:visible, .demo-label:visible')
    let previousRight = header.x
    for (const control of await controls.all()) {
      const bounds = (await control.boundingBox())!
      expect(bounds.y).toBeGreaterThanOrEqual(header.y)
      expect(bounds.y + bounds.height).toBeLessThanOrEqual(header.y + header.height)
      expect(bounds.x).toBeGreaterThanOrEqual(previousRight)
      expect(bounds.x + bounds.width).toBeLessThanOrEqual(header.x + header.width)
      previousRight = bounds.x + bounds.width
    }
  }
  await expect(heading.getByRole('heading')).toHaveText('New chat')
  await assertHeaderLayout()
  await page.getByRole('button', { name: 'Approve all', exact: true }).click()
  await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Investigate the current heap')
  await page.getByRole('button', { name: 'Send message', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Copy response' })).toBeVisible()
  await expect(heading.getByRole('heading')).toHaveText('Investigate the current heap')
  await heading.getByRole('button', { name: 'Rename current session' }).click()
  await heading.getByRole('textbox', { name: 'Current session name' }).fill('Heap ownership and allocation lifetime investigation')
  await assertHeaderLayout()
  await page.screenshot({ path: testInfo.outputPath('session-name-edit.png') })
  await heading.getByRole('button', { name: 'Save session name' }).click()
  await expect(heading.getByRole('heading')).toHaveText('Heap ownership and allocation lifetime investigation')
  await assertHeaderLayout()
  await page.screenshot({ path: testInfo.outputPath('session-name.png') })
  await page.getByRole('button', { name: 'New chat', exact: true }).click()
  await expect(heading.getByRole('heading')).toHaveText('New chat')
  await expect(page.locator('.message')).toHaveCount(0)
  await page.getByRole('button', { name: 'Session history', exact: true }).click()
  await page.locator('.history-select').filter({ hasText: 'Heap ownership and allocation lifetime investigation' }).click()
  await expect(heading.getByRole('heading')).toHaveText('Heap ownership and allocation lifetime investigation')
  await expect(page.locator('.message.user')).toContainText('Investigate the current heap')
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
})

test('account switch starts a fresh Ask session and keeps the selected model', async ({ page }, testInfo) => {
  await page.goto('/')
  await expect(page.getByRole('button', { name: 'Model', exact: true })).toHaveText('Auto')
  await page.getByRole('button', { name: 'Model', exact: true }).click()
  await page.getByRole('option', { name: 'Demo text model', exact: true }).click()
  await page.getByRole('button', { name: 'Approve all', exact: true }).click()
  await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Draft for previous account')
  await page.getByLabel('GitHub account').click()
  await page.screenshot({ path: testInfo.outputPath('account-menu.png') })
  await page.getByRole('button', { name: 'Switch account', exact: true }).click()
  await expect(page.getByLabel('GitHub account')).toHaveText('@other-demo-user')
  await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveValue('')
  await expect(page.getByRole('button', { name: 'Ask every time', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.getByRole('button', { name: 'Model', exact: true })).toHaveText('Demo text model')
  await expect(page.locator('.message')).toHaveCount(0)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
})

test('history rename resume delete and automatic approvals', async ({ page }, testInfo) => {
  await page.goto('/')
  await page.getByRole('button', { name: 'Approve all', exact: true }).click()
  await expect(page.locator('.warning')).toContainText('result sharing are automatically approved')
  await page.locator('.warning').getByRole('button', { name: 'Dismiss notification' }).click()
  await expect(page.locator('.warning')).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Approve all', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Saved investigation')
  await page.getByRole('button', { name: 'Send message', exact: true }).click()
  await expect(page.locator('.approval')).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Copy response' })).toBeVisible()
  await page.getByRole('button', { name: 'New chat', exact: true }).click()
  await page.getByRole('button', { name: 'Session history', exact: true }).click()
  const history = page.getByRole('region', { name: 'Session history' })
  await history.getByRole('button', { name: 'Rename Saved investigation', exact: true }).click()
  await history.getByRole('textbox', { name: 'Session name' }).fill('Renamed investigation')
  await history.getByRole('button', { name: 'Save name' }).click()
  await expect(history.getByRole('button', { name: 'Rename Renamed investigation' })).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('history.png') })
  await history.locator('.history-select').filter({ hasText: 'Renamed investigation' }).click()
  await expect(history).toHaveCount(0)
  await expect(page.locator('.message.user')).toContainText('Saved investigation')
  await expect(page.getByRole('button', { name: 'Ask every time', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await page.getByRole('button', { name: 'New chat', exact: true }).click()
  await page.getByRole('button', { name: 'Session history', exact: true }).click()
  await history.getByRole('button', { name: 'Delete Renamed investigation', exact: true }).click()
  await history.getByRole('button', { name: 'Delete', exact: true }).click()
  await expect(history.getByText('Renamed investigation', { exact: true })).toHaveCount(0)
})

test('input height supports pointer keyboard and reload', async ({ page }) => {
  await page.goto('/')
  const input = page.getByRole('textbox', { name: 'Message', exact: true })
  const handle = page.getByRole('separator', { name: 'Input height' })
  const before = (await input.boundingBox())!.height
  await handle.focus()
  await page.keyboard.press('ArrowUp')
  expect((await input.boundingBox())!.height).toBeGreaterThan(before)
  const bounds = (await handle.boundingBox())!
  await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2)
  await page.mouse.down()
  await page.mouse.move(bounds.x + bounds.width / 2, bounds.y - 40)
  await page.mouse.up()
  const height = (await input.boundingBox())!.height
  expect(height).toBeGreaterThan(before + 20)
  await page.reload()
  expect((await input.boundingBox())!.height).toBeCloseTo(height, 0)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
})

test('published reasoning collapses on completion and supports speech and explicit links', async ({ page }, testInfo) => {
  await page.addInitScript(() => {
    let handler: ((event: { data: unknown }) => void) | undefined
    let sequence = 0
    const requests: unknown[] = []
    const spoken: string[] = []
    Object.defineProperty(window, 'SpeechSynthesisUtterance', { configurable: true, value: class { constructor(public text: string) {} } })
    Object.defineProperty(window, 'speechSynthesis', { configurable: true, value: Object.assign(new EventTarget(), {
      getVoices: () => [{ localService: true, lang: 'en-US', name: 'Local voice' }],
      speak: (utterance: { text: string }) => spoken.push(utterance.text), cancel: () => {},
    }) })
    const snapshot = { sessionId: 'session', mode: 'AskEveryTime', busy: true, status: 'Thinking', error: null, model: null,
      target: { available: true }, approvals: [], models: [], messages: [
        { id: 'reason', role: 'reasoning', title: 'Reasoning', text: 'Published reasoning from the test provider.', complete: false },
        { id: 'tool', role: 'tool', title: 'debugger_target', text: '{"Type":"UserDump","State":"Stopped"}', complete: true },
        { id: 'answer', role: 'assistant', complete: true, text: '[Documentation](https://learn.microsoft.com/windows-hardware/drivers/debugger/)\n\n[Run stack](windbg-command:%6B)\n\n[Forbidden](file:///C:/secret)' },
      ] }
    const emit = () => handler?.({ data: { version: 1, sequence: ++sequence, type: 'snapshot', payload: structuredClone(snapshot) } })
    Object.assign(window, { featureTest: { requests, spoken, complete: () => { snapshot.busy = false; snapshot.status = 'Ready'; snapshot.messages[0].complete = true; emit() } } })
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_type: string, callback: typeof handler) => { handler = callback }, removeEventListener: () => {},
      postMessage: (request: { type: string }) => { requests.push(request); if (request.type === 'ready') { handler?.({ data: { version: 1, sequence: ++sequence, type: 'theme', payload: { theme: 'dark' } } }); emit() } },
    } } })
  })
  await page.goto('/')
  const activity = page.getByRole('region', { name: 'Reasoning', exact: true })
  await expect(activity.getByRole('button', { name: 'Reasoning In progress' })).toHaveAttribute('aria-expanded', 'true')
  await activity.getByRole('button', { name: 'Read activity aloud' }).click()
  expect(await page.evaluate('window.featureTest.spoken')).toEqual(['Published reasoning from the test provider.'])
  await activity.getByRole('button', { name: 'Stop reading' }).click()
  await page.evaluate('window.featureTest.complete()')
  await expect(activity.getByRole('button', { name: 'Reasoning Completed' })).toHaveAttribute('aria-expanded', 'false')
  await activity.getByRole('button', { name: 'Reasoning Completed' }).click()
  await expect(activity).toContainText('Published reasoning')
  await expect(activity.locator('[data-activity-icon="reasoning"]')).toHaveCount(1)
  const toolActivity = page.getByRole('region', { name: 'debugger_target', exact: true })
  await expect(toolActivity.locator('[data-activity-icon="tool"]')).toHaveCount(1)
  await expect(toolActivity.getByRole('button', { name: 'Read activity aloud' })).toHaveCount(0)
  await expect(page.locator('.message-heading')).toHaveCount(0)
  await page.getByRole('region', { name: 'debugger_target', exact: true }).getByRole('button', { name: 'debugger_target Completed' }).click()
  await expect(page.getByRole('region', { name: 'debugger_target', exact: true })).toContainText('UserDump')
  await expect(page.getByRole('link', { name: 'Forbidden' })).toHaveCount(0)
  await page.getByRole('link', { name: 'Documentation', exact: true }).click()
  await page.getByRole('link', { name: 'Run stack', exact: true }).click()
  const requests = await page.evaluate('window.featureTest.requests')
  expect(requests.filter((request: { type: string }) => request.type === 'command')).toEqual([expect.objectContaining({ text: 'k', sessionId: 'session' })])
  expect(requests.filter((request: { type: string }) => request.type === 'openUrl')).toEqual([expect.objectContaining({ text: 'https://learn.microsoft.com/windows-hardware/drivers/debugger/' })])
  expect(requests.filter((request: { type: string }) => request.type === 'send')).toHaveLength(0)
  await page.screenshot({ path: testInfo.outputPath('activity-dark.png') })
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
})

test('model details and switching preserve the current conversation', async ({ page }, testInfo) => {
  await page.goto('/')
  await page.getByRole('textbox', { name: 'Message', exact: true }).fill('First turn')
  await page.getByRole('button', { name: 'Send message', exact: true }).click()
  await page.getByRole('button', { name: 'Deny', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Copy response' })).toBeVisible()
  await page.getByRole('button', { name: 'Session info', exact: true }).click()
  const sessionId = await page.locator('.session-id').innerText()
  await page.keyboard.press('Escape')
  await page.getByRole('button', { name: 'Approve all', exact: true }).click()
  const picker = page.getByRole('button', { name: 'Model', exact: true })
  await picker.click()
  const dialog = page.getByRole('dialog', { name: 'Choose model' })
  const details = dialog.locator('.model-details')
  await page.getByRole('option', { name: 'Demo model', exact: true }).hover()
  await expect(details).toContainText('Credits per 1M tokens')
  await expect(details.locator('dd').first()).toHaveText('100')
  await expect(details).toContainText('low, medium, high')
  const secondOption = page.getByRole('option', { name: 'Demo text model', exact: true })
  const beforeHover = (await secondOption.boundingBox())!
  await secondOption.hover()
  await expect(details).toContainText('Not supported')
  expect((await secondOption.boundingBox())!.y).toBeCloseTo(beforeHover.y, 0)
  await page.getByRole('option', { name: 'Demo model', exact: true }).hover()
  const bounds = (await dialog.boundingBox())!
  expect(bounds.y).toBeGreaterThanOrEqual(0)
  expect(bounds.x).toBeGreaterThanOrEqual(0)
  expect(bounds.x + bounds.width).toBeLessThanOrEqual(page.viewportSize()!.width)
  expect(bounds.y + bounds.height).toBeLessThanOrEqual((await page.locator('.composer').boundingBox())!.y)
  await page.screenshot({ path: testInfo.outputPath('model-details.png') })
  await page.getByRole('combobox', { name: 'Search models' }).fill('no-such-model')
  await expect(dialog).toContainText('No matching models')
  await page.getByRole('combobox', { name: 'Search models' }).fill('Demo text')
  await expect(details).toContainText('Not reported')
  await page.keyboard.press('Enter')
  await expect(picker).toHaveText('Demo text model')
  await expect(page.locator('.message')).toHaveCount(2)
  await expect(page.getByRole('button', { name: 'Approve all', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await page.getByRole('button', { name: 'Session info', exact: true }).click()
  await expect(page.locator('.session-id')).toHaveText(sessionId)
  await page.keyboard.press('Escape')
  await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Second turn')
  await page.getByRole('button', { name: 'Send message', exact: true }).click()
  await expect(picker).toBeDisabled()
  await expect(page.locator('.approval')).toHaveCount(0)
  await expect(page.locator('.message.assistant').last()).toContainText('Demo text model')
})

test('file chooser, drop, clipboard and attachment-only sending', async ({ page }, testInfo) => {
  await page.goto('/')
  const picker = page.getByRole('button', { name: 'Model', exact: true })
  const files = page.locator('input[type=file]')
  await files.setInputFiles({ name: 'trace.log', mimeType: 'text/plain', buffer: Buffer.from('Selected log content') })
  await expect(page.getByRole('list', { name: 'Attachments to send' })).toContainText('trace.log')
  await expect(page.locator('.message')).toHaveCount(0)
  await page.evaluate(() => {
    const data = new DataTransfer()
    data.items.add(new File(['Dropped content'], 'dropped.txt', { type: 'text/plain' }))
    document.querySelector('textarea')!.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: data }))
  })
  await expect(page.getByRole('list', { name: 'Attachments to send' })).toContainText('dropped.txt')
  await page.evaluate(() => {
    const data = new DataTransfer()
    const bytes = Uint8Array.from(atob('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII='), character => character.charCodeAt(0))
    data.items.add(new File([bytes], 'clipboard.png', { type: 'image/png' }))
    document.querySelector('textarea')!.dispatchEvent(new ClipboardEvent('paste', { bubbles: true, cancelable: true, clipboardData: data }))
  })
  await expect(page.getByRole('list', { name: 'Attachments to send' })).toContainText('clipboard.png')
  const preview = page.getByRole('button', { name: 'Preview clipboard.png', exact: true })
  await expect(preview.locator('img')).toBeVisible()
  await expect.poll(() => preview.locator('img').evaluate(image => (image as HTMLImageElement).naturalWidth)).toBeGreaterThan(0)
  await preview.click()
  await expect(page.getByRole('dialog', { name: 'Image preview: clipboard.png' })).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog', { name: 'Image preview: clipboard.png' })).toHaveCount(0)
  await page.getByRole('button', { name: 'Remove dropped.txt', exact: true }).click()
  await expect(page.getByRole('list', { name: 'Attachments to send' }).locator('li')).toHaveCount(2)
  expect(await page.evaluate(() => {
    const data = new DataTransfer(); data.setData('text/plain', 'ordinary paste')
    return document.querySelector('textarea')!.dispatchEvent(new ClipboardEvent('paste', { bubbles: true, cancelable: true, clipboardData: data }))
  })).toBe(true)
  await picker.click()
  await page.getByRole('option', { name: 'Demo text model', exact: true }).click()
  await page.getByRole('button', { name: 'Send message', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('does not support images')
  await expect(page.locator('.message')).toHaveCount(0)
  await picker.click()
  await page.getByRole('option', { name: 'Demo model', exact: true }).click()
  await page.screenshot({ path: testInfo.outputPath('draft-attachments.png') })
  await page.getByRole('button', { name: 'Send message', exact: true }).click()
  await expect(page.getByRole('list', { name: 'Attachments to send' })).toHaveCount(0)
  await expect(page.getByRole('list', { name: 'Sent attachments' })).toContainText('trace.log')
  await expect(page.getByRole('list', { name: 'Sent attachments' })).toContainText('clipboard.png')
  await expect(page.getByRole('list', { name: 'Sent attachments' }).getByRole('img', { name: 'clipboard.png' })).toBeVisible()
  await page.getByRole('button', { name: 'Cancel response', exact: true }).click()
  await files.setInputFiles({ name: 'large.log', mimeType: 'text/plain', buffer: Buffer.alloc(256 * 1024 + 1) })
  await expect(page.getByRole('alert')).toContainText('256 KiB')
  await files.setInputFiles({ name: 'dump.dmp', mimeType: 'application/octet-stream', buffer: Buffer.from('binary') })
  await expect(page.getByRole('alert')).toContainText('Unsupported file')
  await files.setInputFiles({ name: 'new.txt', mimeType: 'text/plain', buffer: Buffer.from('New draft') })
  await expect(page.getByRole('list', { name: 'Attachments to send' })).toBeVisible()
  await page.getByRole('button', { name: 'New chat', exact: true }).click()
  await expect(page.getByRole('list', { name: 'Attachments to send' })).toHaveCount(0)
})

test('native model or attachment rejection preserves model and draft', async ({ page }) => {
  await page.addInitScript(() => {
    let handler: ((event: { data: unknown }) => void) | undefined
    let sequence = 0
    const requests: unknown[] = []
    Object.assign(window, { testRequests: requests })
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_type: string, callback: typeof handler) => { handler = callback }, removeEventListener: () => { handler = undefined },
      postMessage: (request: { type: string }) => {
        requests.push(request)
        if (request.type === 'ready') handler?.({ data: { version: 1, sequence: ++sequence, type: 'snapshot', payload: {
          sessionId: 'same-session', mode: 'AskEveryTime', busy: false, status: 'Ready', error: null, model: 'first',
          target: { available: true }, approvals: [], messages: [],
          models: [{ id: 'first', name: 'First model' }, { id: 'second', name: 'Second model' }],
        } } })
        else handler?.({ data: { version: 1, sequence: ++sequence, type: 'error', payload: { message: 'Rejected test request' } } })
      },
    } } })
  })
  await page.goto('/')
  const picker = page.getByRole('button', { name: 'Model', exact: true })
  await picker.click()
  await page.getByRole('option', { name: 'Second model', exact: true }).click()
  await expect(page.getByRole('alert')).toHaveText('Rejected test request')
  await expect(picker).toHaveText('First model')
  await expect(picker).toBeEnabled()
  await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Retain my draft')
  await page.locator('input[type=file]').setInputFiles({ name: 'notes.txt', mimeType: 'text/plain', buffer: Buffer.from('Explicit file') })
  await page.getByRole('button', { name: 'Send message', exact: true }).click()
  await expect(page.getByRole('alert')).toHaveText('Rejected test request')
  await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveValue('Retain my draft')
  await expect(page.getByRole('list', { name: 'Attachments to send' })).toContainText('notes.txt')
  const requests = await page.evaluate('window.testRequests')
  expect(requests.find((request: { type: string }) => request.type === 'model')).toMatchObject({ sessionId: 'same-session', model: 'second' })
  expect(requests.find((request: { type: string }) => request.type === 'new')).toBeUndefined()
  expect(requests.find((request: { type: string }) => request.type === 'send')).toMatchObject({ sessionId: 'same-session', text: 'Retain my draft', attachments: [{ name: 'notes.txt', mimeType: 'text/plain', data: Buffer.from('Explicit file').toString('base64') }] })
})

test('composer contains settings without submitting the draft', async ({ page }, testInfo) => {
  await page.goto('/')
  const header = page.locator('header.topbar')
  await expect(header.getByLabel('GitHub account')).toHaveText('@demo-user')
  await expect(header.getByRole('button', { name: 'New chat' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Copilot Chat', exact: true })).toHaveCount(0)
  await expect(page.getByText('Ready', { exact: true })).toHaveCount(0)
  expect((await header.boundingBox())!.height).toBeLessThanOrEqual(42)
  const composer = page.locator('form.composer')
  const message = composer.getByRole('textbox', { name: 'Message', exact: true })
  const model = composer.getByRole('button', { name: 'Model', exact: true })
  const modes = composer.getByRole('group', { name: 'Execution approval mode' })
  await message.fill('Keep this draft while changing settings')
  await model.click()
  await page.getByRole('option', { name: 'Demo model', exact: true }).click()
  await expect(model).toHaveText('Demo model')
  await modes.getByRole('button', { name: 'Approve all', exact: true }).click()
  await expect(modes.getByRole('button', { name: 'Approve all', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.locator('.message')).toHaveCount(0)
  await expect(message).toHaveValue('Keep this draft while changing settings')
  await modes.getByRole('button', { name: 'Ask every time', exact: true }).click()
  await expect(modes.getByRole('button', { name: 'Ask every time', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.locator('.message')).toHaveCount(0)
  const frame = (await composer.boundingBox())!
  const sessionTrigger = (await page.locator('footer').getByRole('button', { name: 'Session info', exact: true }).boundingBox())!
  expect(sessionTrigger.y).toBeGreaterThanOrEqual(frame.y + frame.height)
  expect(sessionTrigger.x + sessionTrigger.width).toBeCloseTo(frame.x + frame.width, 0)
  const text = (await message.boundingBox())!
  const send = composer.getByRole('button', { name: 'Send message', exact: true })
  for (const control of [model, modes, send]) {
    const bounds = (await control.boundingBox())!
    expect(bounds.x).toBeGreaterThanOrEqual(frame.x)
    expect(bounds.x + bounds.width).toBeLessThanOrEqual(frame.x + frame.width)
    expect(bounds.y).toBeGreaterThanOrEqual(text.y + text.height)
    expect(bounds.y + bounds.height).toBeLessThanOrEqual(frame.y + frame.height)
  }
  const modeBounds = (await modes.boundingBox())!
  const sendBounds = (await send.boundingBox())!
  expect(modeBounds.x + modeBounds.width).toBeLessThanOrEqual(sendBounds.x)
  await page.screenshot({ path: testInfo.outputPath('integrated-composer.png') })
  await send.click()
  await expect(page.getByRole('region', { name: 'Execute command approval' })).toBeVisible()
  await expect(page.getByRole('status')).not.toBeEmpty()
  await expect(model).toBeDisabled()
  await composer.getByRole('button', { name: 'Cancel response', exact: true }).click()
  await expect(page.getByRole('status')).toHaveText('Cancelled')
})

test('role alignment, turn hover actions, account and session info', async ({ page }, testInfo) => {
  await page.addInitScript(() => {
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: {
      writeText: async (text: string) => { Object.assign(window, { copiedResponse: text }) },
    } })
  })
  await page.goto('/')
  await expect(page.getByLabel('GitHub account')).toHaveText('@demo-user')
  await page.getByRole('textbox', { name: 'Message', exact: true }).fill('What is the current target?')
  await page.getByRole('button', { name: 'Send message', exact: true }).click()
  await page.getByRole('button', { name: 'Deny', exact: true }).click()
  const answer = page.getByRole('article', { name: 'Copilot response' })
  await expect(answer).toContainText('No further debugger action was taken.')
  await expect(answer).toContainText('0.125 credits')
  await page.getByRole('textbox', { name: 'Message', exact: true }).hover()
  await expect(answer.locator('.turn-details')).toHaveCSS('opacity', '0')
  await answer.hover()
  await expect(answer.locator('.turn-details')).toHaveCSS('opacity', '1')
  await expect(answer.locator('time')).toHaveAttribute('datetime', /\d{4}-\d{2}-\d{2}T/)
  await expect(answer).toContainText('Auto')
  await answer.getByRole('button', { name: 'Copy response' }).click()
  expect(await page.evaluate('window.copiedResponse')).toContain('No further debugger action')
  const userBox = await page.locator('.message.user').boundingBox()
  const answerBox = await answer.boundingBox()
  expect(userBox!.x).toBeGreaterThan(answerBox!.x)
  expect(userBox!.x + userBox!.width).toBeGreaterThan(answerBox!.x + answerBox!.width)
  await answer.focus()
  await expect(answer.locator('.turn-details')).toHaveCSS('opacity', '1')
  await page.screenshot({ path: testInfo.outputPath('turn-details.png') })
  await page.getByRole('button', { name: 'Session info', exact: true }).click()
  const info = page.getByRole('region', { name: 'Session info' })
  await expect(info).toBeVisible()
  await expect(info).toContainText('0.125 credits')
  await expect(info.getByRole('progressbar')).toHaveAttribute('value', '17')
  await expect(info).toContainText('1,360 / 8,000 tokens')
  await page.screenshot({ path: testInfo.outputPath('session-info.png') })
  const bounds = await info.boundingBox()
  expect(bounds!.x).toBeGreaterThanOrEqual(0)
  expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(page.viewportSize()!.width)
  expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(page.viewportSize()!.height)
  const trigger = (await page.getByRole('button', { name: 'Session info', exact: true }).boundingBox())!
  expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(trigger.y)
  await page.keyboard.press('Escape')
  await expect(info).toHaveCount(0)
  await page.getByRole('button', { name: 'New chat', exact: true }).click()
  await expect(page.locator('.message')).toHaveCount(0)
  await page.getByRole('button', { name: 'Session info', exact: true }).click()
  await expect(info).toContainText('Not reported')
  await expect(info.getByRole('progressbar')).toHaveCount(0)
  await expect(page.getByLabel('GitHub account')).toHaveText('@demo-user')
})

test('execution, separate output consent, Markdown and Mermaid', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.goto('/')
  await page.getByRole('textbox', { name: 'Message', exact: true }).fill('Analyze the stack')
  await page.getByRole('button', { name: 'Send message', exact: true }).click()
  await expect(page.getByRole('region', { name: 'Execute command approval' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Run command', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Run command', exact: true }).click()
  await expect(page.getByRole('region', { name: 'Share output approval' })).toBeVisible()
  await page.getByRole('button', { name: 'Share output', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Stack summary' })).toBeVisible()
  await expect(page.locator('.diagram svg')).toBeVisible()
  await expect(page.locator('.diagram svg text').filter({ hasText: 'main' })).toBeVisible()
  await expect(page.locator('.diagram svg text').filter({ hasText: 'Worker' })).toBeVisible()
  await page.locator('.diagram').scrollIntoViewIfNeeded()
  await page.screenshot({ path: testInfo.outputPath('chat.png') })
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  const footer = await page.locator('footer').boundingBox()
  const send = await page.getByRole('button', { name: 'Send message', exact: true }).boundingBox()
  expect(send!.y + send!.height).toBeLessThanOrEqual(footer!.y + footer!.height)
  expect(errors).toEqual([])
  await page.getByRole('button', { name: 'New chat', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Ask every time' })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.locator('.message')).toHaveCount(0)
})

test('IME enter does not submit, cancellation drops pending approvals', async ({ page }) => {
  await page.goto('/')
  const composer = page.getByRole('textbox', { name: 'Message', exact: true })
  await composer.fill('Japanese composition')
  await composer.dispatchEvent('keydown', { key: 'Enter', code: 'Enter', isComposing: true })
  await expect(page.locator('.message')).toHaveCount(0)
  await page.getByRole('button', { name: 'Send message', exact: true }).click()
  await page.getByRole('button', { name: 'Cancel response', exact: true }).click()
  await expect(page.locator('.approval')).toHaveCount(0)
  await expect(page.getByRole('status')).toHaveText('Cancelled')
  await composer.fill('Second request')
  await page.getByRole('button', { name: 'Send message', exact: true }).click()
  await page.getByRole('button', { name: 'Deny', exact: true }).click()
  await expect(page.getByText('The request was denied. No further debugger action was taken.')).toBeVisible()
})

test('native bridge rejects stale snapshots and does not render raw HTML or images', async ({ page }, testInfo) => {
  await page.addInitScript(() => {
    let handler: ((event: { data: unknown }) => void) | undefined
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_type: string, callback: typeof handler) => { handler = callback },
      removeEventListener: () => { handler = undefined },
      postMessage: () => {
        const snapshot = { sessionId: 'test', mode: 'AskEveryTime', busy: false, status: 'Ready', error: null, model: null,
          target: { available: true }, approvals: [], models: [],
          account: { authenticated: true, login: 'a-long-github-account-name-for-layout', host: 'github.example.com' },
          messages: [{ id: 'answer', role: 'assistant', complete: true, text: 'Visible prose\n\n<img src="https://example.com/tracker" onerror="alert(1)">\n\n![tracking](https://example.com/pixel)\n\n[bad](javascript:alert(1))' },
            { id: 'empty', role: 'assistant', complete: true, text: '' }] }
        handler?.({ data: { version: 1, sequence: 1, type: 'theme', payload: { theme: 'dark' } } })
        handler?.({ data: { version: 1, sequence: 2, type: 'snapshot', payload: snapshot } })
        handler?.({ data: { version: 1, sequence: 1, type: 'snapshot', payload: { ...snapshot, messages: [] } } })
      },
    } } })
  })
  const remote: string[] = []
  page.on('request', request => { if (request.url().startsWith('https://example.com')) remote.push(request.url()) })
  await page.goto('/')
  await expect(page.getByText('Visible prose')).toBeVisible()
  await expect(page.locator('.message img')).toHaveCount(0)
  await expect(page.locator('.message a')).toHaveCount(0)
  expect(remote).toEqual([])
  await expect(page.locator('.message')).toHaveCount(1)
  await page.getByRole('article', { name: 'Copilot response' }).focus()
  await expect(page.locator('.turn-details')).toContainText('Not reported')
  await expect(page.locator('.turn-details')).toContainText('Model not reported')
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')
  await page.getByRole('button', { name: 'Session info', exact: true }).click()
  const info = page.getByRole('region', { name: 'Session info' })
  await expect(info).toContainText('@a-long-github-account-name-for-layout')
  await expect(info).toContainText('Not reported')
  await expect(info.getByRole('progressbar')).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('dark-unreported-session.png') })
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await page.getByLabel('GitHub account').click()
  await expect(info).toHaveCount(0)
})

test('late local voices, replacement, stale callbacks and new-chat stop', async ({ page }) => {
  await page.addInitScript(() => {
    let handler: ((event: { data: unknown }) => void) | undefined
    let sequence = 0
    const voice = { localService: true, lang: 'en-US', name: 'Local test voice', default: false, voiceURI: 'local' }
    const state = { voices: [] as unknown[], utterances: [] as { onend?: () => void; onerror?: () => void }[], cancellations: 0 }
    const synthesis = Object.assign(new EventTarget(), {
      getVoices: () => state.voices,
      speak: (utterance: { onend?: () => void; onerror?: () => void }) => state.utterances.push(utterance),
      cancel: () => { state.cancellations++ },
    })
    Object.defineProperty(window, 'speechSynthesis', { value: synthesis, configurable: true })
    Object.defineProperty(window, 'SpeechSynthesisUtterance', { value: class { constructor(public text: string) {} }, configurable: true })
    Object.assign(window, { speechTest: { state, enable: () => { state.voices = [voice]; synthesis.dispatchEvent(new Event('voiceschanged')) } } })
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_type: string, callback: typeof handler) => { handler = callback }, removeEventListener: () => { handler = undefined },
      postMessage: (request: { type: string }) => handler?.({ data: { version: 1, sequence: ++sequence, type: 'snapshot', payload: {
        sessionId: request.type === 'new' ? 'new-session' : 'session', mode: 'AskEveryTime', busy: false, status: 'Ready', error: null, model: null,
        target: { available: true }, approvals: [], models: [], messages: request.type === 'new' ? [] : [
          { id: 'first', role: 'assistant', complete: true, text: 'First answer.\n\n```text\nDo not read this\n```' },
          { id: 'second', role: 'assistant', complete: true, text: 'Second answer.' },
        ],
      } } }),
    } } })
  })
  await page.goto('/')
  await expect(page.getByRole('button', { name: 'Read aloud', exact: true }).first()).toBeDisabled()
  await page.evaluate('window.speechTest.enable()')
  await page.getByRole('button', { name: 'Read aloud', exact: true }).first().click()
  expect(await page.evaluate('window.speechTest.state.utterances[0].text')).toBe('First answer.')
  await page.getByRole('button', { name: 'Read aloud', exact: true }).click()
  await page.evaluate('window.speechTest.state.utterances[0].onend()')
  await expect(page.getByRole('button', { name: 'Stop reading', exact: true })).toHaveCount(1)
  await page.evaluate('window.speechTest.state.utterances[1].onerror()')
  await expect(page.getByRole('button', { name: 'Stop reading', exact: true })).toHaveCount(0)
  await page.getByRole('button', { name: 'Read aloud', exact: true }).first().click()
  const before = await page.evaluate('window.speechTest.state.cancellations')
  await page.getByRole('button', { name: 'New chat', exact: true }).click()
  expect(await page.evaluate('window.speechTest.state.cancellations')).toBeGreaterThan(before)
  await expect(page.getByRole('button', { name: 'Stop reading', exact: true })).toHaveCount(0)
})