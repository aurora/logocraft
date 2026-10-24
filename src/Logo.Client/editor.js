// Thin bridge between CodeMirror 6, Yjs and the F#/Fable application.
// All application logic (state, interpreter, UI) lives in F# (see Model.fs and View.fs).

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
import { markdown } from '@codemirror/lang-markdown'
import { tags as t } from '@lezer/highlight'
import * as Y from 'yjs'
import { WebsocketProvider } from 'y-websocket'
import { IndexeddbPersistence } from 'y-indexeddb'
import { yCollab, yUndoManagerKeymap } from 'y-codemirror.next'

// ---------------------------------------------------------------------------
// LOGO syntax highlighting (CodeMirror ships no Logo mode)
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
  { tag: t.keyword, color: '#4cd7f6', fontWeight: '600' },
  { tag: t.standard(t.variableName), color: '#acedff' },
  { tag: t.definition(t.variableName), color: '#d0bcff', fontWeight: '700' },
  { tag: t.local(t.variableName), color: '#6ffbbe' },
  { tag: t.string, color: '#fbbf24' },
  { tag: t.number, color: '#4cd7f6' },
  { tag: t.comment, color: '#86948a', fontStyle: 'italic' },
  { tag: t.operator, color: '#bbcabf' },
  { tag: t.bracket, color: '#bbcabf' }
])

const markdownHighlight = HighlightStyle.define([
  { tag: t.heading, color: '#d0bcff', fontWeight: '700' },
  { tag: t.strong, color: '#dae2fd', fontWeight: '700' },
  { tag: t.emphasis, color: '#acedff', fontStyle: 'italic' },
  { tag: t.link, color: '#4cd7f6', textDecoration: 'underline' },
  { tag: t.url, color: '#4edea3' },
  { tag: t.monospace, color: '#6ffbbe' },
  { tag: t.quote, color: '#bbcabf', fontStyle: 'italic' },
  { tag: t.list, color: '#bbcabf' }
])

// ---------------------------------------------------------------------------
// Error line marker
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
// Autocompletion: primitives + user-defined procedures
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
// Contributor identity (display only — there is no user management)
// ---------------------------------------------------------------------------

const ADJECTIVES = ['Swift', 'Clever', 'Curious', 'Busy', 'Calm', 'Brave', 'Playful', 'Quiet']
const ANIMALS = ['Turtle', 'Owl', 'Fox', 'Otter', 'Hedgehog', 'Penguin', 'Octopus', 'Badger']
const COLORS = ['#e11d48', '#2563eb', '#16a34a', '#d97706', '#9333ea', '#0891b2', '#db2777', '#4f46e5']
const NAME_KEY = 'logo-name'
const SUGGESTION_KEY = 'logo-suggested-name'

function pick(list) { return list[Math.floor(Math.random() * list.length)] }

// CodeMirror normalizes line breaks to \n. The Yjs text must have the same form,
// otherwise positions and lengths diverge between the editor and the shared document.
const normalizeNewlines = text => text.replace(/\r\n?/g, '\n')

const normalizeName = raw =>
    String(raw ?? '').trim().replace(/\s+/g, ' ').slice(0, 32)

/** The name the user confirmed earlier (empty string if this is a first visit). */
export function getStoredName() {
  try { return normalizeName(localStorage.getItem(NAME_KEY)) } catch { return '' }
}

/** A stable suggestion for this tab, so the prompt and the awareness name agree. */
export function getSuggestedName() {
  const stored = getStoredName()
  if (stored) return stored
  try {
    const cached = normalizeName(sessionStorage.getItem(SUGGESTION_KEY))
    if (cached) return cached
  } catch { /* ignore */ }
  const name = `${pick(ADJECTIVES)} ${pick(ANIMALS)}`
  try { sessionStorage.setItem(SUGGESTION_KEY, name) } catch { /* ignore */ }
  return name
}

function identity(preferredName) {
  let stored = null
  try { stored = JSON.parse(sessionStorage.getItem('logo-identity') || 'null') } catch { /* ignore */ }
  const color = stored && stored.color ? stored.color : pick(COLORS)
  const name = normalizeName(preferredName) || getStoredName() || getSuggestedName()
  const id = { name, color, colorLight: color + '33' }
  try { sessionStorage.setItem('logo-identity', JSON.stringify(id)) } catch { /* ignore */ }
  return id
}

// ---------------------------------------------------------------------------
// Local persistence (IndexedDB): one database per room
// ---------------------------------------------------------------------------

const dbName = room => `logo-room-${room}`

/** Deletes a room's local data and calls `onDone` afterwards (also on error/block). */
export function deleteRoomData(room, onDone) {
  let finished = false
  const done = () => { if (!finished) { finished = true; onDone() } }
  try {
    const request = indexedDB.deleteDatabase(dbName(room))
    request.onsuccess = request.onerror = done
    // If another tab keeps the database open, deletion happens later — don't wait forever.
    setTimeout(done, 1500)
  } catch {
    done()
  }
}

// ---------------------------------------------------------------------------
// Export helpers
// ---------------------------------------------------------------------------

const SVG_NS = 'http://www.w3.org/2000/svg'
const CANVAS_BACKGROUND = '#0b1326'

function canvasClone() {
  const source = document.querySelector('svg.canvas')
  if (!source) return null
  const clone = source.cloneNode(true)
  clone.setAttribute('xmlns', SVG_NS)
  const box = (source.getAttribute('viewBox') || '0 0 600 600').trim().split(/\s+/).map(Number)
  const width = box[2] || 600
  const height = box[3] || 600
  clone.setAttribute('width', String(width))
  clone.setAttribute('height', String(height))
  const background = document.createElementNS(SVG_NS, 'rect')
  background.setAttribute('x', String(box[0] || 0))
  background.setAttribute('y', String(box[1] || 0))
  background.setAttribute('width', String(width))
  background.setAttribute('height', String(height))
  background.setAttribute('fill', CANVAS_BACKGROUND)
  clone.insertBefore(background, clone.firstChild)
  return { clone, width, height }
}

function downloadBlob(blob, filename) {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = filename
  document.body.appendChild(link)
  link.click()
  link.remove()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

export function exportSvg(filename = 'logo-drawing.svg') {
  const result = canvasClone()
  if (!result) return
  const source = new XMLSerializer().serializeToString(result.clone)
  downloadBlob(new Blob([source], { type: 'image/svg+xml;charset=utf-8' }), filename)
}

export function exportPng(filename = 'logo-drawing.png') {
  const result = canvasClone()
  if (!result) return
  const source = new XMLSerializer().serializeToString(result.clone)
  const svgUrl = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(source)
  const image = new Image()
  image.onload = () => {
    const scale = 2
    const canvas = document.createElement('canvas')
    canvas.width = Math.max(1, Math.round(result.width * scale))
    canvas.height = Math.max(1, Math.round(result.height * scale))
    const context = canvas.getContext('2d')
    if (!context) return
    context.fillStyle = CANVAS_BACKGROUND
    context.fillRect(0, 0, canvas.width, canvas.height)
    context.drawImage(image, 0, 0, canvas.width, canvas.height)
    canvas.toBlob(blob => { if (blob) downloadBlob(blob, filename) }, 'image/png')
  }
  image.src = svgUrl
}

function downloadText(text, filename, type) {
  downloadBlob(new Blob([text], { type }), filename)
}

/** Downloads the LOGO source as a plain .logo file. */
export function downloadProgram(code, filename) {
  downloadText(code, filename, 'text/plain;charset=utf-8')
}

/** Downloads a versioned JSON bundle containing the room, program and notes. */
export function downloadRoom(room, code, notes, filename) {
  const url = location.origin + location.pathname + '?room=' + encodeURIComponent(room)
  const bundle = {
    format: 'logocraft-room',
    version: 1,
    room,
    url,
    exportedAt: new Date().toISOString(),
    program: code,
    notes
  }
  downloadText(JSON.stringify(bundle, null, 2), filename, 'application/json;charset=utf-8')
}

/** Opens a file picker and returns the chosen text file's contents. */
export function pickLogoFile(onText) {
  const input = document.createElement('input')
  input.type = 'file'
  input.accept = '.logo,.lgo,.txt,text/plain'
  input.style.display = 'none'
  input.addEventListener('change', () => {
    const file = input.files && input.files[0]
    if (!file) { input.remove(); return }
    const reader = new FileReader()
    reader.onload = () => {
      try { onText(String(reader.result ?? '')) } finally { input.remove() }
    }
    reader.onerror = () => input.remove()
    reader.readAsText(file)
  })
  document.body.appendChild(input)
  input.click()
}

/**
 * Zooms with the mouse wheel without scrolling the page.
 * `onZoom` receives 1 (in) or -1 (out); events are lightly throttled.
 */
export function attachWheelZoom(element, onZoom) {
  let last = 0
  const handler = event => {
    event.preventDefault()
    if (event.deltaY === 0) return
    const now = performance.now()
    if (now - last < 60) return
    last = now
    onZoom(event.deltaY < 0 ? 1 : -1)
  }
  element.addEventListener('wheel', handler, { passive: false })
  return () => element.removeEventListener('wheel', handler)
}

// ---------------------------------------------------------------------------
// Editor
// ---------------------------------------------------------------------------

/**
 * @param {HTMLElement} codeParent
 * @param {HTMLElement} notesParent
 * @param {{
 *   room: string,
 *   userName: string,
 *   primitives: string[],
 *   seedText: string,
 *   notesSeed: string,
 *   onChange: (text: string) => void,
 *   onRun: () => void,
 *   onStatus: (status: string) => void,
 *   onPeers: (names: string[], colors: string[], selfIndex: number) => void
 * }} opts
 */
export function createEditor(codeParent, notesParent, opts) {
  const ydoc = new Y.Doc()
  const ytext = ydoc.getText('code')
  const ynotes = ydoc.getText('notes')

  const proto = location.protocol === 'https:' ? 'wss' : 'ws'
  // BroadcastChannel (same-browser tab sync) is off: everything goes through the server,
  // which knows which awareness entry belongs to a connection and removes it on disconnect.
  const provider = new WebsocketProvider(`${proto}://${location.host}/ws`, opts.room, ydoc, { disableBc: true })
  const awareness = provider.awareness
  let me = identity(opts.userName)
  awareness.setLocalStateField('user', me)

  // --- Report code changes to F# (lightly debounced) ---
  let changeDelay = 150
  let timer = null
  const emitChange = () => opts.onChange(ytext.toString())
  ytext.observe(() => {
    clearTimeout(timer)
    timer = setTimeout(emitChange, changeDelay)
  })

  // --- Local copy in the browser (IndexedDB). The server is only a relay: anyone who has
  // opened a room keeps it even if the server forgot it (it syncs back on connect). ---
  let idbSynced = false
  let serverSynced = false
  let idbFallback = null
  try {
    const persistence = new IndexeddbPersistence(dbName(opts.room), ydoc)
    persistence.on('synced', () => { idbSynced = true; trySeed() })
    // Don't wait forever if IndexedDB is unavailable (e.g. blocked)
    idbFallback = setTimeout(() => { idbSynced = true; trySeed() }, 3000)
  } catch {
    idbSynced = true
  }

  // Seed content only if the room is still empty after syncing. A fixed client ID makes all
  // clients generate the same update, so simultaneous first visits don't duplicate content.
  function seedTextIfEmpty(target, content, clientId) {
    if (target.length !== 0 || !content) return false
    const seed = new Y.Doc()
    seed.clientID = clientId
    seed.getText(target === ytext ? 'code' : 'notes').insert(0, normalizeNewlines(content))
    Y.applyUpdate(ydoc, Y.encodeStateAsUpdate(seed))
    return true
  }

  function trySeed() {
    if (!idbSynced || !serverSynced) return
    seedTextIfEmpty(ytext, opts.seedText, 1)
    seedTextIfEmpty(ynotes, opts.notesSeed, 2)
    emitChange()
  }

  provider.on('sync', synced => {
    if (!synced) return
    serverSynced = true
    trySeed()
  })

  provider.on('status', e => opts.onStatus(e.status))
  opts.onStatus('connecting')

  // --- Contributors ---
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

  // --- Theme ---
  const darkTheme = EditorView.theme({
    '&': {
      height: '100%', color: '#dae2fd', backgroundColor: 'transparent', fontSize: '13px'
    },
    '.cm-content': {
      fontFamily: "'JetBrains Mono', ui-monospace, 'Cascadia Code', Consolas, monospace",
      lineHeight: '22px', padding: '8px 0',
      caretColor: '#4edea3'
    },
    '.cm-scroller': { overflow: 'auto' },
    '.cm-gutters': { backgroundColor: 'transparent', color: '#475569', border: 'none' },
    '.cm-lineNumbers .cm-gutterElement': { padding: '0 10px 0 12px' },
    '.cm-activeLine': { backgroundColor: 'rgba(255,255,255,0.03)' },
    '.cm-activeLineGutter': { backgroundColor: 'rgba(255,255,255,0.04)', color: '#f8fafc' },
    '.cm-error-line': { backgroundColor: 'rgba(239,68,68,0.14)', boxShadow: 'inset 3px 0 0 #ef4444' },
    '.cm-selectionBackground, &.cm-focused .cm-selectionBackground, ::selection': {
      backgroundColor: 'rgba(78,222,163,0.22)'
    },
    '.cm-cursor, .cm-dropCursor': { borderLeftColor: '#4edea3' },
    '.cm-tooltip': {
      backgroundColor: '#171f33', color: '#dae2fd',
      border: '1px solid rgba(255,255,255,0.12)', borderRadius: '8px'
    },
    '.cm-tooltip-autocomplete > ul > li[aria-selected]': {
      backgroundColor: '#222a3d', color: '#4edea3'
    },
    '.cm-panels': { backgroundColor: '#131b2e', color: '#dae2fd' },
    '&.cm-focused': { outline: 'none' }
  }, { dark: true })

  // --- LOGO editor ---
  const view = new EditorView({
    parent: codeParent,
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
        // In Logo `"` is a word prefix, not a quote pair — only auto-close brackets.
        EditorState.languageData.of(() => [{ closeBrackets: { brackets: ['(', '['] } }]),
        autocompletion({ override: [completionSource(opts.primitives)] }),
        logoLanguage(opts.primitives),
        syntaxHighlighting(logoHighlight),
        errorLineField,
        darkTheme,
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

  // --- Notes editor (Markdown, same Yjs document) ---
  const notesView = new EditorView({
    parent: notesParent,
    state: EditorState.create({
      doc: ynotes.toString(),
      extensions: [
        markdown(),
        syntaxHighlighting(markdownHighlight),
        drawSelection(),
        EditorView.lineWrapping,
        darkTheme,
        keymap.of([...yUndoManagerKeymap, ...defaultKeymap]),
        yCollab(ynotes, awareness)
      ]
    })
  })

  // Debug: http://localhost:5030/?debug  ->  window.__logo
  if (location.search.includes('debug')) {
    window.__logo = { ydoc, ytext, ynotes, view, notesView, provider }
  }

  return {
    /** Replaces the shared code for all contributors. */
    setText(text) {
      ydoc.transact(() => {
        ytext.delete(0, ytext.length)
        ytext.insert(0, normalizeNewlines(text))
      })
    },
    /** Marks a line (1-based) as erroneous; 0 removes the marker. */
    setErrorLine(line) {
      view.dispatch({ effects: setErrorLineEffect.of(line) })
    },
    /** Re-measures the newly visible editor after a tab switch. */
    setEditorTab(tab) {
      const target = tab === 'notes' ? notesView : view
      requestAnimationFrame(() => {
        target.requestMeasure()
        target.focus()
      })
    },
    /** Adjusts the debounce for reporting code changes (the "live delay"). */
    setChangeDelay(ms) {
      changeDelay = Math.max(0, Number(ms) || 0)
    },
    /** Changes the display name for this tab and remembers it for future visits. */
    setUserName(name) {
      const clean = normalizeName(name) || getSuggestedName()
      try { localStorage.setItem(NAME_KEY, clean) } catch { /* ignore */ }
      // Replace the user object instead of mutating it: y-protocols compares the
      // previous and next state deeply and would otherwise skip the change event.
      // The new color also forces y-codemirror to rebuild its caret label (its
      // widget equality only compares colors, not names).
      const colorIndex = COLORS.indexOf(me.color)
      const color = COLORS[(colorIndex + 1 + COLORS.length) % COLORS.length]
      me = { ...me, name: clean, color, colorLight: color + '33' }
      awareness.setLocalStateField('user', me)
      try { sessionStorage.setItem('logo-identity', JSON.stringify(me)) } catch { /* ignore */ }
    },
    /** Current content of the shared notes tab. */
    getNotes() {
      return ynotes.toString()
    },
    exportSvg,
    exportPng,
    destroy() {
      clearTimeout(timer)
      clearTimeout(idbFallback)
      awareness.off('change', reportPeers)
      view.destroy()
      notesView.destroy()
      provider.destroy()
      ydoc.destroy()
    }
  }
}
