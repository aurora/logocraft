module Logo.Client.View

open System
open Fable.Core
open Feliz
open Logo.Core
open Logo.Core.Interpreter
open Logo.Client.Model

// ---------------------------------------------------------------------------
// Small helpers
// ---------------------------------------------------------------------------

let private icon (name: string) =
    Html.span [
        prop.className "ms"
        prop.custom ("aria-hidden", "true")
        prop.text name
    ]

/// Arbitrary SVG attribute (Feliz.Svg does not know every attribute, e.g. decimal viewBoxes).
let private attr (name: string) (value: obj) : ISvgAttribute = svg.custom (name, value)

let private num (v: float) = (Math.Round(v, 2)).ToString()

/// Formats the playback speed (stored in percent) as e.g. "0.5×", "1.0×", "2.5×".
let private speedLabel (speed: int) =
    let whole = speed / 100
    let tenths = (speed % 100) / 10
    let hundredths = speed % 10
    string whole + "." + string tenths + (if hundredths = 0 then "" else string hundredths) + "×"

/// The interpreter's default ink is almost black; use the theme foreground on the dark canvas.
let private ink (color: string) =
    if color.ToLowerInvariant() = "#111827" then "#dae2fd" else color

let private pointsAttr (points: (float * float * float)[]) =
    points |> Array.map (fun (x, y, _) -> num x + "," + num (-y)) |> String.concat " "

let private initials (name: string) =
    name.Split(' ')
    |> Array.choose (fun part -> if part.Length > 0 then Some(string part.[0]) else None)
    |> Array.truncate 2
    |> String.concat ""

let private statusText status =
    match status with
    | Connected -> "Connected"
    | Connecting -> "Connecting…"
    | Disconnected -> "Disconnected"

let private statusClass status =
    match status with
    | Connected -> "ok"
    | Connecting -> "wait"
    | Disconnected -> "bad"

[<Emit("location.origin + location.pathname + '?room=' + encodeURIComponent($0)")>]
let private inviteUrl (room: string) : string = jsNative

let private btn (cls: string) (title: string) (testId: string) (msg: Msg) (dispatch: Msg -> unit) (iconName: string) (label: string) =
    Html.button [
        prop.className cls
        prop.type'.button
        prop.title title
        prop.custom ("data-testid", testId)
        prop.onClick (fun _ -> dispatch msg)
        prop.children [
            if iconName <> "" then icon iconName
            if label <> "" then Html.span [ prop.text label ]
        ]
    ]

// ---------------------------------------------------------------------------
// Canvas drawing
// ---------------------------------------------------------------------------

/// (minX, minY, width, height) in SVG coordinates (y points down).
let private viewBoxOf (model: Model) =
    let baseBox =
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
            extend model.Drawing.Turtle.X model.Drawing.Turtle.Y
            for p in model.Drawing.Paths do
                for (x, y, _) in p.Points do
                    extend x y
            let size = (max 100.0 (max (maxX - minX) (maxY - minY))) * 1.1
            let cx = (minX + maxX) / 2.0
            let cy = (minY + maxY) / 2.0
            (cx - size / 2.0, -cy - size / 2.0, size, size)

    let (vx, vy, vw, vh) = baseBox
    let factor = 100.0 / float (max 1 model.Zoom)
    let w = vw * factor
    let h = vh * factor
    let cx = vx + vw / 2.0
    let cy = vy + vh / 2.0
    (cx - w / 2.0, cy - h / 2.0, w, h)

/// Reveals only the first `progress` segments (fractions included) of the drawing.
let private partialPaths (paths: Path[]) (progress: float) =
    if progress <= 0.0 then
        [||]
    else
        let result = ResizeArray<Path>()
        let mutable remaining = progress
        for path in paths do
            if remaining > 0.0 && path.Points.Length > 1 then
                let segments = path.Points.Length - 1
                if remaining >= float segments then
                    result.Add path
                    remaining <- remaining - float segments
                else
                    let whole = int (Math.Floor remaining)
                    let fraction = remaining - float whole
                    let count = whole + 1
                    let points = Array.sub path.Points 0 count
                    let points =
                        if fraction > 0.0 && count < path.Points.Length then
                            let (x0, y0, z0) = path.Points.[whole]
                            let (x1, y1, z1) = path.Points.[whole + 1]
                            Array.append
                                points
                                [| (x0 + (x1 - x0) * fraction,
                                    y0 + (y1 - y0) * fraction,
                                    z0 + (z1 - z0) * fraction) |]
                        else
                            points
                    result.Add { path with Points = points }
                    remaining <- 0.0
        result.ToArray()

/// Position and heading of the turtle at `progress` segments into the drawing.
let private animatedTurtle (drawing: Drawing) (progress: float) =
    let total = float (segmentsOf drawing)
    if progress >= total then
        drawing.Turtle
    else
        let mutable remaining = max 0.0 progress
        let mutable found: TurtleState option = None
        for path in drawing.Paths do
            if found.IsNone && path.Points.Length > 1 then
                let segments = path.Points.Length - 1
                if remaining >= float segments then
                    remaining <- remaining - float segments
                else
                    let whole = int (Math.Floor remaining)
                    let fraction = remaining - float whole
                    let (x0, y0, z0) = path.Points.[whole]
                    let (x1, y1, z1) = path.Points.[whole + 1]
                    let px = x0 + (x1 - x0) * fraction
                    let py = y0 + (y1 - y0) * fraction
                    let pz = z0 + (z1 - z0) * fraction
                    let heading = Math.Atan2(x1 - x0, -(y1 - y0)) * 180.0 / Math.PI
                    let heading = (heading + 360.0) % 360.0
                    found <-
                        Some
                            { drawing.Turtle with
                                X = px
                                Y = py
                                Z = pz
                                Heading = heading
                                Pitch = 0.0
                                PenDown = true }
        match found with
        | Some turtle -> turtle
        | None -> drawing.Turtle

let private turtleNow (model: Model) =
    if model.Animation.Playing then animatedTurtle model.Drawing model.Animation.Progress
    else model.Drawing.Turtle

let private pathsNow (model: Model) =
    if model.Animation.Playing then partialPaths model.Drawing.Paths model.Animation.Progress
    else model.Drawing.Paths

let private canvas (model: Model) =
    let (vx, vy, vw, vh) = viewBoxOf model
    let t = turtleNow model

    let axis (x1, y1, x2, y2) =
        Svg.line [
            attr "x1" (box x1)
            attr "y1" (box y1)
            attr "x2" (box x2)
            attr "y2" (box y2)
            attr "stroke" (box "rgba(255,255,255,0.08)")
            attr "stroke-width" (box 1.5)
            attr "stroke-dasharray" (box "3 3")
        ]

    Svg.svg [
        svg.className "canvas"
        attr "viewBox" (box $"{num vx} {num vy} {num vw} {num vh}")
        attr "preserveAspectRatio" (box "xMidYMid meet")
        svg.children [
            if model.ShowGrid then
                Svg.defs [
                    svg.children [
                        Svg.pattern [
                            attr "id" (box "grid")
                            attr "width" (box 40.0)
                            attr "height" (box 40.0)
                            attr "patternUnits" (box "userSpaceOnUse")
                            svg.children [
                                Svg.path [
                                    attr "d" (box "M 40 0 L 0 0 0 40")
                                    attr "fill" (box "none")
                                    attr "stroke" (box "rgba(255,255,255,0.045)")
                                    attr "stroke-width" (box 1.0)
                                ]
                                Svg.path [
                                    attr "d" (box "M 20 0 L 20 40 M 0 20 L 40 20")
                                    attr "fill" (box "none")
                                    attr "stroke" (box "rgba(255,255,255,0.018)")
                                    attr "stroke-width" (box 0.5)
                                ]
                            ]
                        ]
                    ]
                ]
                Svg.rect [
                    attr "x" (box (vx - 2000.0))
                    attr "y" (box (vy - 2000.0))
                    attr "width" (box (vw + 4000.0))
                    attr "height" (box (vh + 4000.0))
                    attr "fill" (box "url(#grid)")
                ]
            axis (vx, 0.0, vx + vw, 0.0)
            axis (0.0, vy, 0.0, vy + vh)
            Svg.circle [
                attr "cx" (box 0.0)
                attr "cy" (box 0.0)
                attr "r" (box 3.0)
                attr "fill" (box "#4edea3")
                attr "fill-opacity" (box 0.4)
            ]
            Svg.g [
                svg.className "turtle-paths"
                svg.children [
                    for p in pathsNow model do
                        Svg.polyline [
                            svg.points (pointsAttr p.Points)
                            svg.fill "none"
                            svg.stroke (ink p.Color)
                            svg.strokeWidth p.Width
                            attr "stroke-linecap" (box "round")
                            attr "stroke-linejoin" (box "round")
                        ]
                ]
            ]
            if model.ShowTurtle && t.Visible then
                Svg.g [
                    svg.transform [ transform.translate (t.X, -t.Y); transform.rotate (t.Heading) ]
                    svg.children [
                        Svg.circle [
                            attr "cx" (box 0.0)
                            attr "cy" (box 0.0)
                            attr "r" (box 14.0)
                            attr "fill" (box "#4edea3")
                            attr "fill-opacity" (box 0.12)
                            attr "stroke" (box "#4edea3")
                            attr "stroke-opacity" (box 0.3)
                            attr "stroke-width" (box 1.0)
                        ]
                        Svg.polygon [
                            svg.points "0,-12 10,8 0,3 -10,8"
                            svg.fill "#10b981"
                            svg.stroke "#064e3b"
                            svg.strokeWidth 1.5
                            attr "stroke-linejoin" (box "round")
                        ]
                        Svg.line [
                            attr "x1" (box 0.0)
                            attr "y1" (box 3.0)
                            attr "x2" (box 0.0)
                            attr "y2" (box -18.0)
                            attr "stroke" (box "#4cd7f6")
                            attr "stroke-linecap" (box "round")
                            attr "stroke-width" (box 2.0)
                        ]
                        Svg.polygon [
                            svg.points "0,-22 -3,-17 3,-17"
                            svg.fill "#4cd7f6"
                        ]
                        Svg.circle [
                            attr "cx" (box 0.0)
                            attr "cy" (box 0.0)
                            attr "r" (box 2.0)
                            attr "fill" (box "#dae2fd")
                        ]
                    ]
                ]
        ]
    ]

// ---------------------------------------------------------------------------
// Top bar
// ---------------------------------------------------------------------------

let private roomChip (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "room-chip"
        prop.children [
            icon "tag"
            Html.span [ prop.className "room-id"; prop.text model.Room ]
            Html.button [
                prop.className "icon-btn tiny"
                prop.type'.button
                prop.title "Copy room link"
                prop.custom ("data-testid", "copy-link")
                prop.onClick (fun _ -> dispatch CopyInvite)
                prop.children [ icon (if model.Copied then "check" else "content_copy") ]
            ]
        ]
    ]

let private presencePills (model: Model) =
    let shown = model.Peers |> List.truncate 3
    Html.div [
        prop.className "presence-pills"
        prop.children [
            for p in shown do
                Html.div [
                    prop.className "presence-pill"
                    prop.style [ style.borderColor (p.Color + "66") ]
                    prop.children [
                        Html.span [ prop.className "status-dot"; prop.style [ style.backgroundColor p.Color ] ]
                        Html.span [ prop.text (if p.IsSelf then p.Name + " (you)" else p.Name) ]
                    ]
                ]
            if model.Peers.Length > 3 then
                Html.div [ prop.className "presence-more"; prop.text $"+{model.Peers.Length - 3} others" ]
        ]
    ]

let private menuItem (dispatch: Msg -> unit) (closeMsg: Msg) (label: string) (iconName: string) (msg: Msg) (danger: bool) =
    Html.button [
        prop.className ("dropdown-item" + (if danger then " danger" else ""))
        prop.type'.button
        prop.onClick (fun _ ->
            dispatch msg
            dispatch closeMsg)
        prop.children [
            icon iconName
            Html.span [ prop.text label ]
        ]
    ]

let private fileMenu (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "menu-wrap"
        prop.children [
            Html.button [
                prop.className "ghost-btn"
                prop.type'.button
                prop.title "File menu"
                prop.custom ("data-testid", "file-menu")
                prop.onClick (fun _ -> dispatch ToggleFileMenu)
                prop.children [ Html.span [ prop.text "File" ]; icon "expand_more" ]
            ]
            if model.FileMenuOpen then
                Html.div [ prop.className "menu-backdrop"; prop.onClick (fun _ -> dispatch ToggleFileMenu) ]
                Html.div [
                    prop.className "dropdown"
                    prop.children [
                        menuItem dispatch ToggleFileMenu "New room" "add" NewRoom false
                        menuItem dispatch ToggleFileMenu "Join room" "login" JoinRoom false
                        menuItem dispatch ToggleFileMenu "Import .logo file" "file_open" ImportFile false
                        Html.div [ prop.className "dropdown-sep" ]
                        Html.div [ prop.className "dropdown-label"; prop.text "Examples" ]
                        for (name, _) in Examples.all do
                            menuItem dispatch ToggleFileMenu name "auto_awesome" (LoadExample name) false
                        Html.div [ prop.className "dropdown-sep" ]
                        menuItem dispatch ToggleFileMenu "Forget this room" "delete" ForgetRoom true
                    ]
                ]
        ]
    ]

let private exportMenu (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "menu-wrap"
        prop.children [
            Html.button [
                prop.className "ghost-btn"
                prop.type'.button
                prop.title "Export menu"
                prop.custom ("data-testid", "export-menu")
                prop.onClick (fun _ -> dispatch ToggleExportMenu)
                prop.children [ Html.span [ prop.text "Export" ]; icon "expand_more" ]
            ]
            if model.ExportMenuOpen then
                Html.div [ prop.className "menu-backdrop"; prop.onClick (fun _ -> dispatch ToggleExportMenu) ]
                Html.div [
                    prop.className "dropdown"
                    prop.children [
                        menuItem dispatch ToggleExportMenu "LOGO program (.logo)" "code_blocks" ExportProgram false
                        menuItem dispatch ToggleExportMenu "Room bundle (.json)" "data_object" ExportRoom false
                        Html.div [ prop.className "dropdown-sep" ]
                        menuItem dispatch ToggleExportMenu "Snapshot as SVG" "shape_line" ExportSvg false
                        menuItem dispatch ToggleExportMenu "Snapshot as PNG" "photo_camera" ExportPng false
                    ]
                ]
        ]
    ]

let private topbar (model: Model) (dispatch: Msg -> unit) =
    Html.header [
        prop.className "topbar"
        prop.children [
            Html.div [
                prop.className "topbar-left"
                prop.children [
                    Html.button [
                        prop.className "icon-btn mobile-menu"
                        prop.type'.button
                        prop.title "Toggle sidebar"
                        prop.onClick (fun _ -> dispatch ToggleSidebar)
                        prop.children [ icon "menu" ]
                    ]
                    Html.div [
                        prop.className "brand"
                        prop.children [
                            Html.span [ prop.className "brand-mark"; prop.text "L" ]
                            Html.span [ prop.className "brand-name"; prop.text "LogoCraft Studio" ]
                        ]
                    ]
                    Html.div [ prop.className "topbar-divider" ]
                    roomChip model dispatch
                ]
            ]
            Html.div [
                prop.className "topbar-right"
                prop.children [
                    presencePills model
                    fileMenu model dispatch
                    exportMenu model dispatch
                    btn "btn btn-primary btn-sm" "Run program (Ctrl+Enter)" "run" Run dispatch "play_arrow" "Run"
                    btn "btn btn-ghost btn-sm" "Clear the canvas" "clear" ClearCanvas dispatch "restart_alt" "Clear"
                    Html.div [
                        prop.className "avatar"
                        prop.title (if model.MyName = "" then "You" else model.MyName)
                        prop.children [
                            match model.Peers |> List.tryFind (fun p -> p.IsSelf) with
                            | Some p -> Html.span [ prop.text (initials p.Name) ]
                            | None -> icon "person"
                        ]
                    ]
                ]
            ]
        ]
    ]

// ---------------------------------------------------------------------------
// Sidebar
// ---------------------------------------------------------------------------

let private navItem (model: Model) (dispatch: Msg -> unit) (label: string) (iconName: string) (panel: Panel) (testId: string) =
    Html.button [
        prop.className ("nav-item" + (if model.Panel = panel then " active" else ""))
        prop.type'.button
        prop.custom ("data-testid", testId)
        prop.onClick (fun _ -> dispatch (OpenPanel panel))
        prop.children [
            icon iconName
            Html.span [ prop.text label ]
        ]
    ]

let private sidebar (model: Model) (dispatch: Msg -> unit) =
    let turtleStatus =
        match model.Result.Error with
        | Some _ -> "Turtle: Error"
        | None ->
            match model.Status with
            | Connected -> "Turtle: Ready"
            | Connecting -> "Turtle: Waiting"
            | Disconnected -> "Turtle: Offline"

    Html.aside [
        prop.className ("sidebar" + (if model.SidebarOpen then " open" else ""))
        prop.children [
            Html.div [
                prop.className "sidebar-scroll"
                prop.children [
                    Html.div [ prop.className "sidebar-label"; prop.text "Workspace & Tools" ]
                    Html.nav [
                        prop.className "nav"
                        prop.children [
                            navItem model dispatch "Code & Canvas" "terminal" CodeCanvas "nav-code"
                            navItem model dispatch "Project Files" "folder_special" ProjectFiles "nav-files"
                            navItem model dispatch "Multiplayer Room" "groups" Multiplayer "nav-multiplayer"
                            navItem model dispatch "Logo Reference" "menu_book" Reference "nav-reference"
                            navItem model dispatch "Vector Exporter" "shape_line" Exporter "nav-exporter"
                        ]
                    ]
                ]
            ]
            Html.div [
                prop.className "sidebar-footer"
                prop.children [
                    Html.div [
                        prop.className "status-card"
                        prop.children [
                            Html.div [
                                prop.className "status-card-head"
                                prop.children [
                                    Html.span [ prop.text "Interpreter status" ]
                                    Html.span [ prop.className ("status-dot " + statusClass model.Status) ]
                                ]
                            ]
                            Html.div [ prop.className "status-card-value"; prop.text turtleStatus ]
                            Html.div [ prop.className "status-card-meta"; prop.text (statsText model.Drawing) ]
                        ]
                    ]
                ]
            ]
        ]
    ]

// ---------------------------------------------------------------------------
// Subheader
// ---------------------------------------------------------------------------

let private peerAvatar (p: Peer) =
    Html.div [
        prop.className "peer"
        prop.title (if p.IsSelf then $"{p.Name} (you)" else p.Name)
        prop.style [ style.borderColor (p.Color + "66") ]
        prop.children [
            Html.span [
                prop.className "peer-initial"
                prop.style [ style.backgroundColor (p.Color + "22"); style.color p.Color ]
                prop.text (initials p.Name)
            ]
            if p.IsSelf then Html.span [ prop.className "peer-you"; prop.text "You" ]
        ]
    ]

let private avatarStack (model: Model) =
    Html.div [
        prop.className "avatar-stack"
        prop.children [
            if model.Peers.IsEmpty then
                Html.span [ prop.className "peer-empty"; prop.text "Just you" ]
            else
                for p in model.Peers |> List.truncate 5 do
                    peerAvatar p
                if model.Peers.Length > 5 then
                    Html.div [ prop.className "avatar-more"; prop.text $"+{model.Peers.Length - 5}" ]
        ]
    ]

let private subheader (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "subheader"
        prop.children [
            Html.div [
                prop.className "subheader-left"
                prop.children [
                    Html.div [
                        prop.className "room-block"
                        prop.children [
                            Html.span [ prop.className ("status-dot " + statusClass model.Status) ]
                            Html.div [
                                prop.children [
                                    Html.div [
                                        prop.className "room-line"
                                        prop.children [
                                            Html.span [ prop.className "eyebrow"; prop.text "Room" ]
                                            Html.span [ prop.className "room-name"; prop.text ("#" + model.Room) ]
                                        ]
                                    ]
                                    Html.div [
                                        prop.className "room-meta"
                                        prop.children [
                                            Html.span [ prop.className ("status " + statusClass model.Status); prop.text (statusText model.Status) ]
                                            Html.span [ prop.text " • " ]
                                            Html.span [ prop.text $"{model.Peers.Length} connected" ]
                                        ]
                                    ]
                                ]
                            ]
                        ]
                    ]
                    btn "btn btn-ghost btn-sm" "Copy the room link" "copy-link-2" CopyInvite dispatch (if model.Copied then "check" else "share") (if model.Copied then "Copied" else "Copy link")
                    Html.div [ prop.className "subheader-divider" ]
                    Html.div [
                        prop.className "quick-actions"
                        prop.children [
                            btn "ghost-btn" "Import a .logo file" "import-file" ImportFile dispatch "file_open" "Import"
                            btn "ghost-btn" "Export an SVG snapshot" "export-svg" ExportSvg dispatch "download" "Export"
                            btn "ghost-btn" "Clear the canvas" "reset-session" ClearCanvas dispatch "restart_alt" "Reset"
                        ]
                    ]
                ]
            ]
            Html.div [
                prop.className "subheader-right"
                prop.children [
                    avatarStack model
                    Html.button [
                        prop.className "icon-btn"
                        prop.type'.button
                        prop.title "Room settings"
                        prop.custom ("data-testid", "settings-open")
                        prop.onClick (fun _ -> dispatch OpenSettings)
                        prop.children [ icon "tune" ]
                    ]
                    Html.div [
                        prop.className "run-cluster"
                        prop.children [
                            btn "btn btn-primary btn-sm" "Run program (Ctrl+Enter)" "run-sub" Run dispatch "play_arrow" "Run"
                            Html.button [
                                prop.className "btn btn-ghost btn-sm"
                                prop.type'.button
                                prop.disabled true
                                prop.title "Single-step execution is not implemented yet"
                                prop.children [ icon "step_over"; Html.span [ prop.text "Step" ] ]
                            ]
                            btn "btn btn-ghost btn-sm danger" "Clear the canvas" "stop" ClearCanvas dispatch "stop" ""
                        ]
                    ]
                ]
            ]
        ]
    ]

// ---------------------------------------------------------------------------
// Editor and REPL
// ---------------------------------------------------------------------------

let private editorTabs (model: Model) (dispatch: Msg -> unit) =
    let tabButton (label: string) (iconName: string) (testId: string) (tab: EditorTab) =
        Html.button [
            prop.className ("tab" + (if model.EditorTab = tab then " active" else ""))
            prop.type'.button
            prop.custom ("data-testid", testId)
            prop.onClick (fun _ -> dispatch (SetEditorTab tab))
            prop.children [
                icon iconName
                Html.span [ prop.text label ]
                if model.EditorTab = tab then Html.span [ prop.className "tab-dot" ]
            ]
        ]

    Html.div [
        prop.className "editor-tabs"
        prop.children [
            Html.div [
                prop.className "tab-list"
                prop.children [
                    tabButton "Logo Script (main.logo)" "code_blocks" "tab-code" CodeTab
                    tabButton "Notes & Docs (README.md)" "article" "tab-notes" NotesTab
                ]
            ]
            Html.div [
                prop.className "tab-meta"
                prop.children [
                    Html.span [ prop.className "badge"; prop.text "UTF-8" ]
                ]
            ]
        ]
    ]

[<ReactComponent>]
let private EditorHost (model: Model) (dispatch: Msg -> unit) =
    let codeHost = React.useElementRef ()
    let notesHost = React.useElementRef ()
    let handle = React.useRef<Interop.EditorHandle option> None

    React.useEffectOnce (fun () ->
        match codeHost.current, notesHost.current with
        | Some codeElement, Some notesElement ->
            let options: Interop.EditorOptions =
                { room = model.Room
                  userName = model.MyName
                  primitives = Interpreter.primitiveNames
                  seedText = Examples.flower
                  notesSeed = Examples.defaultNotes
                  onChange = Action<string>(fun text -> dispatch (CodeChanged text))
                  onRun = Action(fun () -> dispatch Run)
                  onStatus = Action<string>(fun s -> dispatch (StatusChanged s))
                  onPeers = Action<string[], string[], int>(fun names colors selfIndex -> dispatch (PeersChanged(names, colors, selfIndex))) }
            let editor = Interop.createEditor codeElement notesElement options
            handle.current <- Some editor
            Interop.current <- Some editor
            { new IDisposable with
                member _.Dispose() =
                    editor.destroy ()
                    Interop.current <- None }
        | _ -> { new IDisposable with member _.Dispose() = () })

    let errorLine =
        match model.Result.Error with
        | Some e -> e.Line
        | None -> 0

    React.useEffect (
        (fun () ->
            handle.current |> Option.iter (fun editor -> editor.setErrorLine errorLine)),
        [| box errorLine; box model.RunId |]
    )

    React.useEffect (
        (fun () ->
            handle.current
            |> Option.iter (fun editor ->
                editor.setEditorTab (if model.EditorTab = NotesTab then "notes" else "code"))),
        [| box model.EditorTab; box model.Panel |]
    )

    React.useEffect (
        (fun () ->
            handle.current |> Option.iter (fun editor -> editor.setChangeDelay model.LiveDelayMs)),
        [| box model.LiveDelayMs |]
    )

    Html.div [
        prop.className "editors"
        prop.children [
            Html.div [
                prop.ref codeHost
                prop.className ("editor-host" + (if model.EditorTab = CodeTab then "" else " is-hidden"))
            ]
            Html.div [
                prop.ref notesHost
                prop.className ("notes-host" + (if model.EditorTab = NotesTab then "" else " is-hidden"))
            ]
        ]
    ]

let private replKindLabel kind =
    match kind with
    | Command -> "You"
    | Output -> "Out"
    | Error -> "Error"
    | System -> "System"

let private replKindClass kind =
    match kind with
    | Command -> "command"
    | Output -> "output"
    | Error -> "error"
    | System -> "system"

let private replLine (entry: ReplEntry) =
    Html.div [
        prop.className ("repl-line " + replKindClass entry.Kind)
        prop.children [
            Html.span [ prop.className "repl-time"; prop.text $"[{entry.Time}]" ]
            Html.span [ prop.className "repl-sender"; prop.text (replKindLabel entry.Kind + ":") ]
            Html.span [ prop.className "repl-text"; prop.text entry.Text ]
        ]
    ]

[<ReactComponent>]
let private ReplPanel (model: Model) (dispatch: Msg -> unit) =
    let logRef = React.useElementRef ()
    let command, setCommand = React.useState ("")

    let submit () =
        let text = command.Trim()
        if text <> "" then
            dispatch (ReplSubmit text)
            setCommand ""

    React.useEffect (
        (fun () ->
            logRef.current
            |> Option.iter (fun element -> element.scrollTop <- element.scrollHeight)),
        [| box model.ReplLog.Length |]
    )

    Html.div [
        prop.className "repl"
        prop.children [
            Html.div [
                prop.className "repl-header"
                prop.children [
                    icon "terminal"
                    Html.span [ prop.className "repl-title"; prop.text "Logo REPL & Protocol" ]
                    Html.span [ prop.className "badge primary"; prop.text "local" ]
                    Html.div [ prop.className "spacer" ]
                    Html.button [
                        prop.className "icon-btn tiny"
                        prop.type'.button
                        prop.title "Clear console"
                        prop.custom ("data-testid", "clear-console")
                        prop.onClick (fun _ -> dispatch ClearConsole)
                        prop.children [ icon "backspace" ]
                    ]
                ]
            ]
            Html.div [
                prop.className "program-status"
                prop.children [
                    Html.span [ prop.className "console-stats"; prop.text (statsText model.Drawing) ]
                    for line in model.Result.Output do
                        Html.span [ prop.className "console-out"; prop.text line ]
                    match model.Result.Error with
                    | Some e ->
                        Html.div [
                            prop.className "console-err"
                            prop.text (if e.Line > 0 then $"Line {e.Line}: {e.Message}" else e.Message)
                        ]
                    | None -> ()
                ]
            ]
            Html.div [
                prop.ref logRef
                prop.className "repl-log"
                prop.children [ for entry in model.ReplLog -> replLine entry ]
            ]
            Html.div [
                prop.className "repl-input-row"
                prop.children [
                    Html.span [ prop.className "repl-prompt"; prop.text "?" ]
                    Html.input [
                        prop.className "repl-input"
                        prop.type'.text
                        prop.placeholder "Logo command (e.g. fd 50 rt 90)"
                        prop.value command
                        prop.custom ("data-testid", "repl-input")
                        prop.onChange (fun (text: string) -> setCommand text)
                        prop.onKeyDown (fun (e: Browser.Types.KeyboardEvent) ->
                            if e.key = "Enter" then
                                e.preventDefault ()
                                submit ())
                    ]
                    Html.button [
                        prop.className "btn btn-primary btn-sm"
                        prop.type'.button
                        prop.custom ("data-testid", "repl-send")
                        prop.onClick (fun _ -> submit ())
                        prop.children [ Html.span [ prop.text "Send" ]; icon "keyboard_return" ]
                    ]
                ]
            ]
        ]
    ]

// ---------------------------------------------------------------------------
// Canvas pane
// ---------------------------------------------------------------------------

let private telemetryItem (label: string) (value: string) (cls: string) =
    Html.div [
        prop.className "telemetry-item"
        prop.children [
            Html.span [ prop.className "telemetry-label"; prop.text label ]
            Html.span [ prop.className ("telemetry-value " + cls); prop.text value ]
        ]
    ]

let private telemetry (model: Model) =
    let t = turtleNow model
    Html.div [
        prop.className "hud telemetry"
        prop.children [
            telemetryItem "X:" (num t.X) "primary"
            Html.span [ prop.className "telemetry-sep"; prop.text "•" ]
            telemetryItem "Y:" (num t.Y) "secondary"
            Html.span [ prop.className "telemetry-sep"; prop.text "•" ]
            telemetryItem "Heading:" (num t.Heading + "°") "tertiary"
            Html.span [ prop.className "telemetry-sep"; prop.text "•" ]
            telemetryItem "Pitch:" (num t.Pitch + "°") "muted"
            Html.span [ prop.className "telemetry-sep"; prop.text "•" ]
            Html.div [
                prop.className "telemetry-item"
                prop.children [
                    Html.span [ prop.className ("status-dot " + (if t.PenDown then "ok" else "bad")) ]
                    Html.span [ prop.className "telemetry-value"; prop.text (if t.PenDown then "Pen down" else "Pen up") ]
                ]
            ]
        ]
    ]

let private canvasTools (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "hud canvas-tools"
        prop.children [
            Html.button [
                prop.className "icon-btn tiny"
                prop.type'.button
                prop.title "Zoom out"
                prop.custom ("data-testid", "zoom-out")
                prop.onClick (fun _ -> dispatch ZoomOut)
                prop.children [ icon "remove" ]
            ]
            Html.span [ prop.className "zoom-value"; prop.text (string model.Zoom + "%") ]
            Html.button [
                prop.className "icon-btn tiny"
                prop.type'.button
                prop.title "Zoom in"
                prop.custom ("data-testid", "zoom-in")
                prop.onClick (fun _ -> dispatch ZoomIn)
                prop.children [ icon "add" ]
            ]
            Html.button [
                prop.className "ghost-btn tiny"
                prop.type'.button
                prop.title "Reset zoom"
                prop.custom ("data-testid", "zoom-reset")
                prop.onClick (fun _ -> dispatch ZoomReset)
                prop.children [ Html.span [ prop.text "Reset" ] ]
            ]
            Html.div [ prop.className "tool-sep" ]
            Html.button [
                prop.className ("ghost-btn tiny" + (if model.ShowTurtle then " active" else ""))
                prop.type'.button
                prop.title "Show/hide turtle"
                prop.custom ("data-testid", "turtle-toggle")
                prop.onClick (fun _ -> dispatch ToggleTurtle)
                prop.children [ icon "pest_control"; Html.span [ prop.text "Turtle" ] ]
            ]
            Html.button [
                prop.className ("ghost-btn tiny" + (if model.ShowGrid then " active" else ""))
                prop.type'.button
                prop.title "Show/hide grid"
                prop.custom ("data-testid", "grid-toggle")
                prop.onClick (fun _ -> dispatch ToggleGrid)
                prop.children [ icon "grid_on"; Html.span [ prop.text "Grid" ] ]
            ]
            Html.div [ prop.className "tool-sep" ]
            Html.button [
                prop.className ("ghost-btn tiny" + (if model.Animate then " active" else ""))
                prop.type'.button
                prop.title "Animate drawing (turns off live run)"
                prop.custom ("data-testid", "animate-toggle")
                prop.onClick (fun _ -> dispatch ToggleAnimate)
                prop.children [ icon "animation"; Html.span [ prop.text "Animate" ] ]
            ]
            Html.button [
                prop.className "icon-btn tiny"
                prop.type'.button
                prop.title "Replay drawing"
                prop.custom ("data-testid", "animate-replay")
                prop.onClick (fun _ -> dispatch StartAnimation)
                prop.children [ icon "play_circle" ]
            ]
            Html.div [ prop.className "tool-sep" ]
            btn "icon-btn tiny" "Export SVG" "export-svg-2" ExportSvg dispatch "shape_line" ""
            btn "icon-btn tiny" "Export PNG" "export-png" ExportPng dispatch "photo_camera" ""
        ]
    ]

let private liveDelay (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "hud hud-bottom"
        prop.children [
            icon "speed"
            Html.span [ prop.className "hud-label"; prop.text "Live delay" ]
            Html.input [
                prop.className "slider"
                prop.type'.range
                prop.min 20
                prop.max 500
                prop.step 10
                prop.value (string model.LiveDelayMs)
                prop.onChange (fun (value: string) ->
                    match Int32.TryParse value with
                    | true, ms -> dispatch (SetLiveDelay ms)
                    | _ -> ())
            ]
            Html.span [ prop.className "hud-value"; prop.text $"{model.LiveDelayMs} ms" ]
            Html.div [ prop.className "hud-sep" ]
            icon "animation"
            Html.span [ prop.className "hud-label"; prop.text "Anim speed" ]
            Html.input [
                prop.className "slider anim-speed-slider"
                prop.type'.range
                prop.min 25
                prop.max 400
                prop.step 25
                prop.title "Animation speed"
                prop.value (string model.AnimSpeed)
                prop.custom ("data-testid", "anim-speed")
                prop.onChange (fun (value: string) ->
                    match Int32.TryParse value with
                    | true, speed -> dispatch (SetAnimSpeed speed)
                    | _ -> ())
            ]
            Html.span [
                prop.className "hud-value"
                prop.custom ("data-testid", "anim-speed-value")
                prop.text (speedLabel model.AnimSpeed)
            ]
        ]
    ]

let private canvasWatermark (model: Model) =
    if model.Animation.Playing then
        let pct =
            if model.Animation.Total > 0.0 then
                int (Math.Round(100.0 * model.Animation.Progress / model.Animation.Total))
            else
                100
        Html.div [
            prop.className "canvas-watermark playing"
            prop.custom ("data-testid", "anim-progress")
            prop.text ("drawing… " + string pct + "%")
        ]
    else
        Html.div [
            prop.className "canvas-watermark"
            prop.text $"{statsText model.Drawing} · local REPL · scroll to zoom"
        ]

[<ReactComponent>]
let private CanvasSurface (model: Model) (dispatch: Msg -> unit) =
    let surface = React.useElementRef ()

    React.useEffectOnce (fun () ->
        match surface.current with
        | Some element ->
            let dispose =
                Interop.attachWheelZoom
                    element
                    (Action<int>(fun direction -> dispatch (ZoomBy (direction * 10))))
            { new IDisposable with
                member _.Dispose() = dispose () }
        | None -> { new IDisposable with member _.Dispose() = () })

    Html.div [
        prop.ref surface
        prop.className "canvas-surface"
        prop.title "Scroll to zoom"
        prop.children [
            canvas model
            Html.div [ prop.className "hud hud-top"; prop.children [ telemetry model; canvasTools model dispatch ] ]
            liveDelay model dispatch
            canvasWatermark model
        ]
    ]

let private canvasPane (model: Model) (dispatch: Msg -> unit) =
    Html.section [
        prop.className "canvas-pane"
        prop.children [ CanvasSurface model dispatch ]
    ]

// ---------------------------------------------------------------------------
// Panels
// ---------------------------------------------------------------------------

let private panelHeader (title: string) (subtitle: string) =
    Html.div [
        prop.className "panel-header"
        prop.children [
            Html.h1 [ prop.className "panel-title"; prop.text title ]
            Html.p [ prop.className "panel-subtitle"; prop.text subtitle ]
        ]
    ]

let private projectFilesPanel (model: Model) (dispatch: Msg -> unit) =
    Html.section [
        prop.className "panel-view"
        prop.children [
            panelHeader "Project Files" "Rooms stored in this browser. The room ID is the only key."
            Html.div [
                prop.className "panel-card"
                prop.children [
                    Html.div [
                        prop.className "panel-actions"
                        prop.children [
                            btn "btn btn-primary btn-sm" "Create a new room" "new-room" NewRoom dispatch "add" "New room"
                            btn "btn btn-ghost btn-sm" "Join by ID or link" "join-room" JoinRoom dispatch "login" "Join room"
                            btn "btn btn-ghost btn-sm" "Copy the room link" "copy-link-3" CopyInvite dispatch "share" "Copy link"
                            btn "btn btn-ghost btn-sm danger" "Forget this room locally" "forget-room" ForgetRoom dispatch "delete" "Forget"
                        ]
                    ]
                    Html.div [
                        prop.className "room-list"
                        prop.children [
                            for entry in model.Rooms do
                                Html.div [
                                    prop.className ("room-row" + (if entry.Id = model.Room then " active" else ""))
                                    prop.children [
                                        Html.div [
                                            prop.className "room-row-main"
                                            prop.children [
                                                icon "tag"
                                                Html.span [ prop.className "room-row-id"; prop.text entry.Id ]
                                                if entry.Id = model.Room then
                                                    Html.span [ prop.className "badge primary"; prop.text "current" ]
                                            ]
                                        ]
                                        Html.div [
                                            prop.className "room-row-actions"
                                            prop.children [
                                                Html.button [
                                                    prop.className "btn btn-ghost btn-xs"
                                                    prop.type'.button
                                                    prop.disabled (entry.Id = model.Room)
                                                    prop.onClick (fun _ -> dispatch (OpenRoom entry.Id))
                                                    prop.children [ Html.span [ prop.text "Open" ] ]
                                                ]
                                                btn "btn btn-ghost btn-xs danger" "Remove from this browser" ("remove-" + entry.Id) (ForgetRoomById entry.Id) dispatch "delete" ""
                                            ]
                                        ]
                                    ]
                                ]
                        ]
                    ]
                ]
            ]
        ]
    ]

let private multiplayerPanel (model: Model) (dispatch: Msg -> unit) =
    Html.section [
        prop.className "panel-view"
        prop.children [
            panelHeader "Multiplayer Room" "Everyone in this room shares the code and notes live."
            Html.div [
                prop.className "panel-card"
                prop.children [
                    Html.div [
                        prop.className "info-block"
                        prop.children [
                            Html.div [ prop.className "info-label"; prop.text "Status" ]
                            Html.div [ prop.className "info-value"; prop.text (statusText model.Status) ]
                        ]
                    ]
                    Html.div [
                        prop.className "info-block"
                        prop.children [
                            Html.div [ prop.className "info-label"; prop.text "Invite link" ]
                            Html.div [ prop.className "invite-row"; prop.children [
                                Html.code [ prop.className "invite-link"; prop.text (inviteUrl model.Room) ]
                                btn "btn btn-primary btn-sm" "Copy the invite link" "copy-invite" CopyInvite dispatch "content_copy" (if model.Copied then "Copied" else "Copy")
                            ] ]
                        ]
                    ]
                    Html.div [ prop.className "info-label"; prop.text $"Participants ({model.Peers.Length})" ]
                    Html.div [
                        prop.className "participant-list"
                        prop.children [
                            for p in model.Peers do
                                Html.div [
                                    prop.className "participant"
                                    prop.children [
                                        Html.span [
                                            prop.className "participant-initial"
                                            prop.style [ style.backgroundColor (p.Color + "22"); style.color p.Color ]
                                            prop.text (initials p.Name)
                                        ]
                                        Html.div [
                                            prop.children [
                                                Html.div [ prop.className "participant-name"; prop.text p.Name ]
                                                Html.div [ prop.className "participant-meta"; prop.text (if p.IsSelf then "You · editing now" else "Connected · editing now") ]
                                            ]
                                        ]
                                    ]
                                ]
                        ]
                    ]
                ]
            ]
        ]
    ]

let private referenceSections =
    [ "Movement",
      [ "forward / fd NUMBER"; "back / bk NUMBER"; "left / lt DEGREES"; "right / rt DEGREES"
        "home"; "setxy X Y"; "setx X"; "sety Y"; "setheading / seth DEGREES"
        "arc ANGLE RADIUS" ]
      "3D",
      [ "pitchup DEGREES"; "pitchdown DEGREES"; "rollright DEGREES"; "rollleft DEGREES"
        "setz Z"; "setxyz X Y Z"; "zcor"; "pitch" ]
      "Pen",
      [ "penup / pu"; "pendown / pd"; "setpencolor / setpc NAME | 0-15 | [R G B]"
        "setpensize / setps PIXELS" ]
      "Screen",
      [ "clearscreen / cs"; "clean"; "hideturtle / ht"; "showturtle / st" ]
      "Control",
      [ "repeat N [ ... ]"; "if CONDITION [ ... ]"; "ifelse CONDITION [ ... ] [ ... ]"
        "for [i START END [STEP]] [ ... ]"; "while [CONDITION] [ ... ]"
        "run LIST"; "stop"; "output / op VALUE"; "repcount" ]
      "Variables",
      [ "make \"NAME VALUE"; ":NAME"; "local NAME" ]
      "Procedures",
      [ "to NAME :PARAM ... end"; "call NAME ARG ..." ]
      "Math",
      [ "+ - * / %"; "= <> < > <= >="; "sum difference product quotient remainder"
        "minus abs int round sqrt power"; "sin cos tan arctan"; "random N"; "rerandom SEED"; "pi" ]
      "Logic",
      [ "and"; "or"; "not"; "true"; "false" ]
      "Lists",
      [ "list A B"; "fput ITEM LIST"; "lput ITEM LIST"; "first"; "last"
        "butfirst / bf"; "butlast / bl"; "item INDEX LIST"; "count"; "empty? / emptyp" ]
      "Output",
      [ "print / pr VALUE"; "show VALUE" ] ]

let private referencePanel =
    Html.section [
        prop.className "panel-view"
        prop.children [
            panelHeader "Logo Reference" "A practical subset of UCBLogo. Case does not matter; ; starts a comment."
            Html.div [
                prop.className "reference-grid"
                prop.children [
                    for (title, commands) in referenceSections do
                        Html.div [
                            prop.className "reference-card"
                            prop.children [
                                Html.h2 [ prop.className "reference-title"; prop.text title ]
                                for command in commands do
                                    Html.code [ prop.className "reference-code"; prop.text command ]
                            ]
                        ]
                ]
            ]
        ]
    ]

let private exporterPanel (model: Model) (dispatch: Msg -> unit) =
    Html.section [
        prop.className "panel-view"
        prop.children [
            panelHeader "Vector Exporter" "Save the current drawing as a vector or raster snapshot."
            Html.div [
                prop.className "stat-grid"
                prop.children [
                    Html.div [ prop.className "stat-card"; prop.children [
                        Html.div [ prop.className "stat-label"; prop.text "Paths" ]
                        Html.div [ prop.className "stat-value"; prop.text (string model.Drawing.Paths.Length) ] ] ]
                    Html.div [ prop.className "stat-card"; prop.children [
                        Html.div [ prop.className "stat-label"; prop.text "Segments" ]
                        Html.div [ prop.className "stat-value"; prop.text (string (segmentsOf model.Drawing)) ] ] ]
                    Html.div [ prop.className "stat-card"; prop.children [
                        Html.div [ prop.className "stat-label"; prop.text "Steps" ]
                        Html.div [ prop.className "stat-value"; prop.text (string model.Drawing.Steps) ] ] ]
                    Html.div [ prop.className "stat-card"; prop.children [
                        Html.div [ prop.className "stat-label"; prop.text "Turtle" ]
                        Html.div [ prop.className "stat-value"; prop.text $"({num model.Drawing.Turtle.X}, {num model.Drawing.Turtle.Y})" ] ] ]
                ]
            ]
            Html.div [
                prop.className "panel-card"
                prop.children [
                    Html.div [ prop.className "panel-actions"; prop.children [
                        btn "btn btn-primary btn-sm" "Download the LOGO program as .logo" "export-program" ExportProgram dispatch "code_blocks" "Export .logo"
                        btn "btn btn-ghost btn-sm" "Download the room bundle as JSON" "export-room" ExportRoom dispatch "data_object" "Export room JSON"
                        btn "btn btn-ghost btn-sm" "Download the drawing as SVG" "export-svg-panel" ExportSvg dispatch "shape_line" "Export SVG"
                        btn "btn btn-ghost btn-sm" "Download the drawing as PNG" "export-png-panel" ExportPng dispatch "photo_camera" "Export PNG"
                        btn "btn btn-ghost btn-sm" "Replace the shared code from a file" "import-file-panel" ImportFile dispatch "file_open" "Import .logo"
                    ] ]
                    Html.p [
                        prop.className "panel-note"
                        prop.text "The .logo file contains the program. The room bundle adds shared notes, the room URL and an export timestamp. Snapshots contain the current drawing, grid and turtle; the REPL stays local."
                    ]
                ]
            ]
        ]
    ]

// ---------------------------------------------------------------------------
// Settings modal
// ---------------------------------------------------------------------------

let private toggleRow (label: string) (description: string) (checkedValue: bool) (msg: Msg) (dispatch: Msg -> unit) =
    Html.label [
        prop.className "toggle-row"
        prop.children [
            Html.div [
                prop.className "toggle-text"
                prop.children [
                    Html.div [ prop.className "toggle-label"; prop.text label ]
                    Html.div [ prop.className "toggle-desc"; prop.text description ]
                ]
            ]
            Html.input [
                prop.className "switch"
                prop.type'.checkbox
                prop.isChecked checkedValue
                prop.onChange (fun (_: bool) -> dispatch msg)
            ]
        ]
    ]

let private nameWarning (name: string) (model: Model) =
    if nameTaken name model then
        Html.div [
            prop.className "name-warning"
            prop.text "Another collaborator already uses this name. Colors and cursor labels still tell you apart."
        ]
    else
        Html.div [ prop.className "is-hidden" ]

[<ReactComponent>]
let private NamePromptModal (model: Model) (dispatch: Msg -> unit) =
    let name, setName = React.useState (model.SuggestedName)
    Html.div [
        prop.className "overlay"
        prop.custom ("role", "dialog")
        prop.custom ("aria-modal", "true")
        prop.children [
            Html.div [
                prop.className "modal name-modal"
                prop.custom ("data-testid", "name-prompt")
                prop.children [
                    Html.div [
                        prop.className "modal-header"
                        prop.children [
                            Html.div [
                                prop.children [
                                    Html.div [
                                        prop.className "modal-title-row"
                                        prop.children [
                                            Html.h2 [ prop.className "modal-title"; prop.text "Welcome to LogoCraft Studio" ]
                                        ]
                                    ]
                                    Html.p [
                                        prop.className "modal-subtitle"
                                        prop.text "Choose how other collaborators see you in this room."
                                    ]
                                ]
                            ]
                        ]
                    ]
                    Html.div [
                        prop.className "modal-body"
                        prop.children [
                            Html.div [ prop.className "modal-section-title"; prop.text "Display name" ]
                            Html.input [
                                prop.className "text-input"
                                prop.type'.text
                                prop.placeholder "Your display name"
                                prop.value name
                                prop.custom ("data-testid", "name-input")
                                prop.autoFocus true
                                prop.onChange (fun (text: string) -> setName text)
                                prop.onKeyDown (fun (e: Browser.Types.KeyboardEvent) ->
                                    if e.key = "Enter" then
                                        e.preventDefault ()
                                        dispatch (SaveName name))
                            ]
                            Html.p [
                                prop.className "panel-note"
                                prop.text $"Suggested: {model.SuggestedName}. You can change this later in Room settings."
                            ]
                            nameWarning name model
                        ]
                    ]
                    Html.div [
                        prop.className "modal-footer"
                        prop.children [
                            Html.span [ prop.className "modal-footer-note"; prop.text "Display only — no account is created." ]
                            Html.div [
                                prop.className "run-cluster"
                                prop.children [
                                    btn "btn btn-ghost btn-sm" "Use the suggested name" "name-suggested" (SaveName model.SuggestedName) dispatch "" "Use suggested"
                                    btn "btn btn-primary btn-sm" "Join the room with this name" "name-save" (SaveName name) dispatch "" "Join room"
                                ]
                            ]
                        ]
                    ]
                ]
            ]
        ]
    ]

[<ReactComponent>]
let private NameField (model: Model) (dispatch: Msg -> unit) =
    let name, setName = React.useState (if model.MyName = "" then model.SuggestedName else model.MyName)
    let save () = dispatch (SaveName name)
    Html.div [
        prop.className "info-block"
        prop.children [
            Html.div [ prop.className "info-label"; prop.text "Display name" ]
            Html.div [
                prop.className "invite-row"
                prop.children [
                    Html.input [
                        prop.className "text-input"
                        prop.type'.text
                        prop.placeholder "Your display name"
                        prop.value name
                        prop.custom ("data-testid", "name-settings-input")
                        prop.onChange (fun (text: string) -> setName text)
                        prop.onKeyDown (fun (e: Browser.Types.KeyboardEvent) ->
                            if e.key = "Enter" then
                                e.preventDefault ()
                                save ())
                    ]
                    btn "btn btn-primary btn-sm" "Save display name" "name-settings-save" (SaveName name) dispatch "check" "Save"
                ]
            ]
            nameWarning name model
            Html.p [
                prop.className "panel-note"
                prop.text "Shown to everyone in this room. Duplicate names are allowed; colors and cursor labels help tell collaborators apart."
            ]
        ]
    ]

let private settingsModal (model: Model) (dispatch: Msg -> unit) =
    let tabButton (label: string) (iconName: string) (tab: SettingsTab) =
        Html.button [
            prop.className ("modal-tab" + (if model.SettingsTab = tab then " active" else ""))
            prop.type'.button
            prop.onClick (fun _ -> dispatch (SetSettingsTab tab))
            prop.children [ icon iconName; Html.span [ prop.text label ] ]
        ]

    let generalTab =
        Html.div [
            prop.className "modal-body"
            prop.children [
                Html.div [ prop.className "modal-section-title"; prop.text "Profile" ]
                NameField model dispatch
                Html.div [ prop.className "modal-section-title"; prop.text "Room access" ]
                Html.div [
                    prop.className "info-block"
                    prop.children [
                        Html.div [ prop.className "info-label"; prop.text "Invite link" ]
                        Html.div [
                            prop.className "invite-row"
                            prop.children [
                                Html.code [ prop.className "invite-link"; prop.text (inviteUrl model.Room) ]
                                btn "btn btn-primary btn-sm" "Copy the invite link" "copy-invite-modal" CopyInvite dispatch "content_copy" (if model.Copied then "Copied" else "Copy")
                            ]
                        ]
                    ]
                ]
                Html.p [
                    prop.className "panel-note"
                    prop.text "There are no accounts or roles in this prototype: anyone with the room ID can read and edit. Treat the ID as the secret."
                ]
                Html.div [ prop.className "modal-section-title"; prop.text $"Participants ({model.Peers.Length})" ]
                Html.div [
                    prop.className "participant-list"
                    prop.children [
                        for p in model.Peers do
                            Html.div [
                                prop.className "participant"
                                prop.children [
                                    Html.span [
                                        prop.className "participant-initial"
                                        prop.style [ style.backgroundColor (p.Color + "22"); style.color p.Color ]
                                        prop.text (initials p.Name)
                                    ]
                                    Html.div [
                                        prop.children [
                                            Html.div [ prop.className "participant-name"; prop.text p.Name ]
                                            Html.div [ prop.className "participant-meta"; prop.text (if p.IsSelf then "You" else "Collaborator") ]
                                        ]
                                    ]
                                ]
                            ]
                    ]
                ]
                Html.div [ prop.className "modal-section-title"; prop.text "Room actions" ]
                Html.div [
                    prop.className "panel-actions"
                    prop.children [
                        btn "btn btn-ghost btn-sm" "Create a new room" "new-room-modal" NewRoom dispatch "add" "New room"
                        btn "btn btn-ghost btn-sm" "Join by ID or link" "join-room-modal" JoinRoom dispatch "login" "Join room"
                        btn "btn btn-ghost btn-sm danger" "Forget this room locally" "forget-room-modal" ForgetRoom dispatch "delete" "Forget room"
                    ]
                ]
            ]
        ]

    let canvasTab =
        Html.div [
            prop.className "modal-body"
            prop.children [
                Html.div [ prop.className "modal-section-title"; prop.text "Canvas & interpreter" ]
                toggleRow "Live run" "Re-run the program while typing (turns off animation)." model.AutoRun ToggleAutoRun dispatch
                toggleRow "Fit drawing" "Fit the view to the drawing bounds." model.Fit ToggleFit dispatch
                toggleRow "Show turtle" "Render the turtle at its current position." model.ShowTurtle ToggleTurtle dispatch
                toggleRow "Show grid" "Draw the coordinate grid on the canvas." model.ShowGrid ToggleGrid dispatch
                toggleRow "Animate drawing" "Reveal the drawing segment by segment on Run (turns off live run)." model.Animate ToggleAnimate dispatch
                Html.div [
                    prop.className "toggle-row"
                    prop.children [
                        Html.div [
                            prop.className "toggle-text"
                            prop.children [
                                Html.div [ prop.className "toggle-label"; prop.text "Animation speed" ]
                                Html.div [ prop.className "toggle-desc"; prop.text "Playback speed for the animated drawing." ]
                            ]
                        ]
                        Html.div [
                            prop.className "inline-control"
                            prop.children [
                                Html.input [
                                    prop.className "slider"
                                    prop.type'.range
                                    prop.min 25
                                    prop.max 400
                                    prop.step 25
                                    prop.value (string model.AnimSpeed)
                                    prop.custom ("data-testid", "anim-speed-settings")
                                    prop.onChange (fun (value: string) ->
                                        match Int32.TryParse value with
                                        | true, speed -> dispatch (SetAnimSpeed speed)
                                        | _ -> ())
                                ]
                                Html.span [ prop.className "hud-value"; prop.text (speedLabel model.AnimSpeed) ]
                            ]
                        ]
                    ]
                ]
                Html.div [
                    prop.className "toggle-row"
                    prop.children [
                        Html.div [
                            prop.className "toggle-text"
                            prop.children [
                                Html.div [ prop.className "toggle-label"; prop.text "Live delay" ]
                                Html.div [ prop.className "toggle-desc"; prop.text "Debounce before the editor reports changes." ]
                            ]
                        ]
                        Html.div [
                            prop.className "inline-control"
                            prop.children [
                                Html.input [
                                    prop.className "slider"
                                    prop.type'.range
                                    prop.min 20
                                    prop.max 500
                                    prop.step 10
                                    prop.value (string model.LiveDelayMs)
                                    prop.onChange (fun (value: string) ->
                                        match Int32.TryParse value with
                                        | true, ms -> dispatch (SetLiveDelay ms)
                                        | _ -> ())
                                ]
                                Html.span [ prop.className "hud-value"; prop.text $"{model.LiveDelayMs} ms" ]
                            ]
                        ]
                    ]
                ]
                Html.div [
                    prop.className "toggle-row"
                    prop.children [
                        Html.div [
                            prop.className "toggle-text"
                            prop.children [
                                Html.div [ prop.className "toggle-label"; prop.text "Zoom" ]
                                Html.div [ prop.className "toggle-desc"; prop.text "Current canvas magnification." ]
                            ]
                        ]
                        Html.div [
                            prop.className "inline-control"
                            prop.children [
                                Html.span [ prop.className "zoom-value"; prop.text (string model.Zoom + "%") ]
                                btn "btn btn-ghost btn-xs" "Reset zoom" "zoom-reset-modal" ZoomReset dispatch "restart_alt" "Reset"
                            ]
                        ]
                    ]
                ]
            ]
        ]

    Html.div [
        prop.className "overlay"
        prop.tabIndex 0
        prop.custom ("role", "dialog")
        prop.custom ("aria-modal", "true")
        prop.onClick (fun _ -> dispatch CloseSettings)
        prop.onKeyDown (fun (e: Browser.Types.KeyboardEvent) ->
            if e.key = "Escape" then dispatch CloseSettings)
        prop.children [
            Html.div [
                prop.className "modal"
                prop.custom ("data-testid", "settings-modal")
                prop.onClick (fun (e: Browser.Types.MouseEvent) -> e.stopPropagation())
                prop.children [
                    Html.div [
                        prop.className "modal-header"
                        prop.children [
                            Html.div [
                                prop.children [
                                    Html.div [
                                        prop.className "modal-title-row"
                                        prop.children [
                                            icon "tune"
                                            Html.h2 [ prop.className "modal-title"; prop.text "Room settings & permissions" ]
                                            Html.span [ prop.className "badge primary"; prop.text ("#" + model.Room) ]
                                        ]
                                    ]
                                    Html.p [ prop.className "modal-subtitle"; prop.text "Local options for this workspace. The relay has no accounts, passwords or roles." ]
                                ]
                            ]
                            Html.button [
                                prop.className "icon-btn"
                                prop.type'.button
                                prop.title "Close"
                                prop.onClick (fun _ -> dispatch CloseSettings)
                                prop.children [ icon "close" ]
                            ]
                        ]
                    ]
                    Html.div [
                        prop.className "modal-tabs"
                        prop.children [
                            tabButton "General & Access" "admin_panel_settings" GeneralTab
                            tabButton "Canvas & Interpreter" "palette" CanvasTab
                        ]
                    ]
                    (if model.SettingsTab = GeneralTab then generalTab else canvasTab)
                    Html.div [
                        prop.className "modal-footer"
                        prop.children [
                            Html.span [ prop.className "modal-footer-note"; prop.text "Changes apply immediately." ]
                            btn "btn btn-primary btn-sm" "Close settings" "settings-close" CloseSettings dispatch "check" "Close"
                        ]
                    ]
                ]
            ]
        ]
    ]

// ---------------------------------------------------------------------------
// Workspace / root
// ---------------------------------------------------------------------------

let private workspace (model: Model) (dispatch: Msg -> unit) =
    Html.section [
        prop.className ("workspace" + (if model.Panel = CodeCanvas then "" else " is-hidden"))
        prop.children [
            Html.section [
                prop.className "editor-pane"
                prop.children [
                    editorTabs model dispatch
                    EditorHost model dispatch
                    ReplPanel model dispatch
                ]
            ]
            canvasPane model dispatch
        ]
    ]

let view (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "app"
        prop.children [
            topbar model dispatch
            sidebar model dispatch
            Html.div [
                prop.className "shell"
                prop.children [
                    subheader model dispatch
                    Html.main [
                        prop.className "content"
                        prop.children [
                            workspace model dispatch
                            match model.Panel with
                            | CodeCanvas -> ()
                            | ProjectFiles -> projectFilesPanel model dispatch
                            | Multiplayer -> multiplayerPanel model dispatch
                            | Reference -> referencePanel
                            | Exporter -> exporterPanel model dispatch
                        ]
                    ]
                ]
            ]
            if model.SettingsOpen then settingsModal model dispatch
            if model.NamePromptOpen then NamePromptModal model dispatch
        ]
    ]
