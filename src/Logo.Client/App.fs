module Logo.Client.App

open Elmish
open Elmish.React
open Logo.Client.Model
open Logo.Client.View

Program.mkProgram init update view
|> Program.withReactBatched "root"
|> Program.run
