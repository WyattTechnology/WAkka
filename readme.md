# WAkka (aka Wyatt Akka)

This is another interface for using Akka from F#. The computation expression approach in both the standard F# interface for Akka.NET and Akkling breaks composability. The type associated with the computation expression is `Effect<'Msg>` and a computation expression must always evaluate to this type. This means that you can't implement a function that filters messages and then call that from within the actor computation expression. For example, the following will not work:
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

`filterMsgs` must evaluate to an Effect<Msg> which `printMsg` can do nothing with. Using WAkka the above can be done as:

```f#
open WAkka.Simple

type Msg = 
    | S of string
    | D of System.Double
    
let rec filterMsgs () = actor {
    match! receiveAny() with 
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

## Including in another project

This project is published as a Nuget package at `\\wyatt-data3\SoftwareDevBU\LocalNugetPackages`. To include that directory as a Nuget source in your project, first create a NuGet.config file in the root of your source tree (see [here](https://docs.microsoft.com/en-us/nuget/consume-packages/configuring-nuget-behavior)). In that file list `\\wyatt-data3\SoftwareDevBU\LocalNugetPackages` as a source with a name that makes sense. Once that is done, the `WAkka` package should be available to install in projects.

## Usage

### Actions
In WAkka, actors are implemented using the `actor` CE which evaluates to `ActionBase<'Type, 'ActorType>` where `'Type` is the type that the action evaluates to (`'ActorType` is used to restrict certain actions to certain types of actors, e.g. those that use or don't use persistence).The elements of `actor` CE themselves evaluate to `ActionBase<'Type, 'ActorType>`. For example, the `receiveAny` action used above evaluates to `ActionBase<obj, 'AT>` meaning that it will evaluate to an `obj` when a message is available.

Recursion is used process more than one message. For example, an actor that just processes each message that it receives, possibly keeping some sort of state, can be implemented as:

```f#
open WAkka.Simple

let rec handle state = actor {
    match! receieveAny() with 
    | :? string as newState -> 
        return! handle newState
    | _ -> 
        return! handle state
```

It is also possible to construct *workflow* actors that run a set number of steps instead of processing messages in a loop. For example:

```f#
open WAkka.Simple
open WAkka.CommonActions

let workflow = actor {
    let! msg1 = receiveOnly<string>()
    let! msg2 = receiveOnly<string>()
    do! someActor <! $"Got two messages: {msg1}, {msg2}"
```

This actor would wait for two messages, then send a message to another actor, and finally stop (reaching the end of the CE will cause the actor to stop).

### Spawning
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
Actor computation expressions are built using the actions built into WAkka. So far we've seen `receiveAny`, `receiveOnly`, and `send` (in the guise of the `<!` operator) in the examples above. There are three classes of built-in actions:

* Common: Actions that can be used in any context.
* Simple: Actions that can only be used in *simple* contexts (i.e. these cannot be directly used in CE's that define an event sourced actor).
* EventSourced: Actions that can only be used in an event sourced context.

#### Common Actions

These actions can be used directly in both simple and event sourced actors.

* `getActor`: Get the reference for this actor.
* `unsafeGetActorCtx`: Gets the actor context for this actor as a `WAkka.Context.IActorContext`. Normally this context is not needed, its functionality is available through the built-in actions.
* `getLogger`: Gets an `WAkka.Logger.Logger` for this actor.
* `stop`: Stops this actor.
* `createChild`: Creates a child of this actor. The given function will be passed an `IActorRefFactory` to use when creating the new actor.
* `send`: Send the given message to the given actor. We also override the `<!` operator to do this (the Akkling version of this operator should not be used in an actor CE, it can cause unexpected behavior).
* `watch`: Have this actor watch the given actor for termination.
* `unwatch`: Stop watching the given actor for termination.
* `schedule`: Schedule the given message to be sent to the given actor after the given delay. Returns an object that can be used to cancel the sending of the message.
* `scheduleRepeatedly`: Similar to `schedule`, but after the first send, the message will be sent repeatedly with the given interval between sends.
* `select`: Get an actor selection for the given path.
* `selectPath`: Get an actor selection for the given path.
* `setRestartHandler`: Sets a function to be called if the actor restarts. The function will be passed the actor context, message that was being processed when the crash happened, and the exception. If a restart handler was already set, then it will be replaced.
* `clearRestartHandler`: Clears any restart handler that is set.

#### Simple actions

These actions can only be used directly in a simple actor (i.e. those started with `notPersisted` or `checkpointed`).

* `receive`: Wait for a message that satisfies the given filter function (i.e. one for which the function returns `Some`)
* `receiveWithTimeout`: Same as `receive`, but if the given timeout is hit then `None` is returned.
* `receiveAny`: Waits for any message to be received.
* `receiveAnyWithTimeout`: Waits for any message to be received. `None` is returned if the timeout is hit before a message is received.
* `receiveOnly<'Msg>`: Wait for a message of the given type.
* `receiveOnlyWithTimeout<'Msg>`: Wait for a message of the given type. `None` is returned if the timeout is hit before a message is received.
* `getSender`: Gets the sender of the most recently received message.
* `stash`: Stashes the most recently received message.
* `unstashOne`: Unstashes the message at the front of the stash.
* `unstashAll`: Unstashes all stashed messages.

#### Event sourced actions

These actions can only be used in an event sourced actor (i.e. those started using `eventSourced`).

* `persist`: Run the given simple action and persist the result. When the actor is recovering from a crash the simple action is skipped and the persisted result is returned. The result of `persist` is a `PersistResult` which is either the result of the action, or a lifecycle event (recovery finished, recovery failed, or the result of the simple action was rejected by the persistence system).
* `persistSimple`: Run the given simple action and persist the result. When the actor is recovering from a crash the simple action is skipped and the persisted result is returned. Just the result of the simple action is returned, persistence lifecycle events are filtered out. If an action result is rejected by the persistence system or recovery fails then the actor will be stopped.
* `isRecovering`: Gets whether the actor is currently recovering or not.

To make the stateful example above work as an event sourced actor we would do:

```f#
open WAkka

let rec handleMsg () = Simple.actor {
    match! Simple.Actions.receieveAny() with 
    | :? string as newState when newState <> "" -> 
        return newState
    | _ -> 
        return! handleMsg ()
}

let rec handle state = EventSourced.actor {
    let! newState = EventSourced.Actions.persistSimple(handleMsg ())
    return! handle newState
    
let act = Spawn.spawn parent Context.Props.Anonymous (Spawn.eventSourced <| handle "")
```

This actor runs the `handleMsg` action in a simple context. It looks for a `string` message, when one is received, the simple action finishes with that string message as its result. The `persist` action that `handleMsg` was running in then persists this message and returns it. `handle` then recurses with the new state. If the actor were to crash then all the calls to `persist` would skip calling `handleMsg` and return the persisted results instead. Once the persisted results are exhausted, `persist` will begin calling `handleMsg` again. 

Note that in order to have event sourced actor state survive a process restart, you will have to configure a persistence back-end when starting the actor system. That is beyond the scope of this document.

## Versioning

This project follows the [NuGet version of semantic versioning](https://docs.microsoft.com/en-us/nuget/concepts/package-versioning). In short, the version number is `Major.Minor.Patch-Suffix` where changes to the components of the version are made for:
* Major: Breaking changes to existing functionality
* Minor: New functionality, no breaking changes to existing functionality
* Patch: Bug fixes to existing functionality
* Suffix: Used to denote *special* versions (e.g. development version)

A shortcut to set the version on both assembly is to use the `set-version` target of the included build project (Wtc.FakeUtilities uses itself as a build system).

## Development

Before publishing (see below) a new version you should make changes and test those changes with the project that is consuming the changes. The first step is easy, just clone the repo and work like normal. The last step is more difficult, but not too bad. First, go into the nuget settings for the WtcFakeUtilities project and add a suffix to the version, e.g. `-dev`. When the WAkka project is built it will generate a NuGet package. The location of this package will be shown in the build output. The directory that the package is built into needs to be added to the consuming project's NuGet.config file as a source. Once this is done, the `dev` version of the package should now be available in consuming project's NuGet interface. Upgrade the consuming project to the `dev` version. You can now test the consuming project's use of the change made. Note that if you make more changes to this project you will need to remove the existing package from your NuGet cache (C:\Users\<user name>\.nuget\packages) in order for your updated package to be used. When done, remove the `dev` suffix from the package version and publish the package (see below).

## Publishing

When you are ready to publish changes to this library, first increase the version number following the requirements laid out above. Merge your changes, along with the version number change, to the main branch. Now tag your commit with `Version-<version number>`. This will trigger the CI to build the NuGet package and publish it at `\\wyatt-data3\SoftwareDevBU\LocalNugetPackages`. Any consuming projects that need your changes can now be updated to use the new version.
