module Logo.Client.App

open System
open Elmish
open Elmish.React
open Fable.Core
open Feliz
open Logo.Core
open Logo.Core.Interpreter

// ---------------------------------------------------------------------------
// Model
// ---------------------------------------------------------------------------

type Status =
    | Connecting
    | Connected
    | Disconnected

type Peer =
    { Name: string
      Color: string
      IsSelf: bool }

type Model =
    { /// Name des gemeinsamen Raums (?room=… in der URL)
      Room: string
      /// Lokal gemerkte Räume, zuletzt geöffnete zuerst
      Rooms: Rooms.Entry list
      /// Einladungslink wurde gerade kopiert (kurze Rückmeldung)
      Copied: bool
      /// Aktueller (gemeinsamer) Programmtext
      Code: string
      Result: RunResult
      /// Zählt die Programmläufe (damit der Editor die Fehlerzeile neu markiert)
      RunId: int
      Status: Status
      Peers: Peer list
      AutoRun: bool
      Fit: bool }

type Msg =
    | CodeChanged of string
    | Run
    | ToggleAutoRun
    | ToggleFit
    | StatusChanged of string
    | PeersChanged of names: string[] * colors: string[] * selfIndex: int
    | LoadExample of string
    | NewRoom
    | OpenRoom of string
    | JoinRoom
    | CopyInvite
    | CopiedReset
    | ForgetRoom
    | NoOp

// ---------------------------------------------------------------------------
// Init / Update
// ---------------------------------------------------------------------------

[<Emit("new URLSearchParams(window.location.search).get('room')")>]
let private roomParam () : string = jsNative

let private roomFromUrl () : string option =
    let raw = roomParam ()
    let cleaned = if isNull raw then "" else raw |> String.filter Rooms.isAllowedChar
    if cleaned = "" then None else Some(cleaned.Substring(0, min 64 cleaned.Length))

/// Schreibt den Raum in die Adresszeile (ohne neu zu laden), damit man den Link teilen kann.
[<Emit("(() => { const u = new URL(location.href); u.searchParams.set('room', $0); history.replaceState(null, '', u) })()")>]
let private showRoomInUrl (room: string) : unit = jsNative

/// Wechselt in einen anderen Raum (lädt die Seite neu, der Editor startet dann frisch).
[<Emit("location.assign(location.pathname + '?room=' + encodeURIComponent($0))")>]
let private navigateToRoom (room: string) : unit = jsNative

[<Emit("navigator.clipboard.writeText(location.origin + location.pathname + '?room=' + encodeURIComponent($0)).catch(() => {})")>]
let private copyInviteLink (room: string) : unit = jsNative

/// window.prompt: liefert null bei „Abbrechen“
[<Emit("window.prompt($0)")>]
let private promptText (message: string) : string = jsNative

[<Emit("window.confirm($0)")>]
let private confirmDialog (message: string) : bool = jsNative

[<Emit("window.alert($0)")>]
let private alertDialog (message: string) : unit = jsNative

let private joinRoomDialog () =
    let input = promptText "Raum-ID oder Einladungslink:"
    if not (isNull input) && input.Trim() <> "" then
        match Rooms.parse input with
        | Some room -> navigateToRoom room
        | None -> alertDialog "Das ist keine gültige Raum-ID (erlaubt: Buchstaben, Ziffern, - und _, max. 64 Zeichen)."

let private forgetRoomDialog (room: string) =
    let message =
        $"Raum „{room}“ aus diesem Browser entfernen?\n\n"
        + "Die lokale Kopie wird gelöscht. Andere Mitwirkende behalten den Raum; "
        + "mit der Raum-ID kommst du wieder hinein."
    if confirmDialog message then
        // Verbindung und lokale Datenbank schließen, damit sie sich löschen lässt
        Interop.current |> Option.iter (fun editor -> editor.destroy ())
        Interop.current <- None
        Interop.deleteRoomData room (Action(fun () ->
            let next =
                match Rooms.forget room with
                | e :: _ -> e.Id
                | [] -> Rooms.newId ()
            navigateToRoom next))

let init () : Model * Cmd<Msg> =
    let fromUrl = roomFromUrl ()
    // Ohne ?room=: zuletzt geöffneten Raum fortsetzen, sonst einen neuen anlegen
    let room =
        match fromUrl with
        | Some room -> room
        | None ->
            match Rooms.load () with
            | e :: _ -> e.Id
            | [] -> Rooms.newId ()
    { Room = room
      Rooms = Rooms.touch room
      Copied = false
      Code = ""
      Result = Interpreter.run ""
      RunId = 0
      Status = Connecting
      Peers = []
      AutoRun = true
      Fit = false },
    (if fromUrl.IsNone then Cmd.OfFunc.perform showRoomInUrl room (fun () -> NoOp) else Cmd.none)

let private execute (code: string) (model: Model) : Model =
    { model with
        Code = code
        Result = Interpreter.run code
        RunId = model.RunId + 1 }

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | CodeChanged code ->
        (if model.AutoRun then execute code model else { model with Code = code }), Cmd.none
    | Run -> execute model.Code model, Cmd.none
    | ToggleAutoRun ->
        let m = { model with AutoRun = not model.AutoRun }
        (if m.AutoRun then execute m.Code m else m), Cmd.none
    | ToggleFit -> { model with Fit = not model.Fit }, Cmd.none
    | StatusChanged s ->
        let status =
            match s with
            | "connected" -> Connected
            | "connecting" -> Connecting
            | _ -> Disconnected
        { model with Status = status }, Cmd.none
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
            let load () =
                match Interop.current with
                | Some editor -> editor.setText code
                | None -> ()
            model, Cmd.OfFunc.perform load () (fun () -> NoOp)
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
    | NoOp -> model, Cmd.none

// ---------------------------------------------------------------------------
// Views
// ---------------------------------------------------------------------------

/// CodeMirror-Editor (mit Yjs) als React-Komponente. Wird nur einmal eingehängt.
[<ReactComponent>]
let EditorHost (room: string, errorLine: int, runId: int, dispatch: Msg -> unit) =
    let host = React.useElementRef ()
    let handle = React.useRef<Interop.EditorHandle option> None

    React.useEffectOnce (fun () ->
        match host.current with
        | Some element ->
            let options: Interop.EditorOptions =
                { room = room
                  primitives = Interpreter.primitiveNames
                  seedText = Examples.flower
                  onChange = Action<string>(fun text -> dispatch (CodeChanged text))
                  onRun = Action(fun () -> dispatch Run)
                  onStatus = Action<string>(fun s -> dispatch (StatusChanged s))
                  onPeers = Action<string[], string[], int>(fun n c i -> dispatch (PeersChanged(n, c, i))) }
            let editor = Interop.createEditor element options
            handle.current <- Some editor
            Interop.current <- Some editor
            { new IDisposable with
                member _.Dispose() =
                    editor.destroy ()
                    Interop.current <- None }
        | None -> { new IDisposable with member _.Dispose() = () })

    React.useEffect (
        (fun () ->
            match handle.current with
            | Some editor -> editor.setErrorLine errorLine
            | None -> ()),
        [| box errorLine; box runId |]
    )

    Html.div [ prop.ref host; prop.className "editor-host" ]

let private num (v: float) = (Math.Round(v, 2)).ToString()

/// Beliebiges SVG-Attribut (Feliz.Svg kennt nicht alle, z. B. float-Koordinaten oder viewBox mit Dezimalzahlen).
let private attr (name: string) (value: obj) : ISvgAttribute = svg.custom (name, value)

// Vorläufig Draufsicht: z wird ignoriert (Kamera/Projektion folgen)
let private pointsAttr (points: (float * float * float)[]) =
    points |> Array.map (fun (x, y, _) -> num x + "," + num (-y)) |> String.concat " "

/// (minX, minY, breite, höhe) in SVG-Koordinaten (y zeigt nach unten)
let private viewBoxOf (model: Model) =
    if not model.Fit then
        (-300.0, -300.0, 600.0, 600.0)
    else
        let mutable minX = 0.0
        let mutable maxX = 0.0
        let mutable minY = 0.0
        let mutable maxY = 0.0
        let extend (x: float) (y: float) =
            minX <- min minX x
            maxX <- max maxX x
            minY <- min minY y
            maxY <- max maxY y
        extend model.Result.Turtle.X model.Result.Turtle.Y
        for p in model.Result.Paths do
            for (x, y, _) in p.Points do
                extend x y
        let size = (max 100.0 (max (maxX - minX) (maxY - minY))) * 1.1
        let cx = (minX + maxX) / 2.0
        let cy = (minY + maxY) / 2.0
        (cx - size / 2.0, -cy - size / 2.0, size, size)

let private canvas (model: Model) =
    let (vx, vy, vw, vh) = viewBoxOf model
    let t = model.Result.Turtle

    let axis (x1, y1, x2, y2) =
        Svg.line [
            attr "x1" (box (x1: float))
            attr "y1" (box (y1: float))
            attr "x2" (box (x2: float))
            attr "y2" (box (y2: float))
            svg.stroke "#e2e8f0"
            svg.strokeWidth 1
        ]

    Svg.svg [
        svg.className "canvas"
        attr "viewBox" (box ($"{num vx} {num vy} {num vw} {num vh}"))
        attr "preserveAspectRatio" (box ("xMidYMid meet"))
        svg.children [
            // Achsen
            axis (vx, 0.0, vx + vw, 0.0)
            axis (0.0, vy, 0.0, vy + vh)
            // Zeichnung
            for p in model.Result.Paths do
                Svg.polyline [
                    svg.points (pointsAttr p.Points)
                    svg.fill "none"
                    svg.stroke p.Color
                    svg.strokeWidth p.Width
                    attr "strokeLinecap" (box ("round"))
                    attr "strokeLinejoin" (box ("round"))
                ]
            // Schildkröte
            if t.Visible then
                Svg.g [
                    svg.transform [ transform.translate (t.X, -t.Y); transform.rotate (t.Heading) ]
                    svg.children [
                        Svg.polygon [
                            svg.points "0,-11 8,8 0,4 -8,8"
                            svg.fill "#16a34a"
                            svg.fillOpacity 0.85
                            svg.stroke "#14532d"
                            svg.strokeWidth 1.5
                            attr "strokeLinejoin" (box ("round"))
                        ]
                    ]
                ]
        ]
    ]

let private consolePanel (result: RunResult) =
    let lines = result.Paths |> Array.sumBy (fun p -> p.Points.Length - 1)
    Html.div [
        prop.className "console"
        prop.children [
            Html.div [
                prop.className "console-stats"
                prop.text $"{result.Paths.Length} Linienzüge · {lines} Segmente · {result.Steps} Schritte"
            ]
            for line in result.Output do
                Html.div [ prop.className "console-out"; prop.text line ]
            match result.Error with
            | Some e ->
                Html.div [
                    prop.className "console-err"
                    prop.text (if e.Line > 0 then $"Zeile {e.Line}: {e.Message}" else e.Message)
                ]
            | None -> ()
        ]
    ]

let private statusView (status: Status) =
    let cls, text =
        match status with
        | Connected -> "ok", "Verbunden"
        | Connecting -> "wait", "Verbinde …"
        | Disconnected -> "bad", "Getrennt"
    Html.span [ prop.className $"status {cls}"; prop.text text ]

let private peersView (peers: Peer list) =
    Html.div [
        prop.className "peers"
        prop.children [
            for p in peers do
                Html.span [
                    prop.className "peer"
                    prop.title p.Name
                    prop.children [
                        Html.span [ prop.className "dot"; prop.style [ style.backgroundColor p.Color ] ]
                        Html.span [ prop.text (if p.IsSelf then p.Name + " (du)" else p.Name) ]
                    ]
                ]
        ]
    ]

let private roomTools (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "room-tools"
        prop.children [
            Html.select [
                prop.className "rooms"
                prop.value ""
                prop.title "Zu einem anderen Raum wechseln"
                prop.onChange (fun (id: string) -> if id <> "" then dispatch (OpenRoom id))
                prop.children [
                    Html.option [ prop.value ""; prop.text $"Meine Räume ({model.Rooms.Length}) …" ]
                    for e in model.Rooms do
                        Html.option [
                            prop.value e.Id
                            prop.text (if e.Id = model.Room then "● " + e.Id else e.Id)
                        ]
                ]
            ]
            Html.button [
                prop.className "tool"
                prop.title "Neuen, leeren Raum mit zufälliger ID anlegen"
                prop.text "＋ Neu"
                prop.onClick (fun _ -> dispatch NewRoom)
            ]
            Html.button [
                prop.className "tool"
                prop.title "Einem Raum über seine ID oder einen Einladungslink beitreten"
                prop.text "Beitreten"
                prop.onClick (fun _ -> dispatch JoinRoom)
            ]
            Html.button [
                prop.className "tool"
                prop.title "Link zu diesem Raum in die Zwischenablage kopieren"
                prop.text (if model.Copied then "✓ Kopiert" else "🔗 Einladen")
                prop.onClick (fun _ -> dispatch CopyInvite)
            ]
            Html.button [
                prop.className "tool"
                prop.title "Diesen Raum aus diesem Browser entfernen"
                prop.text "🗑"
                prop.onClick (fun _ -> dispatch ForgetRoom)
            ]
        ]
    ]

let private header (model: Model) (dispatch: Msg -> unit) =
    Html.header [
        prop.children [
            Html.div [
                prop.className "brand"
                prop.children [
                    Html.span [ prop.className "logo"; prop.text "🐢 LOGO" ]
                    Html.span [ prop.className "room"; prop.title "Raum-ID"; prop.text $"Raum: {model.Room}" ]
                    statusView model.Status
                ]
            ]
            roomTools model dispatch
            peersView model.Peers
            Html.div [
                prop.className "controls"
                prop.children [
                    Html.select [
                        prop.className "examples"
                        prop.value ""
                        prop.title "Ersetzt den gemeinsamen Code für alle"
                        prop.onChange (fun (name: string) -> if name <> "" then dispatch (LoadExample name))
                        prop.children [
                            Html.option [ prop.value ""; prop.text "Beispiel laden …" ]
                            for (name, _) in Examples.all do
                                Html.option [ prop.value name; prop.text name ]
                        ]
                    ]
                    Html.label [
                        prop.children [
                            Html.input [
                                prop.type'.checkbox
                                prop.isChecked model.Fit
                                prop.onChange (fun (_: bool) -> dispatch ToggleFit)
                            ]
                            Html.text " Einpassen"
                        ]
                    ]
                    Html.label [
                        prop.children [
                            Html.input [
                                prop.type'.checkbox
                                prop.isChecked model.AutoRun
                                prop.onChange (fun (_: bool) -> dispatch ToggleAutoRun)
                            ]
                            Html.text " Live"
                        ]
                    ]
                    Html.button [
                        prop.className "run"
                        prop.title "Strg+Enter"
                        prop.text "▶ Ausführen"
                        prop.onClick (fun _ -> dispatch Run)
                    ]
                ]
            ]
        ]
    ]

let view (model: Model) (dispatch: Msg -> unit) =
    let errorLine =
        match model.Result.Error with
        | Some e -> e.Line
        | None -> 0

    Html.div [
        prop.className "app"
        prop.children [
            header model dispatch
            Html.main [
                prop.className "split"
                prop.children [
                    Html.section [
                        prop.className "pane editor-pane"
                        prop.children [ EditorHost(model.Room, errorLine, model.RunId, dispatch) ]
                    ]
                    Html.section [
                        prop.className "pane output-pane"
                        prop.children [ canvas model; consolePanel model.Result ]
                    ]
                ]
            ]
        ]
    ]

// ---------------------------------------------------------------------------
// Start
// ---------------------------------------------------------------------------

Program.mkProgram init update view
|> Program.withReactBatched "root"
|> Program.run
