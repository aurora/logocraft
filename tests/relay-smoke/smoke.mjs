// Rauchtest für den Yjs-Relay (Logo.Server) mit echten y-websocket-Clients.
// Voraussetzung: Server läuft (Standard: ws://localhost:5030/ws, überschreibbar mit LOGO_WS).
import * as Y from 'yjs'
import { WebsocketProvider } from 'y-websocket'
import WebSocket from 'ws'

const url = process.env.LOGO_WS ?? 'ws://localhost:5030/ws'
const room = 'smoke-' + Math.random().toString(36).slice(2, 8)

const sleep = ms => new Promise(r => setTimeout(r, ms))
let failed = 0
const check = (name, ok) => {
  console.log(`${ok ? 'OK  ' : 'FAIL'} ${name}`)
  if (!ok) failed++
}

function client(name) {
  const doc = new Y.Doc()
  const provider = new WebsocketProvider(url, room, doc, { WebSocketPolyfill: WebSocket, disableBc: true })
  provider.awareness.setLocalStateField('user', { name })
  return { doc, provider, text: doc.getText('code') }
}

const waitSynced = async (c, ms = 3000) => {
  const t0 = Date.now()
  while (!c.provider.synced && Date.now() - t0 < ms) await sleep(20)
  return c.provider.synced
}

const names = c => [...c.provider.awareness.getStates().values()].map(s => s.user?.name).filter(Boolean).sort()

// A verbindet sich und schreibt
const a = client('A')
check('A wird synced (Server antwortet mit leerem SyncStep2)', await waitSynced(a))
a.text.insert(0, 'fd 100')

// B verbindet sich später und sieht den Text
const b = client('B')
check('B wird synced', await waitSynced(b))
await sleep(300)
check('B sieht den Text von A', b.text.toString() === 'fd 100')

// Änderungen in beide Richtungen
b.text.insert(b.text.length, ' rt 90')
await sleep(300)
check('A sieht die Änderung von B', a.text.toString() === 'fd 100 rt 90')

// Awareness (Name/Cursor): beide sehen sich gegenseitig, auch der Spätere sieht den Früheren
check('B sieht A und sich selbst in Awareness', names(b).join() === 'A,B')
check('A sieht B und sich selbst in Awareness', names(a).join() === 'A,B')

// Alle gehen -> Server behält den Stand im Speicher; C bekommt ihn per Replay
a.provider.destroy()
b.provider.destroy()
await sleep(300)
const c = client('C')
check('C wird synced', await waitSynced(c))
await sleep(300)
check('C bekommt den Stand, obwohl A und B weg sind', c.text.toString() === 'fd 100 rt 90')

// Weggang wird bei den anderen sichtbar (Awareness-Entfernung durch den Server)
const d = client('D')
await waitSynced(d)
await sleep(300)
check('C und D sehen sich', names(c).join() === 'C,D')
d.provider.ws.close()
d.provider.shouldConnect = false
await sleep(500)
check('D verschwindet bei C sofort nach dem Verbindungsabbruch', names(c).join() === 'C')

// Echo-Race: Ein Client spiegelt den Eintrag eines bereits gegangenen Clients zurück.
// Der Server darf daraus keinen Phantom-Eintrag machen, den spätere Clients sehen.
{
  const { Awareness, encodeAwarenessUpdate } = await import('y-protocols/awareness')
  const encoding = await import('lib0/encoding')
  const raw = async () => {
    const ws = new WebSocket(`${url}/${room}-echo`)
    ws.binaryType = 'arraybuffer'
    await new Promise(r => ws.on('open', r))
    return ws
  }
  const awarenessMessage = (aw, ids) => {
    const enc = encoding.createEncoder()
    encoding.writeVarUint(enc, 1)
    encoding.writeVarUint8Array(enc, encodeAwarenessUpdate(aw, ids))
    return encoding.toUint8Array(enc)
  }
  const ghost = new Awareness(new Y.Doc())
  ghost.setLocalStateField('user', { name: 'Geist' })
  const echoer = new Awareness(new Y.Doc())
  echoer.setLocalStateField('user', { name: 'Echo' })

  const wsGhost = await raw()
  const wsEcho = await raw()
  wsGhost.send(awarenessMessage(ghost, [ghost.clientID]))
  await sleep(200)
  // Echo-Client bekommt die Nachricht des Geists weitergeleitet ...
  const ghostMsg = awarenessMessage(ghost, [ghost.clientID])
  wsGhost.close()
  await sleep(300)
  // ... und spiegelt sie nach dem Weggang des Geists noch einmal zurück
  wsEcho.send(awarenessMessage(echoer, [echoer.clientID]))
  wsEcho.send(ghostMsg)
  await sleep(200)

  const late = new Y.Doc()
  const lateProvider = new WebsocketProvider(url, room + '-echo', late, { WebSocketPolyfill: WebSocket, disableBc: true })
  lateProvider.awareness.setLocalStateField('user', { name: 'Spät' })
  await waitSynced({ provider: lateProvider })
  await sleep(300)
  const seen = [...lateProvider.awareness.getStates().values()].map(s => s.user?.name).filter(Boolean).sort().join()
  check('Echo eines gegangenen Clients erzeugt keinen Phantom-Eintrag', seen === 'Echo,Spät', `(gesehen: ${seen})`)
  lateProvider.destroy()
  wsEcho.close()
}

// Gleichzeitiges Seeding mit fester Client-ID erzeugt keine Duplikate
const seedUpdate = text => {
  const s = new Y.Doc()
  s.clientID = 1
  s.getText('code').insert(0, text)
  return Y.encodeStateAsUpdate(s)
}
const x = new Y.Doc()
const y = new Y.Doc()
Y.applyUpdate(x, seedUpdate('Start'))
Y.applyUpdate(y, seedUpdate('Start'))
Y.applyUpdate(x, Y.encodeStateAsUpdate(y))
Y.applyUpdate(y, Y.encodeStateAsUpdate(x))
check('Seeding mit fester Client-ID dupliziert nicht', x.getText('code').toString() === 'Start' && y.getText('code').toString() === 'Start')

c.provider.destroy()
console.log(failed === 0 ? '\nAlles in Ordnung' : `\n${failed} Prüfung(en) fehlgeschlagen`)
process.exit(failed === 0 ? 0 : 1)
