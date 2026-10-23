module Logo.Core.Lexer

open System
open System.Globalization

let private isSpace (c: char) = c = ' ' || c = '\t' || c = '\r' || c = '\n'
let private isDigit (c: char) = c >= '0' && c <= '9'
let private isOpChar (c: char) = c = '+' || c = '-' || c = '*' || c = '/' || c = '%' || c = '=' || c = '<' || c = '>'
let private isDelim (c: char) = isSpace c || c = '[' || c = ']' || c = '(' || c = ')'

/// Kulturunabhängiges Parsen ("." ist immer der Dezimaltrenner).
let tryParseNumber (s: string) : float option =
    match Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, v -> Some v
    | _ -> None

let tokenize (src: string) : Token[] =
    let toks = ResizeArray<Token>()
    let n = src.Length
    let mutable i = 0
    let mutable line = 1

    while i < n do
        let c = src.[i]

        if c = '\n' then
            line <- line + 1
            i <- i + 1
        elif isSpace c then
            i <- i + 1
        elif c = ';' then
            while i < n && src.[i] <> '\n' do
                i <- i + 1
        elif c = '[' then
            toks.Add { Kind = TLBracket; Line = line }
            i <- i + 1
        elif c = ']' then
            toks.Add { Kind = TRBracket; Line = line }
            i <- i + 1
        elif c = '(' || c = ')' then
            toks.Add { Kind = TOp(string c); Line = line }
            i <- i + 1
        elif c = '<' || c = '>' then
            let two = if i + 1 < n then src.Substring(i, 2) else ""
            if two = "<=" || two = ">=" || two = "<>" then
                toks.Add { Kind = TOp two; Line = line }
                i <- i + 2
            else
                toks.Add { Kind = TOp(string c); Line = line }
                i <- i + 1
        elif isOpChar c then
            // "10 -5" (Leerzeichen davor, keins dahinter) sind zwei Werte, "10 - 5" und "10-5" eine Subtraktion.
            // Das Präfix-Minus wird als TOp "~" markiert und nur am Anfang eines Ausdrucks als Vorzeichen gelesen.
            let prefixMinus =
                c = '-'
                && (i = 0 || isSpace src.[i - 1])
                && i + 1 < n
                && not (isSpace src.[i + 1])
            toks.Add { Kind = TOp(if prefixMinus then "~" else string c); Line = line }
            i <- i + 1
        elif c = '"' then
            let start = i + 1
            i <- i + 1
            while i < n && not (isDelim src.[i]) do
                i <- i + 1
            toks.Add { Kind = TQuoted(src.Substring(start, i - start)); Line = line }
        elif c = ':' then
            let start = i + 1
            i <- i + 1
            while i < n && not (isDelim src.[i]) && not (isOpChar src.[i]) && src.[i] <> ';' do
                i <- i + 1
            let name = src.Substring(start, i - start).ToLowerInvariant()
            if name = "" then
                raise (LogoError(line, "Nach ':' fehlt ein Variablenname"))
            toks.Add { Kind = TVar name; Line = line }
        elif isDigit c || (c = '.' && i + 1 < n && isDigit src.[i + 1]) then
            let start = i
            while i < n && (isDigit src.[i] || src.[i] = '.') do
                i <- i + 1
            let text = src.Substring(start, i - start)
            match tryParseNumber text with
            | Some v -> toks.Add { Kind = TNumber v; Line = line }
            | None -> raise (LogoError(line, sprintf "'%s' ist keine gültige Zahl" text))
        else
            let start = i
            while i < n
                  && not (isDelim src.[i])
                  && not (isOpChar src.[i])
                  && src.[i] <> ';'
                  && src.[i] <> '"' do
                i <- i + 1
            toks.Add { Kind = TWord(src.Substring(start, i - start).ToLowerInvariant()); Line = line }

    toks.ToArray()
