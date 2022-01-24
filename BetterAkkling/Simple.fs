module BetterAkkling.Simple

open System
open BetterAkkling.Context

type Action<'Result> =
    internal
    | Done of 'Result
    | Simple of (IActionContext -> Action<'Result>)
    | Msg of (obj -> Action<'Result>)
    | Stop of (unit -> Action<'Result>)

let rec private bind (f: 'a -> Action<'b>) (op: Action<'a>) : Action<'b> =
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


module private SimpleActor =

    type Start = {
        checkpoint: Option<obj -> Action<unit>>
        restartHandler: Option<RestartHandler>
    }

    type Actor (persist: bool, startAction: Action<unit>) as this =

        inherit Akka.Actor.UntypedActor ()

        let ctx = Akka.Actor.UntypedActor.Context :> Akka.Actor.IActorContext
        let logger = Akka.Event.Logging.GetLogger ctx
        let mutable stash = Unchecked.defaultof<Akka.Actor.IStash>

        let mutable restartHandler = Option<RestartHandler>.None

        let mutable msgHandler = fun _ -> ()
        let mutable checkpoint = None

        let rec handleActions action =
            match action with
            | Done _
            | Stop _ ->
                ctx.Stop ctx.Self
            | Simple next ->
                handleActions (next this)
            | Msg next ->
                checkpoint <- Some next
                msgHandler <- waitForMsg next

        and waitForMsg next (msg:obj) =
            match msg with
            | :? Start -> ()
            | _ -> handleActions (next msg)

        let waitForStart (msg:obj) =
            match msg with
            | :? Start as start ->
                restartHandler <- start.restartHandler
                match start.checkpoint with
                | None ->
                    handleActions startAction
                | Some action ->
                    checkpoint <- start.checkpoint
                    msgHandler <- waitForMsg action
                stash.UnstashAll ()
            | _ ->
                stash.Stash ()

        do msgHandler <- waitForStart

        override _.OnReceive (msg: obj) = msgHandler msg

        override _.PreRestart(err: exn, msg: obj) =
            logger.Error $"Actor restarting after message {msg}: {err}"
            restartHandler |> Option.iter(fun h -> h this msg err)
            if persist then
                ctx.Self.Tell({checkpoint = checkpoint; restartHandler = restartHandler}, ctx.Self)
            base.PreRestart (err, msg)

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
            member _.Stash = stash
            member _.SetRestartHandler handler = restartHandler <- Some handler
            member _.ClearRestartHandler () = restartHandler <- None

        interface Akka.Actor.IWithUnboundedStash with
            member _.Stash
                with get () = stash
                and set newStash = stash <- newStash

type ActorType =
    | NotPersisted of Action<unit>
    | Checkpointed of Action<unit>

let spawn (parent: Akka.Actor.IActorRefFactory) (props: Props) (actorType: ActorType) =
    let persist, action =
        match actorType with
        | NotPersisted action -> false, action
        | Checkpointed action -> true, action
    let actProps = Akka.Actor.Props.Create(fun () -> SimpleActor.Actor(persist, action))
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
    act.Tell({SimpleActor.checkpoint = None; SimpleActor.restartHandler = None}, act)
    Akkling.ActorRefs.typed act

type private Timeout = {started: DateTime}

let private doSchedule delay (receiver: Akkling.ActorRefs.IActorRef<'msg>) (msg: 'msg) =
    Simple (fun ctx ->
        Done (
            let cancel = new Akka.Actor.Cancelable (ctx.Scheduler)
            ctx.Scheduler.ScheduleTellOnce (delay, Akkling.ActorRefs.untyped receiver, msg, ctx.Self, cancel)
            cancel :> Akka.Actor.ICancelable
        )
    )
let private doScheduleRepeatedly initialDelay interval (receiver: Akkling.ActorRefs.IActorRef<'msg>) (msg: 'msg) =
    Simple (fun ctx ->
        Done (
            let cancel = new Akka.Actor.Cancelable (ctx.Scheduler)
            ctx.Scheduler.ScheduleTellRepeatedly (initialDelay, interval, Akkling.ActorRefs.untyped receiver, msg, ctx.Self, cancel)
            cancel :> Akka.Actor.ICancelable
        )
    )

type CommonActions internal () =

    static member getActor () = Simple (fun ctx -> Done (Akkling.ActorRefs.typed ctx.Self))
    static member unsafeGetActorCtx () = Simple (fun ctx -> Done (ctx :> IActorContext))

    static member getLogger () = Simple (fun ctx -> Done ctx.Logger)

    static member stop () = Stop Done

    static member getSender () = Simple (fun ctx -> Done (Akkling.ActorRefs.typed ctx.Sender))

    static member createChild (make: Akka.Actor.IActorRefFactory -> Akkling.ActorRefs.IActorRef<'Msg>) =
        Simple (fun ctx -> Done (make ctx.ActorFactory))

    static member send (recv: Akkling.ActorRefs.IActorRef<'Msg>) msg = Simple (fun ctx -> Done (recv.Tell (msg, ctx.Self)))

    static member stash () : Action<unit> = Simple (fun ctx -> Done (ctx.Stash.Stash ()))
    static member unstashOne () : Action<unit> = Simple (fun ctx -> Done (ctx.Stash.Unstash ()))
    static member unstashAll () : Action<unit> = Simple (fun ctx -> Done (ctx.Stash.UnstashAll ()))

    static member watch (act: Akkling.ActorRefs.IActorRef<'msg>) = Simple (fun ctx -> Done (ctx.Watch (Akkling.ActorRefs.untyped act)))
    static member watch (act: Akka.Actor.IActorRef) = Simple (fun ctx -> Done (ctx.Watch act))
    static member unwatch (act: Akkling.ActorRefs.IActorRef<'msg>) = Simple (fun ctx -> Done (ctx.Unwatch (Akkling.ActorRefs.untyped act)))
    static member unwatch (act: Akka.Actor.IActorRef) = Simple (fun ctx -> Done (ctx.Unwatch act))

    static member schedule delay receiver msg = doSchedule delay receiver msg
    static member scheduleRepeatedly delay interval receiver msg = doScheduleRepeatedly delay interval receiver msg

    static member select (path: string) = Simple (fun ctx -> Done (ctx.ActorSelection path))
    static member select (path: Akka.Actor.ActorPath) = Simple (fun ctx -> Done (ctx.ActorSelection path))

    static member setRestartHandler handler = Simple (fun ctx -> Done (ctx.SetRestartHandler handler))
    static member clearRestartHandler () = Simple (fun ctx -> Done (ctx.ClearRestartHandler ()))

type Actions private () =

    inherit CommonActions()

    static let doReceive () : Action<obj> = Msg Done

    static member receive (choose: obj -> Option<'Msg>) =
        let rec recv () = actor {
            match! doReceive () with
            | :? Timeout ->
                return! recv ()
            | msg ->
                match choose msg with
                | Some res ->
                    return res
                | None ->
                    return! recv ()
        }
        recv ()

    static member receive (choose: obj -> Option<'Msg>, timeout: TimeSpan) =
        let rec recv started (cancel: Akka.Actor.ICancelable) = actor {
            match! doReceive () with
            | :? Timeout as timeout ->
                if timeout.started = started then
                    return None
                else
                    return! recv started cancel
            | msg ->
                match choose msg with
                | Some res ->
                    cancel.Cancel()
                    return (Some res)
                | None ->
                    return! recv started cancel
        }
        actor {
            let now = DateTime.Now
            let! self = Actions.getActor ()
            let! cancel = doSchedule timeout (Akkling.ActorRefs.retype self) {started = now}
            return! recv now cancel
        }

    static member receiveAny () = Actions.receive Some
    static member receiveAny (timeout: TimeSpan) = Actions.receive(Some, timeout)

    static member receiveOnly<'Msg> () : Action<'Msg> =
        Actions.receive (fun msg ->
            match msg with
            | :? 'Msg as m -> Some m
            | _ -> None
        )
    static member receiveOnly<'Msg> (timeout: TimeSpan) : Action<Option<'Msg>> =
        Actions.receive (
            (fun msg ->
                match msg with
                | :? 'Msg as m -> Some m
                | _ -> None
            ),
            timeout
        )


//    static member private persistObj (action: Action<SimpleActor, obj>): Action<EventSourcedActor<'Snapshotting>, obj> =
//        Persist (action, Done)
//
//    static member persist (action: Action<SimpleActor, 'Result>): Action<EventSourcedActor<'Snapshotting>, 'Result> = actor {
//        let! evt = Actions.persistObj (actor {
//            let! res = action
//            return (res :> obj)
//        })
//        return (evt :?> 'Result)
//    }
//
//    static member snapshot (snapshot: 'Snapshot): Action<EventSourcedActor<WithSnapshotting<'Snapshot>>, 'Result> = actor {
//        return Unchecked.defaultof<_>
//    }

[<AutoOpen>]
module Ops =
    let (<!) (recv: Akkling.ActorRefs.IActorRef<'Msg>) msg = Actions.send recv msg
