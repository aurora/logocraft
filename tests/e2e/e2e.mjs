// End-to-End-Test im Browser (Chrome/Edge headless) gegen den laufenden Server.
// Voraussetzung: Frontend gebaut (npm run build in src/Logo.Client) und Server gestartet.
//   LOGO_URL   (Standard http://localhost:5030)
//   BROWSER    Pfad zu chrome/msedge
import puppeteer from 'puppeteer-core'

const base = process.env.LOGO_URL ?? 'http://localhost:5030'
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

const browser = await puppeteer.launch({ executablePath: browserPath, headless: true, protocolTimeout: 15000 })
const problems = []

async function open() {
  const page = await browser.newPage()
  await page.setViewport({ width: 1400, height: 800 })
  page.on('pageerror', e => problems.push('pageerror: ' + e.message))
  page.on('console', m => { if (m.type() === 'error') problems.push('console: ' + m.text()) })
  await page.goto(`${base}/?room=${room}`)
  await page.waitForSelector('.cm-content')
  return page
}

// Hintergrund-Tabs rendern nicht (requestAnimationFrame pausiert) -> vor dem Lesen nach vorne holen
const front = async page => { await page.bringToFront() }
const editorText = async page => { await front(page); return page.$eval('.cm-content', el => el.innerText.replace(/\u200b/g, '')) }
const count = async (page, sel) => { await front(page); return page.$$eval(sel, els => els.length) }
const text = async (page, sel) => { await front(page); return page.$eval(sel, el => el.innerText).catch(() => null) }
const until = async (fn, ms = 4000) => {
  const t0 = Date.now()
  while (Date.now() - t0 < ms) {
    if (await fn()) return true
    await sleep(100)
  }
  return false
}

try {
  const p1 = await open()
  check('Seite 1: Verbunden', await until(async () => (await text(p1, '.status')) === 'Verbunden'))
  check('Seite 1: Startprogramm erscheint', await until(async () => (await editorText(p1)).includes('flower 24')))
  check('Seite 1: Zeichnung wird gerendert', await until(async () => (await count(p1, 'svg.canvas polyline')) > 0))
  check('Seite 1: Syntax-Highlighting aktiv (Token-Klassen)', (await count(p1, '.cm-line span[class]')) > 5)
  check('Seite 1: Keine Fehlermeldung im Startprogramm', (await count(p1, '.console-err')) === 0)

  const p2 = await open()
  check('Seite 2: Verbunden', await until(async () => (await text(p2, '.status')) === 'Verbunden'))
  check('Seite 2: sieht den Text von Seite 1', await until(async () => (await editorText(p2)).includes('flower 24')))
  check('Seite 2: Startprogramm wurde nicht doppelt eingefügt',
    ((await editorText(p2)).match(/to flower/g) ?? []).length === 1)
  check('Seite 2: Zeichnung wird gerendert', await until(async () => (await count(p2, 'svg.canvas polyline')) > 0))
  check('Beide sehen 2 Mitwirkende', await until(async () => (await count(p1, '.peer')) === 2 && (await count(p2, '.peer')) === 2))

  // Tippen auf Seite 1 -> erscheint auf Seite 2 und verändert die Zeichnung dort
  const before = await count(p2, 'svg.canvas polyline')
  await front(p1)
  await p1.click('.cm-content')
  await p1.keyboard.down('Control')
  await p1.keyboard.press('End')
  await p1.keyboard.up('Control')
  await p1.keyboard.type('\nsetpc "blue\nfd 120')
  check('Seite 2 erhält Änderung von Seite 1', await until(async () => (await editorText(p2)).includes('fd 120')))
  check('Seite 2 zeichnet die neue Linie', await until(async () => (await count(p2, 'svg.canvas polyline')) > before))

  // Fehler -> Meldung + markierte Zeile, bei beiden
  await front(p2)
  await p2.click('.cm-content')
  await p2.keyboard.down('Control')
  await p2.keyboard.press('End')
  await p2.keyboard.up('Control')
  await p2.keyboard.type('\nunbekannt 5')
  check('Fehlermeldung mit Zeilennummer', await until(async () => ((await text(p2, '.console-err')) ?? '').includes('unbekannt')))
  check('Fehlerzeile im Editor markiert', await until(async () => (await count(p2, '.cm-error-line')) === 1))
  check('Fehler auch bei Seite 1 sichtbar', await until(async () => ((await text(p1, '.console-err')) ?? '').includes('unbekannt')))

  // Beispiel laden ersetzt den Code bei allen
  await front(p1)
  await p1.select('select.examples', 'Stern')
  check('Beispiel "Stern" erscheint bei Seite 2', await until(async () => (await editorText(p2)).includes('repeat 5 [fd 150 rt 144]')))
  check('Fehler ist nach Beispiel weg', await until(async () => (await count(p2, '.console-err')) === 0))

  // Seite 2 verlässt den Raum -> Seite 1 sieht nur noch 1 Mitwirkenden
  await p2.close()
  check('Mitwirkender verschwindet nach Schließen', await until(async () => (await count(p1, '.peer')) === 1, 6000))

  // Neu beitreten: Stand bleibt erhalten
  const p3 = await open()
  check('Seite 3: sieht den aktuellen Stand', await until(async () => (await editorText(p3)).includes('repeat 5 [fd 150 rt 144]')))

  await front(p1)
  await sleep(1500)
  const names1 = await p1.$$eval('.peer', e => e.map(x => x.innerText))
  const names3 = await (async () => { await front(p3); return p3.$$eval('.peer', e => e.map(x => x.innerText)) })()
  check('Nach erneutem Beitritt sehen beide genau 2 Mitwirkende', names1.length === 2 && names3.length === 2, JSON.stringify({ names1, names3 }))
  // Lokale Kopie: Ohne Server-Verbindung kommt der Inhalt aus IndexedDB
  const p4 = await browser.newPage()
  await p4.evaluateOnNewDocument(() => {
    window.WebSocket = class extends EventTarget { constructor() { super(); this.readyState = 0 } send() {} close() {} }
  })
  await p4.goto(`${base}/?room=${room}`)
  await p4.waitForSelector('.cm-content')
  check('Ohne Server: Inhalt kommt aus dem lokalen Speicher',
    await until(async () => (await editorText(p4)).includes('repeat 5 [fd 150 rt 144]')))
  check('Ohne Server: Zeichnung wird trotzdem gerendert', await until(async () => (await count(p4, 'svg.canvas polyline')) > 0))
  await p4.close()

  // Raum-IDs, Raumliste, Einladen, Entfernen (frischer Browser-Kontext = leerer localStorage)
  const ctx = await browser.createBrowserContext()
  const q = await ctx.newPage()
  await q.setViewport({ width: 1400, height: 800 })
  q.on('pageerror', e => problems.push('pageerror: ' + e.message))
  let dialogAnswer = ''
  q.on('dialog', d => d.accept(dialogAnswer))
  const roomOf = page => new URL(page.url()).searchParams.get('room')
  const roomCount = page => page.$eval('select.rooms option', o => o.textContent)
  const idbNames = page => page.evaluate(async () => (await indexedDB.databases()).map(d => d.name))
  const roomPattern = /^[a-z]+-[a-z]+-[a-z]+-[a-z]+$/

  await q.goto(base)
  await q.waitForSelector('.cm-content')
  const first = roomOf(q)
  check('Ohne ?room= wird eine Raum-ID aus vier Wörtern angelegt', roomPattern.test(first ?? ''), String(first))
  check('Startprogramm im neuen Raum', await until(async () => (await editorText(q)).includes('flower 24')))
  await q.reload()
  await q.waitForSelector('.cm-content')
  check('Zweiter Besuch ohne ?room= bleibt im selben Raum', roomOf(q) === first, String(roomOf(q)))
  check('Raum steht in der lokalen Liste', (await roomCount(q)).includes('(1)'), await roomCount(q))
  check('Raum liegt in IndexedDB', (await idbNames(q)).includes('logo-room-' + first), JSON.stringify(await idbNames(q)))

  await Promise.all([q.waitForNavigation(), q.evaluate(() => [...document.querySelectorAll('button.tool')].find(b => b.textContent.includes('Neu')).click())])
  await q.waitForSelector('.cm-content')
  const second = roomOf(q)
  check('"Neu" legt einen anderen Raum an', roomPattern.test(second ?? '') && second !== first, String(second))
  check('Beide Räume stehen in der Liste', (await roomCount(q)).includes('(2)'), await roomCount(q))

  dialogAnswer = `${base}/?room=${first}`
  await Promise.all([q.waitForNavigation(), q.evaluate(() => [...document.querySelectorAll('button.tool')].find(b => b.textContent.includes('Beitreten')).click())])
  await q.waitForSelector('.cm-content')
  check('"Beitreten" mit Einladungslink wechselt in den Raum', roomOf(q) === first, String(roomOf(q)))

  await Promise.all([q.waitForNavigation(), q.evaluate(() => [...document.querySelectorAll('button.tool')].find(b => b.textContent.includes('🗑')).click())])
  await q.waitForSelector('.cm-content')
  check('Entfernen wechselt in den anderen Raum', roomOf(q) === second, String(roomOf(q)))
  check('Entfernter Raum ist aus der Liste', (await roomCount(q)).includes('(1)'), await roomCount(q))
  check('Entfernter Raum ist aus IndexedDB gelöscht', !(await idbNames(q)).includes('logo-room-' + first), JSON.stringify(await idbNames(q)))
  await ctx.close()

  await front(p1)
  await p1.screenshot({ path: new URL('./screenshot.png', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1') })
} finally {
  await browser.close()
}

const relevant = problems.filter(p => !p.includes('favicon'))
check('Keine Browser-Fehler', relevant.length === 0, '\n  ' + relevant.join('\n  '))
console.log(failed === 0 ? '\nAlles in Ordnung' : `\n${failed} Prüfung(en) fehlgeschlagen`)
process.exit(failed === 0 ? 0 : 1)
