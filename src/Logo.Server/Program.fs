open System
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Microsoft.Extensions.Hosting
open Logo.Server

let private roomPattern = Regex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled)

let private envInt (name: string) (fallback: int) =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> fallback
    | raw ->
        match Int32.TryParse raw with
        | true, value -> value
        | _ -> fallback

let private envIntMin (name: string) (fallback: int) (minimum: int) =
    max minimum (envInt name fallback)

let private envIntZero (name: string) (fallback: int) =
    max 0 (envInt name fallback)

let private envList (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> []
    | raw ->
        raw.Split([| ','; ';' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun value -> value.Trim().TrimEnd('/'))
        |> Array.filter (fun value -> value <> "")
        |> Array.toList

let private limits: YjsRelay.RelayLimits =
    let maxMessageBytes = envIntMin "LOGO_MAX_MESSAGE_BYTES" (1024 * 1024) 1024
    let maxLogBytes = envIntMin "LOGO_MAX_LOG_BYTES" (4 * 1024 * 1024) 65536
    let maxByteBurst =
        max maxMessageBytes (envIntMin "LOGO_MAX_BYTE_BURST" (4 * 1024 * 1024) maxMessageBytes)

    { MaxRooms = envIntMin "LOGO_MAX_ROOMS" 100 1
      MaxPeersPerRoom = envIntMin "LOGO_MAX_PEERS_PER_ROOM" 24 1
      MaxConnections = envIntMin "LOGO_MAX_CONNECTIONS" 128 1
      MaxMessageBytes = maxMessageBytes
      MaxLogBytes = maxLogBytes
      MaxTotalLogBytes =
        max maxLogBytes (envIntMin "LOGO_MAX_TOTAL_LOG_BYTES" (64 * 1024 * 1024) maxLogBytes)
      MaxLogEntries = envIntMin "LOGO_MAX_LOG_ENTRIES" 4096 1
      MaxAwarenessEntries = envIntMin "LOGO_MAX_AWARENESS_ENTRIES" 64 1
      MaxAwarenessEntriesPerPeer = envIntMin "LOGO_MAX_AWARENESS_ENTRIES_PER_PEER" 8 1
      MaxAwarenessStateBytes = envIntMin "LOGO_MAX_AWARENESS_STATE_BYTES" 8192 256
      MaxMessagesPerSecond = envIntZero "LOGO_MAX_MESSAGES_PER_SECOND" 30
      MaxMessageBurst = envIntMin "LOGO_MAX_MESSAGE_BURST" 60 1
      MaxBytesPerSecond = envIntZero "LOGO_MAX_BYTES_PER_SECOND" (1024 * 1024)
      MaxByteBurst = maxByteBurst
      RoomTtl = TimeSpan.FromMinutes(float (envIntZero "LOGO_ROOM_TTL_MINUTES" 60))
      AllowedOrigins = envList "LOGO_ALLOWED_ORIGINS" }

let private originAllowed (ctx: HttpContext) =
    let origins = ctx.Request.Headers["Origin"]
    if origins.Count = 0 then
        // Non-browser clients (CLI/tests) send no Origin. This is not authentication.
        true
    elif origins.Count > 1 then
        false
    else
        let origin = origins.ToString().Trim().TrimEnd('/')
        if origin = "null" then
            false
        elif not limits.AllowedOrigins.IsEmpty then
            limits.AllowedOrigins
            |> List.exists (fun allowed -> String.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase))
        else
            // Same-host default: no config needed behind Coolify/Traefik.
            match Uri.TryCreate(origin, UriKind.Absolute) with
            | true, uri -> String.Equals(uri.Host, ctx.Request.Host.Host, StringComparison.OrdinalIgnoreCase)
            | _ -> false

[<EntryPoint>]
let main args =
    let builder = WebApplication.CreateBuilder(args)
    let app = builder.Build()

    app.UseWebSockets(WebSocketOptions(KeepAliveInterval = TimeSpan.FromSeconds 20.0)) |> ignore

    // Das vom Client-Build erzeugte Frontend (siehe README) liegt in wwwroot.
    app.UseDefaultFiles() |> ignore
    app.UseStaticFiles() |> ignore

    // Yjs-Relay: ws://host/ws/{raum}  (so verbindet sich y-websocket: serverUrl + "/" + raumname)
    app.MapGet(
        "/ws/{room}",
        RequestDelegate(fun ctx ->
            task {
                let room = string ctx.Request.RouteValues["room"]
                if not ctx.WebSockets.IsWebSocketRequest then
                    ctx.Response.StatusCode <- StatusCodes.Status400BadRequest
                elif not (originAllowed ctx) then
                    ctx.Response.StatusCode <- StatusCodes.Status403Forbidden
                elif not (roomPattern.IsMatch room) then
                    ctx.Response.StatusCode <- StatusCodes.Status400BadRequest
                else
                    use! socket = ctx.WebSockets.AcceptWebSocketAsync()
                    do! YjsRelay.serve limits room socket ctx.RequestAborted
            }
            :> Task)
    )
    |> ignore

    use _cleanup = YjsRelay.startCleanup limits
    app.Run()
    0
