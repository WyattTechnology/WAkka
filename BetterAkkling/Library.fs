namespace BetterAkkling

module Actor =

    type private ActorState () = class end

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

    type private Action<'cont> =
        | Stop of (unit -> 'cont)
        | GetActor of (Akka.Actor.IActorRef -> 'cont)
        | UnsafeGetActorCtx of (Akkling.Actors.Actor<obj> -> 'cont)
        | GetLogger of (Logger -> 'cont)
        | GetMsg of (obj -> 'cont)
        | GetSender of (Akka.Actor.IActorRef -> 'cont)
        | CreateChildActor of (Akka.Actor.IActorRefFactory -> Akka.Actor.IActorRef) * (Akka.Actor.IActorRef -> 'cont)
        | Stash of (unit -> 'cont)
        | UnstashOne of (unit -> 'cont)
        | UnstashAll of (unit -> 'cont)
        | Watch of Akka.Actor.IActorRef * (unit -> 'cont)
        | Unwatch of Akka.Actor.IActorRef * (unit -> 'cont)
        | Schedule of (Akkling.Actors.Actor<obj> -> Akka.Actor.ICancelable) * (Akka.Actor.ICancelable -> 'cont)
        | Select of string * (Akka.Actor.ActorSelection -> 'cont)
//        | SetCrashHandler
//        | ClearCrashHandler


    type Program<'Result> =
        private
        | Done of 'Result
        | More of Action<Program<'Result>>

    let private mapA f a =
        match a with
        | Stop next -> Stop (next >> f)
        | GetActor next -> GetActor (next >> f)
        | UnsafeGetActorCtx next -> UnsafeGetActorCtx (next >> f)
        | GetLogger next -> GetLogger (next >> f)
        | GetMsg next -> GetMsg (next >> f)
        | GetSender next -> GetSender (next >> f)
        | CreateChildActor (make, next) -> CreateChildActor (make, next >> f)
        | Stash next -> Stash (next >> f)
        | UnstashOne next -> UnstashOne (next >> f)
        | UnstashAll next -> UnstashAll (next >> f)
        | Watch (act, next) -> Watch (act, next >> f)
        | Unwatch (act, next) -> Unwatch (act, next >> f)
        | Schedule (act, next) -> Schedule (act, next >> f)
        | Select (act, next) -> Select (act, next >> f)

    let rec private bind f p =
        match p with
        | More next -> next |> mapA (bind f) |> More
        | Done res -> f res

    type ActorBuilder () =
        member this.Bind (x, f) = bind f x
        member this.Return x = Done x
        member this.ReturnFrom x = x
        member this.Zero () = Done ()
        member this.Combine(m1, m2) = this.Bind (m1, fun _ -> m2)
        member this.Delay f = f ()

    let actor = ActorBuilder ()

    type Props = {
        dispatcher: Option<string>
        mailbox: Option<string>
        deploy: Option<Akka.Actor.Deploy>
        router: Option<Akka.Routing.RouterConfig>
        supervisionStrategy: Option<Akka.Actor.SupervisorStrategy>
        program: Program<unit>
    }

    let props program = {
        dispatcher = None
        mailbox = None
        deploy = None
        router = None
        supervisionStrategy = None
        program = program
    }

    let private doSpawn spawnFunc props =

        let runActor (ctx: Akkling.Actors.Actor<obj>) =
            let rec handleNextAction state program =
                match program with
                | Done _ ->
                    Akkling.Spawn.stop ()
                | More nextAction ->
                    match nextAction with
                    | Stop _ ->
                        Akkling.Spawn.stop ()
                    | GetActor next ->
                        handleNextAction state (next (Akkling.ActorRefs.untyped ctx.Self))
                    | UnsafeGetActorCtx next ->
                        handleNextAction state (next ctx)
                    | GetLogger next ->
                        handleNextAction state (next (Logger ctx))
                    | GetMsg next ->
                        Akkling.Spawn.become (handleMessage state next)
                    | GetSender next ->
                        handleNextAction state (next (ctx.Sender () |> Akkling.ActorRefs.untyped))
                    | CreateChildActor(make, next) ->
                        let newAct = make ctx
                        handleNextAction state (next newAct)
                    | Stash next ->
                        ctx.Stash ()
                        handleNextAction state (next ())
                    | UnstashOne next ->
                        ctx.Unstash ()
                        handleNextAction state (next ())
                    | UnstashAll next ->
                        ctx.UnstashAll ()
                        handleNextAction state (next ())
                    | Watch (act, next) ->
                        ctx.Watch act |> ignore
                        handleNextAction state (next ())
                    | Unwatch (act, next) ->
                        ctx.Unwatch act |> ignore
                        handleNextAction state (next ())
                    | Schedule (sched, next) ->
                        let cancel = sched ctx
                        handleNextAction state (next cancel)
                    | Select (path, next) ->
                        let selection = ctx.ActorSelection path
                        handleNextAction state (next selection)

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
        doSpawn (Akkling.Spawn.spawn parent name) props

    let spawnAnonymous parent props =
        doSpawn (Akkling.Spawn.spawnAnonymous parent) props

    let getActor () = More (GetActor Done)
    let unsafeGetActorCtx () = More (UnsafeGetActorCtx Done)
    let getLogger () = More (GetLogger Done)
    let stop () = More (Stop Done)
    let receive () = More (GetMsg Done)
    let rec receiveOnly<'Msg> () = actor {
        match! receive () with
        | :? 'Msg as m -> return m
        | _ -> return! receiveOnly<'Msg> ()
    }
    let getSender () = More (GetSender Done)
    let createChild (make: Akka.Actor.IActorRefFactory -> Akkling.ActorRefs.IActorRef<'Msg>) =
        let create () = More (CreateChildActor (make >> Akkling.ActorRefs.untyped, Done))
        actor {
            let! untyped = create ()
            let typed : Akkling.ActorRefs.IActorRef<'Msg> = Akkling.ActorRefs.typed untyped
            return typed
        }

    let stash () = More (Stash Done)
    let unstashOne () = More (UnstashOne Done)
    let unstashAll () = More (UnstashAll Done)
    let watch act = More (Watch (Akkling.ActorRefs.untyped act, Done))
    let unwatch act = More (Unwatch (Akkling.ActorRefs.untyped act, Done))
    let schedule delay receiver msg = More (Schedule ((fun ctx -> ctx.Schedule delay receiver msg), Done))
    let scheduleRepeatedly delay interval receiver msg =
        More (Schedule ((fun ctx -> ctx.ScheduleRepeatedly delay interval receiver msg), Done))
    let select path = More (Select (path, Done))


