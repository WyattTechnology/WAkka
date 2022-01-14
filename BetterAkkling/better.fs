module  BetterAkkling.Better

type private ActorState = {
    onRestart: Option<obj -> exn -> unit>
}
with
    static member Default = {
        onRestart = None
    }

type Action<'Type, 'Result> =
    private
    | Done of 'Result
    | Simple of (Akkling.Actors.Actor<obj> -> Action<'Type, 'Result>)
    | Msg of (obj -> Action<'Type, 'Result>)
    | StateUpdate of (ActorState -> ActorState) * (unit -> Action<'Type, 'Result>)
    | Stop of (unit -> Action<'Type, 'Result>)

let rec private bind (f: 'a -> Action<'t, 'b>) (op: Action<'t, 'a>) : Action<'t, 'b> =
    match op with
    | Done res -> f res
    | Simple cont -> Simple (cont >> bind f)
    | Msg cont -> Msg (cont >> bind f)
    | StateUpdate (update, cont) -> StateUpdate (update, cont >> bind f)
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

let private doSpawnSimple spawnFunc (props: Props) action =

    let runActor (ctx: Akkling.Actors.Actor<obj>) =

        let rec handleNextAction state program =
            match program with
            | Stop _
            | Done _ ->
                Akkling.Spawn.stop ()
            | Simple next ->
                handleNextAction state (next ctx)
            | Msg next ->
                Akkling.Spawn.become (handleMessage state next)
            | StateUpdate (update, next) ->
                handleNextAction (update state) (next ())

        and handleMessage state next (msg: obj) =
            match msg with
            | :? Akkling.Actors.LifecycleEvent as evt ->
                match evt with
                | Akkling.Actors.PreRestart(exn, msg) ->
                    state.onRestart |> Option.iter (fun f -> f msg exn)
                | _ -> ()
            | _ ->
                ()

            handleNextAction state (next msg)

        handleNextAction ActorState.Default action

    Akkling.ActorRefs.retype <| spawnFunc {
        Akkling.Props.props runActor with
            Dispatcher = props.dispatcher
            Mailbox = props.mailbox
            Deploy = props.deploy
            Router = props.router
            SupervisionStrategy = props.supervisionStrategy
    }

let spawn parent props actorType =
    match props.name, actorType with
    | Some name, NotPersistent action ->
        doSpawnSimple (Akkling.Spawn.spawn parent name) props action
    | None, NotPersistent action ->
        doSpawnSimple (Akkling.Spawn.spawnAnonymous parent) props action
    | _ ->
        failwith "Not support yet"

let getActor () = Simple (fun ctx -> Done (Akkling.ActorRefs.retype ctx.Self))
let unsafeGetActorCtx () = Simple (fun ctx -> Done ctx)

type Logger internal (ctx:Akkling.Actors.Actor<obj>) =
    member _.Log level msg = Akkling.Logging.log level ctx msg
    member _.Logf level = Akkling.Logging.logf level ctx
    member _.LogException err = Akkling.Logging.logException ctx err
    member _.Debug msg = Akkling.Logging.logDebug ctx msg
    member _.Debugf = Akkling.Logging.logDebugf ctx
    member _.Info msg = Akkling.Logging.logInfo ctx msg
    member _.Infof = Akkling.Logging.logInfof ctx
    member _.Warning msg = Akkling.Logging.logWarning ctx msg
    member _.Warningf = Akkling.Logging.logWarningf ctx
    member _.Error msg = Akkling.Logging.logError ctx msg
    member _.Errorf = Akkling.Logging.logErrorf ctx

let getLogger () = Simple (fun ctx -> Done (Logger ctx))

let stop () = Stop Done

let receiveAny () : Action<SimpleActor, obj> = Msg Done
let rec receiveOnly<'Msg> () : Action<SimpleActor, 'Msg> = actor {
    match! receiveAny () with
    | :? 'Msg as m -> return m
    | _ -> return! receiveOnly<'Msg> ()
}
let getSender () = Simple (fun ctx -> Done (ctx.Sender ()))

let createChild (make: Akka.Actor.IActorRefFactory -> Akkling.ActorRefs.IActorRef<'Msg>) =
    Simple (fun ctx -> Done (make ctx))

let stash () = Simple (fun ctx -> Done (ctx.Stash ()))
let unstashOne () = Simple (fun ctx -> Done (ctx.Unstash ()))
let unstashAll () = Simple (fun ctx -> Done (ctx.UnstashAll ()))

let watch act = Simple (fun ctx -> Done (ctx.Watch (Akkling.ActorRefs.untyped act) |> ignore))
let unwatch act = Simple (fun ctx -> Done (ctx.Unwatch (Akkling.ActorRefs.untyped act) |> ignore))

let schedule delay receiver msg = Simple (fun ctx -> Done (ctx.Schedule delay receiver msg))
let scheduleRepeatedly delay interval receiver msg =
    Simple (fun ctx -> Done (ctx.ScheduleRepeatedly delay interval receiver msg))

let select (path: string) = Simple (fun ctx -> Done (ctx.ActorSelection path))

let setOnRestart handler = StateUpdate ((fun state -> {state with onRestart = Some handler}), Done)
let clearOnRestart () = StateUpdate ((fun state -> {state with onRestart = None}), Done)

type PersistResult<'Result> =
    | Persist of 'Result
    | NoPersist of 'Result

let persist (_action: Action<SimpleActor, PersistResult<'Result>>) : Action<PersistentActor, 'Result> = actor {
    return Unchecked.defaultof<_>
}
