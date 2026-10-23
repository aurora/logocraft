open System
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Microsoft.Extensions.Hosting
open Logo.Server

let private roomPattern = Regex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled)

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
                elif not (roomPattern.IsMatch room) then
                    ctx.Response.StatusCode <- StatusCodes.Status400BadRequest
                else
                    use! socket = ctx.WebSockets.AcceptWebSocketAsync()
                    do! YjsRelay.serve room socket ctx.RequestAborted
            }
            :> Task)
    )
    |> ignore

    app.Run()
    0
