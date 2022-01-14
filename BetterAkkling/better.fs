module  BetterAkkling.Better

open Akkling

type RestartHandler = obj -> exn -> unit

type Action<'Type, 'Result> =
    private
    | Done of 'Result
    | Simple of (Actors.Actor<obj> -> Action<'Type, 'Result>)
    | Msg of (obj -> Action<'Type, 'Result>)
    | RestartHandlerUpdate of Option<RestartHandler> * (unit -> Action<'Type, 'Result>)
    | Stop of (unit -> Action<'Type, 'Result>)

let rec private bind (f: 'a -> Action<'t, 'b>) (op: Action<'t, 'a>) : Action<'t, 'b> =
    match op with
    | Done res -> f res
    | Simple cont -> Simple (cont >> bind f)
    | Msg cont -> Msg (cont >> bind f)
    | RestartHandlerUpdate (update, cont) -> RestartHandlerUpdate (update, cont >> bind f)
    | Stop cont -> Stop (cont >> bind f)

type ActorBuilder () =
    member this.Bind (x, f) = bind f x
    member this.Return x = Done x
    member this.ReturnFrom x = x
    member this.Zero () = Done ()
    member this.Combine(m1, m2) = this.Bind (m1, fun _ -> m2)
    member this.Delay f = f ()

let actor = ActorBuilder ()

type SimpleActor = class end
type PersistentActor = class end

type ActorType =
    | NotPersistent of Action<SimpleActor, unit>
    | InMemoryPersistent of Action<SimpleActor, unit>
    | ProcessPersistent of Action<PersistentActor, unit>

type Props = {
    name: Option<string>
    dispatcher: Option<string>
    mailbox: Option<string>
    deploy: Option<Akka.Actor.Deploy>
    router: Option<Akka.Routing.RouterConfig>
    supervisionStrategy: Option<Akka.Actor.SupervisorStrategy>
}
with
    static member Anonymous = {
        name =  None
        dispatcher = None
        mailbox = None
        deploy = None
        router = None
        supervisionStrategy = None
    }

    static member Named name = {
        name =  Some name
        dispatcher = None
        mailbox = None
        deploy = None
        router = None
        supervisionStrategy = None
    }

type private Start = Start
type RestartHandlerMsg = RestartHandlerMsg

let private doSpawnSimple spawnFunc (props: Props) action =

    let runActor (ctx: Actors.Actor<obj>) =

        let rec handleNextAction restartHandler program =
            match program with
            | Stop _
            | Done _ ->
                stop ()
            | Simple next ->
                handleNextAction restartHandler (next ctx)
            | Msg next ->
                become (waitForMessage restartHandler next)
            | RestartHandlerUpdate (newHandler, next) ->
                ActorRefs.retype ctx.Self <! RestartHandlerMsg
                become (waitForNewHandler newHandler next)

        and waitForNewHandler restartHandler next (msg: obj) =
            match msg with
            | :? RestartHandlerMsg ->
                ctx.UnstashAll ()
                handleNextAction restartHandler (next ())
            | :? LifecycleEvent as evt ->
                match evt with
                | PreRestart(exn, msg) ->
                    restartHandler |> Option.iter (fun f -> f msg exn)
                | _ ->
                    ()
                ignored ()
            | _ ->
                ctx.Stash ()
                ignored ()

        and waitForMessage restartHandler next (msg: obj) =
            match msg with
            | :? LifecycleEvent as evt ->
                match evt with
                | PreRestart(exn, msg) ->
                    restartHandler |> Option.iter (fun f -> f msg exn)
                | _ ->
                    ()
                ignored ()
            | _ ->
                handleNextAction restartHandler (next msg)

        let waitForStart (msg: obj) =
            match msg with
            | :? LifecycleEvent as evt ->
                match evt with
                | PreRestart(exn, msg) ->
                    Logging.logErrorf ctx "Actor crashed before actions started with msg %A: %A" msg exn
                | _ ->
                    ()
                ignored ()
            | :? Start ->
                handleNextAction None action
            | _ ->
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
    retype act <! Start
    retype act

let spawn parent props actorType =
    match props.name, actorType with
    | Some name, NotPersistent action ->
        doSpawnSimple (spawn parent name) props action
    | None, NotPersistent action ->
        doSpawnSimple (spawnAnonymous parent) props action
    | _ ->
        failwith "Not support yet"

let getActor () = Simple (fun ctx -> Done (ActorRefs.retype ctx.Self))
let unsafeGetActorCtx () = Simple (fun ctx -> Done ctx)

type Logger internal (ctx:Actors.Actor<obj>) =
    member _.Log level msg = Logging.log level ctx msg
    member _.Logf level = Logging.logf level ctx
    member _.LogException err = Logging.logException ctx err
    member _.Debug msg = Logging.logDebug ctx msg
    member _.Debugf = Logging.logDebugf ctx
    member _.Info msg = Logging.logInfo ctx msg
    member _.Infof = Logging.logInfof ctx
    member _.Warning msg = Logging.logWarning ctx msg
    member _.Warningf = Logging.logWarningf ctx
    member _.Error msg = Logging.logError ctx msg
    member _.Errorf = Logging.logErrorf ctx

let getLogger () = Simple (fun ctx -> Done (Logger ctx))

let stop () = Stop Done

let receiveAny () : Action<SimpleActor, obj> = Msg Done
let rec receiveOnly<'Msg> () : Action<SimpleActor, 'Msg> = actor {
    match! receiveAny () with
    | :? 'Msg as m -> return m
    | _ -> return! receiveOnly<'Msg> ()
}
let getSender () = Simple (fun ctx -> Done (ctx.Sender ()))

let createChild (make: Akka.Actor.IActorRefFactory -> ActorRefs.IActorRef<'Msg>) =
    Simple (fun ctx -> Done (make ctx))

let stash () = Simple (fun ctx -> Done (ctx.Stash ()))
let unstashOne () = Simple (fun ctx -> Done (ctx.Unstash ()))
let unstashAll () = Simple (fun ctx -> Done (ctx.UnstashAll ()))

let watch act = Simple (fun ctx -> Done (ctx.Watch (ActorRefs.untyped act) |> ignore))
let unwatch act = Simple (fun ctx -> Done (ctx.Unwatch (ActorRefs.untyped act) |> ignore))

let schedule delay receiver msg = Simple (fun ctx -> Done (ctx.Schedule delay receiver msg))
let scheduleRepeatedly delay interval receiver msg =
    Simple (fun ctx -> Done (ctx.ScheduleRepeatedly delay interval receiver msg))

let select (path: string) = Simple (fun ctx -> Done (ctx.ActorSelection path))

let setRestartHandler handler = RestartHandlerUpdate (Some handler, Done)
let clearRestartHandler () = RestartHandlerUpdate (None, Done)

type PersistResult<'Result> =
    | Persist of 'Result
    | NoPersist of 'Result

let persist (_action: Action<SimpleActor, PersistResult<'Result>>) : Action<PersistentActor, 'Result> = actor {
    return Unchecked.defaultof<_>
}
