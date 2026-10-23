namespace Logo.Core

type TokenKind =
    | TNumber of float
    | TWord of string
    | TQuoted of string
    | TVar of string
    | TOp of string
    | TLBracket
    | TRBracket

type Token = { Kind: TokenKind; Line: int }

type Value =
    | VNum of float
    | VWord of string
    | VList of Value list

/// Fehler bei Lexen/Parsen/Ausführen. `line` ist 1-basiert (0 = unbekannt).
exception LogoError of line: int * message: string
