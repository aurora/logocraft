// Dünne Anbindung von CodeMirror 6 und Yjs für die F#/Fable-Anwendung.
// Die gesamte Anwendungslogik (Zustand, Interpreter, UI) liegt in F# (siehe App.fs).

import { EditorState, StateEffect, StateField } from '@codemirror/state'
import {
  EditorView, keymap, lineNumbers, highlightActiveLine, highlightActiveLineGutter,
  drawSelection, Decoration
} from '@codemirror/view'
import {
  StreamLanguage, syntaxHighlighting, HighlightStyle, bracketMatching, indentOnInput
} from '@codemirror/language'
import { defaultKeymap, indentWithTab } from '@codemirror/commands'
import { closeBrackets, closeBracketsKeymap, autocompletion } from '@codemirror/autocomplete'
import { tags as t } from '@lezer/highlight'
import * as Y from 'yjs'
import { WebsocketProvider } from 'y-websocket'
import { IndexeddbPersistence } from 'y-indexeddb'
import { yCollab, yUndoManagerKeymap } from 'y-codemirror.next'

// ---------------------------------------------------------------------------
// Syntax-Highlighting für LOGO (CodeMirror liefert keinen Logo-Modus mit)
// ---------------------------------------------------------------------------

const CONTROL = new Set([
  'to', 'end', 'repeat', 'if', 'ifelse', 'for', 'while', 'stop',
  'output', 'op', 'run', 'make', 'local'
])

function logoLanguage(primitives) {
  const known = new Set(primitives)
  return StreamLanguage.define({
    name: 'logo',
    startState: () => ({ afterTo: false }),
    token(stream, state) {
      if (stream.eatSpace()) return null
      if (stream.eat(';')) { stream.skipToEnd(); return 'comment' }
      if (stream.match(/^"[^\s\[\]()]*/)) return 'string'
      if (stream.match(/^:[^\s\[\]()+\-*\/%=<>;]*/)) return 'variableName.local'
      if (stream.match(/^(\d+\.?\d*|\.\d+)/)) return 'number'
      if (stream.match(/^(<=|>=|<>|[+\-*\/%=<>])/)) return 'operator'
      if (stream.match(/^[\[\]()]/)) return 'bracket'
      if (stream.match(/^[^\s\[\]()+\-*\/%=<>;":]+/)) {
        const word = stream.current().toLowerCase()
        if (state.afterTo) { state.afterTo = false; return 'variableName.definition' }
        if (word === 'to') { state.afterTo = true; return 'keyword' }
        if (CONTROL.has(word)) return 'keyword'
        if (known.has(word)) return 'variableName.standard'
        return null
      }
      stream.next()
      return null
    }
  })
}

const logoHighlight = HighlightStyle.define([
  { tag: t.keyword, color: '#7c3aed', fontWeight: '600' },
  { tag: t.standard(t.variableName), color: '#2563eb' },
  { tag: t.definition(t.variableName), color: '#b45309', fontWeight: '700' },
  { tag: t.local(t.variableName), color: '#0f766e' },
  { tag: t.string, color: '#c2410c' },
  { tag: t.number, color: '#0e7490' },
  { tag: t.comment, color: '#6b7280', fontStyle: 'italic' },
  { tag: t.operator, color: '#475569' },
  { tag: t.bracket, color: '#475569' }
])

// ---------------------------------------------------------------------------
// Fehlerzeile markieren
// ---------------------------------------------------------------------------

const setErrorLineEffect = StateEffect.define()
const errorLineField = StateField.define({
  create: () => Decoration.none,
  update(deco, tr) {
    deco = deco.map(tr.changes)
    for (const e of tr.effects) {
      if (e.is(setErrorLineEffect)) {
        const n = e.value
        if (n >= 1 && n <= tr.state.doc.lines) {
          const line = tr.state.doc.line(n)
          deco = Decoration.set([Decoration.line({ class: 'cm-error-line' }).range(line.from)])
        } else {
          deco = Decoration.none
        }
      }
    }
    return deco
  },
  provide: f => EditorView.decorations.from(f)
})

// ---------------------------------------------------------------------------
// Auto-Vervollständigung: Primitive + selbst definierte Prozeduren
// ---------------------------------------------------------------------------

function completionSource(primitives) {
  const builtin = [...new Set([...primitives, ...CONTROL])].sort()
  return context => {
    const word = context.matchBefore(/[A-Za-z][A-Za-z0-9_.?]*/)
    if (!word || (word.from === word.to && !context.explicit)) return null
    const text = context.state.doc.toString()
    const user = [...text.matchAll(/^\s*to\s+([^\s\[\]()]+)/gim)].map(m => m[1].toLowerCase())
    const options = [
      ...builtin.map(label => ({ label, type: 'keyword' })),
      ...user.map(label => ({ label, type: 'function', boost: 1 }))
    ]
    return { from: word.from, options, validFor: /^[A-Za-z0-9_.?]*$/ }
  }
}

// ---------------------------------------------------------------------------
// Identität der Mitwirkenden (nur für die Anzeige, keine Benutzerverwaltung)
// ---------------------------------------------------------------------------

const ADJECTIVES = ['Flinke', 'Kluge', 'Neugierige', 'Fleißige', 'Gemütliche', 'Mutige', 'Verspielte', 'Leise']
const ANIMALS = ['Schildkröte', 'Eule', 'Fuchs', 'Otter', 'Igel', 'Pinguin', 'Krake', 'Dachs']
const COLORS = ['#e11d48', '#2563eb', '#16a34a', '#d97706', '#9333ea', '#0891b2', '#db2777', '#4f46e5']

function pick(list) { return list[Math.floor(Math.random() * list.length)] }

// CodeMirror normalisiert Zeilenumbrüche intern auf \n. Der Yjs-Text muss dieselbe Form haben,
// sonst weichen Positionen und Längen zwischen Editor und gemeinsamem Dokument voneinander ab.
const normalizeNewlines = text => text.replace(/\r\n?/g, '\n')

function identity() {
  try {
    const stored = sessionStorage.getItem('logo-identity')
    if (stored) return JSON.parse(stored)
  } catch { /* ignorieren */ }
  const color = pick(COLORS)
  const id = { name: `${pick(ADJECTIVES)} ${pick(ANIMALS)}`, color, colorLight: color + '33' }
  try { sessionStorage.setItem('logo-identity', JSON.stringify(id)) } catch { /* ignorieren */ }
  return id
}

// ---------------------------------------------------------------------------
// Lokale Speicherung (IndexedDB): pro Raum eine eigene Datenbank
// ---------------------------------------------------------------------------

const dbName = room => `logo-room-${room}`

/** Löscht die lokalen Daten eines Raums und ruft danach `onDone` auf (auch bei Fehler/Blockade). */
export function deleteRoomData(room, onDone) {
  let finished = false
  const done = () => { if (!finished) { finished = true; onDone() } }
  try {
    const request = indexedDB.deleteDatabase(dbName(room))
    request.onsuccess = request.onerror = done
    // Hält noch ein anderer Tab die Datenbank offen, wird erst später gelöscht -> nicht ewig warten
    setTimeout(done, 1500)
  } catch {
    done()
  }
}

// ---------------------------------------------------------------------------
// Editor
// ---------------------------------------------------------------------------

/**
 * @param {HTMLElement} parent
 * @param {{
 *   room: string,
 *   primitives: string[],
 *   seedText: string,
 *   onChange: (text: string) => void,
 *   onRun: () => void,
 *   onStatus: (status: string) => void,
 *   onPeers: (names: string[], colors: string[], selfIndex: number) => void
 * }} opts
 */
export function createEditor(parent, opts) {
  const ydoc = new Y.Doc()
  const ytext = ydoc.getText('code')

  const proto = location.protocol === 'https:' ? 'wss' : 'ws'
  // BroadcastChannel (Sync zwischen Tabs desselben Browsers) aus: alles läuft über den Server, der
  // anhand der Verbindung weiß, wem welcher Awareness-Eintrag gehört und ihn beim Verlassen entfernen kann.
  const provider = new WebsocketProvider(`${proto}://${location.host}/ws`, opts.room, ydoc, { disableBc: true })
  const awareness = provider.awareness
  const me = identity()
  awareness.setLocalStateField('user', me)

  // --- Änderungen an F# melden (leicht entprellt) ---
  let timer = null
  const emitChange = () => opts.onChange(ytext.toString())
  ytext.observe(() => {
    clearTimeout(timer)
    timer = setTimeout(emitChange, 150)
  })

  // --- Lokale Kopie im Browser (IndexedDB). Der Server ist nur Vermittler: Wer einen Raum schon
  // einmal geöffnet hat, behält ihn, auch wenn der Server ihn vergessen hat (er wird beim Verbinden
  // wieder zurücksynchronisiert).
  let idbSynced = false
  let serverSynced = false
  let idbFallback = null
  try {
    const persistence = new IndexeddbPersistence(dbName(opts.room), ydoc)
    persistence.on('synced', () => { idbSynced = true; trySeed() })
    // Ohne funktionierende IndexedDB (z. B. gesperrt) nicht ewig auf den lokalen Speicher warten
    idbFallback = setTimeout(() => { idbSynced = true; trySeed() }, 3000)
  } catch {
    idbSynced = true
  }

  // --- Startinhalt: nur wenn der Raum nach dem Sync (Server und lokaler Speicher) noch leer ist.
  // Alle Clients erzeugen dasselbe Update mit fester Client-ID, dadurch entstehen auch bei
  // gleichzeitigem Beitritt keine Duplikate (Yjs ignoriert bereits bekannte Operationen).
  function trySeed() {
    if (!idbSynced || !serverSynced) return
    if (ytext.length === 0) {
      const seed = new Y.Doc()
      seed.clientID = 1
      seed.getText('code').insert(0, normalizeNewlines(opts.seedText))
      Y.applyUpdate(ydoc, Y.encodeStateAsUpdate(seed))
    }
    emitChange()
  }
  provider.on('sync', synced => {
    if (!synced) return
    serverSynced = true
    trySeed()
  })

  provider.on('status', e => opts.onStatus(e.status))
  opts.onStatus('connecting')

  // --- Mitwirkende ---
  const reportPeers = () => {
    const names = []
    const colors = []
    let selfIndex = -1
    awareness.getStates().forEach((state, clientId) => {
      if (!state.user) return
      if (clientId === ydoc.clientID) selfIndex = names.length
      names.push(state.user.name)
      colors.push(state.user.color)
    })
    opts.onPeers(names, colors, selfIndex)
  }
  awareness.on('change', reportPeers)
  reportPeers()

  // --- Editor-Ansicht ---
  const theme = EditorView.theme({
    '&': { height: '100%', fontSize: '15px', backgroundColor: '#ffffff' },
    '.cm-scroller': { fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace", lineHeight: '1.5' },
    '.cm-gutters': { backgroundColor: '#f8fafc', color: '#94a3b8', border: 'none' },
    '.cm-activeLine': { backgroundColor: '#f1f5f9' },
    '.cm-activeLineGutter': { backgroundColor: '#e2e8f0' },
    '.cm-error-line': { backgroundColor: '#fee2e2', boxShadow: 'inset 3px 0 0 #dc2626' },
    '&.cm-focused': { outline: 'none' }
  })

  const view = new EditorView({
    parent,
    state: EditorState.create({
      doc: ytext.toString(),
      extensions: [
        lineNumbers(),
        highlightActiveLineGutter(),
        highlightActiveLine(),
        drawSelection(),
        indentOnInput(),
        bracketMatching(),
        closeBrackets(),
        // In Logo ist " nur ein Präfix ("wort), kein Anführungszeichenpaar -> nur Klammern schließen
        EditorState.languageData.of(() => [{ closeBrackets: { brackets: ['(', '['] } }]),
        autocompletion({ override: [completionSource(opts.primitives)] }),
        logoLanguage(opts.primitives),
        syntaxHighlighting(logoHighlight),
        errorLineField,
        theme,
        keymap.of([
          { key: 'Mod-Enter', run: () => { opts.onRun(); return true } },
          ...closeBracketsKeymap,
          ...yUndoManagerKeymap,
          ...defaultKeymap,
          indentWithTab
        ]),
        yCollab(ytext, awareness)
      ]
    })
  })

  // Zum Debuggen im Browser: http://localhost:5030/?debug  ->  window.__logo
  if (location.search.includes('debug')) window.__logo = { ydoc, ytext, view, provider }

  return {
    /** Ersetzt den gemeinsamen Text (für alle Mitwirkenden). */
    setText(text) {
      ydoc.transact(() => {
        ytext.delete(0, ytext.length)
        ytext.insert(0, normalizeNewlines(text))
      })
    },
    /** Markiert eine Zeile (1-basiert) als fehlerhaft; 0 entfernt die Markierung. */
    setErrorLine(line) {
      view.dispatch({ effects: setErrorLineEffect.of(line) })
    },
    destroy() {
      clearTimeout(timer)
      clearTimeout(idbFallback)
      awareness.off('change', reportPeers)
      view.destroy()
      provider.destroy()
      ydoc.destroy()
    }
  }
}
