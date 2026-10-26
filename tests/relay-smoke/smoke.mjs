// Smoke test for the Yjs relay (Logo.Server) with real y-websocket clients.
// Prerequisite: server is running (default ws://localhost:5030/ws, override with LOGO_WS).
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

// Cross-origin browser connections are rejected before a room is created.
const rejectedOrigin = await new Promise(resolve => {
  const ws = new WebSocket(`${url}/${room}-origin`, { headers: { Origin: 'https://evil.example' } })
  const done = value => { try { ws.terminate() } catch { /* ignore */ } ; resolve(value) }
  ws.on('open', () => done(false))
  ws.on('unexpected-response', (_req, res) => done(res.statusCode === 403))
  ws.on('error', () => done(true))
  setTimeout(() => done(false), 3000)
})
check('Cross-origin WebSocket is rejected', rejectedOrigin)

// A connects and writes
const a = client('A')
check('A is synced (server replies with an empty SyncStep2)', await waitSynced(a))
a.text.insert(0, 'fd 100')

// B connects later and sees the text
const b = client('B')
check('B is synced', await waitSynced(b))
await sleep(300)
check('B sees the text from A', b.text.toString() === 'fd 100')

// Changes in both directions
b.text.insert(b.text.length, ' rt 90')
await sleep(300)
check('A sees the change from B', a.text.toString() === 'fd 100 rt 90')

// Awareness (name/cursor): both see each other, including the later client
check('B sees A and itself in awareness', names(b).join() === 'A,B')
check('A sees B and itself in awareness', names(a).join() === 'A,B')

// Everyone leaves -> the server keeps the state in memory; C receives it via replay
a.provider.destroy()
b.provider.destroy()
await sleep(300)
const c = client('C')
check('C is synced', await waitSynced(c))
await sleep(300)
check('C receives the state even though A and B are gone', c.text.toString() === 'fd 100 rt 90')

// A disconnect becomes visible to the others (the server removes the awareness entry)
const d = client('D')
await waitSynced(d)
await sleep(300)
check('C and D see each other', names(c).join() === 'C,D')
d.provider.ws.close()
d.provider.shouldConnect = false
await sleep(500)
check('D disappears from C immediately after the connection drops', names(c).join() === 'C')

// Echo race: a client mirrors the entry of a client that has already left.
// The server must not turn this into a phantom entry that later clients see.
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
  ghost.setLocalStateField('user', { name: 'Ghost' })
  const echoer = new Awareness(new Y.Doc())
  echoer.setLocalStateField('user', { name: 'Echo' })

  const wsGhost = await raw()
  const wsEcho = await raw()
  wsGhost.send(awarenessMessage(ghost, [ghost.clientID]))
  await sleep(200)
  // The echo client receives the ghost message forwarded ...
  const ghostMsg = awarenessMessage(ghost, [ghost.clientID])
  wsGhost.close()
  await sleep(300)
  // ... and mirrors it back again after the ghost has left
  wsEcho.send(awarenessMessage(echoer, [echoer.clientID]))
  wsEcho.send(ghostMsg)
  await sleep(200)

  const late = new Y.Doc()
  const lateProvider = new WebsocketProvider(url, room + '-echo', late, { WebSocketPolyfill: WebSocket, disableBc: true })
  lateProvider.awareness.setLocalStateField('user', { name: 'Late' })
  await waitSynced({ provider: lateProvider })
  await sleep(300)
  const seen = [...lateProvider.awareness.getStates().values()].map(s => s.user?.name).filter(Boolean).sort().join()
  check('An echo from a departed client creates no phantom entry', seen === 'Echo,Late', `(seen: ${seen})`)
  lateProvider.destroy()
  wsEcho.close()
}

// Concurrent seeding with a fixed client ID creates no duplicates
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
check('Seeding with a fixed client ID does not duplicate', x.getText('code').toString() === 'Start' && y.getText('code').toString() === 'Start')

c.provider.destroy()
console.log(failed === 0 ? '\nAll good' : `\n${failed} check(s) failed`)
process.exit(failed === 0 ? 0 : 1)
