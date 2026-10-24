/// Bridge to `editor.js` (CodeMirror 6 + Yjs).
module Logo.Client.Interop

open System
open Fable.Core
open Browser.Types

type EditorOptions =
    { room: string
      /// Confirmed display name (empty on a first visit; editor.js then uses its suggestion)
      userName: string
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
    /// Marks the current step line (1-based); 0 removes the marker
    abstract setStepLine: int -> unit
    /// Re-measures the newly visible editor after switching tabs
    abstract setEditorTab: string -> unit
    /// Changes the debounce used for reporting code changes ("live delay")
    abstract setChangeDelay: int -> unit
    /// Changes the display name in Yjs awareness and remembers it locally
    abstract setUserName: string -> unit
    /// Current content of the shared notes
    abstract getNotes: unit -> string
    abstract exportSvg: string -> unit
    abstract exportPng: string -> unit
    abstract destroy: unit -> unit

[<Import("createEditor", "./editor.js")>]
let createEditor (codeParent: HTMLElement) (notesParent: HTMLElement) (options: EditorOptions) : EditorHandle = jsNative

/// Opens a file picker and calls `onText` with the selected UTF-8 text file.
[<Import("pickLogoFile", "./editor.js")>]
let pickLogoFile (onText: Action<string>) : unit = jsNative

/// Zooms with the mouse wheel without scrolling the page. Returns a dispose function.
/// `onZoom` receives 1 (zoom in) or -1 (zoom out).
[<Import("attachWheelZoom", "./editor.js")>]
let attachWheelZoom (element: HTMLElement) (onZoom: Action<int>) : (unit -> unit) = jsNative

/// Orbits the 3D camera with a drag (active while the element has `view-3d`).
/// `onOrbit` receives the horizontal and vertical deltas in pixels.
[<Import("attachOrbit", "./editor.js")>]
let attachOrbit (element: HTMLElement) (onOrbit: Action<int, int>) : (unit -> unit) = jsNative

/// Downloads the LOGO program as a plain-text file.
[<Import("downloadProgram", "./editor.js")>]
let downloadProgram (code: string) (filename: string) : unit = jsNative

/// Downloads a JSON bundle with room metadata, program and notes.
[<Import("downloadRoom", "./editor.js")>]
let downloadRoom (room: string) (code: string) (notes: string) (filename: string) : unit = jsNative

/// The display name the user confirmed earlier (empty string on a first visit).
[<Import("getStoredName", "./editor.js")>]
let storedName () : string = jsNative

/// A stable generated-name suggestion for this tab.
[<Import("getSuggestedName", "./editor.js")>]
let suggestedName () : string = jsNative

/// Deletes the locally (IndexedDB) stored content of a room. `onDone` is called afterwards.
/// The room must not be open anymore (destroy the editor first).
[<Import("deleteRoomData", "./editor.js")>]
let deleteRoomData (room: string) (onDone: Action) : unit = jsNative

/// The currently open editor (there is only one).
let mutable current: EditorHandle option = None
