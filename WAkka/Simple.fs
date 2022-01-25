module WAkka.Simple

open System

open Context
open CommonActions

type SimpleType () = class end

type SimpleAction<'Result> = ActionBase<'Result, SimpleType>

let rec private bind (f: 'a -> SimpleAction<'b>) (op: SimpleAction<'a>) : SimpleAction<'b> =
    bindBase f op

type ActorBuilder () =
    member this.Bind (x, f) = bind f x
    member this.Return x = Done x
    member this.ReturnFrom x = x
    member this.Zero () = Done ()
    member this.Combine(m1, m2) = this.Bind (m1, fun _ -> m2)
    member this.Delay f = f ()

let actor = ActorBuilder ()

module private SimpleActor =

    type Start = {
        checkpoint: Option<obj -> SimpleAction<unit>>
        restartHandler: Option<RestartHandler>
    }

    type Actor (persist: bool, startAction: SimpleAction<unit>) as this =

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
            | Extra (_, next) ->
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

let internal spawn (parent: Akka.Actor.IActorRefFactory) (props: Props) (persist: bool) (action: SimpleAction<unit>) =
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


module Actions =

    type private Timeout = {started: DateTime}

    let private doReceive () : SimpleAction<obj> = Extra (SimpleType (), Done)

    let receive (choose: obj -> Option<'Msg>) =
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

    let receiveWithTimeout (choose: obj -> Option<'Msg>, timeout: TimeSpan) =
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
            let! self = getActor ()
            let! cancel = doSchedule timeout (Akkling.ActorRefs.retype self) {started = now}
            return! recv now cancel
        }

    let receiveAny () = receive Some
    let receiveAnyWithTimeout (timeout: TimeSpan) = receiveWithTimeout(Some, timeout)

    let receiveOnly<'Msg> () : SimpleAction<'Msg> =
        receive (fun msg ->
            match msg with
            | :? 'Msg as m -> Some m
            | _ -> None
        )
    let receiveOnlyWithTimeout<'Msg> (timeout: TimeSpan) : SimpleAction<Option<'Msg>> =
        receiveWithTimeout (
            (fun msg ->
                match msg with
                | :? 'Msg as m -> Some m
                | _ -> None
            ),
            timeout
        )

    let getSender () = Simple (fun ctx -> Done (Akkling.ActorRefs.typed ctx.Sender))

    let stash () : SimpleAction<unit> = Simple (fun ctx -> Done (ctx.Stash.Stash ()))
    let unstashOne () : SimpleAction<unit> = Simple (fun ctx -> Done (ctx.Stash.Unstash ()))
    let unstashAll () : SimpleAction<unit> = Simple (fun ctx -> Done (ctx.Stash.UnstashAll ()))

