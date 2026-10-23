/// Minimaler, y-websocket-kompatibler Relay-Server.
///
/// Der Server kennt keine Yjs-Dokumente, sondern verteilt nur die binären Nachrichten
/// ("sync" und "awareness") zwischen allen Clients eines Raums. Damit Neuankömmlinge auch dann
/// den aktuellen Stand bekommen, wenn sonst niemand mehr online ist, merkt er sich alle
/// Dokument-Updates eines Raums im Arbeitsspeicher und spielt sie beim Verbinden ab
/// (Yjs-Updates sind idempotent). Zusätzlich führt er eine Tabelle der Awareness-Zustände
/// (Name, Farbe, Cursor), um sie Neuankömmlingen zu schicken und beim Verlassen eines Clients
/// zu entfernen. Es gibt bewusst keine Persistenz.
module Logo.Server.YjsRelay

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Net.WebSockets
open System.Text
open System.Threading
open System.Threading.Tasks

// y-websocket-Protokoll
[<Literal>]
let private MessageSync = 0uy

[<Literal>]
let private MessageAwareness = 1uy

[<Literal>]
let private MessageQueryAwareness = 3uy

[<Literal>]
let private SyncStep1 = 0uy

[<Literal>]
let private SyncStep2 = 1uy

[<Literal>]
let private SyncUpdate = 2uy

/// Antwort auf SyncStep1: SyncStep2 mit leerem Update. Der Client gilt damit als "synced".
let private emptyStep2 = [| MessageSync; SyncStep2; 2uy; 0uy; 0uy |]

let private maxMessageBytes = 2 * 1024 * 1024
let private maxLogBytes = 8 * 1024 * 1024

// ---------------------------------------------------------------------------
// lib0-Encoding (nur was für Awareness-Nachrichten nötig ist)
// ---------------------------------------------------------------------------

type private Reader(data: byte[]) =
    let mutable pos = 0

    member _.VarUint() : uint64 =
        let mutable num = 0UL
        let mutable mult = 1UL
        let mutable go = true
        while go do
            if pos >= data.Length then
                failwith "unexpected end of message"
            let b = data.[pos]
            pos <- pos + 1
            num <- num + uint64 (b &&& 0x7Fuy) * mult
            mult <- mult * 128UL
            go <- b >= 0x80uy
        num

    member r.VarString() : string =
        let len = int (r.VarUint())
        if pos + len > data.Length then
            failwith "unexpected end of message"
        let s = Encoding.UTF8.GetString(data, pos, len)
        pos <- pos + len
        s

let private writeVarUint (ms: MemoryStream) (value: uint64) =
    let mutable v = value
    while v > 127UL do
        ms.WriteByte(byte (128UL ||| (v &&& 127UL)))
        v <- v / 128UL
    ms.WriteByte(byte v)

let private writeVarString (ms: MemoryStream) (s: string) =
    let bytes = Encoding.UTF8.GetBytes s
    writeVarUint ms (uint64 bytes.Length)
    ms.Write(bytes, 0, bytes.Length)

/// Ein Eintrag einer Awareness-Nachricht. `State` ist JSON; "null" bedeutet "Client entfernt".
type private AwarenessEntry =
    { ClientId: uint64
      Clock: uint64
      State: string }

let private parseAwareness (message: byte[]) : AwarenessEntry list option =
    try
        let r = Reader message
        r.VarUint() |> ignore // Nachrichtentyp
        r.VarUint() |> ignore // Länge der Nutzlast
        let count = int (r.VarUint())
        Some
            [ for _ in 1..count ->
                  let clientId = r.VarUint()
                  let clock = r.VarUint()
                  let state = r.VarString()
                  { ClientId = clientId; Clock = clock; State = state } ]
    with _ ->
        None

let private buildAwareness (entries: AwarenessEntry list) : byte[] =
    let payload = new MemoryStream()
    writeVarUint payload (uint64 (List.length entries))
    for e in entries do
        writeVarUint payload e.ClientId
        writeVarUint payload e.Clock
        writeVarString payload e.State
    let result = new MemoryStream()
    result.WriteByte MessageAwareness
    writeVarUint result (uint64 payload.Length)
    payload.WriteTo result
    result.ToArray()

// ---------------------------------------------------------------------------
// Räume und Verbindungen
// ---------------------------------------------------------------------------

[<AllowNullLiteral>]
type private Peer(socket: WebSocket) =
    let sendLock = new SemaphoreSlim(1, 1)

    /// Client-IDs, die der Server diesem Client bereits mitgeteilt hat (nur unter dem Raum-Lock benutzen).
    /// Meldet der Client eine solche ID zurück, ist das ein Echo und keine neue Information.
    member val Known = HashSet<uint64>()

    member _.Send(data: byte[]) : Task =
        task {
            do! sendLock.WaitAsync()
            try
                try
                    if socket.State = WebSocketState.Open then
                        do!
                            socket
                                .SendAsync(ReadOnlyMemory<byte>(data), WebSocketMessageType.Binary, true, CancellationToken.None)
                                .AsTask()
                with _ ->
                    () // Verbindung ist weg; der Empfangs-Loop räumt auf
            finally
                sendLock.Release() |> ignore
        }

type private AwarenessRecord =
    { Clock: uint64
      State: string
      Owner: Peer }

type private Room() =
    let peers = ResizeArray<Peer>()
    let updates = ResizeArray<byte[]>()
    /// clientId -> aktueller Awareness-Zustand samt der Verbindung, über die er zuerst kam.
    /// y-websocket-Clients leiten fremde Awareness-Einträge weiter (Echo); als Besitzer gilt
    /// deshalb die Verbindung, die eine Client-ID als erste meldet.
    let awareness = Dictionary<uint64, AwarenessRecord>()
    let mutable logBytes = 0

    /// Aktuelle Awareness-Zustände aller anderen Clients als Nachricht (und merkt sie als "bekannt" für `target`).
    /// Nur unter dem Raum-Lock aufrufen.
    member private _.SnapshotFor(target: Peer) : byte[] option =
        let entries =
            [ for KeyValue(id, r) in awareness do
                  if r.State <> "null" && not (obj.ReferenceEquals(r.Owner, target)) then
                      { ClientId = id; Clock = r.Clock; State = r.State } ]
        for e in entries do
            target.Known.Add e.ClientId |> ignore
        if entries.IsEmpty then None else Some(buildAwareness entries)

    /// Fügt den Client hinzu und liefert, was er nachholen muss: Dokument-Updates und Awareness.
    member m.Join(peer: Peer) : byte[][] * byte[] option =
        lock peers (fun () ->
            let replay = updates.ToArray()
            let snapshot = m.SnapshotFor peer
            peers.Add peer
            replay, snapshot)

    member _.Others(peer: Peer) : Peer[] =
        lock peers (fun () -> [| for p in peers do if not (obj.ReferenceEquals(p, peer)) then p |])

    member m.AwarenessOfOthers(peer: Peer) : byte[] option =
        lock peers (fun () -> m.SnapshotFor peer)

    member _.Record(update: byte[]) =
        lock peers (fun () ->
            if logBytes + update.Length > maxLogBytes then
                // Schutz vor unbegrenztem Wachstum: Verlauf verwerfen, aktive Clients behalten den Stand.
                updates.Clear()
                logBytes <- 0
            updates.Add update
            logBytes <- logBytes + update.Length)

    /// Übernimmt eingehende Awareness-Einträge in die Tabelle und liefert die Empfänger der Weiterleitung.
    member _.RelayAwareness(sender: Peer, entries: AwarenessEntry list) : Peer[] =
        lock peers (fun () ->
            for e in entries do
                match awareness.TryGetValue e.ClientId with
                | true, existing ->
                    let newer = e.Clock > existing.Clock || (e.Clock = existing.Clock && e.State = "null")
                    if newer then
                        awareness.[e.ClientId] <- { existing with Clock = e.Clock; State = e.State }
                | _ ->
                    // Neu anlegen darf nur, wer die ID selbst erzeugt hat. Kannte der Absender sie schon vom
                    // Server, ist es ein Echo (z. B. eines bereits entfernten Clients) und wird ignoriert.
                    if e.State <> "null" && not (sender.Known.Contains e.ClientId) then
                        awareness.[e.ClientId] <- { Clock = e.Clock; State = e.State; Owner = sender }
            let recipients = [| for p in peers do if not (obj.ReferenceEquals(p, sender)) then p |]
            for p in recipients do
                for e in entries do
                    p.Known.Add e.ClientId |> ignore
            recipients)

    /// Entfernt den Client und liefert eine Awareness-Nachricht, die dessen Zustände bei den anderen löscht.
    member _.Leave(peer: Peer) : byte[] option =
        lock peers (fun () ->
            peers.Remove peer |> ignore
            let owned =
                [ for KeyValue(id, r) in awareness do
                      if obj.ReferenceEquals(r.Owner, peer) then id, r ]
            for (id, _) in owned do
                awareness.Remove id |> ignore
            let removals =
                [ for (id, r) in owned do
                      if r.State <> "null" then
                          { ClientId = id; Clock = r.Clock + 1UL; State = "null" } ]
            if removals.IsEmpty then None else Some(buildAwareness removals))

let private rooms = ConcurrentDictionary<string, Room>()

let private broadcast (room: Room) (sender: Peer) (message: byte[]) : Task =
    task {
        for p in room.Others sender do
            do! p.Send message
    }

let private handleMessage (room: Room) (peer: Peer) (msg: byte[]) : Task =
    task {
        if msg.Length >= 2 then
            match msg.[0] with
            | MessageSync ->
                match msg.[1] with
                | SyncStep1 ->
                    do! peer.Send emptyStep2
                    do! broadcast room peer msg
                | SyncStep2 -> do! broadcast room peer msg
                | SyncUpdate ->
                    room.Record msg
                    do! broadcast room peer msg
                | _ -> ()
            | MessageAwareness ->
                match parseAwareness msg with
                | Some entries ->
                    for p in room.RelayAwareness(peer, entries) do
                        do! p.Send msg
                | None -> ()
            | MessageQueryAwareness ->
                match room.AwarenessOfOthers peer with
                | Some snapshot -> do! peer.Send snapshot
                | None -> ()
            | _ -> ()
    }

/// Bedient eine WebSocket-Verbindung bis sie geschlossen wird.
let serve (roomName: string) (socket: WebSocket) (ct: CancellationToken) : Task =
    task {
        let room = rooms.GetOrAdd(roomName, fun _ -> Room())
        let peer = Peer socket
        let replay, snapshot = room.Join peer

        try
            for u in replay do
                do! peer.Send u
            match snapshot with
            | Some a -> do! peer.Send a
            | None -> ()

            let buffer = Array.zeroCreate<byte> 8192
            use ms = new MemoryStream()
            let mutable running = true

            while running && socket.State = WebSocketState.Open && not ct.IsCancellationRequested do
                ms.SetLength 0L
                let mutable receiving = true
                let mutable closed = false
                while receiving do
                    let! result = socket.ReceiveAsync(ArraySegment<byte>(buffer), ct)
                    if result.MessageType = WebSocketMessageType.Close then
                        closed <- true
                        receiving <- false
                    else
                        ms.Write(buffer, 0, result.Count)
                        if ms.Length > int64 maxMessageBytes then
                            closed <- true
                            receiving <- false
                        elif result.EndOfMessage then
                            receiving <- false

                if closed then
                    running <- false
                else
                    do! handleMessage room peer (ms.ToArray())
        with _ ->
            () // Abbruch, Verbindungsfehler o. Ä.: Aufräumen passiert unten

        // Cursor/Name der gegangenen Person bei den anderen entfernen
        match room.Leave peer with
        | Some removal -> do! broadcast room peer removal
        | None -> ()

        if socket.State = WebSocketState.Open || socket.State = WebSocketState.CloseReceived then
            try
                do! socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
            with _ ->
                ()
    }
