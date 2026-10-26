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

/// Limites pro Prozess. Alle Werte sind über Umgebungsvariablen konfigurierbar (siehe Program.fs).
type RelayLimits =
    { MaxRooms: int
      MaxPeersPerRoom: int
      MaxConnections: int
      MaxMessageBytes: int
      MaxLogBytes: int
      MaxTotalLogBytes: int
      MaxLogEntries: int
      MaxAwarenessEntries: int
      MaxAwarenessEntriesPerPeer: int
      MaxAwarenessStateBytes: int
      MaxMessagesPerSecond: int
      MaxMessageBurst: int
      MaxBytesPerSecond: int
      MaxByteBurst: int
      RoomTtl: TimeSpan
      AllowedOrigins: string list }

/// Antwort auf SyncStep1: SyncStep2 mit leerem Update. Der Client gilt damit als "synced".
let private emptyStep2 = [| MessageSync; SyncStep2; 2uy; 0uy; 0uy |]

let private sendTimeout = TimeSpan.FromSeconds 10.0
let private roomsGate = obj()
let mutable private totalLogBytes = 0L
let mutable private connectionCount = 0

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
        if len < 0 || len > data.Length - pos then
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

let private parseAwareness (maxEntries: int) (message: byte[]) : AwarenessEntry list option =
    try
        let r = Reader message
        r.VarUint() |> ignore // Nachrichtentyp
        r.VarUint() |> ignore // Länge der Nutzlast
        let count = int (r.VarUint())
        if count < 0 || count > maxEntries then
            None
        else
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
type private Peer(socket: WebSocket, limits: RelayLimits) =
    let sendLock = new SemaphoreSlim(1, 1)

    let maxKnown = max 64 (limits.MaxAwarenessEntries * 4)
    let messageRate = float (max 0 limits.MaxMessagesPerSecond)
    let messageBurst = float (max 1 limits.MaxMessageBurst)
    let byteRate = float (max 0 limits.MaxBytesPerSecond)
    let byteBurst = float (max 1 limits.MaxByteBurst)
    let mutable messageTokens = messageBurst
    let mutable byteTokens = byteBurst
    let mutable lastRefill = Environment.TickCount64

    /// Client-IDs, die der Server diesem Client bereits mitgeteilt hat (nur unter dem Raum-Lock benutzen).
    /// Meldet der Client eine solche ID zurück, ist das ein Echo und keine neue Information.
    member val Known = HashSet<uint64>()

    member p.Remember(clientId: uint64) =
        if p.Known.Count >= maxKnown then
            p.Known.Clear()
        p.Known.Add clientId |> ignore

    /// Einfacher Token-Bucket gegen Nachrichten- und Byte-Fluten pro Verbindung.
    member _.TryTakeMessage(size: int) : bool =
        if messageRate <= 0.0 && byteRate <= 0.0 then
            true
        else
            let now = Environment.TickCount64
            let elapsed = max 0.0 (float (now - lastRefill) / 1000.0)
            lastRefill <- now
            if messageRate > 0.0 then
                messageTokens <- min messageBurst (messageTokens + elapsed * messageRate)
            else
                messageTokens <- messageBurst
            if byteRate > 0.0 then
                byteTokens <- min byteBurst (byteTokens + elapsed * byteRate)
            else
                byteTokens <- byteBurst
            let messageOk = messageRate <= 0.0 || messageTokens >= 1.0
            let byteOk = byteRate <= 0.0 || byteTokens >= float size
            if messageOk && byteOk then
                if messageRate > 0.0 then
                    messageTokens <- messageTokens - 1.0
                if byteRate > 0.0 then
                    byteTokens <- byteTokens - float size
                true
            else
                false

    member _.Send(data: byte[]) : Task =
        task {
            let! acquired = sendLock.WaitAsync(sendTimeout)
            if not acquired then
                try
                    socket.Abort()
                with _ ->
                    ()
            else
                try
                    try
                        if socket.State = WebSocketState.Open then
                            use cts = new CancellationTokenSource(sendTimeout)
                            do!
                                socket
                                    .SendAsync(ReadOnlyMemory<byte>(data), WebSocketMessageType.Binary, true, cts.Token)
                                    .AsTask()
                    with _ ->
                        // Ein Client, der nicht liest, darf den Broadcast nicht blockieren.
                        try
                            socket.Abort()
                        with _ ->
                            ()
                finally
                    sendLock.Release() |> ignore
        }

type private AwarenessRecord =
    { Clock: uint64
      State: string
      Owner: Peer }

type private Room(limits: RelayLimits) =
    let peers = ResizeArray<Peer>()
    let updates = ResizeArray<byte[]>()
    /// clientId -> aktueller Awareness-Zustand samt der Verbindung, über die er zuerst kam.
    /// y-websocket-Clients leiten fremde Awareness-Einträge weiter (Echo); als Besitzer gilt
    /// deshalb die Verbindung, die eine Client-ID als erste meldet.
    let awareness = Dictionary<uint64, AwarenessRecord>()
    let mutable logBytes = 0
    let mutable lastActivity = Environment.TickCount64

    /// Aktuelle Awareness-Zustände aller anderen Clients als Nachricht (und merkt sie als "bekannt" für `target`).
    /// Nur unter dem Raum-Lock aufrufen.
    member private _.SnapshotFor(target: Peer) : byte[] option =
        let entries =
            [ for KeyValue(id, r) in awareness do
                  if r.State <> "null" && not (obj.ReferenceEquals(r.Owner, target)) then
                      target.Remember id
                      { ClientId = id; Clock = r.Clock; State = r.State } ]
        if entries.IsEmpty then None else Some(buildAwareness entries)

    /// Fügt den Client hinzu und liefert, was er nachholen muss: Dokument-Updates und Awareness.
    /// `None` bedeutet: Raum ist voll.
    member m.Join(peer: Peer) : (byte[][] * byte[] option) option =
        lock peers (fun () ->
            if peers.Count >= limits.MaxPeersPerRoom then
                None
            else
                let replay = updates.ToArray()
                let snapshot = m.SnapshotFor peer
                peers.Add peer
                lastActivity <- Environment.TickCount64
                Some(replay, snapshot))

    member _.Others(peer: Peer) : Peer[] =
        lock peers (fun () -> [| for p in peers do if not (obj.ReferenceEquals(p, peer)) then p |])

    member m.AwarenessOfOthers(peer: Peer) : byte[] option =
        lock peers (fun () -> m.SnapshotFor peer)

    /// Speichert ein Update für spätere Joins. Der Aufruf ist best-effort: Wenn ein Cap
    /// erreicht ist, wird der Log dieses Raums verworfen bzw. das Update nicht gespeichert;
    /// aktive Clients erhalten es trotzdem per Broadcast.
    member _.Record(update: byte[]) =
        lock peers (fun () ->
            lastActivity <- Environment.TickCount64
            let updateBytes = int64 update.Length
            let total = Interlocked.Read(&totalLogBytes)
            let overflow =
                updates.Count >= limits.MaxLogEntries
                || logBytes + update.Length > limits.MaxLogBytes
                || total + updateBytes > int64 limits.MaxTotalLogBytes
            if overflow then
                if logBytes > 0 then
                    Interlocked.Add(&totalLogBytes, -int64 logBytes) |> ignore
                updates.Clear()
                updates.TrimExcess()
                logBytes <- 0
            let canStore =
                updates.Count < limits.MaxLogEntries
                && logBytes + update.Length <= limits.MaxLogBytes
                && Interlocked.Read(&totalLogBytes) + updateBytes <= int64 limits.MaxTotalLogBytes
            if canStore then
                updates.Add update
                logBytes <- logBytes + update.Length
                Interlocked.Add(&totalLogBytes, updateBytes) |> ignore)

    /// Übernimmt eingehende Awareness-Einträge und liefert Empfänger plus gefilterte Nachricht.
    /// Unbekannte IDs werden nur bis zu den Caps akzeptiert; übergroße States werden ignoriert.
    member _.RelayAwareness(sender: Peer, entries: AwarenessEntry list) : Peer[] * byte[] option =
        lock peers (fun () ->
            lastActivity <- Environment.TickCount64
            let mutable owned =
                awareness
                |> Seq.filter (fun kv -> obj.ReferenceEquals(kv.Value.Owner, sender))
                |> Seq.length
            let accepted = ResizeArray<AwarenessEntry>()
            for e in entries do
                let stateBytes = Encoding.UTF8.GetByteCount e.State
                if e.State = "null" || stateBytes <= limits.MaxAwarenessStateBytes then
                    match awareness.TryGetValue e.ClientId with
                    | true, existing ->
                        let newer =
                            e.Clock > existing.Clock
                            || (e.Clock = existing.Clock && e.State = "null")
                        if newer then
                            awareness.[e.ClientId] <- { existing with Clock = e.Clock; State = e.State }
                            accepted.Add e
                    | _ ->
                        // Neu anlegen darf nur, wer die ID selbst erzeugt hat. Kannte der Absender sie
                        // schon vom Server, ist es ein Echo (z. B. eines bereits entfernten Clients).
                        if
                            e.State <> "null"
                            && not (sender.Known.Contains e.ClientId)
                            && awareness.Count < limits.MaxAwarenessEntries
                            && owned < limits.MaxAwarenessEntriesPerPeer
                        then
                            awareness.[e.ClientId] <- { Clock = e.Clock; State = e.State; Owner = sender }
                            owned <- owned + 1
                            accepted.Add e
            let recipients = [| for p in peers do if not (obj.ReferenceEquals(p, sender)) then p |]
            for p in recipients do
                for e in accepted do
                    p.Remember e.ClientId
            let message =
                if accepted.Count = 0 then
                    None
                else
                    Some(buildAwareness (List.ofSeq accepted))
            recipients, message)

    /// Entfernt den Client und liefert eine Awareness-Nachricht, die dessen Zustände bei den anderen löscht.
    member _.Leave(peer: Peer) : byte[] option =
        lock peers (fun () ->
            peers.Remove peer |> ignore
            lastActivity <- Environment.TickCount64
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

    /// Entfernt einen leeren, abgelaufenen Raum und gibt dessen Log-Speicher frei.
    /// Nur unter `roomsGate` aufrufen.
    member _.TryEvict(nowMs: int64, ttlMs: int64) : bool =
        lock peers (fun () ->
            if ttlMs > 0L && peers.Count = 0 && nowMs - lastActivity >= ttlMs then
                if logBytes > 0 then
                    Interlocked.Add(&totalLogBytes, -int64 logBytes) |> ignore
                updates.Clear()
                updates.TrimExcess()
                awareness.Clear()
                logBytes <- 0
                true
            else
                false)

let private rooms = ConcurrentDictionary<string, Room>()

let private tryAcquireConnection (limits: RelayLimits) : bool =
    let next = Interlocked.Increment(&connectionCount)
    if limits.MaxConnections <= 0 || next <= limits.MaxConnections then
        true
    else
        Interlocked.Decrement(&connectionCount) |> ignore
        false

let private releaseConnection () =
    Interlocked.Decrement(&connectionCount) |> ignore

let private getRoomAndJoin (limits: RelayLimits) (name: string) (peer: Peer) :
    (Room * byte[][] * byte[] option) option =
    lock roomsGate (fun () ->
        match rooms.TryGetValue name with
        | true, room ->
            match room.Join peer with
            | Some(replay, snapshot) -> Some(room, replay, snapshot)
            | None -> None
        | _ ->
            if rooms.Count >= limits.MaxRooms then
                None
            else
                let room = Room(limits)
                rooms.[name] <- room
                match room.Join peer with
                | Some(replay, snapshot) -> Some(room, replay, snapshot)
                | None ->
                    rooms.TryRemove name |> ignore
                    None)

let private sweep (limits: RelayLimits) =
    try
        let ttlMs = int64 limits.RoomTtl.TotalMilliseconds
        if ttlMs > 0L then
            let now = Environment.TickCount64
            lock roomsGate (fun () ->
                for kv in rooms do
                    if kv.Value.TryEvict(now, ttlMs) then
                        rooms.TryRemove kv.Key |> ignore)
    with _ ->
        ()

/// Startet die periodische Entfernung leerer, abgelaufener Räume.
let startCleanup (limits: RelayLimits) : IDisposable =
    if limits.RoomTtl <= TimeSpan.Zero then
        { new IDisposable with
            member _.Dispose() = () }
    else
        let interval = TimeSpan.FromSeconds 30.0
        new Timer(TimerCallback(fun _ -> sweep limits), null, interval, interval) :> IDisposable

let private closeWith (socket: WebSocket) (status: WebSocketCloseStatus) (reason: string) : Task =
    task {
        if socket.State = WebSocketState.Open || socket.State = WebSocketState.CloseReceived then
            try
                use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)
                do! socket.CloseAsync(status, reason, cts.Token)
            with _ ->
                try
                    socket.Abort()
                with _ ->
                    ()
    }

let private sendAll (recipients: Peer[]) (message: byte[]) : Task =
    if recipients.Length = 0 then
        Task.CompletedTask
    else
        Task.WhenAll(recipients |> Array.map (fun p -> p.Send message))

let private broadcast (room: Room) (sender: Peer) (message: byte[]) : Task =
    sendAll (room.Others sender) message

let private handleMessage (limits: RelayLimits) (room: Room) (peer: Peer) (msg: byte[]) : Task =
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
                match parseAwareness limits.MaxAwarenessEntries msg with
                | Some entries ->
                    let recipients, message = room.RelayAwareness(peer, entries)
                    match message with
                    | Some accepted -> do! sendAll recipients accepted
                    | None -> ()
                | None -> ()
            | MessageQueryAwareness ->
                match room.AwarenessOfOthers peer with
                | Some snapshot -> do! peer.Send snapshot
                | None -> ()
            | _ -> ()
    }

/// Bedient eine WebSocket-Verbindung bis sie geschlossen wird.
let serve (limits: RelayLimits) (roomName: string) (socket: WebSocket) (ct: CancellationToken) : Task =
    task {
        if not (tryAcquireConnection limits) then
            do! closeWith socket WebSocketCloseStatus.PolicyViolation "server busy"
        else
            try
                let peer = Peer(socket, limits)
                let mutable activeRoom : Room option = None
                let mutable policyReason : string option = None

                try
                    match getRoomAndJoin limits roomName peer with
                    | None ->
                        do! closeWith socket WebSocketCloseStatus.PolicyViolation "room unavailable"
                    | Some(room, replay, snapshot) ->
                        activeRoom <- Some room

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
                                    if ms.Length > int64 limits.MaxMessageBytes then
                                        policyReason <- Some "message too large"
                                        closed <- true
                                        receiving <- false
                                    elif result.EndOfMessage then
                                        receiving <- false

                            if closed then
                                running <- false
                            elif not (peer.TryTakeMessage(int ms.Length)) then
                                policyReason <- Some "rate limit"
                                running <- false
                            else
                                do! handleMessage limits room peer (ms.ToArray())
                with _ ->
                    ()

                match activeRoom with
                | Some room ->
                    try
                        match room.Leave peer with
                        | Some removal -> do! broadcast room peer removal
                        | None -> ()
                    with _ ->
                        ()
                | None -> ()

                match policyReason with
                | Some reason -> do! closeWith socket WebSocketCloseStatus.PolicyViolation reason
                | None -> do! closeWith socket WebSocketCloseStatus.NormalClosure "bye"
            finally
                releaseConnection ()
    }
