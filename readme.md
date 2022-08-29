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

Note that the library has a few limitations:
* Checkpointing is currently not supported for actors that use the Akka persistence system. You will see *checkpointed* actors below, but those actors use a different system to achieve persistence and are only persistent against actor crashes, not actor system crashes. 
* Remote deployment of actors is currently not supported.

Also, performance of actors built with WAkka lags behind those built using Akka or Akkling. In the simple test case given in the PerformanceTest project, the WAkka actor gets around 55-60% of the message processing rate that Akka and Akkling can achieve. On a 2021 Apple M1 Processor, this still worked out to be around 900K messages per second. For us at Wyatt Technology this is way beyond our needs. Your needs may be different, so keep this limitation in mind. This performance difference can probably be addressed, there just hasn't been any effort put into it yet.

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
Actors are spawned using the `WAkka.Spawn.spawn` function. The parent, properties, and actor type for the new actor are passed in. An `Akkling.ActorRefs.IActorRef<'Msg>` pointing at the new actor is returned. The `'Msg` type is inferred from the calling context. If you want to fix the message type in the actor reference then wrap your call to `spawn` in a function and specify the message type for the returned reference:

```f#
open WAkka
let startActor parent : IActorRef<string> = Spawn.spawn parent Context.Props.Anonymous action
```

The properties are given using a `WAkka.Context.Props` object. For most actors, the defaults should be fine and the `Props.Anonymous` or `Props.Named` methods can be used to create an anonymous or named actor respectively. The members of `WAkka.Context.Props` are the same as what you would have when creating an actor in Akkling. So see that documentation if you need to change something from the defaults.

The actor type, which specifies how actor crashes are dealt with, is set using `WAkka.Spawn.ActorType` and associated functions. The simplest actor type is `notPersisted` which just restarts from the given action, no state is maintained across crashes. Next we have `checkpointed` which sets up the actor to return to the last place a message was waited for in the event of a crash. This will restore the state to what it was before the most recent message was received. Note that this actor will only restore state across an an actor restart, the state will not survive a process crash. To survive a process crash one needs to use `eventSourced` which creates an actor that uses the Akka.NET persistence mechanisms (see the built-in `persist` action below).

To start the two actors above one would do:

```f#
open WAkka

let act1 = Spawn.spawn parent Context.Props.Anonymous (Spawn.notPersistent <| handle "")
let act2 = Spawn.spawn parent Context.Props.Anonymous (Spawn.notPersistent workflow)
```

In this case the actors were started with no persistence. To use checkpointing just substitute `Spawn.checkpointed` for `Spawn.notPersistent`. As they stand, the actions are not compatible with `Spawn.eventSourced` which would start an actor that uses Akka.NET persistence (see the built-in `persist` action below).

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

The `Common` module also contains functions to change the result type of an action:
* `ignoreResult`: Ignore the result of a given action, makes things behave as though the result of the action is `unit`.
* `mapResult`: Apply a given function to the result of an action. Changes an action's result type to that of the function.

#### Simple actions

These actions can only be used directly in a simple actor (i.e. those started with `notPersisted` or `checkpointed`)

* `Receive`: Static methods of this class are used to receive messages. Each method has an optional timeout and those that filter messages have a *strategy* for dealing with messages that are filtered out. You can make custom *other message strategies*, but the most useful are already provided in `ignoreOthers` and 'stashOthers'. `ignoreOthers`, which is the default, will ignore any messages that are filtered out. `stashOthers` will stash messages that are filtered out, and then unstash them when a message satisfies the filter.
  * `Any`: Receive the next message (no filtering).
  * `Filter`: Receive messages until one is received that satisfies the given filter.
  * `Only<'Type>`: Receives messages until one of type `'Type` is received.
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

#### Actor Result

It is often useful to have actions that evaluate to the `Result` type. The `ActorResult` module defines a computation expression that combines these two types. I.e., the CE will execute actions until it runs out or an action evaluates to `Error`. The result of the CE is `Ok <value>` if it makes it to the end without getting an `Error`. `<value>` will be the return value of the CE. If an `Error` is encountered than the CE's value will be that error. Note that all elements in the CE must have the same `Error` type, but their `Ok` types can differ.

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
* Many functions for converting other types, like booleans, `Option`, etc. to `ActorResult`. They all start with `require`.
* `ofActor`: Runs the given actor action and wraps the result in `Ok`.
* `ofResult`: Converts a `Result` to an `ActorResult` that evaluates to the original result.

#### Event sourced actions

These actions can only be used in an event sourced actor (i.e. those started using `eventSourced`).

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
    
let act = Spawn.spawn parent Context.Props.Anonymous (Spawn.eventSourced <| handle "")
```

This actor runs the `handleMsg` action in a simple context. It looks for a `string` message, when one is received, the simple action finishes with that string message as its result. The `persist` action that `handleMsg` was running in then persists this message and returns it. `handle` then recurses with the new state. If the actor were to crash then all the calls to `persistSimple` would skip calling `handleMsg` and return the persisted results instead. Once the persisted results are exhausted, `persistSimple` will begin calling `handleMsg` again. 

Note that in order to have event sourced actor state survive a process restart, you will have to configure a persistence back-end when starting the actor system. That is beyond the scope of this document.

Also note: WAkka does not currently support checkpointing in the Akka persistence system.
