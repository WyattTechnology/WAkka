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

open Common

type SimpleType internal () = class end

type SimpleExtra =
    private
    | WaitForMsg
    | TryWith of (unit -> ActionBase<obj, SimpleExtra>) * (exn -> ActionBase<obj, SimpleExtra>)
    | TryFinally of (unit -> ActionBase<obj, SimpleExtra>) * (unit -> unit)

/// An action that can only be used directly in a Simple actor (e.g. started using notPersistent or checkpointed).
type SimpleAction<'Result> = ActionBase<'Result, SimpleExtra>

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
    member this.Using(disposable:#IDisposable, body) =
        let body' = fun () -> body disposable
        this.TryFinally(body', fun () ->
            match disposable with
                | null -> ()
                | disp -> disp.Dispose())

/// Builds a SimpleAction.
let actor = ActorBuilder ()

module private SimpleActor =

    type Start = {
        checkpoint: Option<obj -> unit>
        restartHandlers: LifeCycleHandlers.LifeCycleHandlers<IActorContext * obj * exn>
        stopHandlers: LifeCycleHandlers.LifeCycleHandlers<IActorContext>
    }

    type Actor (persist: bool, startAction: SimpleAction<unit>) as this =

        inherit Akka.Actor.UntypedActor ()

        let ctx = Akka.Actor.UntypedActor.Context :> Akka.Actor.IActorContext
        let mutable stash = Unchecked.defaultof<Akka.Actor.IStash>

        let mutable restartHandlers = LifeCycleHandlers.LifeCycleHandlers<IActorContext * obj * exn>()
        let mutable stopHandlers = LifeCycleHandlers.LifeCycleHandlers<IActorContext>()
        
        let logger = Logger ctx

        let mutable msgHandler = fun msg ->
            logger.Error $"Received message before waitForStart installed: {msg}"

        let convertExnToResult func =
            try
                Ok (func ())
            with
            | err -> Error err
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
                logger.Error $"Got exception when running finally handler: {err}"

        let rec handleActions (action: SimpleAction<unit>) =
            match action with
            | Done _
            | Stop _ ->
                ctx.Stop ctx.Self
            | Simple next ->
                handleActions (next this)
            | Extra (extra, next) ->
                match extra with
                | WaitForMsg ->
                    let cont = next >> handleActions
                    msgHandler <- waitForMsg cont
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
                match convertExnToResult (fun () -> next this) with
                | Ok action -> handleTryWith cont handler action
                | Error err ->
                    match convertExnToResult (fun () -> handler err) with
                    | Ok action -> handleException cont action
                    | Error err -> cont (Error err)
            | Extra (extra, next) ->
                match extra with
                | WaitForMsg ->
                    let cont = next >> handleTryWith cont handler
                    msgHandler <- waitForMsg cont
                | TryWith(body, newHandler) ->
                    let cont' = makeNewCont next
                    let handleErrorBeforeFirstAction err =
                        tryNextAction
                            (fun () -> newHandler err)
                            (handleException cont')
                            (Error >> cont')
                    tryNextAction
                        body
                        (handleTryWith cont' newHandler)
                        handleErrorBeforeFirstAction
                | TryFinally(body, newHandler) ->
                    let cont' = makeNewCont next
                    let handleError err =
                        runFinally newHandler
                        cont' (Error err)
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
                    | Error err -> cont (Error err)
                | Error err ->
                    cont (Error err)
            match action with
            | Done result ->
                cont (Ok result)
            | Stop _ ->
                ctx.Stop ctx.Self
            | Simple next ->
                let nextRes =
                    try
                        Ok (next this)
                    with
                    | err -> Error err
                match nextRes with
                | Ok action -> handleException cont action
                | Error err -> cont (Error err)
            | Extra (extra, next) ->
                match extra with
                | WaitForMsg ->
                    let cont = next >> handleException cont
                    msgHandler <- waitForMsg cont
                | TryWith(body, handler) ->
                    let cont' = makeNewCont next
                    let handleErrorBeforeFirstAction err =
                        tryNextAction
                            (fun () -> handler err)
                            (handleException cont)
                            (Error >> cont)
                    tryNextAction
                        body
                        (handleTryWith cont' handler)
                        handleErrorBeforeFirstAction
                | TryFinally(body, newHandler) ->
                    let cont' = makeNewCont next
                    let handleError err =
                        runFinally newHandler
                        cont' (Error err)
                    tryNextAction
                        body
                        (handleTryFinally cont' newHandler)
                        handleError

        and handleTryFinally (cont: TryWithResult -> unit) (handler: unit -> unit) (action: SimpleAction<obj>) =
            let handleError err =
                runFinally handler
                cont (Error err)
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
                match convertExnToResult (fun () -> next this) with
                | Ok action ->
                    handleTryFinally cont handler action
                | Error err ->
                    handleError err
            | Extra (extra, next) ->
                match extra with
                | WaitForMsg ->
                    let cont' = next >> handleTryFinally cont handler
                    msgHandler <- waitForMsg cont'
                | TryWith(body, newHandler) ->
                    let cont' = makeNewCont next
                    let handleErrorBeforeFirstAction err =
                        tryNextAction
                            (fun () -> newHandler err)
                            (handleException cont')
                            (Error >> cont')
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
                            cont' (Error err)
                        )
                        
        and waitForMsg (handler: obj -> unit) (msg:obj) : unit =
            match msg with
            | :? Start -> ()
            | _ -> handler msg

        let waitForStart (msg:obj) =
            match msg with
            | :? Start as start ->
                restartHandlers <- start.restartHandlers
                stopHandlers <- start.stopHandlers
                match start.checkpoint with
                | None ->
                    handleActions startAction
                | Some cont ->
                    msgHandler <- waitForMsg cont
                stash.UnstashAll ()
            | _ ->
                stash.Stash ()

        do msgHandler <- waitForStart

        override _.OnReceive (msg: obj) = msgHandler msg

        override _.PreRestart(err: exn, msg: obj) =
            let logger = Akka.Event.Logging.GetLogger (ctx.System, ctx.Self.Path.ToStringWithAddress())
            logger.Error $"Actor restarting after message {msg}: {err}"
            restartHandlers.ExecuteHandlers (this :> IActorContext, msg, err)
            let startMsg = 
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

let internal spawn (parent: Akka.Actor.IActorRefFactory) (props: Props) (persist: bool) (action: SimpleAction<unit>) =
    let applyMod arg modifier current =
        match arg with
        | Some a -> modifier a current
        | None -> current    
    let actProps =
        Akka.Actor.Props.Create(fun () -> SimpleActor.Actor(persist, action))
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
    let startMsg : SimpleActor.Start = {
        checkpoint = None
        restartHandlers = LifeCycleHandlers.LifeCycleHandlers<IActorContext * obj * exn>()
        stopHandlers = LifeCycleHandlers.LifeCycleHandlers<IActorContext>()
    }
    act.Tell(startMsg, act)
    Akkling.ActorRefs.typed act

[<AutoOpen>]
module Actions =

    type private Timeout = {started: DateTime}

    /// Stashes the most recently received message.
    let stash () : SimpleAction<unit> = Simple (fun ctx -> Done (ctx.Stash.Stash ()))
    /// Unstashes the message at the front of the stash.
    let unstashOne () : SimpleAction<unit> = Simple (fun ctx -> Done (ctx.Stash.Unstash ()))
    /// Unstashes all of the messages in the stash.
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

    type Receive () =
        static let nextMsg () : SimpleAction<obj> = Extra (WaitForMsg, Done)

        /// Wait for a message for which the given function returns `Some`. What to do with other messages received
        /// while filtering is given by the otherMsg argument, it defaults to ignoreOthers.
        static member Filter (choose: obj -> Option<'Msg>, ?otherMsg) =
            let otherMsg = defaultArg otherMsg ignoreOthers
            let rec recv () = actor {
                match! nextMsg () with
                | :? Timeout ->
                    return! recv ()
                | msg ->
                    match choose msg with
                    | Some res ->
                        do! otherMsg.OnDone ()
                        return res
                    | None ->
                        do! otherMsg.OtherMsg msg
                        return! recv ()
            }
            recv ()
        /// Wait for a message for which the given function returns `Some`. If the timeout is reached before an appropriate
        /// message is received then None is returned. What to do with other messages received while filtering is given
        /// by the otherMsg argument, it defaults to ignoreOthers.
        static member Filter (choose: obj -> Option<'Msg>, timeout: TimeSpan, ?otherMsg) =
            let otherMsg = defaultArg otherMsg ignoreOthers
            let rec recv started (cancel: Akka.Actor.ICancelable) = actor {
                match! nextMsg () with
                | :? Timeout as timeout ->
                    if timeout.started = started then
                        do! otherMsg.OnDone ()
                        return None
                    else
                        return! recv started cancel
                | msg ->
                    match choose msg with
                    | Some res ->
                        cancel.Cancel()
                        do! otherMsg.OnDone ()
                        return (Some res)
                    | None ->
                        do! otherMsg.OtherMsg msg
                        return! recv started cancel
            }
            actor {
                let now = DateTime.Now
                let! self = getActor ()
                let! cancel = doSchedule timeout (Akkling.ActorRefs.retype self) {started = now}
                return! recv now cancel
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
            let! res = func values.[i]
            results.[i] <- res
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


