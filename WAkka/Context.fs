module WAkka.Context

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



