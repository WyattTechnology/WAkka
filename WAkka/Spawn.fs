module WAkka.Spawn

type ActorType =
    private
    | NotPersisted of Simple.SimpleAction<unit>
    | Checkpointed of Simple.SimpleAction<unit>
    | EventSourced of EventSourced.EventSourcedAction<unit>

let notPersisted action = NotPersisted action
let checkpointed action = Checkpointed action
let eventSourced action = EventSourced action

let spawn (parent: Akka.Actor.IActorRefFactory) (props: Context.Props) (actorType: ActorType) =

    match actorType with
    | NotPersisted action ->
        Simple.spawn parent props false action
    | Checkpointed action ->
        Simple.spawn parent props true action
    | EventSourced action ->
        EventSourced.spawn parent props action
