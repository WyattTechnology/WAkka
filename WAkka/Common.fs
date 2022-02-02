module WAkka.Common

/// A logger tied to an actor.
type Logger internal (logger: Akka.Event.ILoggingAdapter) =
    /// Log the given message at the given level.
    member _.Log level msg = logger.Log (level, msg)

    ///Log the given exception.
    member _.LogException (err: exn) = logger.Error err.Message

    ///Log the given message at the debug level.
    member _.Debug msg = logger.Debug msg

    ///Log the given message at the info level.
    member _.Info msg = logger.Info msg

    ///Log the given message at the warning level.
    member _.Warning msg = logger.Warning msg

    ///Log the given message at the error level.
    member _.Error msg = logger.Error msg
    member _.Errorf fmt = Printf.kprintf logger.Error fmt

/// An actor context.
type IActorContext =
    /// The reference for the actor.
    abstract member Self: Akka.Actor.IActorRef
    /// The logger for the actor.
    abstract member Logger: Logger
    /// The sender of the message most recently received by the actor.
    abstract member Sender: Akka.Actor.IActorRef
    /// The scheduler for the actor system that the actor is part of.
    abstract member Scheduler: Akka.Actor.IScheduler
    /// An IActorRefFactory that can be used to create children of the actor.
    abstract member ActorFactory: Akka.Actor.IActorRefFactory
    /// Watch the given actor for termination using this actor.
    abstract member Watch: Akka.Actor.IActorRef -> unit
    /// Stop watching the given actor for termination.
    abstract member Unwatch: Akka.Actor.IActorRef -> unit
    /// Get an actor selection for the given actor path.
    abstract member ActorSelection: string -> Akka.Actor.ActorSelection
    /// Get an actor selection for the given actor path.
    abstract member ActorSelection: Akka.Actor.ActorPath -> Akka.Actor.ActorSelection

/// A function the can be passed to the setRestartHandler action. The function is passed the actor context, the message
/// that was being processed when the crash happened, and the exception that caused the crash.
type RestartHandler = IActorContext -> obj -> exn -> unit

type internal IActionContext =
    inherit IActorContext

    abstract member Stash: Akka.Actor.IStash
    abstract member SetRestartHandler: RestartHandler -> unit
    abstract member ClearRestartHandler: unit -> unit

/// Actor Properties.
type Props = {
    /// The actor's name (usually the Anonymous and Named methods are used instead of setting this directly).
    name: Option<string>
    /// Specifies an alternate dispatcher type.
    dispatcher: Option<string>
    /// Specifies an alternate mailbox type.
    mailbox: Option<string>
    /// Specifies an alternate deployment type.
    deploy: Option<Akka.Actor.Deploy>
    /// Specifies an alternate router type.
    router: Option<Akka.Routing.RouterConfig>
    /// Specifies an alternate supervision strategy type for this actors children.
    supervisionStrategy: Option<Akka.Actor.SupervisorStrategy>
}
with
    /// Sets default properties for an anonymous actor.
    static member Anonymous = {
        name =  None
        dispatcher = None
        mailbox = None
        deploy = None
        router = None
        supervisionStrategy = None
    }

    /// Sets default properties for an actor with the given name.
    static member Named name = {Props.Anonymous with name = Some name}

[<AutoOpen>]
module CommonActions =
    type ActionBase<'Result, 'ExtraArg> =
        internal
        | Done of 'Result
        | Simple of (IActionContext -> ActionBase<'Result, 'ExtraArg>)
        | Stop of (unit -> ActionBase<'Result, 'ExtraArg>)
        | Extra of 'ExtraArg * (obj -> ActionBase<'Result, 'ExtraArg>)

    let rec internal bindBase (f: 'a -> ActionBase<'b, 't>) (op: ActionBase<'a, 't>) : ActionBase<'b, 't> =
        match op with
        | Done res -> f res
        | Simple cont -> Simple (cont >> bindBase f)
        | Stop cont -> Stop (cont >> bindBase f)
        | Extra (extra, cont) -> Extra (extra, cont >> bindBase f)

    let internal  doSchedule delay (receiver: Akkling.ActorRefs.IActorRef<'msg>) (msg: 'msg) =
        Simple (fun ctx ->
            Done (
                let cancel = new Akka.Actor.Cancelable (ctx.Scheduler)
                ctx.Scheduler.ScheduleTellOnce (delay, Akkling.ActorRefs.untyped receiver, msg, ctx.Self, cancel)
                cancel :> Akka.Actor.ICancelable
            )
        )
    let internal doScheduleRepeatedly initialDelay interval (receiver: Akkling.ActorRefs.IActorRef<'msg>) (msg: 'msg) =
        Simple (fun ctx ->
            Done (
                let cancel = new Akka.Actor.Cancelable (ctx.Scheduler)
                ctx.Scheduler.ScheduleTellRepeatedly (initialDelay, interval, Akkling.ActorRefs.untyped receiver, msg, ctx.Self, cancel)
                cancel :> Akka.Actor.ICancelable
            )
        )

    /// Gets the reference for this actor.
    let getActor () = Simple (fun ctx -> Done (Akkling.ActorRefs.typed ctx.Self))
    /// Gets the context for this actor. Normally, this should not be needed. All of its "safe" functionality can be invoked using actions.
    let unsafeGetActorCtx () = Simple (fun ctx -> Done (ctx :> IActorContext))

    /// Gets the logger for this actor.
    let getLogger () = Simple (fun ctx -> Done ctx.Logger)

    /// Stops this actor.
    let stop () = Stop Done

    /// Creates a child of this actor. The given function will be passed an IActorRefFactory to use as the parent for the child actor.
    let createChild (make: Akka.Actor.IActorRefFactory -> Akkling.ActorRefs.IActorRef<'Msg>) =
        Simple (fun ctx -> Done (make ctx.ActorFactory))

    /// Sends a the given message to the given actor. Also see the "<!" operator.
    let send (recv: Akkling.ActorRefs.IActorRef<'Msg>) msg = Simple (fun ctx -> Done (recv.Tell (msg, ctx.Self)))

    /// Watches the given actor for termination with this actor.
    let watch (act: Akkling.ActorRefs.IActorRef<'msg>) = Simple (fun ctx -> Done (ctx.Watch (Akkling.ActorRefs.untyped act)))
    /// Stops watching the given actor for termination.
    let unwatch (act: Akkling.ActorRefs.IActorRef<'msg>) = Simple (fun ctx -> Done (ctx.Unwatch (Akkling.ActorRefs.untyped act)))

    /// Schedules the given message to be sent to the given actor after the given delay. The returned object can be used
    /// to cancel the sending of the message.
    let schedule delay receiver msg = doSchedule delay receiver msg
    /// Schedules the given message to be sent to the given actor after the given delay. After the first send, the message
    /// will be sent repeatedly with the given interval between sends. The returned object can be used to cancel the sending
    /// of the message.
    let scheduleRepeatedly delay interval receiver msg = doScheduleRepeatedly delay interval receiver msg

    /// Gets an actor selection for the given path.
    let select (path: string) = Simple (fun ctx -> Done (ctx.ActorSelection path))
    /// Gets an actor selection for the given path.
    let selectPath (path: Akka.Actor.ActorPath) = Simple (fun ctx -> Done (ctx.ActorSelection path))

    /// Set the function to call if the actor restarts. If there is already a restart handler set, then this function will
    /// replace it. The function is passed the actor context, the message that was being processed when the crash happened,
    /// and the exception that caused the crash.
    let setRestartHandler handler = Simple (fun ctx -> Done (ctx.SetRestartHandler handler))
    /// Clears the restart handler.
    let clearRestartHandler () = Simple (fun ctx -> Done (ctx.ClearRestartHandler ()))

[<AutoOpen>]
module Operators =
    /// Sends the given message to the given actor.
    let inline (<!) (recv: Akkling.ActorRefs.IActorRef<'Msg>) msg : ActionBase<unit, 'Extra> = send recv msg
