module BetterAkkling.Context

type IActorContext =
    abstract member Self: Akka.Actor.IActorRef
    abstract member Logger: Logger.Logger
    abstract member Sender: Akka.Actor.IActorRef
    abstract member Scheduler: Akka.Actor.IScheduler
    abstract member ActorFactory: Akka.Actor.IActorRefFactory
    abstract member Watch: Akka.Actor.IActorRef -> unit
    abstract member Unwatch: Akka.Actor.IActorRef -> unit
    abstract member ActorSelection: string -> Akka.Actor.ActorSelection
    abstract member ActorSelection: Akka.Actor.ActorPath -> Akka.Actor.ActorSelection

type RestartHandler = IActorContext -> obj -> exn -> unit

type internal IActionContext =
    inherit IActorContext

    abstract member Stash: Akka.Actor.IStash
    abstract member SetRestartHandler: RestartHandler -> unit
    abstract member ClearRestartHandler: unit -> unit


