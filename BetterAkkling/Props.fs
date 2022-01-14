module BetterAkkling.Props

open Core

type SimpleActor = class end
type PersistentActor = class end

type ActorType =
    | NotPersistent of Action<SimpleActor, unit>
    | InMemoryPersistent of Action<SimpleActor, unit>
    | ProcessPersistent of Action<PersistentActor, unit>

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
