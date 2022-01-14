module  BetterAkkling.Better

type Action<'Type, 'Result> =
    private
    | Done of 'Result
    | Simple of (Akkling.Actors.Actor<obj> -> Action<'Type, 'Result>)
    | Msg of (obj -> Action<'Type, 'Result>)
    | Stop of (unit -> Action<'Type, 'Result>)

let rec private bind (f: 'a -> Action<'t, 'b>) (op: Action<'t, 'a>) : Action<'t, 'b> =
    match op with
    | Done res -> f res
    | Simple cont -> Simple (cont >> bind f)
    | Msg cont -> Msg (cont >> bind f)
    | Stop cont -> Stop (cont >> bind f)

type ActorBuilder () =
    member this.Bind (x, f) = bind f x
    member this.Return x = Done x
    member this.ReturnFrom x = x
    member this.Zero () = Done ()
    member this.Combine(m1, m2) = this.Bind (m1, fun _ -> m2)
    member this.Delay f = f ()

let actor = ActorBuilder ()

type private ActorState () = class end

type Props<'Type> = {
    dispatcher: Option<string>
    mailbox: Option<string>
    deploy: Option<Akka.Actor.Deploy>
    router: Option<Akka.Routing.RouterConfig>
    supervisionStrategy: Option<Akka.Actor.SupervisorStrategy>
    program: Action<'Type, unit>
}

let props program = {
    dispatcher = None
    mailbox = None
    deploy = None
    router = None
    supervisionStrategy = None
    program = program
}

type SimpleActor = class end

let private doSpawnSimple spawnFunc (props: Props<SimpleActor>) =

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

        and handleMessage state next msg =
            //TODO: handle system messages
            handleNextAction state (next msg)

        handleNextAction (ActorState ()) props.program

    Akkling.ActorRefs.retype <| spawnFunc {
        Akkling.Props.props runActor with
            Dispatcher = props.dispatcher
            Mailbox = props.mailbox
            Deploy = props.deploy
            Router = props.router
            SupervisionStrategy = props.supervisionStrategy
    }

let spawn parent name props =
    doSpawnSimple (Akkling.Spawn.spawn parent name) props

let spawnAnonymous parent props =
    doSpawnSimple (Akkling.Spawn.spawnAnonymous parent) props

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
