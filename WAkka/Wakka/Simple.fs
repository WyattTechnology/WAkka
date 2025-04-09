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

module WAkka.Simple

open System

open Akka.Event

open Common

type SimpleType internal () = class end


type SimpleExtra =
    private
    | WaitForMsg of IMessageHandlerFactory
    | TryWith of (unit -> ActionBase<obj, SimpleExtra>) * (exn -> ActionBase<obj, SimpleExtra>)
    | TryFinally of (unit -> ActionBase<obj, SimpleExtra>) * (unit -> unit)
/// An action that can only be used directly in a Simple actor (e.g. started using notPersistent or checkpointed).
and SimpleAction<'Result> = ActionBase<'Result, SimpleExtra>
and private IMessageHandlerFactory =
    abstract member CreateObj : (obj -> SimpleAction<obj>) * (SimpleAction<obj> -> unit) -> IMessageHandler
and internal IMessageHandler =
    abstract member HandleMessage : obj -> unit    

let rec private bind (f: 'a -> SimpleAction<'b>) (op: SimpleAction<'a>) : SimpleAction<'b> =
    bindBase f op

type Delayed<'t> = unit -> SimpleAction<'t>

type private TryWithResult = Result<obj, exn>

type ActorBuilder () =
    member this.Bind (x, f) = bind f x
    member this.Return x = Done x
    member this.ReturnFrom (x: SimpleAction<'a>) = x
    member this.Zero () = Done ()
    member this.Combine(m1, m2) = this.Bind (m1, m2)
    member this.Delay (f: unit -> SimpleAction<'t>): Delayed<'t> = f
    member this.Run f = f ()
    member this.TryWith (expr: Delayed<'a>, handler: exn -> SimpleAction<'a>) : SimpleAction<'a> =
        let expr' () = expr () |> bindBase(fun a -> Done (a :> obj))
        let handler' err = handler err |> bindBase(fun a -> Done (a :> obj))
        let cont (res: obj) : SimpleAction<'a> = Done (res :?> 'a)
        Extra (TryWith (expr', handler'), cont)
    member this.TryFinally (expr: Delayed<'a>, handler: unit -> unit) : SimpleAction<'a> =
        let expr' () = expr () |> bindBase(fun a -> Done (a :> obj))
        let cont (res: obj) : SimpleAction<'a> = Done (res :?> 'a)
        Extra (TryFinally (expr', handler), cont)
    member this.For(values: seq<_>, f) =
        match Seq.tryHead values with
        | Some head ->
            let next () = this.For (Seq.tail values, f)
            bind next (f head)
        | None ->
            Done ()
    member this.While(cond: unit -> bool, body: unit -> SimpleAction<unit>) : SimpleAction<unit> =
        if cond () then
            bind (fun () -> this.While (cond, body)) (body ()) 
        else
            Done ()
    member this.Using(disposable:#IDisposable, body) =
        let body' = fun () -> body disposable
        this.TryFinally(body', fun () ->
            match disposable with
                | null -> ()
                | disp -> disp.Dispose())

/// Builds a SimpleAction.
let actor = ActorBuilder ()

module private SimpleActorPrivate =

    type Start = {
        checkpoint: Option<IMessageHandler>
        restartHandlers: LifeCycleHandlers.LifeCycleHandlers<IActorContext * obj * exn>
        stopHandlers: LifeCycleHandlers.LifeCycleHandlers<IActorContext>
    }

let internal handleSimpleActions (
    ctx: Akka.Actor.IActorContext,
    actionCtx: IActionContext,
    setMsgHandler,
    onDone,
    startAction: SimpleAction<obj>) =
    
    let convertExnToResult func =
        try
            Ok (func ())
        with
        | err -> Result.Error err
    let tryNextAction body okHandler errorHandler =
        match convertExnToResult body with
        | Ok action ->
            okHandler action
        | Error err ->
            errorHandler err
    let runFinally handler =
        try
            handler ()
        with
        | err ->
            ctx.GetLogger().Error(err, "Got exception when running finally handler")

    let rec handleActions (action: SimpleAction<obj>) =
        match action with
        | Done res ->
            onDone res
        | Stop _ ->
            ctx.Stop ctx.Self
        | Simple next ->
            handleActions (next actionCtx)
        | Extra (extra, next) ->
            match extra with
            | WaitForMsg factory ->
                setMsgHandler (factory.CreateObj(next, handleActions))
            | TryWith(body, handler) ->
                let cont res =
                    match res with
                    | Ok result -> result |> next |> handleActions 
                    | Error err -> handleException (next >> handleActions) (handler err)
                let handleErrorBeforeFirstAction err =
                    tryNextAction
                        (fun () -> handler err)
                        (handleException cont)
                        raise
                tryNextAction
                    body
                    (handleTryWith cont handler)
                    handleErrorBeforeFirstAction
            | TryFinally (body, handler) ->
                let cont res =
                    match res with
                    | Ok result ->
                        result |> next |> handleActions
                    | Error err ->
                        raise err
                let handleErrorBeforeFirstAction err =
                    runFinally handler
                    raise err
                tryNextAction
                    body
                    (handleTryFinally cont handler)
                    handleErrorBeforeFirstAction

    and handleTryWith (cont: TryWithResult -> unit) (handler: exn -> SimpleAction<obj>) (action: SimpleAction<obj>) =
        let makeNewCont next res =
            match res with
            | Ok result ->
                match convertExnToResult (fun () -> next result) with
                | Ok action -> handleTryWith cont handler action
                | Error err -> handleException cont (handler err)
            | Error err ->
                handleException cont (handler err)
        match action with
        | Done result ->
            cont (Ok result)
        | Stop _ ->
            ctx.Stop ctx.Self
        | Simple next ->
            match convertExnToResult (fun () -> next actionCtx) with
            | Ok action -> handleTryWith cont handler action
            | Error err ->
                match convertExnToResult (fun () -> handler err) with
                | Ok action -> handleException cont action
                | Error err -> cont (Result.Error err)
        | Extra (extra, next) ->
            match extra with
            | WaitForMsg factory ->
                setMsgHandler (factory.CreateObj(next, handleTryWith cont handler))
            | TryWith(body, newHandler) ->
                let cont' = makeNewCont next
                let handleErrorBeforeFirstAction err =
                    tryNextAction
                        (fun () -> newHandler err)
                        (handleException cont')
                        (Result.Error >> cont')
                tryNextAction
                    body
                    (handleTryWith cont' newHandler)
                    handleErrorBeforeFirstAction
            | TryFinally(body, newHandler) ->
                let cont' = makeNewCont next
                let handleError err =
                    runFinally newHandler
                    cont' (Result.Error err)
                tryNextAction
                    body
                    (handleTryFinally cont' newHandler)
                    handleError

    and handleException (cont: TryWithResult -> unit) (action: SimpleAction<obj>) =
        let makeNewCont next res =
            match res with
            | Ok result ->
                match convertExnToResult (fun () -> next result) with
                | Ok action -> handleException cont action
                | Error err -> cont (Result.Error err)
            | Error err ->
                cont (Result.Error err)
        match action with
        | Done result ->
            cont (Ok result)
        | Stop _ ->
            ctx.Stop ctx.Self
        | Simple next ->
            let nextRes =
                try
                    Ok (next actionCtx)
                with
                | err -> Result.Error err
            match nextRes with
            | Ok action -> handleException cont action
            | Error err -> cont (Result.Error err)
        | Extra (extra, next) ->
            match extra with
            | WaitForMsg factory ->
                setMsgHandler (factory.CreateObj(next, handleException cont))
            | TryWith(body, handler) ->
                let cont' = makeNewCont next
                let handleErrorBeforeFirstAction err =
                    tryNextAction
                        (fun () -> handler err)
                        (handleException cont)
                        (Result.Error >> cont)
                tryNextAction
                    body
                    (handleTryWith cont' handler)
                    handleErrorBeforeFirstAction
            | TryFinally(body, newHandler) ->
                let cont' = makeNewCont next
                let handleError err =
                    runFinally newHandler
                    cont' (Result.Error err)
                tryNextAction
                    body
                    (handleTryFinally cont' newHandler)
                    handleError

    and handleTryFinally (cont: TryWithResult -> unit) (handler: unit -> unit) (action: SimpleAction<obj>) =
        let handleError err =
            runFinally handler
            cont (Result.Error err)
        let makeNewCont next res =
            let res' = res |> Result.bind (fun result ->
                convertExnToResult (fun () -> next result)
            )
            match res' with
            | Ok action ->
                handleTryFinally cont handler action
            | Error err ->
                handleError err
        match action with
        | Done result ->
            runFinally handler
            cont (Ok result)
        | Stop _ ->
            ctx.Stop ctx.Self
        | Simple next ->
            match convertExnToResult (fun () -> next actionCtx) with
            | Ok action ->
                handleTryFinally cont handler action
            | Error err ->
                handleError err
        | Extra (extra, next) ->
            match extra with
            | WaitForMsg factory ->
                setMsgHandler (factory.CreateObj(next, handleTryFinally cont handler))
            | TryWith(body, newHandler) ->
                let cont' = makeNewCont next
                let handleErrorBeforeFirstAction err =
                    tryNextAction
                        (fun () -> newHandler err)
                        (handleException cont')
                        (Result.Error >> cont')
                tryNextAction
                    body
                    (handleTryWith cont' newHandler)
                    handleErrorBeforeFirstAction
            | TryFinally(body, newHandler) ->
                let cont' = makeNewCont next
                tryNextAction
                    body
                    (handleTryFinally cont' newHandler)
                    (fun err ->
                        runFinally newHandler
                        cont' (Result.Error err)
                    )
    
    handleActions startAction

/// <summary>
/// The actor class used when spawning an actor of notPersisted or checkpointed type.
/// This class is not meant to be used directly, instead use Spawn.spawn, or derive a new class from NotPersistedActor or CheckpointedActor.
/// </summary>
type SimpleActor (persist: bool, startAction: unit -> SimpleAction<unit>) as this =
    
    inherit Akka.Actor.UntypedActor ()

    let ctx = Akka.Actor.UntypedActor.Context :> Akka.Actor.IActorContext
    do
        let start : SimpleActorPrivate.Start = {
            checkpoint = None
            restartHandlers = LifeCycleHandlers.LifeCycleHandlers<IActorContext * obj * exn>()
            stopHandlers = LifeCycleHandlers.LifeCycleHandlers<IActorContext>()
        }
        ctx.Self.Tell(start, ctx.Self)
        
    let mutable stash = Unchecked.defaultof<Akka.Actor.IStash>

    let mutable restartHandlers = LifeCycleHandlers.LifeCycleHandlers<IActorContext * obj * exn>()
    let mutable stopHandlers = LifeCycleHandlers.LifeCycleHandlers<IActorContext>()
    
    let logger = ctx.GetLogger()

    let mutable msgHandler = {
        new IMessageHandler with
            member this.HandleMessage msg =
                logger.Error("Received message before waitForStart installed: {0}", msg)
    }

    do
        let onDone _ = ctx.Stop ctx.Self
        msgHandler <- {
            new IMessageHandler with
                member _.HandleMessage (msg: obj) =
                    match msg with
                    | :? SimpleActorPrivate.Start as start ->
                        restartHandlers <- start.restartHandlers
                        stopHandlers <- start.stopHandlers
                        match start.checkpoint with
                        | None ->
                            handleSimpleActions(ctx, this, (fun h -> msgHandler <- h), onDone, (startAction () |> mapResult (fun a -> a :> obj)))
                        | Some cont ->
                            msgHandler <- cont
                        stash.UnstashAll ()
                    | _ ->
                        stash.Stash ()
        }

    override _.OnReceive (msg: obj) = msgHandler.HandleMessage msg

    override _.PreRestart(err: exn, msg: obj) =
        let logger = Logging.GetLogger (ctx.System, ctx.Self.Path.ToStringWithAddress())
        logger.Error $"Actor restarting after message {msg}: {err}"
        restartHandlers.ExecuteHandlers (this :> IActorContext, msg, err)
        let startMsg : SimpleActorPrivate.Start = 
            if persist then
                {
                    checkpoint = Some msgHandler
                    restartHandlers = restartHandlers
                    stopHandlers = stopHandlers
                }
            else
                {
                    checkpoint = None
                    restartHandlers = LifeCycleHandlers.LifeCycleHandlers<IActorContext * obj * exn>()
                    stopHandlers = LifeCycleHandlers.LifeCycleHandlers<IActorContext>()
                }
        ctx.Self.Tell(startMsg, ctx.Self)
        base.PreRestart (err, msg)
    
    override _.PostStop () =
        stopHandlers.ExecuteHandlers (this :> IActorContext)
        base.PostStop()
        
    interface IActionContext with
        member _.Context = ctx
        member _.Self = ctx.Self
        member _.Logger = Logger ctx
        member _.Sender = ctx.Sender
        member _.Scheduler = ctx.System.Scheduler
        member _.ActorFactory = ctx :> Akka.Actor.IActorRefFactory
        member _.Watch act = ctx.Watch act |> ignore
        member _.Unwatch act = ctx.Unwatch act |> ignore
        member _.ActorSelection (path: string) = ctx.ActorSelection path
        member _.ActorSelection (path: Akka.Actor.ActorPath) = ctx.ActorSelection path
        member _.Stash = stash
        member _.SetRestartHandler handler = restartHandlers.AddHandler handler
        member _.ClearRestartHandler id = restartHandlers.RemoveHandler id
        member this.SetStopHandler handler = stopHandlers.AddHandler handler
        member this.ClearStopHandler id = stopHandlers.RemoveHandler id

    interface Akka.Actor.IWithUnboundedStash with
        member _.Stash
            with get () = stash
            and set newStash = stash <- newStash

/// <summary>
/// Class that can be used to spawn notPersisted actors. Usually, Spawn.spawn should be used to spawn actors, but if
/// you need a class type to use with Akka.Actor.Props.Create for special cases (like remote deployment) then a new class
/// can be derived from this class instead.  
/// </summary>
/// <param name="action">The action for the actor to run.</param>
type NotPersistedActor(action: unit -> SimpleAction<unit>) =
    
    inherit SimpleActor(false, action)
    
    new (action: SimpleAction<unit>) = NotPersistedActor(fun () -> action)
    
/// <summary>
/// Class that can be used to spawn checkpointed actors. Usually, Spawn.spawn should be used to spawn actors, but if
/// you need a class type to use with Akka.Actor.Props.Create for special cases (like remote deployment) then a new class
/// can be derived from this class instead.
/// </summary>
/// <param name="action">The action for the actor to run.</param>
type CheckpointedActor(action: unit -> SimpleAction<unit>) =
    
    inherit SimpleActor(true, action)
    
    new (action: SimpleAction<unit>) = CheckpointedActor(fun () -> action)
    
let internal spawn (parent: Akka.Actor.IActorRefFactory) (props: Props) (persist: bool) (action: unit -> SimpleAction<unit>) =
    let applyMod arg modifier current =
        match arg with
        | Some a -> modifier a current
        | None -> current    
    let actProps =
        Akka.Actor.Props.Create(fun () -> SimpleActor(persist, action))
        |> applyMod props.dispatcher (fun d a -> a.WithDispatcher d)
        |> applyMod props.deploy (fun d a -> a.WithDeploy d)
        |> applyMod props.mailbox (fun d a -> a.WithMailbox d)
        |> applyMod props.router (fun d a -> a.WithRouter d)
        |> applyMod props.supervisionStrategy (fun d a -> a.WithSupervisorStrategy d)
    let act =
        match props.name with
        | Some name ->
            parent.ActorOf(actProps, name)
        | None ->
            parent.ActorOf(actProps)
    Akkling.ActorRefs.typed act

type Spawn () =
    
    /// <summary>
    /// Spawns an actor whose state is not persisted if it crashes (i.e., it restarts from the initial action).
    /// </summary>
    /// <param name="parent">The actor's parent.</param>
    /// <param name="props">The actor's properties.</param>
    /// <param name="action">Function that generates the initial action for the actor</param>
    static member NotPersisted(parent, props, action) =
        spawn parent props false action
    
    /// <summary>
    /// Spawns an actor whose state is persisted if it crashes (i.e., the actor restarts from the last point where it
    /// waited for a message).
    /// </summary>
    /// <param name="parent">The actor's parent.</param>
    /// <param name="props">The actor's properties.</param>
    /// <param name="action">Function that generates the initial action for the actor</param>
    static member Checkpointed(parent, props, action) =
        spawn parent props true action

/// <summary>
/// Creates an actor that goes back to the given action if it restarts. NOTE: This function is deprecated, use
/// Simple.Spawn.NotPersisted instead.
/// </summary>
/// <param name="parent">The parent for the actor.</param>
/// <param name="props">The properties for the actor.</param>
/// <param name="action">The action for the actor to execute.</param>
let spawnNotPersisted parent props action = spawn parent props false (fun () -> action)

/// <summary>
/// Creates and actor that runs the given action. If the actor crashes then it restarts from the last point where it
/// was waiting for a message. NOTE: This function is deprecated, use Simple.Spawn.Checkpointed instead.
/// </summary>
/// <param name="parent">The parent for the actor.</param>
/// <param name="props">The properties for the actor.</param>
/// <param name="action">The action for the actor to execute.</param>
let spawnCheckpointed parent props action = spawn parent props true (fun () -> action)

[<AutoOpen>]
module Actions =

    type private Timeout = {started: DateTime}

    /// The actor context. Note that all functionality in this interface is also available via individual actions.
    /// This interface is mostly meant to be used by actors that are entirely contained within a Receive.HandleMessages
    /// action.
    type IContext =
        /// Gets a reference to this actor.
        abstract member GetSelf: unit -> Akkling.ActorRefs.IActorRef<_>
        /// Gets the actor factory to use when starting children of this actor.
        abstract member ActorFactory: Akka.Actor.IActorRefFactory
        /// Gets the actor's logger.
        abstract member Logger: Logger
        
        /// Gets the sender of the current message.
        abstract member GetSender: unit -> Akkling.ActorRefs.IActorRef<_>
        /// Stashes the current message.
        abstract member Stash: unit -> unit
        /// Unstashes one message.
        abstract member Unstash: unit -> unit
        /// Unstashes all stashed messages.
        abstract member UnstashAll: unit -> unit

        /// Watches the given actor for termination.
        abstract member Watch: Akka.Actor.IActorRef -> unit
        /// Watches the given actor for termination.
        abstract member Watch: Akkling.ActorRefs.IActorRef<_> -> unit
        /// Stops watching the given actor for termination.
        abstract member Unwatch: Akka.Actor.IActorRef -> unit
        /// Stops watching the given actor for termination.
        abstract member Unwatch: Akkling.ActorRefs.IActorRef<_> -> unit

        /// Schedules the given message to be sent to the given actor after the given delay. The returned object can be used
        /// to cancel the sending of the message.
        abstract member Schedule: TimeSpan * 'Msg * Akkling.ActorRefs.IActorRef<'Msg> -> Akka.Actor.ICancelable
        /// Schedules the given message to be sent to the given actor after the given delay. After the first send, the message
        /// will be sent repeatedly with the given interval between sends. The returned object can be used to cancel the sending
        /// of the message.
        abstract member ScheduleRepeatedly: TimeSpan * TimeSpan * 'Msg * Akkling.ActorRefs.IActorRef<'Msg> -> Akka.Actor.ICancelable
        
        /// Get an actor selection for the given actor path.
        abstract member Select: string -> Akka.Actor.ActorSelection
        /// Get an actor selection for the given actor path.
        abstract member Select: Akka.Actor.ActorPath -> Akka.Actor.ActorSelection

        /// Add a function to call if the actor restarts. The function is passed the actor context, the message that was
        /// being processed when the crash happened, and the exception that caused the crash. This action returns an ID
        /// that can be used to remove the handler via the ClearRestartHandler action.
        abstract member SetRestartHandler: RestartHandler -> int
        /// Clears the restart handler for the given index.
        abstract member ClearRestartHandler: int -> unit
        /// Add a function to call when the actor stops. The function is passed the actor context. This action returns an ID
        /// that can be used to remove the handler via the ClearStopHandler action.
        abstract member SetStopHandler: StopHandler -> int
        /// Clears the restart handler for the given index.
        abstract member ClearStopHandler: int -> unit

    type private Context(ctx: IActionContext) =
        interface IContext with
            member _.GetSelf () = Akkling.ActorRefs.typed ctx.Self
            member this.ActorFactory = ctx.ActorFactory
            member this.Logger = ctx.Logger
            member this.GetSender() = Akkling.ActorRefs.typed ctx.Sender
            member this.Stash() = ctx.Stash.Stash()
            member this.Unstash() = ctx.Stash.Unstash()
            member this.UnstashAll() = ctx.Stash.UnstashAll()
            member this.Unwatch (actor: Akka.Actor.IActorRef) = ctx.Unwatch actor
            member this.Unwatch<'Msg> (actor: Akkling.ActorRefs.IActorRef<'Msg>) = ctx.Unwatch (Akkling.ActorRefs.untyped actor)
            member this.Watch (actor: Akka.Actor.IActorRef) = ctx.Watch actor
            member this.Watch<'Msg> (actor: Akkling.ActorRefs.IActorRef<'Msg>) = ctx.Watch (Akkling.ActorRefs.untyped actor)
            member this.Schedule (delay, msg, receiver) =
                let cancel = new Akka.Actor.Cancelable (ctx.Scheduler)
                ctx.Scheduler.ScheduleTellOnce (delay, Akkling.ActorRefs.untyped receiver, msg, ctx.Self, cancel)
                cancel :> Akka.Actor.ICancelable
            member this.ScheduleRepeatedly (delay, interval, msg, receiver) =
                let cancel = new Akka.Actor.Cancelable (ctx.Scheduler)
                ctx.Scheduler.ScheduleTellRepeatedly (delay, interval, Akkling.ActorRefs.untyped receiver, msg, ctx.Self, cancel)
                cancel :> Akka.Actor.ICancelable
            member this.Select (path: string) = ctx.ActorSelection path
            member this.Select (path: Akka.Actor.ActorPath) = ctx.ActorSelection path
            member this.SetRestartHandler handler = ctx.SetRestartHandler handler
            member this.ClearRestartHandler index = ctx.ClearRestartHandler index
            member this.SetStopHandler handler = ctx.SetStopHandler handler
            member this.ClearStopHandler index = ctx.ClearStopHandler index
            
    /// Gets the actor context. Note that all functionality in IContext is also available via individual actions.
    /// This interface is mostly meant to be used by actors that are entirely contained within a Receive.HandleMessages
    /// action.
    let getContext () : SimpleAction<IContext> = Simple(fun ctx -> Done (Context ctx :> IContext))

    /// Stashes the most recently received message.
    let stash () : SimpleAction<unit> = Simple (fun ctx -> Done (ctx.Stash.Stash ()))
    /// Unstashes the message at the front of the stash.
    let unstashOne () : SimpleAction<unit> = Simple (fun ctx -> Done (ctx.Stash.Unstash ()))
    /// Unstashes all the messages in the stash.
    let unstashAll () : SimpleAction<unit> = Simple (fun ctx -> Done (ctx.Stash.UnstashAll ()))

    /// Strategy for dealing with "other messages" when using receive and sleep methods.
    type IOtherMsgStrategy =
        abstract member OtherMsg: obj -> SimpleAction<unit>
        abstract member OnDone: unit -> SimpleAction<unit>

    /// "other message" strategy that stashes messages.
    let stashOthers = {
        new IOtherMsgStrategy with
            member _.OtherMsg _msg = stash ()
            member _.OnDone () = unstashAll ()
    }

    /// "other message" strategy that ignores messages.
    let ignoreOthers = {
        new IOtherMsgStrategy with
            member _.OtherMsg _msg = actor.Return ()
            member _.OnDone () = actor.Return ()
    }
    
     /// The result of handling a message via Receive.HandleMessages.
    type HandleMessagesResult<'Msg, 'Result> =
        /// We are done handling messages with the given result. If HandleMessages was the only action in the actor then
        /// the actor will stop, else the result given here will be the result of the HandleMessages action. 
        | IsDone of 'Result
        /// Continue processing messages using the current message handling function.
        | Continue
        /// Continue processing messages using the given message handling function.
        | ContinueWith of ('Msg -> HandleMessagesResult<'Msg, 'Result>)
        /// Continue the actor with the given action. The result of the action will become the result of the HandleMessages
        /// action.
        | ContinueWithAction of SimpleAction<'Result>

    type private MessageHandler<'Msg, 'Result, 'ContResult>(
        handler: 'Msg -> HandleMessagesResult<'Msg, 'Result>,
        cont: obj -> SimpleAction<'ContResult>,
        actionHandler: SimpleAction<'ContResult> -> unit
    ) =
        
        let mutable handle = handler

        interface IMessageHandler with
            member _.HandleMessage (msg: obj) =
                match msg with
                | :? 'Msg as msg ->
                    match handle msg with
                    | IsDone res -> actionHandler (cont res)
                    | Continue -> ()
                    | ContinueWith newHandler -> handle <- newHandler
                    | ContinueWithAction action -> actionHandler (bind cont action)
                | _ ->
                    ()
    
    type private MessageHandlerFactory<'Msg, 'Result>(
        handler: 'Msg -> HandleMessagesResult<'Msg, 'Result>
    ) =
        interface IMessageHandlerFactory with
            member _.CreateObj(cont: obj -> SimpleAction<obj>, actionHandler: SimpleAction<obj> -> unit) =                
                MessageHandler<'Msg, 'Result, obj>(handler, cont, actionHandler)
            
    type Receive () =
        static let handle (handler: 'Msg -> HandleMessagesResult<'Msg, 'Result>) =
            Extra(WaitForMsg (MessageHandlerFactory handler), (fun res -> Done(res :?> 'Result)))
        
        /// Use the given function to handle messages. The result of the function determines how to proceed after a
        /// message is received.
        static member HandleMessages (handler: 'Msg -> HandleMessagesResult<'Msg, 'Result>) = handle handler 
        /// Use the given function to handle messages. The result of the function determines how to proceed after a
        /// message is received. The context for the actor is passed to the function as well.
        static member HandleMessages (handler: IContext -> 'Msg -> HandleMessagesResult<'Msg, 'Result>) = actor {
            let! ctx = getContext ()
            return! handle (handler ctx) 
        }
        
        /// Wait for a message for which the given function returns `Some`. What to do with other messages received
        /// while filtering is given by the otherMsg argument, it defaults to ignoreOthers.
        static member Filter (choose: obj -> Option<'Msg>, ?otherMsg) =
            let otherMsg = defaultArg otherMsg ignoreOthers
            let rec recv (msg: obj) =
                match msg with
                | :? Timeout ->
                    Continue
                | msg ->
                    match choose msg with
                    | Some res ->
                        ContinueWithAction (actor {
                            do! otherMsg.OnDone ()
                            return res
                        })
                    | None ->
                        ContinueWithAction (actor {
                            do! otherMsg.OtherMsg msg
                            return! handle recv
                        })
            handle recv
            
        /// Wait for a message for which the given function returns `Some`. If the timeout is reached before an appropriate
        /// message is received then None is returned. What to do with other messages received while filtering is given
        /// by the otherMsg argument, it defaults to ignoreOthers.
        static member Filter (choose: obj -> Option<'Msg>, timeout: TimeSpan, ?otherMsg) =
            let otherMsg = defaultArg otherMsg ignoreOthers
            let rec recv started (cancel: Akka.Actor.ICancelable) (msg: obj)= 
                match msg with
                | :? Timeout as timeout ->
                    if timeout.started = started then
                        ContinueWithAction (actor{
                            do! otherMsg.OnDone ()
                            return None                            
                        })
                    else
                        Continue
                | msg ->
                    match choose msg with
                    | Some res ->
                        cancel.Cancel()
                        ContinueWithAction (actor{
                            do! otherMsg.OnDone ()
                            return (Some res)
                        })
                    | None ->
                        ContinueWithAction (actor{
                            do! otherMsg.OtherMsg msg
                            return! handle (recv started cancel)
                        })
            actor {
                let now = DateTime.Now
                let! self = getActor ()
                let! cancel = doSchedule timeout (Akkling.ActorRefs.retype self) {started = now}
                return! handle (recv now cancel)
            }

        /// Waits for any message to be received.
        static member Any () = Receive.Filter Some
        /// Waits for any message to be received. If the timeout is reached before a message is received, then None is
        /// returned.
        static member Any (timeout: TimeSpan) = Receive.Filter(Some, timeout)

        /// Waits for a message of the given type to be received. What to do with other messages received
        /// while filtering is given by the otherMsg argument, it defaults to ignoreOthers.
        static member Only<'Msg> ?otherMsg : SimpleAction<'Msg> =
            let filter (msg: obj) =
                match msg with
                | :? 'Msg as m ->
                    Some m
                | _ ->
                    None
            Receive.Filter (filter, ?otherMsg = otherMsg)
        /// Waits for a message of the given type to be received. If the timeout is reached before an appropriate
        /// message is received then None is returned. What to do with other messages received while filtering is
        /// given by the otherMsg argument, it defaults to ignoreOthers.
        static member Only<'Msg> (timeout: TimeSpan, ?otherMsg) : SimpleAction<Option<'Msg>> =
            let filter (msg: obj) =
                match msg with
                | :? 'Msg as m -> Some m
                | _ -> None
            Receive.Filter (filter, timeout, ?otherMsg = otherMsg)

        /// Waits for a message of the given type that passes the given filter to be received. What to do with other
        /// messages received while filtering is given by the otherMsg argument, it defaults to ignoreOthers.
        static member FilterOnly<'Msg>(filter: 'Msg -> bool, ?otherMsg) : SimpleAction<'Msg> =
            let filterType (msg: obj) =
                match msg with
                | :? 'Msg as m -> if filter m then Some m else None
                | _ -> None
            Receive.Filter (filterType, ?otherMsg = otherMsg)
        /// Waits for a message of the given type that passes the given filter to be received. If the timeout is reached
        /// before an appropriate message is received then None is returned. What to do with other messages received
        /// while filtering is given by the otherMsg argument, it defaults to ignoreOthers.
        static member FilterOnly<'Msg> (timeout: TimeSpan, filter: 'Msg -> bool, ?otherMsg) : SimpleAction<Option<'Msg>> =
            let filterType (msg: obj) =
                match msg with
                | :? 'Msg as m -> if filter m then Some m else None
                | _ -> None
            Receive.Filter (filterType, timeout, ?otherMsg = otherMsg)
    
    /// Gets the sender of the most recently received message.
    let getSender () = Simple (fun ctx -> Done (Akkling.ActorRefs.typed ctx.Sender))

    type private SleepDone = SleepDone

    /// Sleeps for the given amount of time, message received during the sleep are handled using otherMsgHandler
    let sleep interval (otherMsgHandler: IOtherMsgStrategy) : SimpleAction<unit> = actor {
        let! self = getActor ()
        let! _ = schedule interval self SleepDone
        let rec loop () = actor {
            match! Receive.Any() with
            | :? SleepDone ->
                do! otherMsgHandler.OnDone ()
                return ()
            | msg ->
                do! otherMsgHandler.OtherMsg msg
                return! loop ()
        }
        return! loop ()
    }

    type Termination private () =
        static member Wait(terminated: Akkling.ActorRefs.IActorRef<'msg>, otherMsgHandler: IOtherMsgStrategy) = actor {
            do! watch terminated
            let rec loop () = actor {
                let! msg = Receive.Only<Akka.Actor.Terminated> otherMsgHandler
                if msg.ActorRef = Akkling.ActorRefs.untyped terminated then
                    return ()
                else
                    return! loop ()
            }
            return! loop ()
        }

        static member Wait (terminated: Akkling.ActorRefs.IActorRef<'msg>, timeout: TimeSpan, otherMsgHandler: IOtherMsgStrategy) = actor {
            do! watch terminated
            let rec loop () = actor {
                match! Receive.Only<Akka.Actor.Terminated> (timeout, otherMsgHandler) with
                | Some msg ->
                    if msg.ActorRef = Akkling.ActorRefs.untyped terminated then
                        return true
                    else
                        return! loop ()
                | None ->
                    return false
            }
            return! loop ()
        }

///Maps the given function over the given array within an actor expression.
let mapArray (func: 'a -> SimpleAction<'b>) (values: 'a []) : SimpleAction<'b []> =
    let rec loop (results: 'b []) i = actor {
        if i < values.Length then
            let! res = func values[i]
            results[i] <- res
            return! loop results (i + 1)
        else
            return results
    }
    loop (Array.zeroCreate values.Length) 0

///Maps the given function over the given list within an actor expression.
let mapList (func: 'a -> SimpleAction<'b>) (values: List<'a>) : SimpleAction<List<'b>> =
    let rec loop (left: List<'a>) (results: List<'b>) = actor {
        match left with
        | head::tail ->
            let! res = func head
            return! loop tail (res :: results)
        | [] ->
            return List.rev results
    }
    loop values []

///Folds the given function over the given sequence of actions within an actor expression.
let foldActions (func: 'a -> 'res -> SimpleAction<'res>) (init: 'res) (values: seq<SimpleAction<'a>>) : SimpleAction<'res> =
    let rec loop left cur = actor {
        if Seq.isEmpty left then
            return cur
        else
            let! value = Seq.head left
            let! res = func value cur
            return! loop (Seq.tail left) res
    }
    loop values init

/// Folds the given function over the given sequence of values within an actor expression.
let foldValues (func: 'a -> 'res -> SimpleAction<'res>) (init: 'res) (values: seq<'a>) : SimpleAction<'res> =
    let rec loop left cur = actor {
        if Seq.isEmpty left then
            return cur
        else
            let! res = func (Seq.head left) cur
            return! loop (Seq.tail left) res
    }
    loop values init

/// Executes body as long as condition evaluates to true
let executeWhile (condition: SimpleAction<Option<'condRes>>) (body: 'condRes -> SimpleAction<unit>) : SimpleAction<unit> =
    let rec loop () = actor {
        match! condition with
        | Some condRes ->
            let! newRes = body condRes
            return! loop newRes
        | None ->
            return ()
    }
    loop ()


