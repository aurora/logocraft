module Logo.Core.Interpreter

open System
open System.Collections.Generic
open System.Globalization

// ---------------------------------------------------------------------------
// Ergebnis-Typen
// ---------------------------------------------------------------------------

/// Ein zusammenhängender Linienzug in einer Farbe/Stiftstärke.
/// Koordinaten: x nach rechts, y nach oben, z aus dem Bildschirm heraus (2D-Programme bleiben bei z = 0).
type Path =
    { Color: string
      Width: float
      Points: (float * float * float)[] }

type TurtleState =
    { X: float
      Y: float
      Z: float
      /// Himmelsrichtung der Blickrichtung in der xy-Ebene: 0 = nach oben (+y), im Uhrzeigersinn positiv
      Heading: float
      /// Neigung der Blickrichtung gegen die xy-Ebene in Grad (+90 = senkrecht nach oben, +z)
      Pitch: float
      /// Einheitsvektor der Blickrichtung
      Forward: float * float * float
      /// Einheitsvektor "oben" der Turtle (Rückenrichtung)
      Up: float * float * float
      PenDown: bool
      Visible: bool
      Color: string }

type ErrorInfo = { Line: int; Message: string }

type RunResult =
    { Paths: Path[]
      Turtle: TurtleState
      Output: string[]
      Error: ErrorInfo option
      Steps: int }

/// One recorded turtle movement for the step-through viewer.
type StepEvent =
    { /// 1-based source line of the movement (0 if unknown)
      Line: int
      Turtle: TurtleState
      /// Number of drawing points that exist after this movement
      PointCount: int }

// ---------------------------------------------------------------------------
// Grenzen (Schutz vor Endlosschleifen im Browser)
// ---------------------------------------------------------------------------

let private maxSteps = 300000
let private maxDepth = 200
let private maxPoints = 200000
let private maxOutputLines = 2000

// ---------------------------------------------------------------------------
// Primitive: Aliase und Stelligkeit
// ---------------------------------------------------------------------------

let private aliases : Map<string, string> =
    Map.ofList
        [ "fd", "forward"
          "bk", "back"
          "lt", "left"
          "rt", "right"
          "pu", "penup"
          "pd", "pendown"
          "ht", "hideturtle"
          "st", "showturtle"
          "cs", "clearscreen"
          "seth", "setheading"

          "setpc", "setpencolor"
          "setcolor", "setpencolor"
          "setps", "setpensize"
          "setwidth", "setpensize"
          "pr", "print"
          "op", "output"
          "bf", "butfirst"
          "bl", "butlast"
          "pendown?", "pendownp"
          "empty?", "emptyp" ]

let private arities : Map<string, int> =
    Map.ofList
        [ // Turtle
          "forward", 1
          "back", 1
          "left", 1
          "right", 1
          "penup", 0
          "pendown", 0
          "home", 0
          "clearscreen", 0
          "clean", 0
          "hideturtle", 0
          "showturtle", 0
          "setxy", 2
          "setx", 1
          "sety", 1
          "setheading", 1
          "pitchup", 1
          "pitchdown", 1
          "rollright", 1
          "rollleft", 1
          "setz", 1
          "setxyz", 3
          "setpencolor", 1
          "setpensize", 1
          "arc", 2
          // Ausgabe
          "print", 1
          "show", 1
          // Variablen
          "make", 2
          "local", 1
          // Kontrollstrukturen
          "repeat", 2
          "if", 2
          "ifelse", 3
          "while", 2
          "for", 2
          "run", 1
          "stop", 0
          "output", 1
          "repcount", 0
          // Rechnen
          "sum", 2
          "difference", 2
          "product", 2
          "quotient", 2
          "remainder", 2
          "minus", 1
          "abs", 1
          "int", 1
          "round", 1
          "sqrt", 1
          "power", 2
          "sin", 1
          "cos", 1
          "tan", 1
          "arctan", 1
          "random", 1
          "rerandom", 1
          "pi", 0
          // Abfragen
          "xcor", 0
          "ycor", 0
          "zcor", 0
          "heading", 0
          "pitch", 0
          "pendownp", 0
          // Logik
          "true", 0
          "false", 0
          "and", 2
          "or", 2
          "not", 1
          // Listen
          "list", 2
          "fput", 2
          "lput", 2
          "first", 1
          "last", 1
          "butfirst", 1
          "butlast", 1
          "item", 2
          "count", 1
          "emptyp", 1 ]

let private canonical (name: string) =
    match Map.tryFind name aliases with
    | Some c -> c
    | None -> name

/// Alle bekannten Primitive samt Aliasen (z. B. für Syntax-Highlighting im Editor).
let primitiveNames : string[] =
    [| for KeyValue(k, _) in arities -> k
       for KeyValue(k, _) in aliases -> k |]

// ---------------------------------------------------------------------------
// Farben
// ---------------------------------------------------------------------------

let private palette =
    [| "#111827"; "#2563eb"; "#16a34a"; "#0891b2"; "#dc2626"; "#c026d3"; "#eab308"; "#ffffff"
       "#92400e"; "#d6b88a"; "#166534"; "#67e8f9"; "#fb7185"; "#7c3aed"; "#f97316"; "#6b7280" |]

let private namedColors : Map<string, string> =
    Map.ofList
        [ "black", palette.[0]
          "blue", palette.[1]
          "green", palette.[2]
          "cyan", palette.[3]
          "red", palette.[4]
          "magenta", palette.[5]
          "yellow", palette.[6]
          "white", palette.[7]
          "brown", palette.[8]
          "tan", palette.[9]
          "aqua", palette.[11]
          "salmon", palette.[12]
          "purple", palette.[13]
          "orange", palette.[14]
          "gray", palette.[15]
          "grey", palette.[15]
          "pink", "#ec4899" ]

let private isHexDigit (c: char) =
    (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')

let private isHexColor (s: string) =
    (s.Length = 4 || s.Length = 7)
    && s.[0] = '#'
    && s.Substring(1) |> Seq.forall isHexDigit

let private isPlainName (s: string) =
    s.Length > 0 && s.Length <= 24 && s |> Seq.forall (fun c -> c >= 'a' && c <= 'z')

// ---------------------------------------------------------------------------
// Werte
// ---------------------------------------------------------------------------

let formatNumber (n: float) =
    if Double.IsNaN n then "NaN"
    elif Double.IsInfinity n then (if n > 0.0 then "Infinity" else "-Infinity")
    else
        let r = Math.Round(n, 10)
        // -0 (z. B. aus Rundung von -1e-17) soll als 0 erscheinen
        (if r = 0.0 then 0.0 else r).ToString(CultureInfo.InvariantCulture)

let rec private formatValue (nested: bool) (v: Value) : string =
    match v with
    | VNum n -> formatNumber n
    | VWord w -> w
    | VList items ->
        let inner = items |> List.map (formatValue true) |> String.concat " "
        if nested then "[" + inner + "]" else inner

let private showValue (v: Value) =
    match v with
    | VList items -> "[" + (items |> List.map (formatValue true) |> String.concat " ") + "]"
    | _ -> formatValue false v

let private mkBool (b: bool) = VWord(if b then "true" else "false")

let private wordsEqual (a: string) (b: string) =
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

let rec private valuesEqual (a: Value) (b: Value) : bool =
    match a, b with
    | VNum x, VNum y -> x = y
    | VWord x, VWord y ->
        match Lexer.tryParseNumber x, Lexer.tryParseNumber y with
        | Some nx, Some ny -> nx = ny
        | _ -> wordsEqual x y
    | VNum x, VWord y
    | VWord y, VNum x ->
        match Lexer.tryParseNumber y with
        | Some ny -> x = ny
        | None -> false
    | VList xs, VList ys -> List.length xs = List.length ys && List.forall2 valuesEqual xs ys
    | _ -> false

/// Wandelt Listeninhalt zurück in Token, damit er ausgeführt werden kann.
let rec private valuesToTokens (line: int) (items: Value list) : Token list =
    items
    |> List.collect (fun v ->
        match v with
        | VNum n -> [ { Kind = TNumber n; Line = line } ]
        | VList inner ->
            [ { Kind = TLBracket; Line = line } ] @ valuesToTokens line inner @ [ { Kind = TRBracket; Line = line } ]
        | VWord w ->
            if w.StartsWith "\"" then [ { Kind = TQuoted(w.Substring 1); Line = line } ]
            elif w.StartsWith ":" && w.Length > 1 then [ { Kind = TVar(w.Substring(1).ToLowerInvariant()); Line = line } ]
            elif w = "+" || w = "-" || w = "~" || w = "*" || w = "/" || w = "%" || w = "=" || w = "<" || w = ">"
                 || w = "<=" || w = ">=" || w = "<>" || w = "(" || w = ")" then
                [ { Kind = TOp w; Line = line } ]
            else
                match Lexer.tryParseNumber w with
                | Some n when w.Length > 0 && (Char.IsDigit w.[0] || w.[0] = '.') -> [ { Kind = TNumber n; Line = line } ]
                | _ -> [ { Kind = TWord(w.ToLowerInvariant()); Line = line } ])

let private tokenText (t: Token) =
    match t.Kind with
    | TNumber n -> formatNumber n
    | TWord w -> w
    | TQuoted q -> "\"" + q
    | TVar v -> ":" + v
    | TOp "~" -> "-"
    | TOp o -> o
    | TLBracket -> "["
    | TRBracket -> "]"

// ---------------------------------------------------------------------------
// Interpreter
// ---------------------------------------------------------------------------

type private Signal =
    | NoSignal
    | StopSignal
    | OutputSignal of Value

type private ProcDef =
    { Name: string
      Params: string list
      Body: Token[] }

type private Cursor(toks: Token[]) =
    let mutable pos = 0
    member _.Pos
        with get () = pos
        and set v = pos <- v
    member _.AtEnd = pos >= toks.Length
    member _.Peek : Token option = if pos < toks.Length then Some toks.[pos] else None
    member _.Next() : Token =
        let t = toks.[pos]
        pos <- pos + 1
        t

type private Machine() =
    let globals = Dictionary<string, Value>()
    let frames = ResizeArray<Dictionary<string, Value>>()
    let procs = Dictionary<string, ProcDef>()
    let repeatCounts = ResizeArray<int>()
    let paths = ResizeArray<Path>()
    let output = ResizeArray<string>()
    /// Zufallszahlen: eigener, deterministischer Generator (Park-Miller). Dasselbe Programm liefert in jedem
    /// Browser und bei jedem Lauf dieselbe Zeichnung, wichtig, weil alle Mitwirkenden den Code selbst ausführen.
    /// Alle Zwischenwerte liegen unter 2^53, die Rechnung mit float ist deshalb exakt (in .NET und JS gleich).
    let mutable rngState = 12345.0

    // Position und Orientierung der Turtle: Blickrichtung f und "oben" u (Einheitsvektoren, senkrecht
    // zueinander); "rechts" ergibt sich als f x u. Startzustand: f = +y, u = +z, rechts = +x.
    let mutable x = 0.0
    let mutable y = 0.0
    let mutable z = 0.0
    let mutable fx = 0.0
    let mutable fy = 1.0
    let mutable fz = 0.0
    let mutable ux = 0.0
    let mutable uy = 0.0
    let mutable uz = 1.0
    let mutable penDown = true
    let mutable visible = true
    let mutable color = palette.[0]
    let mutable width = 2.0

    let mutable current : ResizeArray<float * float * float> = null
    let mutable currentColor = color
    let mutable currentWidth = width
    let mutable pointCount = 0

    let mutable signal = NoSignal
    let mutable steps = 0
    let mutable depth = 0
    let mutable line = 0
    let stepTrace = ResizeArray<StepEvent>()
    let mutable tracing = false

    // -- Hilfsfunktionen ----------------------------------------------------

    let fail (msg: string) : 'a = raise (LogoError(line, msg))

    let noSignal () =
        match signal with
        | NoSignal -> true
        | _ -> false

    let flush () =
        if not (isNull current) then
            if current.Count >= 2 then
                paths.Add { Color = currentColor; Width = currentWidth; Points = current.ToArray() }
            current <- null

    let num (v: Value) : float =
        match v with
        | VNum n -> n
        | VWord w ->
            match Lexer.tryParseNumber w with
            | Some n -> n
            | None -> fail (sprintf "'%s' is not a number" w)
        | VList _ -> fail "A list is not a number"

    let toBool (v: Value) : bool =
        match v with
        | VWord w when wordsEqual w "true" -> true
        | VWord w when wordsEqual w "false" -> false
        | _ -> fail (sprintf "'%s' is neither true nor false" (showValue v))

    let normalizeAngle (a: float) =
        let r = a % 360.0
        if r < 0.0 then r + 360.0 else r

    let toRad (deg: float) = deg * Math.PI / 180.0

    let moveTo (nx: float) (ny: float) (nz: float) =
        if penDown then
            if isNull current then
                current <- ResizeArray<float * float * float>()
                currentColor <- color
                currentWidth <- width
                current.Add((x, y, z))
                pointCount <- pointCount + 1
            current.Add((nx, ny, nz))
            pointCount <- pointCount + 1
            if pointCount > maxPoints then
                fail "The drawing is too large (too many lines)"
        else
            flush ()
        x <- nx
        y <- ny
        z <- nz

    let forward (d: float) = moveTo (x + d * fx) (y + d * fy) (z + d * fz)

    // -- Orientierung ---------------------------------------------------------

    /// "rechts" = f x u
    let rightVec () = (fy * uz - fz * uy, fz * ux - fx * uz, fx * uy - fy * ux)

    /// Gleicht Rundungsfehler aus, damit f und u auch nach vielen Drehungen Einheitsvektoren
    /// senkrecht zueinander bleiben.
    let orthonormalize () =
        let fl = Math.Sqrt(fx * fx + fy * fy + fz * fz)
        fx <- fx / fl
        fy <- fy / fl
        fz <- fz / fl
        let d = ux * fx + uy * fy + uz * fz
        ux <- ux - d * fx
        uy <- uy - d * fy
        uz <- uz - d * fz
        let ul = Math.Sqrt(ux * ux + uy * uy + uz * uz)
        ux <- ux / ul
        uy <- uy / ul
        uz <- uz / ul

    /// Drehung um "oben" (nach rechts bei positivem Winkel). Im 2D-Fall genau das klassische `right`.
    let turnRight (deg: float) =
        let t = toRad deg
        let c = Math.Cos t
        let s = Math.Sin t
        let (rx, ry, rz) = rightVec ()
        fx <- fx * c + rx * s
        fy <- fy * c + ry * s
        fz <- fz * c + rz * s
        orthonormalize ()

    /// Nase nach oben (positiver Winkel) bzw. unten. Drehung um "rechts".
    let pitchUp (deg: float) =
        let t = toRad deg
        let c = Math.Cos t
        let s = Math.Sin t
        let nfx = fx * c + ux * s
        let nfy = fy * c + uy * s
        let nfz = fz * c + uz * s
        ux <- ux * c - fx * s
        uy <- uy * c - fy * s
        uz <- uz * c - fz * s
        fx <- nfx
        fy <- nfy
        fz <- nfz
        orthonormalize ()

    /// Seitliches Rollen um die Blickrichtung (positiv = nach rechts, rechter Flügel unten).
    let rollRight (deg: float) =
        let t = toRad deg
        let c = Math.Cos t
        let s = Math.Sin t
        let (rx, ry, rz) = rightVec ()
        ux <- ux * c + rx * s
        uy <- uy * c + ry * s
        uz <- uz * c + rz * s
        orthonormalize ()

    let levelHeading (deg: float) =
        let t = toRad deg
        fx <- Math.Sin t
        fy <- Math.Cos t
        fz <- 0.0
        ux <- 0.0
        uy <- 0.0
        uz <- 1.0

    /// Himmelsrichtung der Blickrichtung in der xy-Ebene (0 = +y, im Uhrzeigersinn). Auf 9 Stellen gerundet,
    /// damit z. B. nach 4 x `right 90` wieder genau 0 herauskommt und nicht 359.99999999999994.
    let headingDeg () =
        if Math.Abs fx < 1e-12 && Math.Abs fy < 1e-12 then
            0.0
        else
            normalizeAngle (Math.Round(Math.Atan2(fx, fy) * 180.0 / Math.PI, 9))

    let pitchDeg () =
        Math.Round(Math.Asin(Math.Max(-1.0, Math.Min(1.0, fz))) * 180.0 / Math.PI, 9)

    /// Records the current turtle state for the step-through viewer.
    let recordStep () =
        if tracing then
            let heading =
                if Math.Abs fx < 1e-12 && Math.Abs fy < 1e-12 then 0.0
                else normalizeAngle (Math.Round(Math.Atan2(fx, fy) * 180.0 / Math.PI, 9))
            stepTrace.Add
                { Line = line
                  Turtle =
                    { X = x
                      Y = y
                      Z = z
                      Heading = heading
                      Pitch = pitchDeg ()
                      Forward = (fx, fy, fz)
                      Up = (ux, uy, uz)
                      PenDown = penDown
                      Visible = visible
                      Color = color }
                  PointCount = pointCount }

    let resetOrientation () = levelHeading 0.0

    let resetTurtle () =
        flush ()
        x <- 0.0
        y <- 0.0
        z <- 0.0
        resetOrientation ()

    /// Ganzzahl im Bereich 0 .. n-1
    let nextRandom (n: float) : float =
        rngState <- (rngState * 16807.0) % 2147483647.0
        Math.Floor((rngState - 1.0) / 2147483646.0 * n)

    let parseColor (v: Value) : string =
        match v with
        | VNum n ->
            let i = int (Math.Round n)
            if i >= 0 && i < palette.Length then palette.[i] else fail (sprintf "Unknown color number %d (allowed: 0-15)" i)
        | VWord w ->
            let name = w.ToLowerInvariant()
            match Map.tryFind name namedColors with
            | Some c -> c
            | None ->
                match Lexer.tryParseNumber name with
                | Some n ->
                    let i = int (Math.Round n)
                    if i >= 0 && i < palette.Length then palette.[i] else fail (sprintf "Unknown color number %d (allowed: 0-15)" i)
                | None ->
                    if isHexColor name || isPlainName name then name else fail (sprintf "Unknown color '%s'" w)
        | VList [ r; g; b ] ->
            let c (v: Value) = int (Math.Round(Math.Max(0.0, Math.Min(100.0, num v)) * 2.55))
            sprintf "rgb(%d,%d,%d)" (c r) (c g) (c b)
        | VList _ -> fail "A color list needs three values [red green blue] (0-100)"

    let tryGetVar (name: string) : Value option =
        let mutable result : Value option = None
        let mutable i = frames.Count - 1
        while result.IsNone && i >= 0 do
            match frames.[i].TryGetValue name with
            | true, v -> result <- Some v
            | _ -> ()
            i <- i - 1
        if result.IsSome then
            result
        else
            match globals.TryGetValue name with
            | true, v -> Some v
            | _ -> None

    let setVar (name: string) (v: Value) =
        let mutable assigned = false
        let mutable i = frames.Count - 1
        while not assigned && i >= 0 do
            if frames.[i].ContainsKey name then
                frames.[i].[name] <- v
                assigned <- true
            i <- i - 1
        if not assigned then
            globals.[name] <- v

    let itemsOf (v: Value) : Value list =
        match v with
        | VList items -> items
        | VWord w -> [ for c in w -> VWord(string c) ]
        | VNum n -> [ for c in formatNumber n -> VWord(string c) ]

    // -- Öffentliche Zustände ----------------------------------------------

    member _.Steps = steps

    member _.Output = output.ToArray()

    member _.Paths =
        flush ()
        paths.ToArray()

    member _.Turtle : TurtleState =
        { X = x
          Y = y
          Z = z
          Heading = headingDeg ()
          Pitch = pitchDeg ()
          Forward = (fx, fy, fz)
          Up = (ux, uy, uz)
          PenDown = penDown
          Visible = visible
          Color = color }

    member _.CurrentLine = line

    member _.EnableTracing() = tracing <- true

    member _.Trace = stepTrace.ToArray()

    member _.Define(p: ProcDef) = procs.[p.Name] <- p

    // -- Ausführung ----------------------------------------------------------

    member m.Exec(toks: Token[]) : unit =
        let cur = Cursor toks
        while not cur.AtEnd && noSignal () do
            m.ExecStatement cur

    member m.ExecList(items: Value list) : unit =
        m.Exec(valuesToTokens line items |> List.toArray)

    member m.ExecStatement(cur: Cursor) : unit =
        let t = cur.Next()
        if t.Line > 0 then
            line <- t.Line
        match t.Kind with
        | TWord name ->
            match m.CallWord(name, cur) with
            | None -> ()
            | Some v ->
                fail (
                    sprintf
                        "'%s' returns %s, but nothing happens with it (missing print or fd?)"
                        name
                        (showValue v)
                )
        | _ -> fail (sprintf "I don't know what to do with '%s'" (tokenText t))

    /// Wertet eine Liste als einzelnen Ausdruck aus (für while-Bedingungen).
    member m.EvalList(items: Value list) : Value =
        let cur = Cursor(valuesToTokens line items |> List.toArray)
        if cur.AtEnd then
            fail "Empty expression"
        let v = m.EvalExpr cur
        if not cur.AtEnd then
            fail (sprintf "Unexpected: '%s'" (tokenText cur.Peek.Value))
        v

    member m.CallProc(p: ProcDef, args: Value list) : Value option =
        depth <- depth + 1
        if depth > maxDepth then
            fail (sprintf "Recursion too deep (more than %d levels) in '%s'" maxDepth p.Name)
        let frame = Dictionary<string, Value>()
        List.iter2 (fun (n: string) v -> frame.[n] <- v) p.Params args
        frames.Add frame
        m.Exec p.Body
        frames.RemoveAt(frames.Count - 1)
        depth <- depth - 1
        let result =
            match signal with
            | OutputSignal v -> Some v
            | _ -> None
        signal <- NoSignal
        result

    member m.EvalArg(cur: Cursor, forName: string) : Value =
        if cur.AtEnd then
            fail (sprintf "'%s' needs another input" forName)
        m.EvalExpr cur

    member m.CallWord(name: string, cur: Cursor) : Value option =
        steps <- steps + 1
        if steps > maxSteps then
            fail "Too many steps — infinite loop?"
        match procs.TryGetValue name with
        | true, p ->
            let args = [ for _ in p.Params -> m.EvalArg(cur, name) ]
            m.CallProc(p, args)
        | _ ->
            let prim = canonical name
            match Map.tryFind prim arities with
            | Some n ->
                let args = [ for _ in 1..n -> m.EvalArg(cur, name) ]
                m.Apply(prim, args)
            | None -> fail (sprintf "I don't know how to do '%s'" name)

    // -- Ausdrücke -----------------------------------------------------------

    member m.EvalExpr(cur: Cursor) : Value = m.EvalComparison cur

    member m.EvalComparison(cur: Cursor) : Value =
        let mutable left = m.EvalAdditive cur
        let mutable go = true
        while go do
            match cur.Peek with
            | Some { Kind = TOp op } when op = "=" || op = "<>" || op = "<" || op = ">" || op = "<=" || op = ">=" ->
                cur.Next() |> ignore
                let right = m.EvalAdditive cur
                left <-
                    match op with
                    | "=" -> mkBool (valuesEqual left right)
                    | "<>" -> mkBool (not (valuesEqual left right))
                    | "<" -> mkBool (num left < num right)
                    | ">" -> mkBool (num left > num right)
                    | "<=" -> mkBool (num left <= num right)
                    | _ -> mkBool (num left >= num right)
            | _ -> go <- false
        left

    member m.EvalAdditive(cur: Cursor) : Value =
        let mutable left = m.EvalMultiplicative cur
        let mutable go = true
        while go do
            match cur.Peek with
            | Some { Kind = TOp "+" } ->
                cur.Next() |> ignore
                left <- VNum(num left + num (m.EvalMultiplicative cur))
            | Some { Kind = TOp "-" } ->
                cur.Next() |> ignore
                left <- VNum(num left - num (m.EvalMultiplicative cur))
            | _ -> go <- false
        left

    member m.EvalMultiplicative(cur: Cursor) : Value =
        let mutable left = m.EvalUnary cur
        let mutable go = true
        while go do
            match cur.Peek with
            | Some { Kind = TOp "*" } ->
                cur.Next() |> ignore
                left <- VNum(num left * num (m.EvalUnary cur))
            | Some { Kind = TOp "/" } ->
                cur.Next() |> ignore
                let d = num (m.EvalUnary cur)
                if d = 0.0 then
                    fail "Division by zero"
                left <- VNum(num left / d)
            | Some { Kind = TOp "%" } ->
                cur.Next() |> ignore
                let d = num (m.EvalUnary cur)
                if d = 0.0 then
                    fail "Division by zero"
                left <- VNum(num left % d)
            | _ -> go <- false
        left

    member m.EvalUnary(cur: Cursor) : Value =
        match cur.Peek with
        | Some { Kind = TOp "-" }
        | Some { Kind = TOp "~" } ->
            cur.Next() |> ignore
            VNum(-(num (m.EvalUnary cur)))
        | _ -> m.EvalPrimary cur

    member m.EvalPrimary(cur: Cursor) : Value =
        if cur.AtEnd then
            fail "A value is missing here"
        let t = cur.Next()
        if t.Line > 0 then
            line <- t.Line
        match t.Kind with
        | TNumber n -> VNum n
        | TQuoted q -> VWord q
        | TVar v ->
            match tryGetVar v with
            | Some value -> value
            | None -> fail (sprintf "Variable ':%s' has no value" v)
        | TLBracket -> m.ReadList cur
        | TOp "(" ->
            let v = m.EvalExpr cur
            match cur.Peek with
            | Some { Kind = TOp ")" } ->
                cur.Next() |> ignore
                v
            | _ -> fail "Missing closing ')'"
        | TWord name ->
            match m.CallWord(name, cur) with
            | Some v -> v
            | None -> fail (sprintf "'%s' does not return a value" name)
        | TRBracket -> fail "Unexpected ']'"
        | TOp o -> fail (sprintf "Unexpected operator '%s'" o)

    /// Liest eine Liste bis zur passenden ']' (die öffnende wurde bereits konsumiert).
    member m.ReadList(cur: Cursor) : Value =
        let items = ResizeArray<Value>()
        let mutable closed = false
        while not closed do
            if cur.AtEnd then
                fail "Missing closing ']'"
            let t = cur.Next()
            match t.Kind with
            | TRBracket -> closed <- true
            | TLBracket -> items.Add(m.ReadList cur)
            | TOp "~" ->
                // "[0 -200]": Präfix-Minus direkt vor einer Zahl ist ein negatives Zahlenliteral
                match cur.Peek with
                | Some { Kind = TNumber n } ->
                    cur.Next() |> ignore
                    items.Add(VNum(-n))
                | _ -> items.Add(VWord "-")
            | TNumber n -> items.Add(VNum n)
            | TWord w -> items.Add(VWord w)
            | TQuoted q -> items.Add(VWord("\"" + q))
            | TVar v -> items.Add(VWord(":" + v))
            | TOp o -> items.Add(VWord o)
        VList(List.ofSeq items)

    // -- Primitive -----------------------------------------------------------

    member m.Apply(prim: string, args: Value list) : Value option =
        match prim, args with
        // Turtle
        | "forward", [ d ] ->
            forward (num d)
            recordStep ()
            None
        | "back", [ d ] ->
            forward (-(num d))
            recordStep ()
            None
        | "left", [ a ] ->
            turnRight (-(num a))
            None
        | "right", [ a ] ->
            turnRight (num a)
            None
        | "pitchup", [ a ] ->
            pitchUp (num a)
            None
        | "pitchdown", [ a ] ->
            pitchUp (-(num a))
            None
        | "rollright", [ a ] ->
            rollRight (num a)
            None
        | "rollleft", [ a ] ->
            rollRight (-(num a))
            None
        | "penup", [] ->
            flush ()
            penDown <- false
            None
        | "pendown", [] ->
            penDown <- true
            None
        | "home", [] ->
            moveTo 0.0 0.0 0.0
            resetOrientation ()
            recordStep ()
            None
        | "clearscreen", [] ->
            resetTurtle ()
            paths.Clear()
            pointCount <- 0
            stepTrace.Clear()
            None
        | "clean", [] ->
            flush ()
            paths.Clear()
            pointCount <- 0
            stepTrace.Clear()
            None
        | "hideturtle", [] ->
            visible <- false
            None
        | "showturtle", [] ->
            visible <- true
            None
        | "setxy", [ nx; ny ] ->
            moveTo (num nx) (num ny) z
            recordStep ()
            None
        | "setx", [ nx ] ->
            moveTo (num nx) y z
            recordStep ()
            None
        | "sety", [ ny ] ->
            moveTo x (num ny) z
            recordStep ()
            None
        | "setz", [ nz ] ->
            moveTo x y (num nz)
            recordStep ()
            None
        | "setxyz", [ nx; ny; nz ] ->
            moveTo (num nx) (num ny) (num nz)
            recordStep ()
            None
        | "setheading", [ a ] ->
            // richtet die Turtle waagerecht aus (Neigung und Rollen werden zurückgesetzt)
            levelHeading (num a)
            None
        | "setpencolor", [ c ] ->
            let parsed = parseColor c
            flush ()
            color <- parsed
            None
        | "setpensize", [ w ] ->
            let nw = Math.Max(0.5, num w)
            flush ()
            width <- nw
            None
        | "arc", [ angle; radius ] ->
            let a = num angle
            let r = num radius
            if penDown && a <> 0.0 then
                flush ()
                let segs = Math.Max(2, int (Math.Ceiling(Math.Abs a / 3.0)))
                // Kreisbogen um die Turtle in der Ebene aus Blickrichtung und "rechts", beginnend in Blickrichtung
                let (rx, ry, rz) = rightVec ()
                let pts =
                    [| for k in 0..segs ->
                           let t = toRad (a * float k / float segs)
                           let c = Math.Cos t
                           let s = Math.Sin t
                           (x + r * (fx * c + rx * s), y + r * (fy * c + ry * s), z + r * (fz * c + rz * s)) |]
                pointCount <- pointCount + pts.Length
                if pointCount > maxPoints then
                    fail "The drawing is too large (too many lines)"
                paths.Add { Color = color; Width = width; Points = pts }
                recordStep ()
            None
        // Ausgabe
        | "print", [ v ] ->
            if output.Count < maxOutputLines then
                output.Add(formatValue false v)
            None
        | "show", [ v ] ->
            if output.Count < maxOutputLines then
                output.Add(showValue v)
            None
        // Variablen
        | "make", [ VWord name; v ] ->
            setVar (name.ToLowerInvariant()) v
            None
        | "make", _ -> fail "make needs a quoted name, e.g. make \"x 10"
        | "local", [ VWord name ] ->
            if frames.Count > 0 then
                frames.[frames.Count - 1].[name.ToLowerInvariant()] <- VList []
            None
        // Kontrollstrukturen
        | "repeat", [ n; VList body ] ->
            let count = int (Math.Floor(num n))
            let toks = valuesToTokens line body |> List.toArray
            repeatCounts.Add 0
            let slot = repeatCounts.Count - 1
            let mutable i = 1
            while i <= count && noSignal () do
                repeatCounts.[slot] <- i
                m.Exec toks
                i <- i + 1
            repeatCounts.RemoveAt slot
            None
        | "repeat", _ -> fail "repeat expects a number and a list: repeat 4 [fd 50 rt 90]"
        | "repcount", [] -> Some(VNum(if repeatCounts.Count > 0 then float repeatCounts.[repeatCounts.Count - 1] else -1.0))
        | "if", [ c; VList body ] ->
            if toBool c then
                m.ExecList body
            None
        | "if", _ -> fail "if expects a condition and a list: if :x > 3 [fd 10]"
        | "ifelse", [ c; VList yes; VList no ] ->
            m.ExecList(if toBool c then yes else no)
            None
        | "ifelse", _ -> fail "ifelse expects a condition and two lists: ifelse :x > 3 [fd 10] [bk 10]"
        | "while", [ VList cond; VList body ] ->
            let toks = valuesToTokens line body |> List.toArray
            while noSignal () && toBool (m.EvalList cond) do
                steps <- steps + 1
                if steps > maxSteps then
                    fail "Too many steps — infinite loop?"
                m.Exec toks
            None
        | "while", _ -> fail "while expects two lists: while [:i < 10] [make \"i :i + 1]"
        | "for", [ VList spec; VList body ] ->
            match spec with
            | VWord var :: rest ->
                let cur = Cursor(valuesToTokens line rest |> List.toArray)
                if cur.AtEnd then
                    fail "for [i start end] needs a start and end value"
                let start = num (m.EvalExpr cur)
                if cur.AtEnd then
                    fail "for [i start end] needs an end value"
                let limit = num (m.EvalExpr cur)
                let step = if cur.AtEnd then (if limit >= start then 1.0 else -1.0) else num (m.EvalExpr cur)
                if step = 0.0 then
                    fail "The for step size must not be 0"
                let name = var.ToLowerInvariant()
                let count = Math.Floor((limit - start) / step) + 1.0
                let toks = valuesToTokens line body |> List.toArray
                let frame = Dictionary<string, Value>()
                frames.Add frame
                let mutable k = 0.0
                while k < count && noSignal () do
                    frame.[name] <- VNum(start + k * step)
                    m.Exec toks
                    k <- k + 1.0
                frames.RemoveAt(frames.Count - 1)
                None
            | _ -> fail "for expects [variable start end] as its first list"
        | "for", _ -> fail "for expects two lists: for [i 1 10] [fd :i]"
        | "run", [ VList items ] ->
            m.ExecList items
            None
        | "run", _ -> fail "run expects a list"
        | "stop", [] ->
            signal <- StopSignal
            None
        | "output", [ v ] ->
            signal <- OutputSignal v
            None
        // Rechnen
        | "sum", [ a; b ] -> Some(VNum(num a + num b))
        | "difference", [ a; b ] -> Some(VNum(num a - num b))
        | "product", [ a; b ] -> Some(VNum(num a * num b))
        | "quotient", [ a; b ] ->
            let d = num b
            if d = 0.0 then
                fail "Division by zero"
            Some(VNum(num a / d))
        | "remainder", [ a; b ] ->
            let d = num b
            if d = 0.0 then
                fail "Division by zero"
            Some(VNum(num a % d))
        | "minus", [ a ] -> Some(VNum(-(num a)))
        | "abs", [ a ] -> Some(VNum(Math.Abs(num a)))
        | "int", [ a ] -> Some(VNum(Math.Truncate(num a)))
        | "round", [ a ] -> Some(VNum(Math.Round(num a, MidpointRounding.AwayFromZero)))
        | "sqrt", [ a ] ->
            let v = num a
            if v < 0.0 then
                fail "The square root of a negative number is not defined"
            Some(VNum(Math.Sqrt v))
        | "power", [ a; b ] -> Some(VNum(Math.Pow(num a, num b)))
        | "sin", [ a ] -> Some(VNum(Math.Sin(toRad (num a))))
        | "cos", [ a ] -> Some(VNum(Math.Cos(toRad (num a))))
        | "tan", [ a ] -> Some(VNum(Math.Tan(toRad (num a))))
        | "arctan", [ a ] -> Some(VNum(Math.Atan(num a) * 180.0 / Math.PI))
        | "random", [ a ] ->
            let n = Math.Floor(num a)
            if n < 1.0 then
                fail "random needs a number greater than 0"
            Some(VNum(nextRandom n))
        | "rerandom", [ a ] ->
            // Startwert setzen: dasselbe Programm liefert mit demselben Startwert dieselben Zufallszahlen
            rngState <- Math.Abs(Math.Floor(num a)) % 2147483646.0 + 1.0
            None
        | "pi", [] -> Some(VNum Math.PI)
        // Abfragen
        | "xcor", [] -> Some(VNum x)
        | "ycor", [] -> Some(VNum y)
        | "zcor", [] -> Some(VNum z)
        | "heading", [] -> Some(VNum(headingDeg ()))
        | "pitch", [] -> Some(VNum(pitchDeg ()))
        | "pendownp", [] -> Some(mkBool penDown)
        // Logik
        | "true", [] -> Some(mkBool true)
        | "false", [] -> Some(mkBool false)
        | "and", [ a; b ] -> Some(mkBool (toBool a && toBool b))
        | "or", [ a; b ] -> Some(mkBool (toBool a || toBool b))
        | "not", [ a ] -> Some(mkBool (not (toBool a)))
        // Listen
        | "list", [ a; b ] -> Some(VList [ a; b ])
        | "fput", [ a; VList l ] -> Some(VList(a :: l))
        | "lput", [ a; VList l ] -> Some(VList(l @ [ a ]))
        | ("fput" | "lput"), _ -> fail (sprintf "%s needs a list as its second input" prim)
        | "first", [ v ] ->
            match itemsOf v with
            | h :: _ -> Some h
            | [] -> fail "first: empty list"
        | "last", [ v ] ->
            match itemsOf v with
            | [] -> fail "last: empty list"
            | l -> Some(List.last l)
        | "butfirst", [ v ] ->
            match v, itemsOf v with
            | _, [] -> fail "butfirst: empty list"
            | VList _, _ :: t -> Some(VList t)
            | _, _ :: t -> Some(VWord(t |> List.map (formatValue false) |> String.concat ""))
        | "butlast", [ v ] ->
            match v, itemsOf v with
            | _, [] -> fail "butlast: empty list"
            | VList _, l -> Some(VList(List.take (l.Length - 1) l))
            | _, l -> Some(VWord(List.take (l.Length - 1) l |> List.map (formatValue false) |> String.concat ""))
        | "item", [ i; v ] ->
            let idx = int (Math.Floor(num i))
            let l = itemsOf v
            if idx < 1 || idx > l.Length then
                fail (sprintf "item: index %d out of range (1-%d)" idx l.Length)
            Some(List.item (idx - 1) l)
        | "count", [ v ] -> Some(VNum(float (List.length (itemsOf v))))
        | "emptyp", [ v ] -> Some(mkBool (List.isEmpty (itemsOf v)))
        | _ -> fail (sprintf "'%s' was called with wrong inputs" prim)

// ---------------------------------------------------------------------------
// Einstieg
// ---------------------------------------------------------------------------

/// Trennt `to ... end`-Definitionen vom Hauptprogramm.
let private splitProgram (toks: Token[]) : ProcDef list * Token[] =
    let procs = ResizeArray<ProcDef>()
    let main = ResizeArray<Token>()
    let mutable i = 0
    while i < toks.Length do
        match toks.[i].Kind with
        | TWord "to" ->
            let startLine = toks.[i].Line
            i <- i + 1
            let name =
                if i < toks.Length then
                    match toks.[i].Kind with
                    | TWord w ->
                        i <- i + 1
                        w
                    | _ -> raise (LogoError(startLine, "Missing procedure name after 'to'"))
                else
                    raise (LogoError(startLine, "Missing procedure name after 'to'"))
            let ps = ResizeArray<string>()
            let mutable collecting = true
            while collecting && i < toks.Length do
                match toks.[i].Kind with
                | TVar v ->
                    ps.Add v
                    i <- i + 1
                | _ -> collecting <- false
            let body = ResizeArray<Token>()
            let mutable closed = false
            while not closed do
                if i >= toks.Length then
                    raise (LogoError(startLine, sprintf "'to %s' is missing the closing 'end'" name))
                match toks.[i].Kind with
                | TWord "end" ->
                    closed <- true
                    i <- i + 1
                | _ ->
                    body.Add toks.[i]
                    i <- i + 1
            procs.Add { Name = name; Params = List.ofSeq ps; Body = body.ToArray() }
        | _ ->
            main.Add toks.[i]
            i <- i + 1
    List.ofSeq procs, main.ToArray()

/// Result of a single REPL command. `Paths` and `Output` are the full state since the last
/// `Session.Run`; `NewOutput` contains only what this command produced.
type ReplResult =
    { Paths: Path[]
      Turtle: TurtleState
      Output: string[]
      NewOutput: string[]
      Error: ErrorInfo option
      Steps: int }

/// Stateful interpreter for the interactive REPL. Unlike `run`, the machine (turtle position,
/// globals and procedures) survives between `Exec` calls.
type Session() =
    let mutable machine = Machine()
    let mutable outputCount = 0

    let execute (source: string) : ErrorInfo option =
        try
            let toks = Lexer.tokenize source
            let defs, main = splitProgram toks
            for d in defs do
                machine.Define d
            machine.Exec main
            None
        with
        | LogoError(line, msg) -> Some { Line = line; Message = msg }
        | ex -> Some { Line = machine.CurrentLine; Message = "Internal error: " + ex.Message }

    member _.Reset() =
        machine <- Machine()
        outputCount <- 0

    /// Resets the machine and runs a complete program.
    member s.Run(source: string) : RunResult =
        s.Reset()
        let error = execute source
        { Paths = machine.Paths
          Turtle = machine.Turtle
          Output = machine.Output
          Error = error
          Steps = machine.Steps }

    /// Executes another command on the existing machine (turtle, variables and procedures persist).
    member _.Exec(source: string) : ReplResult =
        let error = execute source
        let output = machine.Output
        let newOutput =
            if output.Length >= outputCount then output.[outputCount..] else output
        outputCount <- output.Length
        { Paths = machine.Paths
          Turtle = machine.Turtle
          Output = output
          NewOutput = newOutput
          Error = error
          Steps = machine.Steps }

/// Runs a Logo program and returns drawing, output and an optional error.
let run (source: string) : RunResult = Session().Run source

/// Runs a program and records every turtle movement for the step-through viewer.
let runTraced (source: string) : RunResult * StepEvent[] =
    let m = Machine()
    m.EnableTracing ()
    let error =
        try
            let toks = Lexer.tokenize source
            let defs, main = splitProgram toks
            for d in defs do
                m.Define d
            m.Exec main
            None
        with
        | LogoError(line, msg) -> Some { Line = line; Message = msg }
        | ex -> Some { Line = m.CurrentLine; Message = "Internal error: " + ex.Message }
    let result =
        { Paths = m.Paths
          Turtle = m.Turtle
          Output = m.Output
          Error = error
          Steps = m.Steps }
    result, m.Trace
