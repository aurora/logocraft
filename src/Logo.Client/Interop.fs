/// Bridge to `editor.js` (CodeMirror 6 + Yjs).
module Logo.Client.Interop

open System
open Fable.Core
open Browser.Types

type EditorOptions =
    { room: string
      /// Names of all primitives, for highlighting and completion
      primitives: string[]
      /// Start program if the room is still empty
      seedText: string
      /// Start content for the shared notes tab if it is still empty
      notesSeed: string
      onChange: Action<string>
      onRun: Action
      onStatus: Action<string>
      onPeers: Action<string[], string[], int> }

type EditorHandle =
    /// Replaces the shared code for all contributors
    abstract setText: string -> unit
    /// Marks a line (1-based) as erroneous; 0 removes the marker
    abstract setErrorLine: int -> unit
    /// Re-measures the newly visible editor after switching tabs
    abstract setEditorTab: string -> unit
    /// Changes the debounce used for reporting code changes ("live delay")
    abstract setChangeDelay: int -> unit
    abstract exportSvg: string -> unit
    abstract exportPng: string -> unit
    abstract destroy: unit -> unit

[<Import("createEditor", "./editor.js")>]
let createEditor (codeParent: HTMLElement) (notesParent: HTMLElement) (options: EditorOptions) : EditorHandle = jsNative

/// Opens a file picker and calls `onText` with the selected UTF-8 text file.
[<Import("pickLogoFile", "./editor.js")>]
let pickLogoFile (onText: Action<string>) : unit = jsNative

/// Deletes the locally (IndexedDB) stored content of a room. `onDone` is called afterwards.
/// The room must not be open anymore (destroy the editor first).
[<Import("deleteRoomData", "./editor.js")>]
let deleteRoomData (room: string) (onDone: Action) : unit = jsNative

/// The currently open editor (there is only one).
let mutable current: EditorHandle option = None
