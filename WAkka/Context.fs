module WAkka.Context

/// An actor context.
type IActorContext =
    /// The reference for the actor.
    abstract member Self: Akka.Actor.IActorRef
    /// The logger for the actor.
    abstract member Logger: Logger.Logger
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
    static member Named name = {
        name =  Some name
        dispatcher = None
        mailbox = None
        deploy = None
        router = None
        supervisionStrategy = None
    }



