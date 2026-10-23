module Tests

open System
open Xunit
open Logo.Core
open Logo.Core.Interpreter

let private close (expected: float) (actual: float) =
    Assert.True(abs (expected - actual) < 1e-6, sprintf "erwartet %f, war %f" expected actual)

let private ok (src: string) =
    let r = run src
    Assert.True(r.Error.IsNone, sprintf "Unerwarteter Fehler: %A" r.Error)
    r

let private err (src: string) =
    let r = run src
    Assert.True(r.Error.IsSome, "Fehler erwartet")
    r.Error.Value

[<Fact>]
let ``forward zeichnet eine Linie nach oben`` () =
    let r = ok "fd 100"
    Assert.Equal(1, r.Paths.Length)
    let (x, y, z) = r.Paths.[0].Points.[1]
    close 0.0 x
    close 100.0 y
    close 0.0 z

[<Fact>]
let ``right dreht im Uhrzeigersinn`` () =
    let r = ok "rt 90 fd 50"
    close 50.0 r.Turtle.X
    close 0.0 r.Turtle.Y
    close 90.0 r.Turtle.Heading

[<Fact>]
let ``repeat zeichnet ein Quadrat und kehrt zurueck`` () =
    let r = ok "repeat 4 [fd 100 rt 90]"
    close 0.0 r.Turtle.X
    close 0.0 r.Turtle.Y
    Assert.Equal(1, r.Paths.Length)
    Assert.Equal(5, r.Paths.[0].Points.Length)

[<Fact>]
let ``penup unterbricht den Linienzug`` () =
    let r = ok "fd 10 pu fd 10 pd fd 10"
    Assert.Equal(2, r.Paths.Length)

[<Fact>]
let ``Prozeduren mit Parametern und Rekursion`` () =
    let r =
        ok
            """
to square :size
  repeat 4 [fd :size rt 90]
end
to spiral :size
  if :size > 50 [stop]
  square :size
  rt 10
  spiral :size + 10
end
spiral 10
"""
    Assert.True(r.Paths.Length > 0)

[<Fact>]
let ``Prozeduren duerfen vor der Definition aufgerufen werden`` () =
    let r = ok "foo\nto foo\n fd 10\nend"
    close 10.0 r.Turtle.Y

[<Fact>]
let ``output liefert Werte aus Prozeduren`` () =
    let r =
        ok
            """
to double :n
  output :n * 2
end
print double 21
"""
    Assert.Equal<string[]>([| "42" |], r.Output)

[<Fact>]
let ``Infix-Rechnung und Klammern`` () =
    let r = ok "print (2 + 3) * 4 print 10 - 2 - 3 print 2 + 3 * 4 print 7 % 3"
    Assert.Equal<string[]>([| "20"; "5"; "14"; "1" |], r.Output)

[<Fact>]
let ``unaeres Minus`` () =
    let r = ok "print -5 + 2 print 3 * -2"
    Assert.Equal<string[]>([| "-3"; "-6" |], r.Output)


[<Fact>]
let ``negative Argumente nach einem Wert`` () =
    let r = ok "setxy 10 -20 print xcor print ycor print 10-5 print 10 - 5"
    Assert.Equal<string[]>([| "10"; "-20"; "5"; "5" |], r.Output)

[<Fact>]
let ``negative Zahlen in Listen`` () =
    let r = ok "make \"p [3 -4] print item 2 :p repeat 1 [setxy 0 -200] print ycor"
    Assert.Equal<string[]>([| "-4"; "-200" |], r.Output)

[<Fact>]
let ``Vergleiche und ifelse`` () =
    let r = ok "ifelse 3 > 2 [print \"ja] [print \"nein] if 1 = 2 [print \"nie]"
    Assert.Equal<string[]>([| "ja" |], r.Output)

[<Fact>]
let ``make und Variablen`` () =
    let r = ok "make \"x 5 make \"x :x + 1 print :x"
    Assert.Equal<string[]>([| "6" |], r.Output)

[<Fact>]
let ``for-Schleife mit Schrittweite`` () =
    let r = ok "for [i 1 10 3] [print :i]"
    Assert.Equal<string[]>([| "1"; "4"; "7"; "10" |], r.Output)

[<Fact>]
let ``for-Schleife rueckwaerts`` () =
    let r = ok "for [i 3 1] [print :i]"
    Assert.Equal<string[]>([| "3"; "2"; "1" |], r.Output)

[<Fact>]
let ``while mit Bedingungsliste`` () =
    let r = ok "make \"i 0 while [:i < 3] [print :i make \"i :i + 1]"
    Assert.Equal<string[]>([| "0"; "1"; "2" |], r.Output)

[<Fact>]
let ``repcount zaehlt mit`` () =
    let r = ok "repeat 3 [print repcount]"
    Assert.Equal<string[]>([| "1"; "2"; "3" |], r.Output)

[<Fact>]
let ``Listenoperationen`` () =
    let r = ok "make \"l [a b c] print first :l print last :l print count :l print item 2 :l print bf :l show fput \"x :l"
    Assert.Equal<string[]>([| "a"; "c"; "3"; "b"; "b c"; "[x a b c]" |], r.Output)

[<Fact>]
let ``Farben`` () =
    let r = ok "setpc \"red fd 10 setpc [0 0 100] fd 10 setpc 2 fd 10"
    Assert.Equal(3, r.Paths.Length)
    Assert.Equal("rgb(0,0,255)", r.Paths.[1].Color)

[<Fact>]
let ``arc zeichnet einen Kreisbogen`` () =
    let r = ok "arc 360 50"
    Assert.Equal(1, r.Paths.Length)
    let (x0, y0, z0) = r.Paths.[0].Points.[0]
    let (x1, y1, z1) = r.Paths.[0].Points |> Array.last
    close x0 x1
    close y0 y1
    close z0 z1

[<Fact>]
let ``Kommentare werden ignoriert`` () =
    let r = ok "fd 10 ; das ist ein Kommentar\nfd 10"
    close 20.0 r.Turtle.Y

[<Fact>]
let ``Endlosschleife wird abgebrochen`` () =
    let e = err "while [true] [fd 0]"
    Assert.Contains("Zu viele", e.Message)

[<Fact>]
let ``Endlosrekursion wird abgebrochen`` () =
    let e = err "to f\n f\nend\nf"
    Assert.Contains("Rekursion", e.Message)

[<Fact>]
let ``Fehler tragen die Zeilennummer`` () =
    let e = err "fd 10\nfd 10\nunbekannt 5"
    Assert.Equal(3, e.Line)

[<Fact>]
let ``Teilergebnis bleibt bei Fehler erhalten`` () =
    let r = run "fd 10\nfoo"
    Assert.True(r.Error.IsSome)
    Assert.Equal(1, r.Paths.Length)

[<Fact>]
let ``fehlende Eingabe ist ein Fehler`` () =
    let e = err "fd"
    Assert.Contains("fd", e.Message)

[<Fact>]
let ``nicht verwendeter Wert ist ein Fehler`` () =
    let e = err "sum 1 2"
    Assert.Contains("sum", e.Message)

[<Fact>]
let ``Division durch null`` () =
    let e = err "print 1 / 0"
    Assert.Contains("Division", e.Message)

[<Fact>]
let ``Zahlen werden kulturunabhaengig geparst`` () =
    let old = Globalization.CultureInfo.CurrentCulture
    try
        Globalization.CultureInfo.CurrentCulture <- Globalization.CultureInfo("de-DE")
        let r = ok "print 1.5 + 1"
        Assert.Equal<string[]>([| "2.5" |], r.Output)
    finally
        Globalization.CultureInfo.CurrentCulture <- old

[<Fact>]
let ``clearscreen loescht die Zeichnung`` () =
    let r = ok "fd 10 cs fd 5"
    Assert.Equal(1, r.Paths.Length)
    close 5.0 r.Turtle.Y

[<Fact>]
let ``alle Beispielprogramme laufen fehlerfrei und zeichnen etwas`` () =
    for (name, code) in Logo.Client.Examples.all do
        let r = run code
        Assert.True(r.Error.IsNone, sprintf "Beispiel '%s': %A" name r.Error)
        Assert.True(r.Paths.Length > 0, sprintf "Beispiel '%s' zeichnet nichts" name)
        Assert.True(r.Steps < 100000, sprintf "Beispiel '%s' braucht zu viele Schritte: %d" name r.Steps)

// ---------------------------------------------------------------------------
// 3D
// ---------------------------------------------------------------------------

let private allPoints (r: RunResult) = r.Paths |> Array.collect (fun p -> p.Points)

[<Fact>]
let ``2D-Programme bleiben flach (z = 0, Neigung 0)`` () =
    let r = ok "repeat 36 [fd 20 rt 10] lt 33 bk 7"
    Assert.All(allPoints r, fun (_, _, z) -> close 0.0 z)
    close 0.0 r.Turtle.Z
    close 0.0 r.Turtle.Pitch
    let (_, _, uz) = r.Turtle.Up
    close 1.0 uz

[<Fact>]
let ``heading ist nach vollen Drehungen exakt 0`` () =
    let r = ok "repeat 4 [rt 90] print heading rt 45 print heading lt 90 print heading"
    Assert.Equal<string[]>([| "0"; "45"; "315" |], r.Output)

[<Fact>]
let ``setheading richtet waagerecht aus`` () =
    let r = ok "pitchup 40 rollright 20 setheading 90 print heading print pitch fd 10"
    Assert.Equal<string[]>([| "90"; "0" |], r.Output)
    close 10.0 r.Turtle.X
    close 0.0 r.Turtle.Z

[<Fact>]
let ``pitchup 90 laeuft senkrecht nach oben`` () =
    let r = ok "pitchup 90 fd 100"
    close 0.0 r.Turtle.X
    close 0.0 r.Turtle.Y
    close 100.0 r.Turtle.Z
    close 90.0 r.Turtle.Pitch

[<Fact>]
let ``pitchdown geht nach unten`` () =
    let r = ok "pitchdown 30 fd 10 print pitch"
    close (-5.0) r.Turtle.Z
    Assert.Equal<string[]>([| "-30" |], r.Output)

[<Fact>]
let ``Quadrat in der senkrechten Ebene`` () =
    let r = ok "pitchup 90 repeat 4 [fd 100 rt 90]"
    let pts = allPoints r
    Assert.All(pts, fun (_, y, _) -> close 0.0 y)
    Assert.Equal(100.0, pts |> Array.map (fun (_, _, z) -> z) |> Array.max, 6)
    Assert.Equal(100.0, pts |> Array.map (fun (x, _, _) -> x) |> Array.max, 6)
    close 0.0 r.Turtle.X
    close 0.0 r.Turtle.Z

[<Fact>]
let ``rollright dreht die Turtle um ihre Blickrichtung`` () =
    // nach Rollen um 90 Grad zeigt "rechts" nach unten (-z)
    let r = ok "rollright 90 rt 90 fd 10"
    close 0.0 r.Turtle.X
    close 0.0 r.Turtle.Y
    close (-10.0) r.Turtle.Z

[<Fact>]
let ``Orientierung bleibt nach vielen Drehungen normiert`` () =
    let r = ok "repeat 5000 [pitchup 7 rollright 11 rt 13 fd 1]"
    let (fx, fy, fz) = r.Turtle.Forward
    let (ux, uy, uz) = r.Turtle.Up
    close 1.0 (fx * fx + fy * fy + fz * fz)
    close 1.0 (ux * ux + uy * uy + uz * uz)
    close 0.0 (fx * ux + fy * uy + fz * uz)

[<Fact>]
let ``setxyz und zcor`` () =
    let r = ok "setxyz 1 2 3 print zcor setz 9 print zcor setxy 5 5 print zcor"
    Assert.Equal<string[]>([| "3"; "9"; "9" |], r.Output)

[<Fact>]
let ``home setzt auch die Orientierung zurueck`` () =
    let r = ok "pitchup 50 rt 20 fd 10 home print heading print pitch"
    close 0.0 r.Turtle.Z
    Assert.Equal<string[]>([| "0"; "0" |], r.Output)

[<Fact>]
let ``arc in 3D liegt in der Ebene aus Blickrichtung und rechts`` () =
    // senkrecht ausgerichtet: der Bogen liegt in der xz-Ebene
    let r = ok "pitchup 90 arc 360 50"
    let pts = allPoints r
    Assert.All(pts, fun (_, y, _) -> close 0.0 y)
    Assert.Equal(50.0, pts |> Array.map (fun (_, _, z) -> z) |> Array.max, 6)

// ---------------------------------------------------------------------------
// Zufall: deterministisch, damit alle Mitwirkenden dieselbe Zeichnung sehen
// ---------------------------------------------------------------------------

[<Fact>]
let ``random ist deterministisch`` () =
    let src = "repeat 20 [print random 1000]"
    let a = (ok src).Output
    let b = (ok src).Output
    Assert.Equal<string[]>(a, b)
    Assert.True((a |> Array.distinct).Length > 10, "Zufallszahlen sollten sich unterscheiden")

[<Fact>]
let ``random liegt im Bereich`` () =
    let r = ok "repeat 500 [print random 6]"
    for s in r.Output do
        let n = int s
        Assert.InRange(n, 0, 5)
    Assert.Equal(6, (r.Output |> Array.distinct).Length)

[<Fact>]
let ``rerandom setzt den Startwert`` () =
    let a = (ok "rerandom 7 print random 1000000000 print random 1000000000").Output
    let b = (ok "rerandom 7 print random 1000000000 print random 1000000000").Output
    let c = (ok "rerandom 8 print random 1000000000 print random 1000000000").Output
    Assert.Equal<string[]>(a, b)
    Assert.NotEqual<string[]>(a, c)

[<Fact>]
let ``Koch-Schneeflocke laeuft durch`` () =
    let r =
        ok
            """
to koch :n :s
  if :n = 0 [fd :s stop]
  koch :n - 1 :s / 3
  lt 60
  koch :n - 1 :s / 3
  rt 120
  koch :n - 1 :s / 3
  lt 60
  koch :n - 1 :s / 3
end
repeat 3 [koch 3 200 rt 120]
"""
    Assert.True(r.Paths.Length > 0)
