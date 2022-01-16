module BetterAkkling.Spawn

open Akkling

open Akkling.Persistence
open BetterAkkling.Core
open BetterAkkling.Props

open Core

module private Simple =

    type Start = {
        checkpoint: Option<obj -> Action<SimpleActor, unit>>
        restartHandler: Option<RestartHandler>
    }

    type RestartHandlerMsg = RestartHandlerMsg

    let doSpawn spawnFunc (props: Props) (persist: bool) (action: Action<SimpleActor, unit>) =

        let runActor (ctx: Actors.Actor<obj>) =

            let handleLifecycle restartHandler checkpoint evt =
                match evt with
                | PreRestart(exn, msg) ->
                    restartHandler |> Option.iter (fun f -> f msg exn)
                    if persist then
                        retype ctx.Self <! {checkpoint = checkpoint; restartHandler = restartHandler}
                    else
                        retype ctx.Self <! {checkpoint = None; restartHandler = None}
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
                | Persist _ ->
                    logError ctx "Got persist action in simple actor"
                    stop ()
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
                | :? Start ->
                    ignored ()
                | _ ->
                    ctx.Stash ()
                    ignored ()

            and waitForMessage restartHandler next (msg: obj) =
                match msg with
                | :? LifecycleEvent as evt ->
                    handleLifecycle restartHandler (Some next) evt
                | :? Start ->
                    ignored ()
                | _ ->
                    handleNextAction restartHandler (Some next) (next msg)

            let waitForStart (msg: obj) =
                match msg with
                | :? LifecycleEvent as evt ->
                    let handler = fun msg exn ->
                        Logging.logErrorf ctx "Actor crashed before actions started with msg %A: %A" msg exn
                    handleLifecycle (Some handler) None evt
                | :? Start as start ->
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
        retype act <! {checkpoint = None; restartHandler = None}
        retype act

module private EventSourced =

    type RestartHandlerMsg = RestartHandlerMsg

    type Persisted = {evt: obj}

    let doSpawn spawnFunc (props: Props) (action: Action<EventSourcedActor<NoSnapshotting>, unit>) =

        let runActor (ctx: Eventsourced<obj>) =

            let handleLifecycle restartHandler evt =
                match evt with
                | PreRestart(exn, msg) ->
                    restartHandler |> Option.iter (fun f -> f msg exn)
                | _ ->
                    ()
                ignored ()

            let waitForNewHandler restartHandler cont (msg: obj) =
                match msg with
                | :? RestartHandlerMsg ->
                    ctx.UnstashAll ()
                    cont ()
                | :? LifecycleEvent as evt ->
                    handleLifecycle restartHandler evt
                | _ ->
                    ctx.Stash ()
                    ignored ()

            let rec handleNextAction restartHandler (action: Action<EventSourcedActor<NoSnapshotting>, unit>) =
                match action with
                | Stop _
                | Done _ ->
                    stop ()
                | Simple next ->
                    handleNextAction restartHandler (next ctx)
                | Msg _ ->
                    logError ctx "Got Msg in persistent workflow"
                    stop ()
                | Persist (action, next) ->
                    handleNextSubAction restartHandler next action
                | RestartHandlerUpdate (newHandler, next) ->
                    ActorRefs.retype ctx.Self <! RestartHandlerMsg
                    become (waitForNewHandler newHandler (fun () -> handleNextAction newHandler (next ())))

            and handleNextSubAction restartHandler cont action =
                match action with
                | Stop _ ->
                    stop ()
                | Done evt ->
                    (become (waitForEvent restartHandler cont)
                        <@> (Akkling.Persistence.Persist ({evt = evt} :> obj)))
                | Simple next ->
                    handleNextSubAction restartHandler cont (next ctx)
                | Msg next ->
                    become (waitForMessage restartHandler cont next)
                | Persist _ ->
                    logError ctx "Got persist in simple actor"
                    stop ()
                | RestartHandlerUpdate (newHandler, next) ->
                    ActorRefs.retype ctx.Self <! RestartHandlerMsg
                    become (waitForNewHandler newHandler (fun () -> handleNextSubAction newHandler cont (next ())))

            and waitForMessage restartHandler cont next (msg: obj) =
                match msg with
                | :? LifecycleEvent as evt ->
                    handleLifecycle restartHandler evt
                | _ ->
                    handleNextSubAction restartHandler cont (next msg)

            and waitForEvent restartHandler next (msg: obj) =
                match msg with
                | :? LifecycleEvent as evt ->
                    handleLifecycle restartHandler evt
                | :? Persisted as persisted ->
                    handleNextAction restartHandler (next persisted.evt)
                | _ ->
                    ignored ()

            let rec handleNextRecoveryAction restartHandler (action: Action<EventSourcedActor<NoSnapshotting>, unit>) =
                match action with
                | Stop _
                | Done _ ->
                    stop ()
                | Simple next ->
                    handleNextRecoveryAction restartHandler (next ctx)
                | Msg _ ->
                    logError ctx "Got Msg in persistent workflow"
                    stop ()
                | Persist (subAction, next) ->
                    become (waitForRecoveryEvent restartHandler subAction next)
                | RestartHandlerUpdate (newHandler, next) ->
                    ActorRefs.retype ctx.Self <! RestartHandlerMsg
                    become (waitForNewHandler newHandler (fun () -> handleNextRecoveryAction newHandler (next ())))

            and waitForRecoveryEvent restartHandler subAction next (msg:obj) =
                match msg with
                | :? LifecycleEvent as evt ->
                    handleLifecycle restartHandler evt
                | :? Persisted as persisted ->
                    handleNextRecoveryAction restartHandler (next persisted.evt)
                | PersistentLifecycleEvent ReplaySucceed ->
                    handleNextSubAction restartHandler next subAction
                | _ ->
                    ignored ()

            let initRestartHandler = Some (fun msg exn ->
                Logging.logErrorf ctx "Actor crashed before actions started with msg %A: %A" msg exn
            )

            let waitForStart (msg: obj) =
                match msg with
                | :? LifecycleEvent as evt ->
                    match evt with
                    | PreStart ->
                        handleNextRecoveryAction initRestartHandler action
                    | _ ->
                        handleLifecycle initRestartHandler evt
                | _ ->
                    ignored ()

            become waitForStart

        retype (spawnFunc {
            propsPersist runActor with
                Dispatcher = props.dispatcher
                Mailbox = props.mailbox
                Deploy = props.deploy
                Router = props.router
                SupervisionStrategy = props.supervisionStrategy
        })

let spawn parent props actorType =
    match props.name, actorType with
    | Some name, NotPersisted action ->
        Simple.doSpawn (Spawn.spawn parent name) props false action
    | None, NotPersisted action ->
        Simple.doSpawn (Spawn.spawnAnonymous parent) props false action
    | Some name, Checkpointed action ->
        Simple.doSpawn (Spawn.spawn parent name) props true action
    | None, Checkpointed action ->
        Simple.doSpawn (Spawn.spawnAnonymous parent) props true action
    | Some name, EventSourced action ->
        EventSourced.doSpawn (Spawn.spawn parent name) props action
    | None, EventSourced action ->
        EventSourced.doSpawn (Spawn.spawnAnonymous parent) props action
    | Some _name, EventSourcedWithSnapshots (_init, _action) ->
        failwith "Not supported yet"
    | None, EventSourcedWithSnapshots (_init, _action) ->
        failwith "Not supported yet"
