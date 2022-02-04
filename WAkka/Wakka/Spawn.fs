module WAkka.Spawn

/// Specifies how an actor responds to restarts.
type ActorType =
    private
    | NotPersisted of Simple.SimpleAction<unit>
    | Checkpointed of Simple.SimpleAction<unit>
    | EventSourced of EventSourced.EventSourcedAction<unit>

/// Creates an actor that goes back to the given action if it restarts.
let notPersisted action = NotPersisted action
/// Creates and actor that runs the given action. If the actor crashes then it restarts from the last point where it
/// was waiting for a message.
let checkpointed action = Checkpointed action
/// Creates an actor that uses the Akka.NET persistence event sourcing mechanism. In the event of a restart, the actor
/// will replay events that were stored using the EventSourced.Actions.persist action.
let eventSourced action = EventSourced action

/// Creates a new actor that is a child of the given parent. The actor will be created using the given properties and
/// actor type. A reference to the new actor will be returned.
let spawn (parent: Akka.Actor.IActorRefFactory) (props: Common.Props) (actorType: ActorType) =

    match actorType with
    | NotPersisted action ->
        Simple.spawn parent props false action
    | Checkpointed action ->
        Simple.spawn parent props true action
    | EventSourced action ->
        EventSourced.spawn parent props action
