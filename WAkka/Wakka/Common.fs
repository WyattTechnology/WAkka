// Copyright (c) 2022, Wyatt Technology Corporation
// All rights reserved.

// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:

// 1. Redistributions of source code must retain the above copyright
// notice, this list of conditions and the following disclaimer.

// 2. Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.

// 3. Neither the name of the copyright holder nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission.

// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

module WAkka.Common

/// A logger tied to an actor.
type Logger internal (ctx: Akka.Actor.IActorContext) =
    let logger = Akka.Event.Logging.GetLogger (ctx.System, ctx.Self.Path.ToStringWithAddress())

    /// Log the given message at the given level.
    member _.Log level (msg: string) = logger.Log (level, msg)

    ///Log the given exception.
    member _.LogException (err: exn) = logger.Error err.Message

    ///Log the given message at the debug level.
    member _.Debug (msg: string) = logger.Debug msg

    ///Log the given message at the info level.
    member _.Info (msg: string) = logger.Info msg

    ///Log the given message at the warning level.
    member _.Warning (msg: string) = logger.Warning msg

    ///Log the given message at the error level.
    member _.Error (msg: string) = logger.Error msg

/// An actor context.
type IActorContext =
    abstract member Context: Akka.Actor.IActorContext
    /// The reference for the actor.
    abstract member Self: Akka.Actor.IActorRef
    /// The logger for the actor.
    abstract member Logger: Logger
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
type RestartHandler = IActorContext * obj * exn -> unit

/// A function the can be passed to the setPostStopHandler action. The function is passed the actor context.
type StopHandler = IActorContext -> unit

type internal IActionContext =
    inherit IActorContext

    abstract member Stash: Akka.Actor.IStash
    abstract member SetRestartHandler: RestartHandler -> int
    abstract member ClearRestartHandler: int -> unit
    abstract member SetStopHandler: StopHandler -> int
    abstract member ClearStopHandler: int -> unit

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
    static member Named name = {Props.Anonymous with name = Some name}

[<AutoOpen>]
module CommonActions =
    type ActionBase<'Result, 'ExtraArg> =
        internal
        | Done of 'Result
        | Simple of (IActionContext -> ActionBase<'Result, 'ExtraArg>)
        | Stop of (unit -> ActionBase<'Result, 'ExtraArg>)
        | Extra of 'ExtraArg * (obj -> ActionBase<'Result, 'ExtraArg>)

    let rec bindBase (f: 'a -> ActionBase<'b, 't>) (op: ActionBase<'a, 't>) : ActionBase<'b, 't> =
        match op with
        | Done res -> f res
        | Simple cont -> Simple (cont >> bindBase f)
        | Stop cont -> Stop (cont >> bindBase f)
        | Extra (extra, cont) -> Extra (extra, cont >> bindBase f)

    /// Maps the result of an action using the given function
    let mapResult f action = action |> bindBase (fun a -> Done (f a))
    /// Ignores the result of an action.
    let ignoreResult action = mapResult ignore action

    let internal  doSchedule delay (receiver: Akkling.ActorRefs.IActorRef<'msg>) (msg: 'msg) =
        Simple (fun ctx ->
            Done (
                let cancel = new Akka.Actor.Cancelable (ctx.Scheduler)
                ctx.Scheduler.ScheduleTellOnce (delay, Akkling.ActorRefs.untyped receiver, msg, ctx.Self, cancel)
                cancel :> Akka.Actor.ICancelable
            )
        )
    let internal doScheduleRepeatedly initialDelay interval (receiver: Akkling.ActorRefs.IActorRef<'msg>) (msg: 'msg) =
        Simple (fun ctx ->
            Done (
                let cancel = new Akka.Actor.Cancelable (ctx.Scheduler)
                ctx.Scheduler.ScheduleTellRepeatedly (initialDelay, interval, Akkling.ActorRefs.untyped receiver, msg, ctx.Self, cancel)
                cancel :> Akka.Actor.ICancelable
            )
        )

    /// Gets the reference for this actor.
    let getActor () = Simple (fun ctx -> Done (Akkling.ActorRefs.typed ctx.Self))
    /// Gets the context for this actor. Normally, this should not be needed. All of its "safe" functionality can be invoked using actions.
    let unsafeGetActorCtx () = Simple (fun ctx -> Done (ctx :> IActorContext))

    /// Gets the logger for this actor.
    let getLogger () = Simple (fun ctx -> Done ctx.Logger)

    /// Stops this actor.
    let stop () =
        // This weird dance is here so that the result of stop can adapt to the context that is it called in and
        // we dont have to do "do! stop (); return! something" and instead just do "return! stop ()" when an
        // Action<'Result, 'a> is expected and 'Result is not unit.
        bindBase (fun () -> Done Unchecked.defaultof<'Result>) (Stop Done)

    /// Creates a child of this actor. The given function will be passed an IActorRefFactory to use as the parent for
    /// the child actor and the function's result will be the result of the action.
    let createChild (make: Akka.Actor.IActorRefFactory -> 'Result) =
        Simple (fun ctx -> Done (make ctx.ActorFactory))

    /// Sends a the given message to the given actor. Also see the "<!" operator.
    let send (recv: Akkling.ActorRefs.IActorRef<'Msg>) msg = Simple (fun ctx -> Done (recv.Tell (msg, ctx.Self)))

    /// Watches the given actor for termination with this actor.
    let watch (act: Akkling.ActorRefs.IActorRef<'msg>) = Simple (fun ctx -> Done (ctx.Watch (Akkling.ActorRefs.untyped act)))
    /// Stops watching the given actor for termination.
    let unwatch (act: Akkling.ActorRefs.IActorRef<'msg>) = Simple (fun ctx -> Done (ctx.Unwatch (Akkling.ActorRefs.untyped act)))

    /// Schedules the given message to be sent to the given actor after the given delay. The returned object can be used
    /// to cancel the sending of the message.
    let schedule delay receiver msg = doSchedule delay receiver msg
    /// Schedules the given message to be sent to the given actor after the given delay. After the first send, the message
    /// will be sent repeatedly with the given interval between sends. The returned object can be used to cancel the sending
    /// of the message.
    let scheduleRepeatedly delay interval receiver msg = doScheduleRepeatedly delay interval receiver msg

    /// Gets an actor selection for the given path.
    let select (path: string) = Simple (fun ctx -> Done (ctx.ActorSelection path))
    /// Gets an actor selection for the given path.
    let selectPath (path: Akka.Actor.ActorPath) = Simple (fun ctx -> Done (ctx.ActorSelection path))

    /// Add a function to call if the actor restarts. The function is passed the actor context, the message that was
    /// being processed when the crash happened, and the exception that caused the crash. This action returns an ID
    /// that can be used to remove the handler via the clearRestartHandler action.
    let setRestartHandler handler = Simple (fun ctx -> Done (ctx.SetRestartHandler handler))
    /// Clears the restart handler.
    let clearRestartHandler id = Simple (fun ctx -> Done (ctx.ClearRestartHandler id))

    /// Add a function to call when the actor stop. The function is passed the actor context. This action returns an ID
    /// that can be used to remove the handler via the clearPostStopHandler action.
    let setStopHandler handler = Simple (fun ctx -> Done (ctx.SetStopHandler handler))
    /// Clears the restart handler.
    let clearStopHandler id = Simple (fun ctx -> Done (ctx.ClearStopHandler id))

[<AutoOpen>]
module Operators =
    /// Sends the given message to the given actor.
    let inline (<!) (recv: Akkling.ActorRefs.IActorRef<'Msg>) msg : ActionBase<unit, 'Extra> = send recv msg

    /// Sends the given message to the given actor immediately, will not sequence into an actor computation expression.
    let tellNow (actor: Akkling.ActorRefs.IActorRef<'msg>) (msg: 'msg) = actor.Tell(msg, Akka.Actor.ActorCell.GetCurrentSelfOrNoSender())
