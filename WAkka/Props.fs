module BetterAkkling.Props

open Core

type ActorType<'SnapshotType> =
    internal
    | NotPersisted of Action<SimpleActor, unit>
    | Checkpointed of Action<SimpleActor, unit>
    | EventSourced of Action<EventSourcedActor<NoSnapshotting>, unit>
    | EventSourcedWithSnapshots of
        initialSnapshot:'SnapshotType * action: ('SnapshotType -> Action<EventSourcedActor<WithSnapshotting<'SnapshotType>>, unit>)

let notPersisted action : ActorType<unit> = NotPersisted action
let checkpointed action : ActorType<unit> = Checkpointed action
let eventSourced action : ActorType<unit> = EventSourced action
let eventSourcedWithSnapshots (initialSnapshot: 'SnapshotType) action : ActorType<'SnapshotType> =
    EventSourcedWithSnapshots (initialSnapshot, action)

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
