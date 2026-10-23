/// Raum-IDs und die lokale Liste „meiner“ Räume (localStorage).
/// Die Inhalte der Räume selbst liegen in IndexedDB (siehe editor.js).
module Logo.Client.Rooms

open System
open Fable.Core

[<Import("words", "./words.js")>]
let private words: string[] = jsNative

[<Emit("crypto.getRandomValues(new Uint32Array(1))[0]")>]
let private randomUint32 () : float = jsNative

[<Emit("Date.now()")>]
let private now () : float = jsNative

[<Emit("(() => { try { return localStorage.getItem($0) } catch { return null } })()")>]
let private getItem (key: string) : string = jsNative

[<Emit("(() => { try { localStorage.setItem($0, $1) } catch { } })()")>]
let private setItem (key: string) (value: string) : unit = jsNative

type Entry = { Id: string; LastOpened: float }

let private storageKey = "logo-rooms"
let private maxRooms = 200
let private maxIdLength = 64

/// Zeichen, die in einer Raum-ID vorkommen dürfen (der Server prüft dasselbe Muster).
let isAllowedChar (c: char) =
    (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c = '-' || c = '_'

let isValid (id: string) =
    id.Length >= 1 && id.Length <= maxIdLength && Seq.forall isAllowedChar id

/// Gleichverteilte Zufallszahl in [0, n) aus dem kryptografischen Zufallsgenerator (ohne Modulo-Verzerrung).
let private randomBelow (n: int) : int =
    let range = 4294967296.0
    let limit = range - (range % float n)
    let rec next () =
        let v = randomUint32 ()
        if v < limit then int (v % float n) else next ()
    next ()

/// Neue, nicht erratbare Raum-ID aus vier zufälligen Wörtern, z. B. `acorn-flip-moss-tulip`
/// (1295 Wörter ^ 4 ≈ 41 Bit). Der Raumname ist das einzige Geheimnis: wer ihn kennt, kommt hinein.
let newId () : string =
    [ for _ in 1..4 -> words.[randomBelow words.Length] ] |> String.concat "-"

/// Macht aus einer Eingabe (Raum-ID oder ganzer Einladungslink) eine gültige Raum-ID.
let parse (input: string) : string option =
    let s = input.Trim()
    let s =
        match s.IndexOf "room=" with
        | -1 -> s
        | i ->
            let rest = s.Substring(i + 5)
            match Seq.tryFindIndex (fun c -> c = '&' || c = '#') rest with
            | Some stop -> rest.Substring(0, stop)
            | None -> rest
    let id = s.Trim().ToLowerInvariant().Replace(' ', '-')
    if isValid id then Some id else None

/// Gespeicherte Räume, zuletzt geöffnete zuerst.
let load () : Entry list =
    let raw = getItem storageKey
    if isNull raw || raw = "" then
        []
    else
        raw.Split(',')
        |> Array.toList
        |> List.choose (fun part ->
            match part.Split(':') with
            | [| id; ts |] when isValid id ->
                match Double.TryParse ts with
                | true, v -> Some { Id = id; LastOpened = v }
                | _ -> None
            | _ -> None)

let private save (entries: Entry list) =
    entries
    |> List.map (fun e -> e.Id + ":" + string e.LastOpened)
    |> String.concat ","
    |> setItem storageKey

/// Merkt sich den Raum als „gerade geöffnet“ (legt ihn bei Bedarf an).
let touch (id: string) : Entry list =
    let entries =
        { Id = id; LastOpened = now () } :: (load () |> List.filter (fun e -> e.Id <> id))
        |> List.truncate maxRooms
    save entries
    entries

/// Entfernt den Raum aus der lokalen Liste.
let forget (id: string) : Entry list =
    let entries = load () |> List.filter (fun e -> e.Id <> id)
    save entries
    entries
