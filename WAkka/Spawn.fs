module BetterAkkling.Spawn

open Akkling

open Akkling.Persistence
open BetterAkkling.Core
open BetterAkkling.Props

open Core

//module private Simple =
//
//    type Start = {
//        checkpoint: Option<obj -> Action<SimpleActor, unit>>
//        restartHandler: Option<RestartHandler>
//    }
//
//    type RestartHandlerMsg = RestartHandlerMsg
//
//    let doSpawn spawnFunc (props: Props) (persist: bool) (action: Action<SimpleActor, unit>) =
//
//        let runActor (ctx: Actors.Actor<obj>) =
//
//            let handleLifecycle restartHandler checkpoint evt =
//                match evt with
//                | PreRestart(exn, msg) ->
//                    restartHandler |> Option.iter (fun f -> f msg exn)
//                    if persist then
//                        retype ctx.Self <! {checkpoint = checkpoint; restartHandler = restartHandler}
//                    else
//                        retype ctx.Self <! {checkpoint = None; restartHandler = None}
//                | _ ->
//                    ()
//                ignored ()
//
//            let rec handleNextAction restartHandler checkpoint action =
//                match action with
//                | Stop _
//                | Done _ ->
//                    stop ()
//                | Simple next ->
//                    handleNextAction restartHandler checkpoint (next ctx)
//                | Msg next ->
//                    become (waitForMessage restartHandler next)
//                | Persist _ ->
//                    logError ctx "Got persist action in simple actor"
//                    stop ()
//                | RestartHandlerUpdate (newHandler, next) ->
//                    ActorRefs.retype ctx.Self <! RestartHandlerMsg
//                    become (waitForNewHandler newHandler checkpoint next)
//
//            and waitForNewHandler restartHandler checkpoint next (msg: obj) =
//                match msg with
//                | :? RestartHandlerMsg ->
//                    ctx.UnstashAll ()
//                    handleNextAction restartHandler checkpoint (next ())
//                | :? LifecycleEvent as evt ->
//                    handleLifecycle restartHandler checkpoint evt
//                | :? Start ->
//                    ignored ()
//                | _ ->
//                    ctx.Stash ()
//                    ignored ()
//
//            and waitForMessage restartHandler next (msg: obj) =
//                match msg with
//                | :? LifecycleEvent as evt ->
//                    handleLifecycle restartHandler (Some next) evt
//                | :? Start ->
//                    ignored ()
//                | _ ->
//                    handleNextAction restartHandler (Some next) (next msg)
//
//            let waitForStart (msg: obj) =
//                match msg with
//                | :? LifecycleEvent as evt ->
//                    let handler = fun msg exn ->
//                        Logging.logErrorf ctx "Actor crashed before actions started with msg %A: %A" msg exn
//                    handleLifecycle (Some handler) None evt
//                | :? Start as start ->
//                    ctx.UnstashAll ()
//                    match start.checkpoint with
//                    | None ->
//                        handleNextAction None None action
//                    | Some checkpoint ->
//                        become (waitForMessage start.restartHandler checkpoint)
//                | _ ->
//                    ctx.Stash ()
//                    ignored ()
//
//            become waitForStart
//
//        let act = spawnFunc {
//            Props.props runActor with
//                Dispatcher = props.dispatcher
//                Mailbox = props.mailbox
//                Deploy = props.deploy
//                Router = props.router
//                SupervisionStrategy = props.supervisionStrategy
//        }
//        retype act <! {checkpoint = None; restartHandler = None}
//        retype act

module private SimpleNew =

    type Start = {
        checkpoint: Option<obj -> Action<SimpleActor, unit>>
        restartHandler: Option<RestartHandler>
    }

    type IActorCtx =
        abstract member Self: Akka.Actor.IActorRef
        abstract member LogDebug: string -> unit
        abstract member LogInfo: string -> unit
        abstract member LogWarning: string -> unit
        abstract member LogError: string -> unit

    type Actor (persist: bool, startAction: Action<SimpleActor, unit>) as this =

        inherit Akka.Actor.UntypedActor ()

        let ctx = Akka.Actor.UntypedActor.Context :> Akka.Actor.IActorContext
        let logger = Akka.Event.Logging.GetLogger ctx
        let stash = (this :> Akka.Actor.IWithUnboundedStash).Stash

        let mutable restartHandler = Option<IActorCtx -> obj -> exn -> unit>.None

        let mutable msgHandler = fun _ -> ()
        let mutable checkpoint = None

        let rec handleActions action =
            match action with
            | Done _
            | Stop _ ->
                ctx.Stop ctx.Self
            | Simple next ->
                handleActions (next ctx)
            | Msg next ->
                msgHandler <- waitForMsg next
            | RestartHandlerUpdate (_, next) ->
                handleActions (next ())
            | Persist _ ->
                logger.Error "Got persist action in simple actor"
                ctx.Stop ctx.Self

        and waitForMsg next (msg:obj) =
            match msg with
            | :? Start -> ()
            | _ -> handleActions (next msg)

        let waitForStart (msg:obj) =
            match msg with
            | :? Start as start ->
                match start.restartHandler with
                | Some handler ->
                    restartHandler <- handler
                | None ->
                    ()
                stash.UnstashAll ()
                match start.checkpoint with
                | None ->
                    handleActions startAction
                | Some action ->
                    checkpoint <- start.checkpoint
                    msgHandler <- waitForMsg action
            | _ ->
                stash.Stash ()

        do
            msgHandler <- waitForStart
            ctx.Self.Tell({checkpoint = None; restartHandler = None}, ctx.Self)

        override _.OnReceive (msg: obj) = msgHandler msg

        override _.PreRestart(err: exn, msg: obj) =
            logger.Error $"Actor restarting after message {msg}: {err}"
            restartHandler this msg err
            if persist then
                ctx.Self.Tell({checkpoint = checkpoint; restartHandler = Some restartHandler}, ctx.Self)
            base.PreRestart (err, msg)

        interface IActorCtx with
            member _.Self = ctx.Self
            member _.LogDebug msg = logger.Debug msg
            member _.LogInfo msg = logger.Info msg
            member _.LogWarning msg = logger.Warning msg
            member _.LogError msg = logger.Error msg

        interface Akka.Actor.IWithUnboundedStash with
            member _.Stash = Unchecked.defaultof<Akka.Actor.IStash>

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

    type Stopped = Stopped

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

            let waitForError restartHandler (msg: obj) =
                match msg with
                | :? LifecycleEvent as evt ->
                    handleLifecycle restartHandler evt
                | _ ->
                    logErrorf ctx "Got unexpected message: %A" msg
                    ignored ()

            let setRestartHandler newHandler =
                let effect = become (waitForError newHandler)
                let actual = ctx :?> ExtActor<obj>
                actual.Become effect

            let rec handleNextAction restartHandler (action: Action<EventSourcedActor<NoSnapshotting>, unit>) =
                match action with
                | Stop _
                | Done _ ->
                    (become waitForStop
                        <@> (Akkling.Persistence.Persist (Stopped :> obj)))
                | Simple next ->
                    handleNextAction restartHandler (next ctx)
                | Msg _ ->
                    logError ctx "Got Msg in persistent workflow"
                    stop ()
                | Persist (action, next) ->
                    handleNextSubAction restartHandler next action
                | RestartHandlerUpdate (newHandler, next) ->
                    setRestartHandler newHandler
                    handleNextAction newHandler (next ())

            and handleNextSubAction restartHandler cont action =
                match action with
                | Stop _ ->
                    (become waitForStop
                        <@> (Akkling.Persistence.Persist (Stopped :> obj)))
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
                    setRestartHandler newHandler
                    handleNextSubAction newHandler cont (next ())

            and waitForMessage restartHandler cont next (msg: obj) =
                match msg with
                | :? LifecycleEvent as evt ->
                    handleLifecycle restartHandler evt
                | :? RestartHandlerMsg ->
                    ignored ()
                | _ ->
                    setRestartHandler restartHandler
                    handleNextSubAction restartHandler cont (next msg)

            and waitForStop (msg: obj) =
                match msg with
                | :? Stopped ->
                    stop ()
                | _ ->
                    ignored ()

            and waitForEvent restartHandler next (msg: obj) =
                match msg with
                | :? LifecycleEvent as evt ->
                    handleLifecycle restartHandler evt
                | :? Persisted as persisted ->
                    setRestartHandler restartHandler
                    let cont = next persisted.evt
                    handleNextAction restartHandler cont
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
                    setRestartHandler newHandler
                    handleNextRecoveryAction newHandler (next ())

            and waitForRecoveryEvent restartHandler subAction next (msg:obj) =
                match msg with
                | :? LifecycleEvent as evt ->
                    handleLifecycle restartHandler evt
                | :? Stopped ->
                    stop ()
                | :? Persisted as persisted ->
                    handleNextRecoveryAction restartHandler (next persisted.evt)
                | PersistentLifecycleEvent ReplaySucceed ->
                    setRestartHandler restartHandler
                    handleNextSubAction restartHandler next subAction
                | PersistentLifecycleEvent (ReplayFailed (exn, cause)) ->
                    logErrorf ctx "Replay failed cur to msg %A: %A" cause exn
                    stop ()
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
                        setRestartHandler initRestartHandler
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

//module private EventSourcedNew =
//
//    type Stopped = Stopped
//
//    type Persisted = {evt: obj}
//
//    let defaultRestartHandler _msg _reason = ()
//
//    type ActionRes<'Result> =
//        | DoStop
//        | DoPersist of Action<SimpleActor, obj> * (obj -> Action<EventSourcedActor<NoSnapshotting>, 'Result>)
//
//    type NextAction<'Result> =
//        | Top of Action<EventSourcedActor<NoSnapshotting>, unit>
//        |
//    type PersistentActor (path: string, action: Action<EventSourcedActor<NoSnapshotting>, unit>) as this =
//
//        inherit Akka.Persistence.PersistentActor()
//        let es = this :> Akka.Persistence.Eventsourced
//
//        let mutable restartHandler : RestartHandler = defaultRestartHandler
//        let mutable nextAction = action
//
//        override _.PersistenceId = path
//        //TODO: implement
//        override _.ReceiveCommand(msg:obj) = false
//        override _.ReceiveRecover(msg:obj) = false
//        override _.PreStart() =
//            let rec handleAction action =
//                match action with
//                | Stop _
//                | RestartHandlerUpdate _
//                | Done _ ->
//                    DoStop
//                | Simple next ->
//                    handleAction (next this)
//                | Msg _ ->
//                    this.Log.Error "Got Msg in persistent workflow"
//                    DoStop
//                | Persist (action, next) ->
//                    DoPersist (action, next)
//
//            match handleAction action with
//            | DoStop ->
//                this.Persist(Stopped, fun _ -> (this :> Akka.Actor.ActorCell).Stop ())
//            | DoPersist (action, next) ->
//                ()
//
//        override this.PreRestart (reason: exn, msg: obj) =
//            this.Log.Error $"Actor crashed with msg {msg}: {reason}"
//            restartHandler msg reason
//            base.PreRestart(reason, msg)
//
//
//
//
//    let doSpawn spawnFunc (props: Props) (action: Action<EventSourcedActor<NoSnapshotting>, unit>) =
//
//        let runActor (ctx: Eventsourced<obj>) =
//
//            let handleLifecycle restartHandler evt =
//                match evt with
//                | PreRestart(exn, msg) ->
//                    restartHandler |> Option.iter (fun f -> f msg exn)
//                | _ ->
//                    ()
//                ignored ()
//
//            let waitForError restartHandler (msg: obj) =
//                match msg with
//                | :? LifecycleEvent as evt ->
//                    handleLifecycle restartHandler evt
//                | _ ->
//                    logErrorf ctx "Got unexpected message: %A" msg
//                    ignored ()
//
//            let setRestartHandler newHandler =
//                let effect = become (waitForError newHandler)
//                let actual = ctx :?> ExtActor<obj>
//                actual.Become effect
//
//            let rec handleNextAction restartHandler (action: Action<EventSourcedActor<NoSnapshotting>, unit>) =
//                match action with
//                | Stop _
//                | Done _ ->
//                    (become waitForStop
//                        <@> (Akkling.Persistence.Persist (Stopped :> obj)))
//                | Simple next ->
//                    handleNextAction restartHandler (next ctx)
//                | Msg _ ->
//                    logError ctx "Got Msg in persistent workflow"
//                    stop ()
//                | Persist (action, next) ->
//                    handleNextSubAction restartHandler next action
//                | RestartHandlerUpdate (newHandler, next) ->
//                    setRestartHandler newHandler
//                    handleNextAction newHandler (next ())
//
//            and handleNextSubAction restartHandler cont action =
//                match action with
//                | Stop _ ->
//                    (become waitForStop
//                        <@> (Akkling.Persistence.Persist (Stopped :> obj)))
//                | Done evt ->
//                    (become (waitForEvent restartHandler cont)
//                        <@> (Akkling.Persistence.Persist ({evt = evt} :> obj)))
//                | Simple next ->
//                    handleNextSubAction restartHandler cont (next ctx)
//                | Msg next ->
//                    become (waitForMessage restartHandler cont next)
//                | Persist _ ->
//                    logError ctx "Got persist in simple actor"
//                    stop ()
//                | RestartHandlerUpdate (newHandler, next) ->
//                    setRestartHandler newHandler
//                    handleNextSubAction newHandler cont (next ())
//
//            and waitForMessage restartHandler cont next (msg: obj) =
//                match msg with
//                | :? LifecycleEvent as evt ->
//                    handleLifecycle restartHandler evt
//                | :? RestartHandlerMsg ->
//                    ignored ()
//                | _ ->
//                    setRestartHandler restartHandler
//                    handleNextSubAction restartHandler cont (next msg)
//
//            and waitForStop (msg: obj) =
//                match msg with
//                | :? Stopped ->
//                    stop ()
//                | _ ->
//                    ignored ()
//
//            and waitForEvent restartHandler next (msg: obj) =
//                match msg with
//                | :? LifecycleEvent as evt ->
//                    handleLifecycle restartHandler evt
//                | :? Persisted as persisted ->
//                    setRestartHandler restartHandler
//                    let cont = next persisted.evt
//                    handleNextAction restartHandler cont
//                | _ ->
//                    ignored ()
//
//            let rec handleNextRecoveryAction restartHandler (action: Action<EventSourcedActor<NoSnapshotting>, unit>) =
//                match action with
//                | Stop _
//                | Done _ ->
//                    stop ()
//                | Simple next ->
//                    handleNextRecoveryAction restartHandler (next ctx)
//                | Msg _ ->
//                    logError ctx "Got Msg in persistent workflow"
//                    stop ()
//                | Persist (subAction, next) ->
//                    become (waitForRecoveryEvent restartHandler subAction next)
//                | RestartHandlerUpdate (newHandler, next) ->
//                    setRestartHandler newHandler
//                    handleNextRecoveryAction newHandler (next ())
//
//            and waitForRecoveryEvent restartHandler subAction next (msg:obj) =
//                match msg with
//                | :? LifecycleEvent as evt ->
//                    handleLifecycle restartHandler evt
//                | :? Stopped ->
//                    stop ()
//                | :? Persisted as persisted ->
//                    handleNextRecoveryAction restartHandler (next persisted.evt)
//                | PersistentLifecycleEvent ReplaySucceed ->
//                    setRestartHandler restartHandler
//                    handleNextSubAction restartHandler next subAction
//                | PersistentLifecycleEvent (ReplayFailed (exn, cause)) ->
//                    logErrorf ctx "Replay failed cur to msg %A: %A" cause exn
//                    stop ()
//                | _ ->
//                    ignored ()
//
//            let initRestartHandler = Some (fun msg exn ->
//                Logging.logErrorf ctx "Actor crashed before actions started with msg %A: %A" msg exn
//            )
//
//            let waitForStart (msg: obj) =
//                match msg with
//                | :? LifecycleEvent as evt ->
//                    match evt with
//                    | PreStart ->
//                        setRestartHandler initRestartHandler
//                        handleNextRecoveryAction initRestartHandler action
//                    | _ ->
//                        handleLifecycle initRestartHandler evt
//                | _ ->
//                    ignored ()
//
//            become waitForStart
//
//        retype (spawnFunc {
//            propsPersist runActor with
//                Dispatcher = props.dispatcher
//                Mailbox = props.mailbox
//                Deploy = props.deploy
//                Router = props.router
//                SupervisionStrategy = props.supervisionStrategy
//        })

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
