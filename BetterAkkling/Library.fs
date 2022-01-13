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

    type private ActionInfo<'Args, 'Result, 'Cont> = {
        args: 'Args
        next: 'Result -> 'Cont
    }

    type private Action<'cont> =
        | Stop of ActionInfo<unit, unit, 'cont>
        | GetActor of ActionInfo<unit, Akka.Actor.IActorRef, 'cont>
        | UnsafeGetActorCtx of ActionInfo<unit, Akkling.Actors.Actor<obj>, 'cont>
        | GetLogger of ActionInfo<unit, Logger, 'cont>
        | GetMsg of ActionInfo<unit, obj, 'cont>
        | GetSender of ActionInfo<unit, Akka.Actor.IActorRef, 'cont>
        | CreateChildActor of ActionInfo<Akka.Actor.IActorRefFactory -> Akka.Actor.IActorRef, Akka.Actor.IActorRef, 'cont>
        | Stash of ActionInfo<unit, unit, 'cont>
        | UnstashOne of ActionInfo<unit, unit, 'cont>
        | UnstashAll of ActionInfo<unit, unit, 'cont>
        | Watch of ActionInfo<Akka.Actor.IActorRef, unit, 'cont>
        | Unwatch of ActionInfo<Akka.Actor.IActorRef, unit, 'cont>
        | Schedule of ActionInfo<Akkling.Actors.Actor<obj> -> Akka.Actor.ICancelable, Akka.Actor.ICancelable, 'cont>
        | Select of ActionInfo<string, Akka.Actor.ActorSelection, 'cont>
//        | SetCrashHandler
//        | ClearCrashHandler


    type Program<'Result> =
        private
        | Done of 'Result
        | More of Action<Program<'Result>>

    let private updateAction case payload f = case {
        args = payload.args
        next = payload.next >> f
    }

    let private mapA f a =
        match a with
        | Stop info -> updateAction Stop info f
        | GetActor info -> updateAction GetActor info f
        | UnsafeGetActorCtx info -> updateAction UnsafeGetActorCtx info f
        | GetLogger info -> updateAction GetLogger info f
        | GetMsg info -> updateAction GetMsg info f
        | GetSender info -> updateAction GetSender info f
        | CreateChildActor info -> updateAction CreateChildActor info f
        | Stash info -> updateAction Stash info f
        | UnstashOne info -> updateAction UnstashOne info f
        | UnstashAll info -> updateAction UnstashAll info f
        | Watch info -> updateAction Watch info f
        | Unwatch info -> updateAction Unwatch info f
        | Schedule info -> updateAction Schedule info f
        | Select info -> updateAction Select info f

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
                    | GetActor info ->
                        handleNextAction state (info.next (Akkling.ActorRefs.untyped ctx.Self))
                    | UnsafeGetActorCtx info ->
                        handleNextAction state (info.next ctx)
                    | GetLogger info ->
                        handleNextAction state (info.next (Logger ctx))
                    | GetMsg info ->
                        Akkling.Spawn.become (handleMessage state info.next)
                    | GetSender info ->
                        handleNextAction state (info.next (ctx.Sender () |> Akkling.ActorRefs.untyped))
                    | CreateChildActor info ->
                        let newAct = info.args ctx
                        handleNextAction state (info.next newAct)
                    | Stash info ->
                        ctx.Stash ()
                        handleNextAction state (info.next ())
                    | UnstashOne info ->
                        ctx.Unstash ()
                        handleNextAction state (info.next ())
                    | UnstashAll info ->
                        ctx.UnstashAll ()
                        handleNextAction state (info.next ())
                    | Watch info ->
                        ctx.Watch info.args |> ignore
                        handleNextAction state (info.next ())
                    | Unwatch info ->
                        ctx.Unwatch info.args |> ignore
                        handleNextAction state (info.next ())
                    | Schedule info ->
                        let cancel = info.args ctx
                        handleNextAction state (info.next cancel)
                    | Select info ->
                        let selection = ctx.ActorSelection info.args
                        handleNextAction state (info.next selection)

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

    let private noArg = {args = (); next = Done}
    let private withArg args = {args = args; next = Done}
    let getActor () = More (GetActor noArg)
    let unsafeGetActorCtx () = More (UnsafeGetActorCtx noArg)
    let getLogger () = More (GetLogger noArg)
    let stop () = More (Stop noArg)
    let receive () = More (GetMsg noArg)
    let rec receiveOnly<'Msg> () = actor {
        match! receive () with
        | :? 'Msg as m -> return m
        | _ -> return! receiveOnly<'Msg> ()
    }
    let getSender () = More (GetSender noArg)
    let createChild (make: Akka.Actor.IActorRefFactory -> Akkling.ActorRefs.IActorRef<'Msg>) =
        let create () = More (CreateChildActor (withArg (make >> Akkling.ActorRefs.untyped)))
        actor {
            let! untyped = create ()
            let typed : Akkling.ActorRefs.IActorRef<'Msg> = Akkling.ActorRefs.typed untyped
            return typed
        }

    let stash () = More (Stash noArg)
    let unstashOne () = More (UnstashOne noArg)
    let unstashAll () = More (UnstashAll noArg)
    let watch act = More (Watch (withArg (Akkling.ActorRefs.untyped act)))
    let unwatch act = More (Unwatch (withArg (Akkling.ActorRefs.untyped act)))
    let schedule delay receiver msg = More (Schedule (withArg (fun ctx -> ctx.Schedule delay receiver msg)))
    let scheduleRepeatedly delay interval receiver msg =
        More (Schedule (withArg (fun ctx -> ctx.ScheduleRepeatedly delay interval receiver msg)))
    let select path = More (Select (withArg path))


