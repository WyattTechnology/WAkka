module BetterAkkling.Spawn

open Akkling

open Core
open Props

type private Start<'ActorType, 'Result> = {
    checkpoint: Option<obj -> Action<'ActorType, 'Result>>
    restartHandler: Option<RestartHandler>
}

type private RestartHandlerMsg = RestartHandlerMsg

let private doSpawnSimple spawnFunc (props: Props) (persist: bool) (action: Action<'ActorType, 'Result>) =

    let runActor (ctx: Actors.Actor<obj>) =

        let handleLifecycle restartHandler checkpoint evt =
            match evt with
            | PreRestart(exn, msg) ->
                restartHandler |> Option.iter (fun f -> f msg exn)
                if persist then
                    let start : Start<'ActorType, 'Result> = {checkpoint = checkpoint; restartHandler = restartHandler}
                    retype ctx.Self <! start
                else
                    let start : Start<'ActorType, 'Result> = {checkpoint = None; restartHandler = None}
                    retype ctx.Self <! start
            | _ ->
                ()
            ignored ()

        let rec handleNextAction restartHandler checkpoint action =
            match action with
            | Stop _
            | Done _ ->
                stop ()
            | Simple next ->
                handleNextAction restartHandler checkpoint (next ctx)
            | Msg next ->
                become (waitForMessage restartHandler next)
            | RestartHandlerUpdate (newHandler, next) ->
                ActorRefs.retype ctx.Self <! RestartHandlerMsg
                become (waitForNewHandler newHandler checkpoint next)

        and waitForNewHandler restartHandler checkpoint next (msg: obj) =
            match msg with
            | :? RestartHandlerMsg ->
                ctx.UnstashAll ()
                handleNextAction restartHandler checkpoint (next ())
            | :? LifecycleEvent as evt ->
                handleLifecycle restartHandler checkpoint evt
            | :? Start<'ActorType, 'Result> ->
                ignored ()
            | _ ->
                ctx.Stash ()
                ignored ()

        and waitForMessage restartHandler next (msg: obj) =
            match msg with
            | :? LifecycleEvent as evt ->
                handleLifecycle restartHandler (Some next) evt
            | :? Start<'ActorType, 'Result> ->
                ignored ()
            | _ ->
                handleNextAction restartHandler (Some next) (next msg)

        let waitForStart (msg: obj) =
            match msg with
            | :? LifecycleEvent as evt ->
                let handler = fun msg exn ->
                    Logging.logErrorf ctx "Actor crashed before actions started with msg %A: %A" msg exn
                handleLifecycle (Some handler) None evt
            | :? Start<'ActorType, 'Result> as start ->
                ctx.UnstashAll ()
                match start.checkpoint with
                | None ->
                    handleNextAction None None action
                | Some checkpoint ->
                    become (waitForMessage start.restartHandler checkpoint)
            | _ ->
                ctx.Stash ()
                ignored ()

        become waitForStart

    let act = spawnFunc {
        Props.props runActor with
            Dispatcher = props.dispatcher
            Mailbox = props.mailbox
            Deploy = props.deploy
            Router = props.router
            SupervisionStrategy = props.supervisionStrategy
    }
    let start : Start<'ActorType, 'Result> = {checkpoint = None; restartHandler = None}
    retype act <! start
    retype act

let spawn parent props actorType =
    match props.name, actorType with
    | Some name, NotPersistent action ->
        doSpawnSimple (Spawn.spawn parent name) props false action
    | Some name, InMemoryPersistent action ->
        doSpawnSimple (Spawn.spawn parent name) props true action
    | None, NotPersistent action ->
        doSpawnSimple (Spawn.spawnAnonymous parent) props false action
    | None, InMemoryPersistent action ->
        doSpawnSimple (Spawn.spawnAnonymous parent) props true action
    | _ ->
        failwith "Not supported yet"
