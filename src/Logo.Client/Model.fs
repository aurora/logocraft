module Logo.Client.Model

open System
open Elmish
open Fable.Core
open Logo.Core
open Logo.Core.Interpreter

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

type Status =
    | Connecting
    | Connected
    | Disconnected

type Peer =
    { Name: string
      Color: string
      IsSelf: bool }

type Panel =
    | CodeCanvas
    | ProjectFiles
    | Multiplayer
    | Reference
    | Exporter

type EditorTab =
    | CodeTab
    | NotesTab

type SettingsTab =
    | GeneralTab
    | CanvasTab

type ReplKind =
    | Command
    | Output
    | Error
    | System

type ReplEntry =
    { Time: string
      Kind: ReplKind
      Text: string }

type Drawing =
    { Paths: Path[]
      Turtle: TurtleState
      Steps: int }

/// Playback state for the animated drawing mode.
type Animation =
    { Progress: float
      Total: float
      Playing: bool
      /// Increments on every start so stale timer ticks are ignored
      Id: int }

type Model =
    { /// Shared room name (?room=… in the URL)
      Room: string
      /// Locally remembered rooms, most recently opened first
      Rooms: Rooms.Entry list
      /// Invite link was just copied (short feedback)
      Copied: bool
      /// Current shared program text
      Code: string
      /// Last program run: error marker and program output
      Result: RunResult
      /// Current state of the interpreter session rendered on the canvas
      Drawing: Drawing
      /// Stateful interpreter used by the REPL
      Session: Session
      /// Code the session was started from
      SessionCode: string
      /// Counts program runs (so the editor re-marks the error line)
      RunId: int
      Status: Status
      Peers: Peer list
      AutoRun: bool
      /// Reveal the drawing segment by segment when a program runs
      Animate: bool
      Animation: Animation
      /// Playback speed in percent (25 = 0.25×, 100 = 1×, 400 = 4×)
      AnimSpeed: int
      Fit: bool
      Zoom: int
      ShowTurtle: bool
      ShowGrid: bool
      LiveDelayMs: int
      Panel: Panel
      EditorTab: EditorTab
      ReplLog: ReplEntry list
      SettingsOpen: bool
      SettingsTab: SettingsTab
      FileMenuOpen: bool
      ExportMenuOpen: bool
      SidebarOpen: bool }

type Msg =
    | CodeChanged of string
    | Run
    | ToggleAutoRun
    | ToggleAnimate
    | StartAnimation
    | AnimTick of int
    | SetAnimSpeed of int
    | ToggleFit
    | ToggleTurtle
    | ToggleGrid
    | ZoomIn
    | ZoomOut
    | ZoomReset
    | SetLiveDelay of int
    | StatusChanged of string
    | PeersChanged of names: string[] * colors: string[] * selfIndex: int
    | LoadExample of string
    | NewRoom
    | OpenRoom of string
    | JoinRoom
    | CopyInvite
    | CopiedReset
    | ForgetRoom
    | ForgetRoomById of string
    | ClearCanvas
    | ClearConsole
    | ReplSubmit of string
    | OpenPanel of Panel
    | SetEditorTab of EditorTab
    | ToggleFileMenu
    | ToggleExportMenu
    | OpenSettings
    | CloseSettings
    | SetSettingsTab of SettingsTab
    | ImportFile
    | ExportSvg
    | ExportPng
    | ToggleSidebar
    | NoOp

// ---------------------------------------------------------------------------
// Browser helpers
// ---------------------------------------------------------------------------

[<Emit("new URLSearchParams(window.location.search).get('room')")>]
let private roomParam () : string = jsNative

let private roomFromUrl () : string option =
    let raw = roomParam ()
    let cleaned = if isNull raw then "" else raw |> String.filter Rooms.isAllowedChar
    if cleaned = "" then None else Some(cleaned.Substring(0, min 64 cleaned.Length))

/// Writes the room into the address bar (without reloading) so the link can be shared.
[<Emit("(() => { const u = new URL(location.href); u.searchParams.set('room', $0); history.replaceState(null, '', u) })()")>]
let private showRoomInUrl (room: string) : unit = jsNative

/// Switches to another room (reloads the page; the editor starts fresh).
[<Emit("location.assign(location.pathname + '?room=' + encodeURIComponent($0))")>]
let private navigateToRoom (room: string) : unit = jsNative

[<Emit("navigator.clipboard.writeText(location.origin + location.pathname + '?room=' + encodeURIComponent($0)).catch(() => {})")>]
let private copyInviteLink (room: string) : unit = jsNative

/// window.prompt: returns null on cancel
[<Emit("window.prompt($0)")>]
let private promptText (message: string) : string = jsNative

[<Emit("window.confirm($0)")>]
let private confirmDialog (message: string) : bool = jsNative

[<Emit("window.alert($0)")>]
let private alertDialog (message: string) : unit = jsNative

let private joinRoomDialog () =
    let input = promptText "Room ID or invite link:"
    if not (isNull input) && input.Trim() <> "" then
        match Rooms.parse input with
        | Some room -> navigateToRoom room
        | None -> alertDialog "That is not a valid room ID (allowed: letters, digits, - and _, max. 64 characters)."

let private forgetRoomDialog (room: string) =
    let message =
        $"Remove room “{room}” from this browser?\n\n"
        + "The local copy will be deleted. Other contributors keep the room; "
        + "you can rejoin with the room ID."
    if confirmDialog message then
        // Close the connection and local database so they can be deleted
        Interop.current |> Option.iter (fun editor -> editor.destroy ())
        Interop.current <- None
        Interop.deleteRoomData room (Action(fun () ->
            let next =
                match Rooms.forget room with
                | e :: _ -> e.Id
                | [] -> Rooms.newId ()
            navigateToRoom next))

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private entry kind text : ReplEntry =
    { Time = DateTime.Now.ToString("HH:mm:ss"); Kind = kind; Text = text }

let private addLogs (entries: ReplEntry list) (model: Model) =
    let all = model.ReplLog @ entries
    let capped =
        if all.Length > 300 then all |> List.skip (all.Length - 300) else all
    { model with ReplLog = capped }

let private addLog kind text model = addLogs [ entry kind text ] model

let private drawingOf (r: RunResult) : Drawing =
    { Paths = r.Paths; Turtle = r.Turtle; Steps = r.Steps }

let segmentsOf (drawing: Drawing) =
    drawing.Paths |> Array.sumBy (fun p -> max 0 (p.Points.Length - 1))

let statsText (drawing: Drawing) =
    $"paths: {drawing.Paths.Length} · segments: {segmentsOf drawing} · steps: {drawing.Steps}"

// ---------------------------------------------------------------------------
// Animated drawing
// ---------------------------------------------------------------------------

let private animTickMs = 40

/// Adaptive duration: small drawings don't crawl, huge drawings stay under 4 s.
let private animationDuration total =
    min 4000.0 (600.0 + total * 6.0)

let private animationStep total speed =
    if total <= 0.0 then
        0.0
    else
        let duration = animationDuration total * 100.0 / float (max 25 speed)
        total * float animTickMs / duration

let private completedAnimation (model: Model) =
    let total = float (segmentsOf model.Drawing)
    { model.Animation with Progress = total; Total = total; Playing = false }

let private stopAnimation (model: Model) =
    { model with Animation = completedAnimation model }

let private animationCommand (model: Model) =
    if model.Animation.Playing then
        Cmd.OfAsync.perform
            (fun () -> Async.Sleep animTickMs)
            ()
            (fun () -> AnimTick model.Animation.Id)
    else
        Cmd.none

let private beginAnimation (model: Model) : Model * Cmd<Msg> =
    let total = float (segmentsOf model.Drawing)
    let animation =
        { Progress = 0.0
          Total = total
          Playing = total > 0.0
          Id = model.Animation.Id + 1 }
    let m = { model with Animation = animation }
    m, animationCommand m

let private emptyResult (model: Model) =
    model.Session.Run ""

// ---------------------------------------------------------------------------
// Init / update
// ---------------------------------------------------------------------------

let init () : Model * Cmd<Msg> =
    let fromUrl = roomFromUrl ()
    // Without ?room=: continue the last room, otherwise create a new one
    let room =
        match fromUrl with
        | Some room -> room
        | None ->
            match Rooms.load () with
            | e :: _ -> e.Id
            | [] -> Rooms.newId ()
    let session = Session()
    let initial = session.Run ""
    { Room = room
      Rooms = Rooms.touch room
      Copied = false
      Code = ""
      Result = initial
      Drawing = drawingOf initial
      Session = session
      SessionCode = ""
      RunId = 0
      Status = Connecting
      Peers = []
      AutoRun = true
      Animate = false
      Animation = { Progress = 0.0; Total = 0.0; Playing = false; Id = 0 }
      AnimSpeed = 100
      Fit = false
      Zoom = 100
      ShowTurtle = true
      ShowGrid = true
      LiveDelayMs = 150
      Panel = CodeCanvas
      EditorTab = CodeTab
      ReplLog = [ entry System $"Workspace #{room} ready. Waiting for the relay…" ]
      SettingsOpen = false
      SettingsTab = GeneralTab
      FileMenuOpen = false
      ExportMenuOpen = false
      SidebarOpen = false },
    (if fromUrl.IsNone then Cmd.OfFunc.perform showRoomInUrl room (fun () -> NoOp) else Cmd.none)

let private execute (code: string) (model: Model) : Model =
    let result = model.Session.Run code
    let drawing = drawingOf result
    let total = float (segmentsOf drawing)
    { model with
        Code = code
        Result = result
        Drawing = drawing
        Animation = { model.Animation with Progress = total; Total = total; Playing = false }
        SessionCode = code
        RunId = model.RunId + 1 }

/// Opens the file picker and replaces the shared code with the chosen file's contents.
let private importFile () =
    Interop.pickLogoFile(Action<string>(fun text ->
        if not (String.IsNullOrWhiteSpace text) then
            match Interop.current with
            | Some editor -> editor.setText text
            | None -> ()))

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | CodeChanged code ->
        (if model.AutoRun then execute code model else { model with Code = code }), Cmd.none
    | Run ->
        let m = execute model.Code model
        let m, cmd =
            if m.Animate then beginAnimation m
            else m, Cmd.none
        (m |> addLog System $"Program executed · {statsText m.Drawing}"), cmd
    | ToggleAutoRun ->
        let enabling = not model.AutoRun
        let next =
            { model with
                AutoRun = enabling
                Animate = if enabling then false else model.Animate }
        let m =
            if enabling then execute next.Code next
            else stopAnimation next
        (m |> addLog System (if enabling then "Live run enabled." else "Live run disabled.")), Cmd.none
    | ToggleAnimate ->
        let enabling = not model.Animate
        let next =
            { model with
                Animate = enabling
                AutoRun = if enabling then false else model.AutoRun }
        if enabling then
            let m, cmd = beginAnimation next
            (m |> addLog System "Animated drawing enabled."), cmd
        else
            (stopAnimation next |> addLog System "Animated drawing disabled."), Cmd.none
    | StartAnimation -> beginAnimation model
    | AnimTick id ->
        if not model.Animation.Playing || id <> model.Animation.Id then
            model, Cmd.none
        else
            let total = model.Animation.Total
            let progress = min total (model.Animation.Progress + animationStep total model.AnimSpeed)
            let playing = progress < total
            let m =
                { model with
                    Animation = { model.Animation with Progress = progress; Playing = playing } }
            m, animationCommand m
    | SetAnimSpeed speed -> { model with AnimSpeed = max 25 (min 400 speed) }, Cmd.none
    | ToggleFit -> { model with Fit = not model.Fit }, Cmd.none
    | ToggleTurtle -> { model with ShowTurtle = not model.ShowTurtle }, Cmd.none
    | ToggleGrid -> { model with ShowGrid = not model.ShowGrid }, Cmd.none
    | ZoomIn -> { model with Zoom = min 200 (model.Zoom + 15) }, Cmd.none
    | ZoomOut -> { model with Zoom = max 50 (model.Zoom - 15) }, Cmd.none
    | ZoomReset -> { model with Zoom = 100 }, Cmd.none
    | SetLiveDelay ms -> { model with LiveDelayMs = max 20 (min 500 ms) }, Cmd.none
    | StatusChanged s ->
        let status =
            match s with
            | "connected" -> Connected
            | "connecting" -> Connecting
            | _ -> Disconnected
        if status = model.Status then
            model, Cmd.none
        else
            let text =
                match status with
                | Connected -> "Connected to the relay."
                | Connecting -> "Connecting to the relay…"
                | Disconnected -> "Disconnected. Changes are kept locally."
            { model with Status = status } |> addLog System text, Cmd.none
    | PeersChanged(names, colors, selfIndex) ->
        let peers =
            [ for i in 0 .. names.Length - 1 ->
                  { Name = names.[i]
                    Color = colors.[i]
                    IsSelf = (i = selfIndex) } ]
        { model with Peers = peers }, Cmd.none
    | LoadExample name ->
        match List.tryFind (fun (n, _) -> n = name) Examples.all with
        | Some(_, code) ->
            match Interop.current with
            | Some editor -> editor.setText code
            | None -> ()
            model |> addLog System $"Loaded example “{name}”.", Cmd.none
        | None -> model, Cmd.none
    | NewRoom -> model, Cmd.OfFunc.perform (fun () -> navigateToRoom (Rooms.newId ())) () (fun () -> NoOp)
    | OpenRoom room ->
        if room = model.Room then model, Cmd.none
        else model, Cmd.OfFunc.perform navigateToRoom room (fun () -> NoOp)
    | JoinRoom -> model, Cmd.OfFunc.perform joinRoomDialog () (fun () -> NoOp)
    | CopyInvite ->
        { model with Copied = true },
        Cmd.batch [
            Cmd.OfFunc.perform copyInviteLink model.Room (fun () -> NoOp)
            Cmd.OfAsync.perform (fun () -> Async.Sleep 2000) () (fun () -> CopiedReset)
        ]
    | CopiedReset -> { model with Copied = false }, Cmd.none
    | ForgetRoom -> model, Cmd.OfFunc.perform forgetRoomDialog model.Room (fun () -> NoOp)
    | ForgetRoomById room ->
        if room = model.Room then
            model, Cmd.OfFunc.perform forgetRoomDialog room (fun () -> NoOp)
        elif confirmDialog $"Remove room “{room}” from this browser? The local copy will be deleted." then
            Interop.deleteRoomData room (Action(fun () -> ()))
            { model with Rooms = Rooms.forget room }, Cmd.none
        else
            model, Cmd.none
    | ClearCanvas ->
        let result = emptyResult model
        let m =
            { model with
                Result = result
                Drawing = drawingOf result
                Animation = { model.Animation with Progress = 0.0; Total = 0.0; Playing = false }
                SessionCode = ""
                RunId = model.RunId + 1 }
        m |> addLog System "Canvas cleared.", Cmd.none
    | ClearConsole -> { model with ReplLog = [] }, Cmd.none
    | ReplSubmit input ->
        let command = input.Trim()
        if command = "" then
            model, Cmd.none
        else
            let baseModel, baseEntries =
                if model.SessionCode <> model.Code then
                    let result = model.Session.Run model.Code
                    { model with
                        Result = result
                        Drawing = drawingOf result
                        SessionCode = model.Code
                        RunId = model.RunId + 1 },
                    [ entry System "Program re-run as the REPL base." ]
                else
                    model, []
            let result = baseModel.Session.Exec command
            let drawing =
                { Paths = result.Paths; Turtle = result.Turtle; Steps = result.Steps }
            let outputEntries =
                result.NewOutput
                |> Array.toList
                |> List.map (fun line -> entry Output line)
            let errorEntries =
                match result.Error with
                | Some e ->
                    let text = if e.Line > 0 then $"Line {e.Line}: {e.Message}" else e.Message
                    [ entry Error text ]
                | None -> []
            let entries =
                baseEntries
                @ [ entry Command command ]
                @ outputEntries
                @ errorEntries
                @ [ entry System $"Exec · {statsText drawing}" ]
            ({ baseModel with
                Drawing = drawing
                Animation =
                    { baseModel.Animation with
                        Progress = float (segmentsOf drawing)
                        Total = float (segmentsOf drawing)
                        Playing = false } }
             |> addLogs entries), Cmd.none
    | OpenPanel panel ->
        { model with Panel = panel; FileMenuOpen = false; ExportMenuOpen = false; SidebarOpen = false }, Cmd.none
    | SetEditorTab tab -> { model with EditorTab = tab }, Cmd.none
    | ToggleFileMenu ->
        { model with FileMenuOpen = not model.FileMenuOpen; ExportMenuOpen = false }, Cmd.none
    | ToggleExportMenu ->
        { model with ExportMenuOpen = not model.ExportMenuOpen; FileMenuOpen = false }, Cmd.none
    | OpenSettings -> { model with SettingsOpen = true; SettingsTab = GeneralTab }, Cmd.none
    | CloseSettings -> { model with SettingsOpen = false }, Cmd.none
    | SetSettingsTab tab -> { model with SettingsTab = tab }, Cmd.none
    | ImportFile -> model, Cmd.OfFunc.perform importFile () (fun () -> NoOp)
    | ExportSvg ->
        match Interop.current with
        | Some editor -> editor.exportSvg $"logo-{model.Room}.svg"
        | None -> ()
        model |> addLog System "Exported an SVG snapshot.", Cmd.none
    | ExportPng ->
        match Interop.current with
        | Some editor -> editor.exportPng $"logo-{model.Room}.png"
        | None -> ()
        model |> addLog System "Exported a PNG snapshot.", Cmd.none
    | ToggleSidebar -> { model with SidebarOpen = not model.SidebarOpen }, Cmd.none
    | NoOp -> model, Cmd.none
