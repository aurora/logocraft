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
      /// Confirmed display name (empty until the onboarding prompt is completed)
      MyName: string
      /// Stable generated suggestion shown on a first visit
      SuggestedName: string
      /// Show the welcome dialog on a first visit
      NamePromptOpen: bool
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
      /// Step-through trace of turtle movements
      StepActive: bool
      StepEvents: StepEvent[]
      StepIndex: int
      AutoRunBeforeStep: bool
      AnimateBeforeStep: bool
      /// Locally set line breakpoints (movement lines only)
      Breakpoints: int list
      Fit: bool
      Zoom: int
      /// Perspective view instead of the top-down projection
      View3D: bool
      /// Camera orbit around the drawing (degrees)
      CameraYaw: int
      CameraPitch: int
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
    | Step
    | ContinueStep
    | ToggleBreakpoint of int
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
    | ZoomBy of int
    | ZoomReset
    | Toggle3D
    | OrbitCamera of dx: int * dy: int
    | ResetCamera
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
    | SaveName of string
    | ImportFile
    | ExportProgram
    | ExportRoom
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

/// True when another participant in this room already uses `name`.
let nameTaken (name: string) (model: Model) =
    let draft = name.Trim().ToLowerInvariant()
    draft <> ""
    && model.Peers
       |> List.exists (fun p -> not p.IsSelf && p.Name.Trim().ToLowerInvariant() = draft)

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

let private endStep (model: Model) =
    { model with
        StepActive = false
        StepEvents = [||]
        StepIndex = 0
        AutoRun = model.AutoRunBeforeStep
        Animate = model.AnimateBeforeStep }

let private tracedResultModel (model: Model) (result: RunResult) =
    let drawing = drawingOf result
    let total = float (segmentsOf drawing)
    { model with
        Result = result
        Drawing = drawing
        RunId = model.RunId + 1
        Animation = { model.Animation with Progress = total; Total = total; Playing = false }
        SessionCode = "" }

let private beginStepAt (model: Model) (result: RunResult) (events: StepEvent[]) (index: int) (message: string) =
    let next = tracedResultModel model result
    if events.Length = 0 then
        (next |> addLog System "No turtle movements to step through."), Cmd.none
    else
        let index = max 0 (min index (events.Length - 1))
        ({ next with
            StepActive = true
            StepEvents = events
            StepIndex = index
            AutoRunBeforeStep = model.AutoRun
            AnimateBeforeStep = model.Animate
            AutoRun = false
            Animate = false }
         |> addLog System message), Cmd.none

/// Blank lines have no movement event; snap the breakpoint to the previous (then next) code line.
let private normalizeBreakpointLine (model: Model) (line: int) =
    let lines = model.Code.Split('\n')
    if line < 1 || line > lines.Length then
        None
    elif lines.[line - 1].Trim() <> "" then
        Some line
    else
        let isCode candidate = lines.[candidate - 1].Trim() <> ""
        let previous = [ line - 1 .. -1 .. 1 ] |> List.tryFind isCode
        match previous with
        | Some candidate -> Some candidate
        | None -> [ line + 1 .. lines.Length ] |> List.tryFind isCode

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
    let stored = Interop.storedName ()
    let suggestion = Interop.suggestedName ()
    let myName = if isNull stored then "" else stored.Trim()
    let namePromptOpen = myName = ""
    { Room = room
      Rooms = Rooms.touch room
      MyName = myName
      SuggestedName = suggestion
      NamePromptOpen = namePromptOpen
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
      StepActive = false
      StepEvents = [||]
      StepIndex = 0
      AutoRunBeforeStep = false
      AnimateBeforeStep = false
      Breakpoints = []
      Fit = false
      Zoom = 100
      View3D = false
      CameraYaw = 25
      CameraPitch = 20
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

let private saveName (raw: string) (model: Model) =
    let trimmed = if isNull raw then "" else raw.Trim()
    let name =
        if trimmed = "" then model.SuggestedName
        elif trimmed.Length > 32 then trimmed.Substring(0, 32)
        else trimmed
    Interop.current |> Option.iter (fun editor -> editor.setUserName name)
    { model with
        MyName = name
        NamePromptOpen = false }
    |> addLog System $"Display name set to “{name}”."

let private continueStep (model: Model) : Model * Cmd<Msg> =
    if model.StepActive then
        let next =
            model.StepEvents
            |> Array.indexed
            |> Array.tryFind (fun (index, event) ->
                index > model.StepIndex
                && List.contains event.Line model.Breakpoints)
        match next with
        | Some(index, _) -> { model with StepIndex = index }, Cmd.none
        | None -> (endStep model |> addLog System "Continue: end of program."), Cmd.none
    else
        let result, events = Interpreter.runTraced model.Code
        let hit =
            if events.Length = 0 then None
            else events |> Array.tryFindIndex (fun event -> List.contains event.Line model.Breakpoints)
        match hit with
        | Some index ->
            beginStepAt
                model
                result
                events
                index
                $"Stopped at breakpoint on line {events.[index].Line}."
        | None ->
            let next = tracedResultModel model result
            let message =
                if model.Breakpoints.IsEmpty then "No breakpoints set; ran to the end."
                else "No breakpoint hit; breakpoints trigger on executed statements."
            (next |> addLog System message), Cmd.none

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | CodeChanged code ->
        let m =
            if model.StepActive then
                endStep model |> addLog System "Step mode ended: the program changed."
            else
                model
        (if m.AutoRun then execute code m else { m with Code = code }), Cmd.none
    | Run ->
        if not model.Breakpoints.IsEmpty then
            continueStep model
        else
            let model = if model.StepActive then endStep model else model
            let m = execute model.Code model
            let m, cmd =
                if m.Animate then beginAnimation m
                else m, Cmd.none
            (m |> addLog System $"Program executed · {statsText m.Drawing}"), cmd
    | Step ->
        if model.StepActive then
            if model.StepIndex + 1 < model.StepEvents.Length then
                { model with StepIndex = model.StepIndex + 1 }, Cmd.none
            else
                (endStep model |> addLog System "Step mode finished."), Cmd.none
        else
            let result, events = Interpreter.runTraced model.Code
            beginStepAt model result events 0 $"Step mode: {events.Length} statements (live run paused)."
    | ContinueStep -> continueStep model
    | ToggleBreakpoint line ->
        match normalizeBreakpointLine model line with
        | None -> model, Cmd.none
        | Some line ->
            let breakpoints =
                if List.contains line model.Breakpoints then
                    model.Breakpoints |> List.filter (fun candidate -> candidate <> line)
                else
                    (line :: model.Breakpoints) |> List.sort
            { model with Breakpoints = breakpoints }, Cmd.none
    | ToggleAutoRun ->
        let wasStep = model.StepActive
        let model = if wasStep then endStep model else model
        let enabling = if wasStep then true else not model.AutoRun
        let next =
            { model with
                AutoRun = enabling
                Animate = if enabling then false else model.Animate }
        let m =
            if enabling then execute next.Code next
            else stopAnimation next
        (m |> addLog System (if enabling then "Live run enabled." else "Live run disabled.")), Cmd.none
    | ToggleAnimate ->
        let wasStep = model.StepActive
        let model = if wasStep then endStep model else model
        let enabling = if wasStep then true else not model.Animate
        let next =
            { model with
                Animate = enabling
                AutoRun = if enabling then false else model.AutoRun }
        if enabling then
            let m, cmd = beginAnimation next
            (m |> addLog System "Animated drawing enabled."), cmd
        else
            (stopAnimation next |> addLog System "Animated drawing disabled."), Cmd.none
    | StartAnimation ->
        let model = if model.StepActive then endStep model else model
        beginAnimation model
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
    | ZoomBy delta -> { model with Zoom = max 50 (min 200 (model.Zoom + delta)) }, Cmd.none
    | ZoomReset -> { model with Zoom = 100 }, Cmd.none
    | Toggle3D -> { model with View3D = not model.View3D }, Cmd.none
    | OrbitCamera(dx, dy) ->
        if not model.View3D then
            model, Cmd.none
        else
            { model with
                CameraYaw = ((model.CameraYaw + dx) % 360 + 360) % 360
                CameraPitch = max -80 (min 80 (model.CameraPitch - dy)) }, Cmd.none
    | ResetCamera -> { model with CameraYaw = 25; CameraPitch = 20 }, Cmd.none
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
        let model = if model.StepActive then endStep model else model
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
        let model = if model.StepActive then endStep model else model
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
    | OpenSettings ->
        { model with SettingsOpen = true; SettingsTab = GeneralTab }, Cmd.none
    | CloseSettings -> { model with SettingsOpen = false }, Cmd.none
    | SetSettingsTab tab -> { model with SettingsTab = tab }, Cmd.none
    | SaveName name -> saveName name model, Cmd.none
    | ImportFile -> model, Cmd.OfFunc.perform importFile () (fun () -> NoOp)
    | ExportProgram ->
        Interop.downloadProgram model.Code $"logo-{model.Room}.logo"
        model |> addLog System "Exported the LOGO program (.logo).", Cmd.none
    | ExportRoom ->
        let notes =
            match Interop.current with
            | Some editor -> editor.getNotes ()
            | None -> ""
        Interop.downloadRoom model.Room model.Code notes $"logocraft-{model.Room}.json"
        model |> addLog System "Exported the room as JSON (program + notes).", Cmd.none
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
