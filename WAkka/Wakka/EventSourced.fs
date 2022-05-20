module WAkka.EventSourced

open Common

/// An action that can only be used directly in an event sourced actor (e.g. started using eventSourced).
type EventSourcedAction<'Result> = ActionBase<'Result, Simple.SimpleAction<obj>>

let rec private bind (f: 'a -> EventSourcedAction<'b>) (op: EventSourcedAction<'a>) : EventSourcedAction<'b> =
    bindBase f op

type ActorBuilder () =
    member this.Bind (x, f) = bind f x
    member this.Return x = Done x
    member this.ReturnFrom x = x
    member this.Zero () = Done ()
    member this.Combine(m1, m2) = this.Bind (m1, m2)
    member this.Delay (f: unit -> EventSourcedAction<'t>): (unit -> EventSourcedAction<'t>) = f
    member this.Run f = f ()
    member this.For(values, f) : ActionBase<unit, 't> =
        match Seq.tryHead values with
        | Some head ->
            let next () = this.For (Seq.tail values, f)
            bindBase next (f head)
        | None ->
            Done ()

/// Builds an EventSourced action.
let actor = ActorBuilder ()

module private EventSourcedActor =

    type Stopped = Stopped

    type Actor(startAction: EventSourcedAction<unit>) as this =

        inherit Akka.Persistence.UntypedPersistentActor ()

        let ctx = Akka.Persistence.Eventsourced.Context
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

        do msgHandler <- (fun recovering msg ->
            let logger = Akka.Event.Logging.GetLogger (ctx.System, ctx.Self.Path.ToStringWithAddress())
            logger.Error $"Got msg before first receive(recovering = {recovering}): {msg}"
        )

        override this.PersistenceId = ctx.Self.Path.ToString()

        override _.OnCommand (msg: obj) =
            msgHandler false msg

        override _.OnPersistRejected(cause, event, sequenceNr) =
            let logger = Akka.Event.Logging.GetLogger (ctx.System, ctx.Self.Path.ToStringWithAddress())
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
            let logger = Akka.Event.Logging.GetLogger (ctx.System, ctx.Self.Path.ToStringWithAddress())
            logger.Error $"recovery failed on message {message}: {reason}"
            ctx.Stop ctx.Self

        override this.PreRestart(reason, message) =
            let logger = Akka.Event.Logging.GetLogger (ctx.System, ctx.Self.Path.ToStringWithAddress())
            logger.Error $"Actor crashed on {message}: {reason}"
            restartHandler |> Option.iter (fun handler -> handler this message reason)
            base.PreRestart(reason, message)

        override _.PreStart () =
            handleActions true startAction

        interface IActionContext with
            member _.Context = ctx
            member _.Self = ctx.Self
            member _.Logger = Logger ctx
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

[<AutoOpen>]
module Actions =

    let private persistObj (action: Simple.SimpleAction<obj>): EventSourcedAction<obj> =
        Extra (action, Done)

    /// Runs the given SimpleAction and then persists the result. If the actor crashes, this action will skip running the
    /// action and return the persisted result instead.
    let persist (action: Simple.SimpleAction<'Msg>): EventSourcedAction<'Result> = actor {
        let! evt = persistObj (Simple.actor {
            let! res = action
            return (res :> obj)
        })
        return (evt :?> 'Result)
    }

///Maps the given function over the given array within an actor expression.
let mapArray (func: 'a -> EventSourcedAction<'b>) (values: 'a []) : EventSourcedAction<'b []> =
    let rec loop (results: 'b []) i = ActorBuilder () {
        if i < values.Length then
            let! res = func values.[i]
            results.[i] <- res
            return! loop results (i + 1)
        else
            return results
    }
    loop (Array.zeroCreate values.Length) 0

///Maps the given function over the given list within an actor expression.
let mapList (func: 'a -> EventSourcedAction<'b>) (values: List<'a>) : EventSourcedAction<List<'b>> =
    let rec loop (left: List<'a>) (results: List<'b>) = ActorBuilder () {
        match left with
        | head::tail ->
            let! res = func head
            return! loop tail (res :: results)
        | [] ->
            return List.rev results
    }
    loop values []

///Folds the given function over the given sequence of actions within an actor expression.
let foldActions (func: 'a -> 'res -> EventSourcedAction<'res>) (init: 'res) (values: seq<EventSourcedAction<'a>>) : EventSourcedAction<'res> =
    let rec loop left cur = actor {
        if Seq.isEmpty left then
            return cur
        else
            let! value = Seq.head left
            let! res = func value cur
            return! loop (Seq.tail left) res
    }
    loop values init

///Folds the given function over the given sequence within an actor expression.
let foldValues (func: 'a -> 'res -> EventSourcedAction<'res>) (init: 'res) (values: seq<'a>) : EventSourcedAction<'res> =
    let rec loop left cur = ActorBuilder () {
        if Seq.isEmpty left then
            return cur
        else
            let! res = func (Seq.head left) cur
            return! loop (Seq.tail left) res
    }
    loop values init
