module Tests

open System
open Xunit
open Logo.Core
open Logo.Core.Interpreter

let private close (expected: float) (actual: float) =
    Assert.True(abs (expected - actual) < 1e-6, sprintf "expected %f, was %f" expected actual)

let private ok (src: string) =
    let r = run src
    Assert.True(r.Error.IsNone, sprintf "Unexpected error: %A" r.Error)
    r

let private err (src: string) =
    let r = run src
    Assert.True(r.Error.IsSome, "Error expected")
    r.Error.Value

[<Fact>]
let ``forward draws a line upwards`` () =
    let r = ok "fd 100"
    Assert.Equal(1, r.Paths.Length)
    let (x, y, z) = r.Paths.[0].Points.[1]
    close 0.0 x
    close 100.0 y
    close 0.0 z

[<Fact>]
let ``right turns clockwise`` () =
    let r = ok "rt 90 fd 50"
    close 50.0 r.Turtle.X
    close 0.0 r.Turtle.Y
    close 90.0 r.Turtle.Heading

[<Fact>]
let ``repeat draws a square and returns`` () =
    let r = ok "repeat 4 [fd 100 rt 90]"
    close 0.0 r.Turtle.X
    close 0.0 r.Turtle.Y
    Assert.Equal(1, r.Paths.Length)
    Assert.Equal(5, r.Paths.[0].Points.Length)

[<Fact>]
let ``penup breaks the line`` () =
    let r = ok "fd 10 pu fd 10 pd fd 10"
    Assert.Equal(2, r.Paths.Length)

[<Fact>]
let ``procedures with parameters and recursion`` () =
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
let ``procedures may be called before their definition`` () =
    let r = ok "foo\nto foo\n fd 10\nend"
    close 10.0 r.Turtle.Y

[<Fact>]
let ``output returns values from procedures`` () =
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
let ``infix arithmetic and parentheses`` () =
    let r = ok "print (2 + 3) * 4 print 10 - 2 - 3 print 2 + 3 * 4 print 7 % 3"
    Assert.Equal<string[]>([| "20"; "5"; "14"; "1" |], r.Output)

[<Fact>]
let ``unary minus`` () =
    let r = ok "print -5 + 2 print 3 * -2"
    Assert.Equal<string[]>([| "-3"; "-6" |], r.Output)


[<Fact>]
let ``negative arguments after a value`` () =
    let r = ok "setxy 10 -20 print xcor print ycor print 10-5 print 10 - 5"
    Assert.Equal<string[]>([| "10"; "-20"; "5"; "5" |], r.Output)

[<Fact>]
let ``negative numbers in lists`` () =
    let r = ok "make \"p [3 -4] print item 2 :p repeat 1 [setxy 0 -200] print ycor"
    Assert.Equal<string[]>([| "-4"; "-200" |], r.Output)

[<Fact>]
let ``comparisons and ifelse`` () =
    let r = ok "ifelse 3 > 2 [print \"yes] [print \"no] if 1 = 2 [print \"nie]"
    Assert.Equal<string[]>([| "yes" |], r.Output)

[<Fact>]
let ``make and variables`` () =
    let r = ok "make \"x 5 make \"x :x + 1 print :x"
    Assert.Equal<string[]>([| "6" |], r.Output)

[<Fact>]
let ``for loop with step size`` () =
    let r = ok "for [i 1 10 3] [print :i]"
    Assert.Equal<string[]>([| "1"; "4"; "7"; "10" |], r.Output)

[<Fact>]
let ``for loop backwards`` () =
    let r = ok "for [i 3 1] [print :i]"
    Assert.Equal<string[]>([| "3"; "2"; "1" |], r.Output)

[<Fact>]
let ``while with a condition list`` () =
    let r = ok "make \"i 0 while [:i < 3] [print :i make \"i :i + 1]"
    Assert.Equal<string[]>([| "0"; "1"; "2" |], r.Output)

[<Fact>]
let ``repcount counts along`` () =
    let r = ok "repeat 3 [print repcount]"
    Assert.Equal<string[]>([| "1"; "2"; "3" |], r.Output)

[<Fact>]
let ``list operations`` () =
    let r = ok "make \"l [a b c] print first :l print last :l print count :l print item 2 :l print bf :l show fput \"x :l"
    Assert.Equal<string[]>([| "a"; "c"; "3"; "b"; "b c"; "[x a b c]" |], r.Output)

[<Fact>]
let ``colors`` () =
    let r = ok "setpc \"red fd 10 setpc [0 0 100] fd 10 setpc 2 fd 10"
    Assert.Equal(3, r.Paths.Length)
    Assert.Equal("rgb(0,0,255)", r.Paths.[1].Color)

[<Fact>]
let ``arc draws a circular arc`` () =
    let r = ok "arc 360 50"
    Assert.Equal(1, r.Paths.Length)
    let (x0, y0, z0) = r.Paths.[0].Points.[0]
    let (x1, y1, z1) = r.Paths.[0].Points |> Array.last
    close x0 x1
    close y0 y1
    close z0 z1

[<Fact>]
let ``comments are ignored`` () =
    let r = ok "fd 10 ; this is a comment\nfd 10"
    close 20.0 r.Turtle.Y

[<Fact>]
let ``infinite loop is aborted`` () =
    let e = err "while [true] [fd 0]"
    Assert.Contains("Too many", e.Message)

[<Fact>]
let ``infinite recursion is aborted`` () =
    let e = err "to f\n f\nend\nf"
    Assert.Contains("Recursion", e.Message)

[<Fact>]
let ``errors carry the line number`` () =
    let e = err "fd 10\nfd 10\nunbekannt 5"
    Assert.Equal(3, e.Line)

[<Fact>]
let ``partial result is kept on error`` () =
    let r = run "fd 10\nfoo"
    Assert.True(r.Error.IsSome)
    Assert.Equal(1, r.Paths.Length)

[<Fact>]
let ``missing input is an error`` () =
    let e = err "fd"
    Assert.Contains("fd", e.Message)

[<Fact>]
let ``unused value is an error`` () =
    let e = err "sum 1 2"
    Assert.Contains("sum", e.Message)

[<Fact>]
let ``division by zero`` () =
    let e = err "print 1 / 0"
    Assert.Contains("Division", e.Message)

[<Fact>]
let ``numbers are parsed culture-independently`` () =
    let old = Globalization.CultureInfo.CurrentCulture
    try
        Globalization.CultureInfo.CurrentCulture <- Globalization.CultureInfo("de-DE")
        let r = ok "print 1.5 + 1"
        Assert.Equal<string[]>([| "2.5" |], r.Output)
    finally
        Globalization.CultureInfo.CurrentCulture <- old

[<Fact>]
let ``clearscreen clears the drawing`` () =
    let r = ok "fd 10 cs fd 5"
    Assert.Equal(1, r.Paths.Length)
    close 5.0 r.Turtle.Y

[<Fact>]
let ``all example programs run without errors and draw something`` () =
    for (name, code) in Logo.Client.Examples.all do
        let r = run code
        Assert.True(r.Error.IsNone, sprintf "Example '%s': %A" name r.Error)
        Assert.True(r.Paths.Length > 0, sprintf "Example '%s' draws nothing" name)
        Assert.True(r.Steps < 100000, sprintf "Example '%s' needs too many steps: %d" name r.Steps)

// ---------------------------------------------------------------------------
// 3D
// ---------------------------------------------------------------------------

let private allPoints (r: RunResult) = r.Paths |> Array.collect (fun p -> p.Points)

[<Fact>]
let ``2D programs stay flat (z = 0, pitch 0)`` () =
    let r = ok "repeat 36 [fd 20 rt 10] lt 33 bk 7"
    Assert.All(allPoints r, fun (_, _, z) -> close 0.0 z)
    close 0.0 r.Turtle.Z
    close 0.0 r.Turtle.Pitch
    let (_, _, uz) = r.Turtle.Up
    close 1.0 uz

[<Fact>]
let ``heading is exactly 0 after full turns`` () =
    let r = ok "repeat 4 [rt 90] print heading rt 45 print heading lt 90 print heading"
    Assert.Equal<string[]>([| "0"; "45"; "315" |], r.Output)

[<Fact>]
let ``setheading levels the turtle`` () =
    let r = ok "pitchup 40 rollright 20 setheading 90 print heading print pitch fd 10"
    Assert.Equal<string[]>([| "90"; "0" |], r.Output)
    close 10.0 r.Turtle.X
    close 0.0 r.Turtle.Z

[<Fact>]
let ``pitchup 90 moves straight up`` () =
    let r = ok "pitchup 90 fd 100"
    close 0.0 r.Turtle.X
    close 0.0 r.Turtle.Y
    close 100.0 r.Turtle.Z
    close 90.0 r.Turtle.Pitch

[<Fact>]
let ``pitchdown moves down`` () =
    let r = ok "pitchdown 30 fd 10 print pitch"
    close (-5.0) r.Turtle.Z
    Assert.Equal<string[]>([| "-30" |], r.Output)

[<Fact>]
let ``square in the vertical plane`` () =
    let r = ok "pitchup 90 repeat 4 [fd 100 rt 90]"
    let pts = allPoints r
    Assert.All(pts, fun (_, y, _) -> close 0.0 y)
    Assert.Equal(100.0, pts |> Array.map (fun (_, _, z) -> z) |> Array.max, 6)
    Assert.Equal(100.0, pts |> Array.map (fun (x, _, _) -> x) |> Array.max, 6)
    close 0.0 r.Turtle.X
    close 0.0 r.Turtle.Z

[<Fact>]
let ``rollright rotates the turtle around its heading`` () =
    // nach Rollen um 90 Grad zeigt "rechts" nach unten (-z)
    let r = ok "rollright 90 rt 90 fd 10"
    close 0.0 r.Turtle.X
    close 0.0 r.Turtle.Y
    close (-10.0) r.Turtle.Z

[<Fact>]
let ``orientation stays normalized after many turns`` () =
    let r = ok "repeat 5000 [pitchup 7 rollright 11 rt 13 fd 1]"
    let (fx, fy, fz) = r.Turtle.Forward
    let (ux, uy, uz) = r.Turtle.Up
    close 1.0 (fx * fx + fy * fy + fz * fz)
    close 1.0 (ux * ux + uy * uy + uz * uz)
    close 0.0 (fx * ux + fy * uy + fz * uz)

[<Fact>]
let ``setxyz and zcor`` () =
    let r = ok "setxyz 1 2 3 print zcor setz 9 print zcor setxy 5 5 print zcor"
    Assert.Equal<string[]>([| "3"; "9"; "9" |], r.Output)

[<Fact>]
let ``home also resets the orientation`` () =
    let r = ok "pitchup 50 rt 20 fd 10 home print heading print pitch"
    close 0.0 r.Turtle.Z
    Assert.Equal<string[]>([| "0"; "0" |], r.Output)

[<Fact>]
let ``arc in 3D lies in the plane of heading and right`` () =
    // oriented vertically: the arc lies in the xz plane
    let r = ok "pitchup 90 arc 360 50"
    let pts = allPoints r
    Assert.All(pts, fun (_, y, _) -> close 0.0 y)
    Assert.Equal(50.0, pts |> Array.map (fun (_, _, z) -> z) |> Array.max, 6)

// ---------------------------------------------------------------------------
// Randomness: deterministic so that all contributors see the same drawing
// ---------------------------------------------------------------------------

[<Fact>]
let ``random is deterministic`` () =
    let src = "repeat 20 [print random 1000]"
    let a = (ok src).Output
    let b = (ok src).Output
    Assert.Equal<string[]>(a, b)
    Assert.True((a |> Array.distinct).Length > 10, "Random numbers should differ")

[<Fact>]
let ``random is in range`` () =
    let r = ok "repeat 500 [print random 6]"
    for s in r.Output do
        let n = int s
        Assert.InRange(n, 0, 5)
    Assert.Equal(6, (r.Output |> Array.distinct).Length)

[<Fact>]
let ``rerandom sets the seed`` () =
    let a = (ok "rerandom 7 print random 1000000000 print random 1000000000").Output
    let b = (ok "rerandom 7 print random 1000000000 print random 1000000000").Output
    let c = (ok "rerandom 8 print random 1000000000 print random 1000000000").Output
    Assert.Equal<string[]>(a, b)
    Assert.NotEqual<string[]>(a, c)

[<Fact>]
let ``Koch snowflake runs through`` () =
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

// ---------------------------------------------------------------------------
// Step trace
// ---------------------------------------------------------------------------

[<Fact>]
let ``runTraced records movements with source lines`` () =
    let result, events = runTraced "fd 10\nfd 10"
    Assert.True(result.Error.IsNone)
    Assert.Equal(2, events.Length)
    Assert.Equal(1, events.[0].Line)
    Assert.Equal(2, events.[1].Line)
    Assert.Equal(2, events.[0].PointCount)
    Assert.Equal(3, events.[1].PointCount)
    close 20.0 events.[1].Turtle.Y

[<Fact>]
let ``runTraced records pen-up moves without drawing`` () =
    let result, events = runTraced "pu fd 10 pd fd 10"
    Assert.True(result.Error.IsNone)
    Assert.Equal(2, events.Length)
    Assert.False(events.[0].Turtle.PenDown)
    Assert.Equal(0, events.[0].PointCount)
    Assert.Equal(2, events.[1].PointCount)
    close 20.0 events.[1].Turtle.Y

[<Fact>]
let ``runTraced records arcs as one move`` () =
    let result, events = runTraced "arc 360 50"
    Assert.True(result.Error.IsNone)
    Assert.Equal(1, events.Length)
    Assert.Equal(1, result.Paths.Length)
    Assert.True(events.[0].PointCount > 2)

[<Fact>]
let ``runTraced keeps the moves before an error`` () =
    let result, events = runTraced "fd 10\nunknown 5"
    Assert.True(result.Error.IsSome)
    Assert.Equal(1, events.Length)

[<Fact>]
let ``runTraced starts over after clearscreen`` () =
    let result, events = runTraced "fd 10\nclearscreen\nfd 10"
    Assert.True(result.Error.IsNone)
    Assert.Equal(1, events.Length)
    Assert.Equal(1, result.Paths.Length)
    close 10.0 events.[0].Turtle.Y


[<Fact>]
let ``session keeps turtle state between commands`` () =
    let s = Session()
    s.Run "fd 10" |> ignore
    let r = s.Exec "fd 10"
    Assert.True(r.Error.IsNone)
    close 20.0 r.Turtle.Y
    Assert.Equal(2, r.Paths.Length)

[<Fact>]
let ``session keeps variables and procedures`` () =
    let s = Session()
    let def = s.Exec "to square :n\n  repeat 4 [fd :n rt 90]\nend\nmake \"n 5"
    Assert.True(def.Error.IsNone)
    let r = s.Exec "square :n"
    Assert.True(r.Error.IsNone)
    Assert.True(r.Paths.Length > 0)

[<Fact>]
let ``session reports only the new output`` () =
    let s = Session()
    let a = s.Exec "print 1"
    let b = s.Exec "print 2"
    Assert.Equal<string[]>([| "1" |], a.NewOutput)
    Assert.Equal<string[]>([| "2" |], b.NewOutput)
    Assert.Equal<string[]>([| "1"; "2" |], b.Output)

[<Fact>]
let ``session recovers from an error`` () =
    let s = Session()
    let bad = s.Exec "unknown 5"
    Assert.True(bad.Error.IsSome)
    let good = s.Exec "fd 10"
    Assert.True(good.Error.IsNone)
    close 10.0 good.Turtle.Y

[<Fact>]
let ``session run resets state`` () =
    let s = Session()
    s.Exec "fd 10" |> ignore
    let r = s.Run "fd 5"
    close 5.0 r.Turtle.Y
    Assert.Equal(1, r.Paths.Length)
    Assert.Empty(r.Output)

[<Fact>]
let ``session handles clearscreen`` () =
    let s = Session()
    s.Exec "fd 10" |> ignore
    let r = s.Exec "clearscreen"
    Assert.True(r.Error.IsNone)
    Assert.Empty(r.Paths)
