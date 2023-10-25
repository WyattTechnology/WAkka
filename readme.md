# WAkka (aka Wyatt Akka)

This is another interface for using [Akka.NET](https://getakka.net) from F#. The computation expression approach in both the standard F# interface for Akka.NET and [Akkling](https://github.com/Horusiath/Akkling) breaks composability. The type associated with the computation expression is `Effect<'Msg>` and a computation expression must always evaluate to this type. This means that you can't implement a function that filters messages and then call that from within the actor computation expression. For example, the following will not work:
```f#
open Akkling

type Msg = 
    | S of string
    | D of System.Double
    
let rec filterMsgs () = actor {
    match! ctx.Receive() with 
    | :? string as s -> return S s
    | :? System.Double as d -> return D d
    | _ -> return! filterMsgs ()
}

let printMsg = 
    actor {
        match! filterMsgs () with 
        | S s -> printfn $"Got string: {s}"
        | D d -> printfn $"Got double: {d}"
    }
```

`filterMsgs` must evaluate to an `Effect<Msg>` which `printMsg` can do nothing with. Using WAkka, the above can be done as:

```f#
open WAkka.Simple

type Msg = 
    | S of string
    | D of System.Double
    
let rec filterMsgs () = actor {
    match! Receive.Any() with 
    | :? string as s -> return S s
    | :? System.Double as d -> return D d
    | _ -> return! filterMsgs ()
}

let printMsg = 
    actor {
        match! filterMsgs () with 
        | S s -> printfn $"Got string: {s}"
        | D d -> printfn $"Got double: {d}"
    }
```

## Should you use this?

That's always a difficult question to answer in a general fashion for a library since every possible user of the library is working under different constraints. One question that will affect everyone: is it stable? As far as we can tell the answer is yes. The library has been through multiple iterations internally at Wyatt Technology in multiple products and appears to be solid. That being said, as with all open source software, no warranty is implied or given. 

Performance of actors built with the `actor` computation expression (see [below](#actions)) lags behind those built using Akka or Akkling. In the simple test case given in the PerformanceTest project, the actor implemented using the computation expression gets around 55-60% of the message processing rate that Akka and Akkling can achieve. On a 2021 Apple M1 Processor, this still worked out to be around 900K messages per second. For us at Wyatt Technology this is way beyond our needs. Your needs may be different, so keep this limitation in mind. If an actor is built using `HandleMessages`, then performance on par with Akkling can be achieved if `ContinueWith` is used for state management, and parity with Akka can be achieved if mutable state and `Continue` is used (see [below](#handlemessages)).  

## Usage

WAkka is built on top of Akka.NET and Akkling. It uses the typed actor references from Akkling, and the general infrastructure from Akka.NET. The actor system should be started as though you're using Akkling. Then, the creation of actors should be done as shown below. 

Note that the Akkling `<!` (*tell* operator) can cause unexpected behavior in WAkka computation expressions. In order to avoid this, WAkka also overrides this operator to behave properly in computation expressions. If both WAkka and Akkling modules are opened, then WAkka must be opened after Akkling so that WAkka's definition of the operator will be used. To send messages outside of a computation expression, the `tellNow` function is provided (open the `WAkka.Common` module to use it).

### Actions
In WAkka, actors are implemented using the `actor` CE which evaluates to `ActionBase<'Type, 'ActorType>` where `'Type` is the type that the action evaluates to and `'ActorType` is used to restrict certain actions to certain types of actors(e.g., those that use or don't use persistence). The elements of the `actor` CE themselves evaluate to `ActionBase<'Type, 'ActorType>`. For example, the `Receive.Any` action used above evaluates to `ActionBase<obj, 'ActorType>` meaning that it will evaluate to an `obj` when a message is available.

Recursion is used to process more than one message. For example, an actor that just processes each message that it receives, possibly keeping some sort of state, can be implemented as:

```f#
open WAkka.Simple

let rec handle state = actor {
    match! Receieve.Any() with 
    | :? string as newState -> 
        return! handle newState
    | _ -> 
        return! handle state
}
```

It is also possible to construct *workflow* actors that run a set number of steps instead of processing messages in a loop. For example:

```f#
open WAkka.Simple
open WAkka.CommonActions

let workflow = actor {
    let! msg1 = Receive.Only<string>()
    let! msg2 = Receive.Only<string>()
    do! someActor <! $"Got two messages: {msg1}, {msg2}"
}
```

This actor would wait for two messages, then send a message to another actor, and finally stop (reaching the end of the CE will cause the actor to stop).

### Spawning Actors
Actors are normally started using *spawn* functions defined in either the WAkka.Simple or WAkka.EventSourced modules. For specialized cases where an actor class is needed (e.g., remote deployment) please see Actor Classes [below](#alternate-spawning-via-actor-classes). The parent, properties, and action for the new actor are passed in. An `Akkling.ActorRefs.IActorRef<'Msg>` pointing at the new actor is returned. The `'Msg` type is inferred from the calling context. If you want to fix the message type in the actor reference then wrap your call to `spawn` in a function and specify the message type for the returned reference:

```f#
open WAkka
let startActor parent : IActorRef<string> = WAkka.Simple.spawnNotPersisted parent Context.Props.Anonymous action
```

The properties are given using a `WAkka.Context.Props` object (`WAkka.EventSourced.Props` if the actor is persistent). For most actors, the defaults should be fine and the `Props.Anonymous` or `Props.Named` methods can be used to create an anonymous or named actor respectively. The members of `WAkka.Context.Props` are the same as what you would have when creating an actor in Akkling. So see that documentation if you need to change something from the defaults.

The actor type, which specifies how actor crashes are dealt with, is set by which *spawn* function is used. The simplest actor type is *not persisted*. They are started by `WAkka.Simple.spawnNotPersisted`, and if they crash then they will restart from the given action, no state is maintained across crashes. Next we have *checkpointed* actors which is started by using `WAkka.Simple.spawnCheckpointed`. These actors return to the last place a message was waited for in the event of a crash. This will restore the state to what it was before the most recent message was received. Note that this actor will only restore state across an actor restart, the state will not survive a process crash. To survive a process crash one needs to use either `WAkka.EventSourced.spawnNoSnapshots` or `WAkka.EventSourced.spawnSnapshots` which creates an actor that uses the Akka.NET persistence mechanisms (see Event Sourced Actions [below](#event-sourced-actions)).

To start the two actors above one would do:

```f#
open WAkka

let act1 = Simple.spawnNotPersisted parent Context.Props.Anonymous (handle "")
let act2 = Simple.spawnNotPersisted parent Context.Props.Anonymous workflow
```

In this case the actors were started with no persistence. To use checkpointing just substitute `Simple.spawnCheckpointed` for `Simple.spawnNotPersisted`. 

#### Deprecated Spawn Functions

Before version 1.5, WAkka spawned actors using the functions in the `WAkka.Spawn` module. Those functions still exist to support backwards compatibility, but should not be used in new code.

#### Alternate spawning via actor classes

In most cases, starting actors using the `spawn*` functions as above should be sufficient. But in some cases (e.g., remote deployment), we need to implement the actor using a class derived from `Akka.Actor.ActorBase` so that we can put a type into `Akka.Actor.Props`. WAkka provides a way to do this via the `WAkka.Simple.NotPersistedActor`, `WAkka.Simple.CheckpointedActor`, `WAkka.EventSourced.EventSourcedActor` and `WAkka.EventSourced.EventSourcedSnapshotActor` classes that correspond to *not persisted*, *checkpointed*, *persistent (no snapshots)*, and *persistent (with snapshots)* actor types respectively. One derives a class from the appropriate base class for the type of actor wanted, passing the action to execute to the super-class constructor. This class can then be used anywhere one would use any other Akka actor class.

Note that if you are using this to do remote deployment or pooled routers in clusters then the usual rules apply. All of your constructor arguments must be serializable and the class must exist in each process where the actors are to be remotely deployed. WAkka actions are not serializable which is why you need to derive a class from one of the provided base classes instead of just using them directly and passing the action to their constructors when doing remote deployment. 

### Built-in actions
Actor computation expressions are built using the actions built into WAkka. So far we've seen `Receive.Any`, `Receive.Only`, and `send` (in the guise of the `<!` operator) in the examples above. There are three classes of built-in actions:

* Common: Actions that can be used in any context.
* Simple: Actions that can only be used in *simple* contexts (i.e., these cannot be directly used in CE's that define an event sourced actor).
* EventSourced: Actions that can only be used in an event sourced context.

#### Common Actions

These actions can be used directly in both simple and event sourced actors.

* `getActor`: Get the reference for this actor.
* `unsafeGetActorCtx`: Gets the actor context for this actor as a `WAkka.Context.IActorContext`. Normally this context is not needed, its functionality is available through the built-in actions.
* `getLogger`: Gets an `WAkka.Logger.Logger` for this actor which can be used to log messages through the Akka.NET logging system.
* `stop`: Stops this actor.
* `createChild`: Creates a child of this actor. The given function will be passed an `IActorRefFactory` to use when creating the new actor.
* `send`: Send the given message to the given actor. We also override the `<!` operator to do this (the Akkling version of this operator should not be used in an actor CE, it can cause unexpected behavior).
* `watch`: Have this actor watch the given actor for termination.
* `unwatch`: Stop watching the given actor for termination.
* `schedule`: Schedule the given message to be sent to the given actor after the given delay. Returns an object that can be used to cancel the sending of the message.
* `scheduleRepeatedly`: Similar to `schedule`, but after the first send, the message will be sent repeatedly with the given interval between sends.
* `select`: Get an actor selection for the given path.
* `selectPath`: Get an actor selection for the given path.
* `setRestartHandler`: Sets a function to be called if the actor restarts. The function will be passed the actor context, message that was being processed when the crash happened, and the exception. The action returns an ID that can be used to remove the handler via the `clearRestartHandler` action. 
* `clearRestartHandler`: Clears any restart handler that was set using the `setRestartHandler` action.
* `setStopHandler`: Sets a function to be called if the actor stops. The function will be passed the actor context. The action returns an ID that can be used to remove the handler via the `clearStopHandler` action. 
* `clearStopHandler`: Clears any stop handler that was set using the `setStopHandler` action.
* `getContext`: Gets an `IContext` object that allows access to the functionality of the above actions. This is mostly meant to be used with `HandleMessages` action (see [below](#handlemessages)).

The `Common` module also contains functions to change the result type of an action:
* `ignoreResult`: Ignore the result of a given action, makes things behave as though the result of the action is `unit`.
* `mapResult`: Apply a given function to the result of an action. Changes an action's result type to that of the function.

#### Simple actions

These actions can only be used directly in a simple actor (i.e. those started with `Simple.spawnNotPersisted` or `Simple.spawnCheckpointed`)

* `Receive`: Static methods of this class are used to receive messages. Each method has an optional timeout and those that filter messages have a *strategy* for dealing with messages that are filtered out. You can make custom *other message strategies*, but the most useful are already provided in `ignoreOthers` and `stashOthers`. `ignoreOthers`, which is the default, will ignore any messages that are filtered out. `stashOthers` will stash messages that are filtered out, and then unstash them when a message satisfies the filter.
  * `Any`: Receive the next message (no filtering).
  * `Filter`: Receive messages until one is received that satisfies the given filter.
  * `Only<'Type>`: Receives messages until one of type `'Type` is received.
  * `HandleMessages`: Switches to processing messages with a message handling function, see [below](#handlemessages).
* `getSender`: Gets the sender of the most recently received message.
* `sleep`: Applies the given *other messages strategy* to any messages received for the given amount of time. 
* `stash`: Stashes the most recently received message.
* `unstashOne`: Unstashes the message at the front of the stash.
* `unstashAll`: Unstashes all stashed messages.

There are also functions for doing maps and folds of actions:

* `mapArray`: Applies a function whose result is an action and applies it an array of values generating an action that evaluates each of the generated actions capturing their results in an array which become the result of the action.
* `mapList`: Same as `mapArray`, but works with lists instead of arrays.
* `foldActions`: takes an initial value of type `'res`, a sequence of actions of type `'a`, and function `'res -> 'a -> Action<'res>`. Creates an action that evaluates the first action from the sequence, passes its result and the initial value to the function and evaluates the resulting action. The result of this action becomes the `'res` value that is combined with the result of the next action from the sequence using the function. This continues until the sequence of actions is exhausted, at which point the final `'res` value is the result of the action.
* `foldValues`: Works similar to `foldActions`, except that an sequence of values is passed in instead of an array of actions and the values in the sequence are used directly in when evaluating the function to generate sub-actions.
* `executeWhile`: Executes a *condition* action, if it returns `Some` then the result is passed to the body function and the resulting action executed. Repeats until the condition action evaluates to `None`.

##### HandleMessages

Many actors just need to be able to handle a given message type in a loop, possibly updating some state. That can be accomplished using the `actor` computation expression:
```f#
let rec handle state = actor {
    match! Receive.Any() with 
    | :? Msg1 as msg -> 
        return! handle {state with value1 = msg.value}
    | :? Msg2 as msg -> 
        return! handle {state with value2 = msg.value}
    | _ ->
        return! handle state
}
spawn parent Props.Anonymous (notPersistent (handle initState))
```

This pattern comes up enough that we provide a special action for it: `HandleMessages`, which takes a message handling function that will be called for each message received. The message handling function returns one of the cases of `HandleMessagesResult`:
* `IsDone result`: Stops message handling and returns control to where `HandleMessages` was called, causing the `HandleMessages` action to evaluate to `result`. If this was a call to a *spawn* function, then the actor will exit.
* `Continue`: Continue to process messages with the same message handler.
* `ContinueWith handler`: Continue to process messages using the given handler.
* `ContinueWithAction action`: Run the given action. When the action finishes, its result will be the result of the `HandleMessages` action.

The above can be written as:
```f#
let rec handle state msg = 
        match msg with
        | :? Msg1 as msg -> 
            HandleMessagesResult.ContinueWith (handle {state with value1 = msg.value})
        | :? Msg2 as msg -> 
            HandleMessagesResult.ContinueWith (handle {state with value2 = msg.value})
        | _ ->
            HandleMessagesResult.Continue
    }
}
Simple.spawnNotPersisted parent Props.Anonymous (Receive.HandleMessages (handle initState))
```
This isn't terribly different than the original code, but has a few advantages. The message handling is not inside of a computation expression, so if the need arises to run it under a debugger, you will have a much easier time. The second example will also execute faster since it bypasses all the computation expression machinery (and it's concomitant memory allocations). For most actors the computation expression is probably fast enough, but if you want parity with either Akkling or Akka then using `HandleMessages` is called for. In fact, once can attain parity with the Akka `UntypedActor` with mutable state by making a change to the above example:
```f#
let mutable state = initState
let rec handle msg = 
        match msg with
        | :? Msg1 as msg -> 
            state.value1 <- msg.value
            HandleMessagesResult.Continue 
        | :? Msg2 as msg -> 
            state.value2 = msg.value
            HandleMessagesResult.Continue 
        | _ ->
            HandleMessagesResult.Continue
    }
}
Simple.spawnNotPersistent parent Props.Anonymous (Receive.HandleMessages handle)
```

This example will not allocate any memory in the course of processing messages. It sacrifices *functional purity* since it uses mutable state, but if you need the absolute best possible performance, it's not uncommon to have to make that sacrifice on a small scale in order to get it. 

Note that `HandleMessages` expects a function with signature `'Msg -> HandleMessagesResult<'Msg, 'Result>`. While the message handler is processing messages, any messages that are not of type `'Msg` will be ignored. If you want to receive all messages then your `'Msg` type must be `obj`. 

`HandleMessages` can be mixed in with computation expression code as well. Since `HandleMessages` is an action, it can be called as 
```f#
actor {
    let! result = Receive.HandleMessages handle
    ... // Do stuff with result
```
In this code, the `handle` function will be called to handle each message that is received until it evaluates to `HandleMessagesResult.IsDone value`. `value` will then be the result of the `HandleMessages` action and `result` will be bound to it. One can also transition from a message handling function to a computation expression by having the message handling function evaluate to `HandleMessageResult.ContinueWithAction action` where `action` is the action that you want to execute. Note that if you want to go back to your message handling function at some point, then you will need to explicitly do a `return! Receive.HandleMessages handle` inside of your action (here `handle` is the message handling function to return to). 

There is often a need to access the functionality that is contained in other action types when in the message handling function of `HandleMessages`. Unfortunately, actions cannot be used in the message handler, except by returning `ContinueWithAction` and the action then doing `return! HandleMessages ...`. This is very cumbersome. One option would to call the `getContext` action before calling `HandleMessages` and capture the `IContext` object in your message handler, then accessing the desired functionality through the captured object. This is a common enough pattern that there is a second version of `HandleMessages` that does it for you. Just pass a message handler with signature `IContext -> 'Msg -> HandleMessagesResult<'Msg, 'Result>` instead of `'Msg -> HandleMessagesResult<'Msg, 'Result>` and the context will be passed to your handler. Note that the context is only passed to the initial handler, if you do `ContinueWith handler` then `handler` must have signature `'Msg -> HandleMessagesResult<'Msg, 'Result>`, so it is up to you to propagate the context to the new handler if it is needed.

So when should you use the `actor` computation expression versus `HandleMessages`? If your actor does the same thing for every message that it receives then `HandleMessages` is probably the way to go. It's easier to trace through in the debugger and you'll get better performance (again, unless you're pushing the limits of processor power, the computation expression approach probably has plenty of performance for you). If you're running *workflow* style code then the computation expression is what you want since it is much more versatile. You can also mix them as we saw above. You could use `HandleMessages`, and when certain messages are received, use `ContinueWithAction` to launch into a workflow, returning to the original message handler via `return! HandleMessages ...` when the workflow is done. 

#### Actor Result

It is sometimes useful to have actions that evaluate to the `Result` type. The `ActorResult` module defines a computation expression that combines these two types. I.e., the CE will execute actions until it runs out or an action evaluates to `Error`. The result of the CE is `Ok <value>` if it makes it to the end without getting an `Error`. `<value>` will be the return value of the CE. If an `Error` is encountered then the CE's value will be that error. Note that all elements in the CE must have the same `Error` type, but their `Ok` types can differ.

The `actorResult` builder is used to build an actor result CE and it is run by passing it to `runActorResult`. For example:
```f#
let act = actor {
    return! runActorResult (actorResult{
        let! msg = ofActor(Receive.Only<Result<string, string>>())
        return $"message was {msg}"
    })
}
```
would wait for a message of type `Result<string, string>`. If it receives `Ok <message>` then the result of running `act` would be `Ok "message was <message>"` since the `return $message...` line would be executed. But if the message received was `Error <error>` then the `return ...` line would be skipped and the result of `act` would be `Error err`. 

The `ActorResult` module contains more functions to make working with this CE easier:

* `retn`, `ok`: Wrap the given value so that it evaluates to `Ok <value>`.
* `returnError`, `error`: Wrap the given value so that is evaluates to `Error <value>`.
* `map`: Applies the given function to the value of an action if that action evaluates to `Ok`. The result of the function is wrapped in `Ok`.
* `mapError`: Similar to `map`, but acts on `Error` instead of `Ok`.
* `orElse`: Evaluates an action, then evaluates another action if the first action evaluates to `Error`. Designed to be chained using the `|>` operator: `action1 |> orElse action2 |> orElse action3...`.
* `orElseWith`: Similar to `orElse`, but the action to evaluate in the error case is generated by a function that is passed the error value.
* `ignore`: Ignores the `Ok` part fo a result converting it from `ActorResult<'a, 'e>` to `ActorResult<unit, 'e>`.
* `require*`: Many functions for converting other types, like booleans, `Option`, etc. to `ActorResult`. They all start with `require`.
* `ofActor`: Runs the given actor action and wraps the result in `Ok`.
* `ofResult`: Converts a `Result` to an `ActorResult` that evaluates to the original result.

#### Event sourced actions

The Akka persistence system is accessed via actions defined in the `EventSourced`  module. These actions can only be used in an *event sourced* actor (i.e. those started using `EventSourced.spawnspawnNoSnapshots` or `EventSourced.spawnSnapshots`).

* `persist`: Run the given simple action and persist the result. When the actor is recovering from a crash the simple action is skipped and the persisted result is returned. The result of `persist` is a `PersistResult` which is either the result of the action, or a lifecycle event (recovery finished, recovery failed, or the result of the simple action was rejected by the persistence system).
* `persistSimple`: Run the given simple action and persist the result. When the actor is recovering from a crash the simple action is skipped and the persisted result is returned. Just the result of the simple action is returned, persistence lifecycle events are filtered out. If an action result is rejected by the persistence system or recovery fails then the actor will be stopped.
* `isRecovering`: Gets whether the actor is currently recovering or not.

To make the stateful example above work as an event sourced actor we would do:

```f#
open WAkka

let rec handleMsg () = Simple.actor {
    match! Simple.Actions.Receieve.Any() with 
    | :? string as newState when newState <> "" -> 
        return newState
    | _ -> 
        return! handleMsg ()
}

let rec handle state = EventSourced.actor {
    let! newState = EventSourced.Actions.persistSimple(handleMsg ())
    return! handle newState
}
    
let act = EventSourced.spawnNoSnapshots parent EventSourced.Props.Anonymous (handle "")
```

This actor runs the `handleMsg` action in a simple context. It looks for a `string` message. When one is received, the simple action finishes with that string message as its result. The `persist` action that `handleMsg` was running in then persists this message and returns it. `handle` then recurses with the new state. If the actor were to crash then all the calls to `persistSimple` would skip calling `handleMsg` and return the persisted results instead. Once the persisted results are exhausted, `persistSimple` will begin calling `handleMsg` again. 

Note that in order to have event sourced actor state survive a process restart, you will have to configure a persistence back-end when starting the actor system. That is beyond the scope of this document.

##### Persistence Ids

By default, the actor path will be used as the persistence id presented to the Akka persistence system. If this is not desired, then the `persistenceId` member of `EventSourced.Props` should be set (`EventSourced.Props.PersistenceId` provides a shortcut to set this and the actor name).

##### Snapshots

If an actor is started with `EventSourced.spawnSnapshots` then it will support persistence snapshots. The difference from `EventSourced.spawnNoSnapshots` is that in addition to passing in an initial action for the actor to execute, a function to handle a snapshot is also passed in. If no snapshot is available, then the initial action is executed. If a snapshot is available, then it is passed to the snapshot handler function which returns the action to execute instead of the initial action.

Snapshots are managed using the following actions (which can only be used if the actor is started using `spawnSnapshots`):
* `getLastSequenceNumber`: Gets the sequence number of the last event that was persisted.
* `saveSnapshot`: Saves the given snapshot. Note that snapshots are typed, and all saved snapshots for a given actor must be the same type. `obj` is a valid snapshot type if you need that amount of flexibility in your snapshots. If you need to track that a snapshot succeeded then watch for `Akka.Persistence.SaveSnapshotSuccess` and/or `Akka.Persistence.SaveSnapshotFailure` messages.
* `deleteEvents`: Delete persisted events up to the given sequence number. This should only be done after saving a snapshot (preferably after receiving a `Akka.Persistence.SaveSnapshotSuccess` since snapshots are saved asynchronously). If you need to track success then watch for `Akka.Persistence.DeleteMessagesSuccess` and/or `Akka.Persistence.DeleteMessagesFailure` messages.
* `deleteSnapshot`: Delete the snapshot with the given sequence number. If you need to track success then watch for `Akka.Persistence.DeleteSnapshotSuccess` and/or `Akka.Persistence.DeleteSnapshotFailure` messages.
* `deleteSnapshots`: Delete snapshots that match the given `Akka.Persistence.SnapshotSelectionCriteria`. If you need to track success then watch for `Akka.Persistence.DeleteSnapshotsSuccess` and/or `Akka.Persistence.DeleteSnapshotsFailure` messages.

Note that if you have an actor that is started using `Spawn.eventSourced`, then it is the equivalent of being started with `spawnNoSnapshots` and it will not be able to use snapshots. To upgrade it to an actor that can use snapshots, you will have to change your code to use `spawnSnapshots` when starting the actor. 
