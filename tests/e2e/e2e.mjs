// End-to-end test in a real browser (Chrome/Edge headless) against the running server.
// Prerequisite: frontend built (npm run build in src/Logo.Client) and server started.
//   LOGO_URL   (default http://localhost:5030)
//   BROWSER    path to chrome/msedge (or the Linux headless shell)
import puppeteer from 'puppeteer-core'
import { writeFileSync, unlinkSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

const base = process.env.LOGO_URL ?? 'http://localhost:5030'
const importFile = join(tmpdir(), `logo-e2e-${Date.now()}.logo`)
const browserPath =
  process.env.BROWSER ??
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'
const room = 'e2e-' + Math.random().toString(36).slice(2, 8)

const sleep = ms => new Promise(r => setTimeout(r, ms))
let failed = 0
const check = (name, ok, extra = '') => {
  console.log(`${ok ? 'OK  ' : 'FAIL'} ${name}${ok ? '' : ' ' + extra}`)
  if (!ok) failed++
}

const browser = await puppeteer.launch({
  executablePath: browserPath,
  headless: true,
  protocolTimeout: 20000,
  args: ['--no-sandbox']
})
const problems = []

async function ensureName(page, name) {
  try {
    await page.waitForSelector('[data-testid="name-prompt"]', { timeout: 700 })
  } catch {
    return false
  }
  await page.$eval('[data-testid="name-input"]', el => { el.focus(); el.select() })
  await page.keyboard.type(name, { delay: 25 })
  await page.click('[data-testid="name-save"]')
  await page.waitForSelector('[data-testid="name-prompt"]', { hidden: true })
  return true
}

async function open(roomId = room, name = 'Alice') {
  const page = await browser.newPage()
  await page.setViewport({ width: 1440, height: 900 })
  page.on('pageerror', e => problems.push('pageerror: ' + e.message))
  page.on('console', m => { if (m.type() === 'error') problems.push('console: ' + m.text()) })
  await page.goto(`${base}/?room=${roomId}`)
  await page.waitForSelector('.editor-host .cm-content')
  await ensureName(page, name)
  return page
}

// Background tabs do not render (requestAnimationFrame pauses) -> bring them to the front
const front = async page => { await page.bringToFront() }
const editorText = async page => {
  await front(page)
  return page.$eval('.editor-host .cm-content', el => el.innerText.replace(/\u200b/g, ''))
}
const notesText = async page => {
  await front(page)
  return page.$eval('.notes-host .cm-content', el => el.innerText.replace(/\u200b/g, ''))
}
const count = async (page, sel) => { await front(page); return page.$$eval(sel, els => els.length) }
const text = async (page, sel) => { await front(page); return page.$eval(sel, el => el.innerText).catch(() => null) }
const until = async (fn, ms = 5000) => {
  const t0 = Date.now()
  while (Date.now() - t0 < ms) {
    if (await fn()) return true
    await sleep(100)
  }
  return false
}

try {
  // First visit: the name prompt (isolated browser context)
  const nameCtx = await browser.createBrowserContext()
  const np = await nameCtx.newPage()
  await np.setViewport({ width: 1440, height: 900 })
  np.on('pageerror', e => problems.push('pageerror: ' + e.message))
  np.on('console', m => { if (m.type() === 'error') problems.push('console: ' + m.text()) })
  await np.goto(base)
  await np.waitForSelector('.editor-host .cm-content')
  check('Name prompt appears on first visit', await until(async () => (await np.$('[data-testid="name-prompt"]')) !== null))
  const suggested = await np.$eval('[data-testid="name-input"]', el => el.value)
  check('Name prompt suggests a generated name', suggested.length > 0)
  await np.$eval('[data-testid="name-input"]', el => { el.focus(); el.select() })
  await np.keyboard.type('Zoe', { delay: 25 })
  await np.click('[data-testid="name-save"]')
  check('Name prompt closes after saving', await until(async () => (await np.$('[data-testid="name-prompt"]')) === null))
  check('Chosen name is remembered', (await np.evaluate(() => localStorage.getItem('logo-name'))) === 'Zoe')
  check('Chosen name appears in the room', await until(async () => ((await text(np, '.presence-pill')) ?? '').includes('Zoe')))

  // LOGO autoindent: list brackets and to/end blocks
  await front(np)
  await np.click('.editor-host .cm-content')
  await np.keyboard.down('Control')
  await np.keyboard.press('End')
  await np.keyboard.up('Control')
  await np.keyboard.press('Enter')
  await np.keyboard.type('repeat 2 [')
  await np.keyboard.press('Enter')
  await np.keyboard.type('fd 10')
  let lines = (await editorText(np)).split('\n')
  let at = lines.lastIndexOf('repeat 2 [')
  check('Autoindent after [', at >= 0 && lines[at + 1] === '  fd 10', JSON.stringify(lines.slice(Math.max(0, at), at + 3)))
  check('Closing bracket dedents', at >= 0 && lines[at + 2] === ']', JSON.stringify(lines.slice(Math.max(0, at), at + 3)))

  await np.keyboard.down('Control')
  await np.keyboard.press('End')
  await np.keyboard.up('Control')
  await np.keyboard.press('Enter')
  await np.keyboard.type('to box :n')
  await np.keyboard.press('Enter')
  await np.keyboard.type('fd :n')
  await np.keyboard.press('Enter')
  await np.keyboard.type('end')
  lines = (await editorText(np)).split('\n')
  at = lines.lastIndexOf('to box :n')
  check('Autoindent after to', at >= 0 && lines[at + 1] === '  fd :n', JSON.stringify(lines.slice(Math.max(0, at), at + 3)))
  check('end dedents', at >= 0 && lines[at + 2] === 'end', JSON.stringify(lines.slice(Math.max(0, at), at + 3)))

  await nameCtx.close()

  const p1 = await open()
  check('Page 1: connected', await until(async () => (await text(p1, '.status')) === 'Connected'))
  check('Page 1: seed program appears', await until(async () => (await editorText(p1)).includes('flower 24')))
  check('Page 1: drawing is rendered', await until(async () => (await count(p1, 'svg.canvas polyline')) > 0))
  check('Page 1: syntax highlighting active (token classes)', (await count(p1, '.cm-line span[class]')) > 5)
  check('Page 1: no error in the seed program', (await count(p1, '.console-err')) === 0)
  check('Page 1: studio shell is visible', (await count(p1, '[data-testid="nav-code"]')) === 1)

  const p2 = await open()
  check('Page 2: connected', await until(async () => (await text(p2, '.status')) === 'Connected'))
  check('Page 2: sees page 1 text', await until(async () => (await editorText(p2)).includes('flower 24')))
  check('Page 2: seed program was not inserted twice',
    ((await editorText(p2)).match(/to flower/g) ?? []).length === 1)
  check('Page 2: drawing is rendered', await until(async () => (await count(p2, 'svg.canvas polyline')) > 0))
  check('Both see 2 collaborators', await until(async () => (await count(p1, '.peer')) === 2 && (await count(p2, '.peer')) === 2))

  // Renaming, duplicate-name warning and always-visible remote cursor labels
  await front(p2)
  await p2.click('[data-testid="settings-open"]')
  await p2.waitForSelector('[data-testid="name-settings-input"]')
  await p2.$eval('[data-testid="name-settings-input"]', el => { el.focus(); el.select() })
  await p2.keyboard.type('Alice', { delay: 25 })
  check('Duplicate name warning appears', await until(async () => (await count(p2, '.name-warning')) === 1))
  await p2.$eval('[data-testid="name-settings-input"]', el => { el.focus(); el.select() })
  await p2.keyboard.type('Bob', { delay: 25 })
  await p2.click('[data-testid="name-settings-save"]')
  await p2.click('[data-testid="settings-close"]')
  await p2.waitForSelector('[data-testid="settings-modal"]', { hidden: true })
  check('Renamed collaborator appears on page 1', await until(async () => ((await text(p1, '.presence-pills')) ?? '').includes('Bob')))
  await front(p2)
  await p2.click('.editor-host .cm-content')
  await p2.keyboard.press('ArrowDown')
  check('Remote cursor shows the collaborator name', await until(async () => {
    await front(p1)
    const labels = await p1.$$eval('.cm-ySelectionInfo', els => els.map(e => e.textContent.trim()))
    return labels.includes('Bob')
  }))
  const labelOpacity = await p1.$eval('.cm-ySelectionInfo', el => getComputedStyle(el).opacity).catch(() => null)
  check('Cursor label is always visible', labelOpacity === '1')

  // Typing on page 1 -> appears on page 2 and changes the drawing there
  const before = await count(p2, 'svg.canvas polyline')
  await front(p1)
  await p1.click('.editor-host .cm-content')
  await p1.keyboard.down('Control')
  await p1.keyboard.press('End')
  await p1.keyboard.up('Control')
  await p1.keyboard.type('\nsetpc "blue\nfd 120')
  check('Page 2 receives the change from page 1', await until(async () => (await editorText(p2)).includes('fd 120')))
  check('Page 2 draws the new line', await until(async () => (await count(p2, 'svg.canvas polyline')) > before))

  // Error -> message + marked line, on both pages
  await front(p2)
  await p2.click('.editor-host .cm-content')
  await p2.keyboard.down('Control')
  await p2.keyboard.press('End')
  await p2.keyboard.up('Control')
  await p2.keyboard.type('\nunbekannt 5')
  check('Error message with line number', await until(async () => ((await text(p2, '.console-err')) ?? '').includes('unbekannt')))
  check('Error line is marked in the editor', await until(async () => (await count(p2, '.cm-error-line')) === 1))
  check('Error is also visible on page 1', await until(async () => ((await text(p1, '.console-err')) ?? '').includes('unbekannt')))

  // Load an example through the File menu (replaces the shared code)
  await front(p1)
  await p1.click('[data-testid="file-menu"]')
  await p1.waitForSelector('.dropdown-item')
  await p1.evaluate(() => {
    const item = [...document.querySelectorAll('.dropdown-item')].find(b => b.textContent.includes('Star'))
    if (item) item.click()
  })
  check('Example "Star" appears on page 2', await until(async () => (await editorText(p2)).includes('repeat 5 [fd 150 rt 144]')))
  check('Error is gone after loading the example', await until(async () => (await count(p2, '.console-err')) === 0))

  // Shared notes tab: a second collaborative Yjs text
  await front(p1)
  await p1.click('[data-testid="tab-notes"]')
  await p1.waitForSelector('.notes-host .cm-content')
  await p1.click('.notes-host .cm-content')
  await p1.keyboard.down('Control')
  await p1.keyboard.press('End')
  await p1.keyboard.up('Control')
  await p1.keyboard.type('\n\nShared note 42')
  await front(p2)
  await p2.click('[data-testid="tab-notes"]')
  check('Page 2 receives the shared note', await until(async () => (await notesText(p2)).includes('Shared note 42')))
  check('Notes do not modify the code', (await editorText(p2)).includes('repeat 5 [fd 150 rt 144]'))
  await p1.click('[data-testid="tab-code"]')
  await p2.click('[data-testid="tab-code"]')

  // Stateful REPL (local to this browser)
  await front(p1)
  const p2BeforeRepl = await count(p2, 'svg.canvas polyline')
  await p1.click('[data-testid="repl-input"]')
  await p1.keyboard.type('cs fd 50', { delay: 40 })
  check('REPL input receives the command', (await p1.$eval('[data-testid="repl-input"]', el => el.value)) === 'cs fd 50')
  await p1.keyboard.press('Enter')
  check('REPL command is logged', await until(async () => (await text(p1, '.repl-log')).includes('cs fd 50')))
  check('REPL command has no error', await until(async () => (await count(p1, '.repl-line.error')) === 0))
  check('REPL command draws on the canvas', await until(async () => {
    const stats = (await text(p1, '.program-status')) ?? ''
    return (await count(p1, 'svg.canvas polyline')) === 1 && stats.includes('paths: 1')
  }))
  await p1.click('[data-testid="repl-input"]')
  await p1.keyboard.type('rt 90 fd 25', { delay: 40 })
  check('REPL keeps the second command', (await p1.$eval('[data-testid="repl-input"]', el => el.value)) === 'rt 90 fd 25')
  await p1.keyboard.press('Enter')
  check('REPL keeps state between commands', await until(async () => {
    const stats = (await text(p1, '.program-status')) ?? ''
    return (await count(p1, 'svg.canvas polyline')) === 2 && stats.includes('paths: 2')
  }))
  check('REPL is local (page 2 drawing unchanged)', (await count(p2, 'svg.canvas polyline')) === p2BeforeRepl)

  // Canvas controls
  await p1.click('[data-testid="zoom-in"]')
  check('Zoom in updates the label', await until(async () => (await text(p1, '.zoom-value')) === '115%'))
  await p1.click('[data-testid="zoom-reset"]')
  check('Zoom reset restores 100%', await until(async () => (await text(p1, '.zoom-value')) === '100%'))
  const surface = await p1.$eval('.canvas-surface', el => {
    const r = el.getBoundingClientRect()
    return { x: r.left + r.width / 2, y: r.top + r.height / 2 }
  })
  await p1.mouse.move(surface.x, surface.y)
  await p1.mouse.wheel({ deltaY: -120 })
  check('Scroll wheel zooms in', await until(async () => parseFloat((await text(p1, '.zoom-value')) ?? '0') > 100))
  const zoomedIn = parseFloat((await text(p1, '.zoom-value')) ?? '100')
  await sleep(120)
  await p1.mouse.wheel({ deltaY: 120 })
  check('Scroll wheel zooms out', await until(async () => parseFloat((await text(p1, '.zoom-value')) ?? '999') < zoomedIn))
  await p1.click('[data-testid="zoom-reset"]')
  const turtleBefore = await count(p1, 'svg.canvas polygon')
  await p1.click('[data-testid="turtle-toggle"]')
  check('Turtle can be hidden', await until(async () => (await count(p1, 'svg.canvas polygon')) < turtleBefore))
  await p1.click('[data-testid="turtle-toggle"]')
  check('Turtle can be shown again', await until(async () => (await count(p1, 'svg.canvas polygon')) === turtleBefore))
  const gridBefore = await count(p1, 'svg.canvas pattern')
  await p1.click('[data-testid="grid-toggle"]')
  check('Grid can be hidden', await until(async () => (await count(p1, 'svg.canvas pattern')) < gridBefore))
  await p1.click('[data-testid="grid-toggle"]')

  // Export helpers (record the download links instead of writing files)
  await p1.evaluate(() => {
    window.__downloads = []
    HTMLAnchorElement.prototype.click = function () { window.__downloads.push(this.download) }
  })
  await p1.click('[data-testid="export-svg-2"]')
  check('SVG export creates a download', await until(async () => (await p1.evaluate(() => window.__downloads)).some(n => n.endsWith('.svg'))))
  await p1.click('[data-testid="export-png"]')
  check('PNG export creates a download', await until(async () => (await p1.evaluate(() => window.__downloads)).some(n => n.endsWith('.png')), 8000))
  await p1.click('[data-testid="export-menu"]')
  await p1.waitForSelector('.dropdown-item')
  await p1.evaluate(() => {
    const item = [...document.querySelectorAll('.dropdown-item')].find(x => x.textContent.includes('LOGO program'))
    if (item) item.click()
  })
  check('Program export creates a .logo download', await until(async () => (await p1.evaluate(() => window.__downloads)).some(n => n.endsWith('.logo'))))
  await p1.click('[data-testid="export-menu"]')
  await p1.waitForSelector('.dropdown-item')
  await p1.evaluate(() => {
    const item = [...document.querySelectorAll('.dropdown-item')].find(x => x.textContent.includes('Room bundle'))
    if (item) item.click()
  })
  check('Room export creates a .json download', await until(async () => (await p1.evaluate(() => window.__downloads)).some(n => n.endsWith('.json'))))

  // Animated drawing mode
  await p1.click('[data-testid="animate-toggle"]')
  check('Animation mode starts playback', await until(async () => (await count(p1, '[data-testid="anim-progress"]')) === 1))
  const transformBefore = await p1.$eval('svg.canvas g[transform]', el => el.getAttribute('transform'))
  await sleep(250)
  const transformAfter = await p1.$eval('svg.canvas g[transform]', el => el.getAttribute('transform'))
  check('Turtle moves during animation', transformBefore !== transformAfter)
  await p1.click('[data-testid="animate-toggle"]')
  check('Disabling animation stops playback', await until(async () => (await count(p1, '[data-testid="anim-progress"]')) === 0))
  await p1.evaluate(() => {
    const slider = document.querySelector('[data-testid="anim-speed"]')
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set
    setter.call(slider, '50')
    slider.dispatchEvent(new Event('input', { bubbles: true }))
    slider.dispatchEvent(new Event('change', { bubbles: true }))
  })
  check('Animation speed can be changed', await until(async () => (await text(p1, '[data-testid="anim-speed-value"]')) === '0.5×'))
  await p1.click('[data-testid="animate-replay"]')
  check('Replay starts playback', await until(async () => (await count(p1, '[data-testid="anim-progress"]')) === 1))
  check('Replay finishes', await until(async () => (await count(p1, '[data-testid="anim-progress"]')) === 0, 6000))

  // Settings modal
  await p1.click('[data-testid="settings-open"]')
  check('Settings modal opens', await until(async () => (await count(p1, '[data-testid="settings-modal"]')) === 1))
  await p1.waitForSelector('.modal-tab')
  await p1.evaluate(() => {
    const tab = [...document.querySelectorAll('.modal-tab')].find(b => b.textContent.includes('Canvas'))
    if (tab) tab.click()
  })
  check('Canvas settings tab renders toggles', await until(async () => (await count(p1, '.toggle-row')) >= 4))
  await p1.click('[data-testid="settings-close"]')
  check('Settings modal closes', await until(async () => (await count(p1, '[data-testid="settings-modal"]')) === 0))

  // Import a program file -> replaces the shared code for everyone
  writeFileSync(importFile, '; imported program\nsetpc "blue\nrepeat 4 [fd 60 rt 90]\n')
  const chooserPromise = p1.waitForFileChooser()
  await p1.click('[data-testid="import-file"]')
  const chooser = await chooserPromise
  await chooser.accept([importFile])
  check('Imported program appears on page 2', await until(async () => (await editorText(p2)).includes('imported program')))

  // A collaborator leaving disappears
  await p2.close()
  check('Collaborator disappears after closing', await until(async () => (await count(p1, '.peer')) === 1, 7000))

  // Rejoining keeps the current state
  const p3 = await open()
  check('Page 3: sees the current state', await until(async () => (await editorText(p3)).includes('imported program')))
  await front(p1)
  await sleep(1000)
  check('After rejoining both see exactly 2 collaborators',
    (await count(p1, '.peer')) === 2 && (await count(p3, '.peer')) === 2)

  // Local copy: without a server connection the content comes from IndexedDB
  const p4 = await browser.newPage()
  await p4.evaluateOnNewDocument(() => {
    window.WebSocket = class extends EventTarget { constructor() { super(); this.readyState = 0 } send() {} close() {} }
  })
  await p4.goto(`${base}/?room=${room}`)
  await p4.waitForSelector('.editor-host .cm-content')
  check('Without server: content comes from local storage',
    await until(async () => (await editorText(p4)).includes('imported program')))
  check('Without server: drawing is still rendered', await until(async () => (await count(p4, 'svg.canvas polyline')) > 0))
  await p4.close()
  await p3.close()

  // Room IDs, room list, invite and removal (fresh browser context = empty localStorage)
  const ctx = await browser.createBrowserContext()
  const q = await ctx.newPage()
  await q.setViewport({ width: 1440, height: 900 })
  q.on('pageerror', e => problems.push('pageerror: ' + e.message))
  let dialogAnswer = ''
  q.on('dialog', d => d.accept(dialogAnswer))
  const roomOf = page => new URL(page.url()).searchParams.get('room')
  const idbNames = page => page.evaluate(async () => (await indexedDB.databases()).map(d => d.name))
  const roomRows = async page => { await front(page); return page.$$eval('.room-row', els => els.length) }
  const openFiles = async page => {
    await front(page)
    await page.click('[data-testid="nav-files"]')
    await page.waitForSelector('[data-testid="new-room"]')
  }
  const roomPattern = /^[a-z]+-[a-z]+-[a-z]+-[a-z]+$/

  await q.goto(base)
  await q.waitForSelector('.editor-host .cm-content')
  await ensureName(q, 'Rooms')
  const first = roomOf(q)
  check('Without ?room= a four-word room ID is created', roomPattern.test(first ?? ''), String(first))
  check('Seed program in the new room', await until(async () => (await editorText(q)).includes('flower 24')))
  await q.reload()
  await q.waitForSelector('.editor-host .cm-content')
  check('A second visit without ?room= stays in the same room', roomOf(q) === first, String(roomOf(q)))
  await openFiles(q)
  check('Room is in the local list', await until(async () => (await roomRows(q)) === 1))
  check('Room is stored in IndexedDB', (await idbNames(q)).includes('logo-room-' + first), JSON.stringify(await idbNames(q)))

  await Promise.all([q.waitForNavigation(), q.click('[data-testid="new-room"]')])
  await q.waitForSelector('.editor-host .cm-content')
  const second = roomOf(q)
  check('"New room" creates a different room', roomPattern.test(second ?? '') && second !== first, String(second))
  await openFiles(q)
  check('Both rooms are in the list', await until(async () => (await roomRows(q)) === 2))

  dialogAnswer = `${base}/?room=${first}`
  await Promise.all([q.waitForNavigation(), q.click('[data-testid="join-room"]')])
  await q.waitForSelector('.editor-host .cm-content')
  check('"Join room" with an invite link switches rooms', roomOf(q) === first, String(roomOf(q)))

  dialogAnswer = ''
  await openFiles(q)
  await Promise.all([q.waitForNavigation(), q.click('[data-testid="forget-room"]')])
  await q.waitForSelector('.editor-host .cm-content')
  check('Forget switches to the other room', roomOf(q) === second, String(roomOf(q)))
  await openFiles(q)
  check('Forgotten room is removed from the list', await until(async () => (await roomRows(q)) === 1))
  check('Forgotten room is removed from IndexedDB', !(await idbNames(q)).includes('logo-room-' + first), JSON.stringify(await idbNames(q)))
  await ctx.close()

  await front(p1)
  await p1.screenshot({ path: new URL('./screenshot.png', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1') })
} finally {
  await browser.close()
  try { unlinkSync(importFile) } catch { /* ignore */ }
}

const relevant = problems.filter(p => !p.includes('favicon'))
check('No browser errors', relevant.length === 0, '\n  ' + relevant.join('\n  '))
console.log(failed === 0 ? '\nAll good' : `\n${failed} check(s) failed`)
process.exit(failed === 0 ? 0 : 1)
