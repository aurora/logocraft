/// Anbindung an `editor.js` (CodeMirror 6 + Yjs).
module Logo.Client.Interop

open System
open Fable.Core
open Browser.Types

type EditorOptions =
    { room: string
      /// Namen aller Primitive, für Highlighting und Vervollständigung
      primitives: string[]
      /// Startprogramm, falls der Raum noch leer ist
      seedText: string
      onChange: Action<string>
      onRun: Action
      onStatus: Action<string>
      onPeers: Action<string[], string[], int> }

type EditorHandle =
    /// Ersetzt den gemeinsamen Text (für alle Mitwirkenden)
    abstract setText: string -> unit
    /// Markiert eine Zeile (1-basiert) als fehlerhaft, 0 entfernt die Markierung
    abstract setErrorLine: int -> unit
    abstract destroy: unit -> unit

[<Import("createEditor", "./editor.js")>]
let createEditor (parent: HTMLElement) (options: EditorOptions) : EditorHandle = jsNative

/// Löscht die lokal (IndexedDB) gespeicherten Inhalte eines Raums. Danach wird `onDone` aufgerufen.
/// Der Raum darf dafür nicht mehr geöffnet sein (Editor vorher `destroy`en).
[<Import("deleteRoomData", "./editor.js")>]
let deleteRoomData (room: string) (onDone: Action) : unit = jsNative

/// Der aktuell geöffnete Editor (es gibt nur einen).
let mutable current: EditorHandle option = None
