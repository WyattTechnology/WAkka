module WAkka.EventSourced

open Context
open CommonActions

type EventSourcedAction<'Result> = ActionBase<'Result, Simple.SimpleAction<obj>>

let rec private bind (f: 'a -> EventSourcedAction<'b>) (op: EventSourcedAction<'a>) : EventSourcedAction<'b> =
    bindBase f op

type ActorBuilder () =
    member this.Bind (x, f) = bind f x
    member this.Return x = Done x
    member this.ReturnFrom x = x
    member this.Zero () = Done ()
    member this.Combine(m1, m2) = this.Bind (m1, fun _ -> m2)
    member this.Delay f = f ()

let actor = ActorBuilder ()

module private EventSourcedActor =

    type Stopped = Stopped

    type Actor(startAction: EventSourcedAction<unit>) as this =

        inherit Akka.Persistence.UntypedPersistentActor ()

        let ctx = Akka.Persistence.Eventsourced.Context
        let logger = Akka.Event.Logging.GetLogger ctx
        let mutable restartHandler = Option<RestartHandler>.None

        let mutable msgHandler = fun (_recovering: bool) _ -> ()

        let rec handleActions recovering action =
            match action with
            | Done _
            | Stop _ ->
                ctx.Stop ctx.Self
            | Simple next ->
                handleActions recovering (next this)
            | Extra (subAction, next) ->
                if recovering then
                    msgHandler <- handleRecoveryAction next subAction
                else
                    handlePersistAction next subAction

        and handlePersistAction cont subAction =
            match subAction with
            | Simple.SimpleAction.Done res ->
                this.Persist (res, fun evt -> handleActions false (cont evt))
            | Simple.SimpleAction.Stop _ ->
                this.Persist(Stopped, fun _ -> ctx.Stop ctx.Self)
            | Simple.SimpleAction.Simple next ->
                handlePersistAction cont (next this)
            | Simple.SimpleAction.Extra (_, next) ->
                msgHandler <- (fun _ msg -> handlePersistAction cont (next msg))

        and handleRecoveryAction next action stillRecovering msg =
            if stillRecovering then
                handleActions true (next msg)
            else
                handlePersistAction next action

        do msgHandler <- (fun recovering msg -> logger.Error $"Got msg before first receive(recovering = {recovering}): {msg}")

        override this.PersistenceId = ctx.Self.Path.ToString()

        override _.OnCommand (msg: obj) =
            msgHandler false msg

        override _.OnPersistRejected(cause, event, sequenceNr) =
            logger.Error $"rejected event ({sequenceNr}) {event}: {cause}"
            ctx.Stop ctx.Self

        override _.OnRecover (msg: obj) =
            match msg with
            | :? Akka.Persistence.RecoveryCompleted ->
                msgHandler false msg
            | :? Stopped ->
                ctx.Stop ctx.Self
            | _ ->
                msgHandler true msg

        override _.OnRecoveryFailure(reason, message) =
            logger.Error $"recovery failed on message {message}: {reason}"
            ctx.Stop ctx.Self

        override this.PreRestart(reason, message) =
            logger.Error $"Actor crashed on {message}: {reason}"
            restartHandler |> Option.iter (fun handler -> handler this message reason)
            base.PreRestart(reason, message)

        override _.PreStart () =
            handleActions true startAction

        interface IActionContext with
            member _.Self = ctx.Self
            member _.Logger = Logger.Logger logger
            member _.Sender = ctx.Sender
            member _.Scheduler = ctx.System.Scheduler
            member _.ActorFactory = ctx :> Akka.Actor.IActorRefFactory
            member _.Watch act = ctx.Watch act |> ignore
            member _.Unwatch act = ctx.Unwatch act |> ignore
            member _.ActorSelection (path: string) = ctx.ActorSelection path
            member _.ActorSelection (path: Akka.Actor.ActorPath) = ctx.ActorSelection path
            member _.Stash = this.Stash
            member _.SetRestartHandler handler = restartHandler <- Some handler
            member _.ClearRestartHandler () = restartHandler <- None


let internal spawn (parent: Akka.Actor.IActorRefFactory) (props: Props) (action: EventSourcedAction<unit>) =
    let actProps = Akka.Actor.Props.Create(fun () -> EventSourcedActor.Actor(action))
    props.dispatcher |> Option.iter(fun d -> actProps.WithDispatcher d |> ignore)
    props.deploy |> Option.iter(fun d -> actProps.WithDeploy d |> ignore)
    props.mailbox |> Option.iter(fun d -> actProps.WithMailbox d |> ignore)
    props.router |> Option.iter(fun d -> actProps.WithRouter d |> ignore)
    props.supervisionStrategy |> Option.iter(fun d -> actProps.WithSupervisorStrategy d |> ignore)
    let act =
        match props.name with
        | Some name ->
            parent.ActorOf(actProps, name)
        | None ->
            parent.ActorOf(actProps)
    Akkling.ActorRefs.typed act

module Actions =

    let private persistObj (action: Simple.SimpleAction<obj>): EventSourcedAction<obj> =
        Extra (action, Done)

    let persist (action: Simple.SimpleAction<'Msg>): EventSourcedAction<'Result> = actor {
        let! evt = persistObj (Simple.actor {
            let! res = action
            return (res :> obj)
        })
        return (evt :?> 'Result)
    }

