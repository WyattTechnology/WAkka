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

module WAkka.EventSourced

open Common

type NoSnapshotExtra = NoSnapshotExtra

type SnapshotExtra<'Snapshot> =
    | GetLastSequenceNumber
    | DeleteEvents of sequenceNr:int64
    | SaveSnapshot of snapshot:'Snapshot
    | DeleteSnapshot of sequenceNr:int64
    | DeleteSnapshots of criteria:Akka.Persistence.SnapshotSelectionCriteria
    
type EventSourcedExtra<'Snapshot> =
    | RunAction of Simple.SimpleAction<obj>
    | GetRecovering
    | Snapshot of 'Snapshot
    
/// An action that can only be used directly in an event sourced actor (e.g. started using eventSourced).
type EventSourcedAction<'Result, 'Snapshot> = ActionBase<'Result, EventSourcedExtra<'Snapshot>>

let rec private bind (f: 'a -> EventSourcedAction<'b, 's>) (op: EventSourcedAction<'a, 's>) : EventSourcedAction<'b, 's> =
    bindBase f op

type ActorBuilder () =
    member this.Bind (x, f) = bind f x
    member this.Return x = Done x
    member this.ReturnFrom x = x
    member this.Zero () = Done ()
    member this.Combine(m1, m2) = this.Bind (m1, m2)
    member this.Delay (f: unit -> EventSourcedAction<'t, 's>): (unit -> EventSourcedAction<'t, 's>) = f
    member this.Run f = f ()
    member this.For(values, f) : ActionBase<unit, 't> =
        match Seq.tryHead values with
        | Some head ->
            let next () = this.For (Seq.tail values, f)
            bindBase next (f head)
        | None ->
            Done ()

/// Builds an EventSourced action.
let actor = ActorBuilder ()

module private EventSourcedActorPrivate =

    type Stopped = Stopped
    
    type PersistenceMsg =
        | Completed
        | Failed of exn * obj
        | Rejected of result:obj * reason:exn * sequenceNr:int64

open EventSourcedActorPrivate

module Internal = 
    type IActionHandler<'Snapshot> =
        abstract member StartAction: EventSourcedAction<unit, 'Snapshot>
        abstract member SnapshotHandler: obj -> EventSourcedAction<unit, 'Snapshot>
        abstract member HandleSnapshotExtra: 'Snapshot -> Akka.Persistence.UntypedPersistentActor -> (obj -> EventSourcedAction<'a, 'Snapshot>) -> EventSourcedAction<'a, 'Snapshot>
    
    type EventSourcedActorBase<'Snapshot>(
        persistenceId: Option<string>,
        handler: IActionHandler<'Snapshot>
    ) as this =

        inherit Akka.Persistence.UntypedPersistentActor ()

        let ctx = Akka.Persistence.Eventsourced.Context
        
        let mutable restartHandlers = LifeCycleHandlers.LifeCycleHandlers<IActorContext * obj * exn>()
        let mutable stopHandlers = LifeCycleHandlers.LifeCycleHandlers<IActorContext>()

        let mutable msgHandler = fun (_recovering: bool) _msg -> ()
        let mutable rejectionHandler = fun (_result:obj, _reasons: exn, _sequenceNr: int64)  -> ()
        
        let rec handleActions recovering action =
            match action with
            | Done _
            | Stop _ ->
                ctx.Stop ctx.Self
            | Simple next ->
                handleActions recovering (next this)
            | Extra (extra, next) ->
                match extra with
                | RunAction subAction ->
                    if recovering then
                        msgHandler <- (fun stillRecovering msg -> handleActions stillRecovering (next msg))
                    else
                        handleSubActions next subAction
                | GetRecovering ->
                    handleActions recovering (next recovering)
                | Snapshot snapshot ->
                    handleActions recovering (handler.HandleSnapshotExtra snapshot this next)

        and handleSubActions cont subAction =
            match subAction with
            | Simple.SimpleAction.Done res ->
                rejectionHandler <- (fun (result, reason, sn) -> handleActions false (cont (Rejected (result, reason, sn) :> obj)))
                this.Persist (res, fun evt -> handleActions false (cont evt))
            | Simple.SimpleAction.Stop _ ->
                rejectionHandler <- (fun _ -> ctx.Stop ctx.Self)
                this.Persist(Stopped, fun _ -> ctx.Stop ctx.Self)
            | Simple.SimpleAction.Simple next ->
                handleSubActions cont (next this)
            | Simple.SimpleAction.Extra (_, next) ->
                msgHandler <- (fun _ msg -> handleSubActions cont (next msg))

        do msgHandler <- (fun recovering msg ->
            let logger = Akka.Event.Logging.GetLogger (ctx.System, ctx.Self.Path.ToStringWithAddress())
            logger.Error $"Got msg before first receive(recovering = {recovering}): {msg}"
        )
        
        let mutable actionsInitialized = false

        override this.PersistenceId = defaultArg persistenceId (ctx.Self.Path.ToString())

        override _.OnCommand (msg: obj) =
            msgHandler false msg

        override _.OnPersistRejected(cause, event, sequenceNr) =
            let logger = Akka.Event.Logging.GetLogger (ctx.System, ctx.Self.Path.ToStringWithAddress())
            logger.Error $"rejected event ({sequenceNr}) {event}: {cause}"
            rejectionHandler (event, cause, sequenceNr)

        override _.OnRecover (msg: obj) =
            match msg with
            | :? Akka.Persistence.SnapshotOffer as snapshot ->
                handleActions true (handler.SnapshotHandler snapshot.Snapshot)
                actionsInitialized <- true
            | :? Akka.Persistence.RecoveryCompleted ->
                if not actionsInitialized then
                    handleActions true handler.StartAction
                    actionsInitialized <- true
                msgHandler false (Completed :> obj)
            | :? EventSourcedActorPrivate.Stopped ->
                ctx.Stop ctx.Self
            | _ ->
                if not actionsInitialized then
                    handleActions true handler.StartAction
                    actionsInitialized <- true
                msgHandler true msg

        override _.OnRecoveryFailure(reason, message) =
            let logger = Akka.Event.Logging.GetLogger (ctx.System, ctx.Self.Path.ToStringWithAddress())
            logger.Error $"recovery failed on message {message}: {reason}"
            msgHandler false (Failed (reason, message) :> obj)

        override this.PreRestart(reason, message) =
            let logger = Akka.Event.Logging.GetLogger (ctx.System, ctx.Self.Path.ToStringWithAddress())
            logger.Error $"Actor crashed on {message}: {reason}"
            restartHandlers.ExecuteHandlers(this :> IActorContext, message, reason)
            base.PreRestart(reason, message)
        
        override _.PostStop () =
            stopHandlers.ExecuteHandlers (this :> IActorContext)
            base.PostStop ()

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
            member _.Stash = this.Stash
            member _.SetRestartHandler handler = restartHandlers.AddHandler handler
            member _.ClearRestartHandler id = restartHandlers.RemoveHandler id
            member _.SetStopHandler handler = stopHandlers.AddHandler handler
            member _.ClearStopHandler id = stopHandlers.RemoveHandler id

type private NoSnapshotHandler (startAction) =
    interface Internal.IActionHandler<NoSnapshotExtra> with
        member _.StartAction = startAction
        member _.SnapshotHandler snapshot = actor {
            let! logger = getLogger()
            logger.Error $"Got snapshot offer without handler (stopping actor): {snapshot}"
            return! stop ()            
        }
        member _.HandleSnapshotExtra extra _act _next = actor {
            let! logger = getLogger()
            logger.Error $"Got snapshot extra without handler (stopping actor): {extra}"
            return! stop ()            
        }
        
/// <summary>
/// Class that can be used to spawn eventSourced actors. Usually, Spawn.spawn should be used to spawn actors, but if
/// you need a class type to use with Akka.Actor.Props.Create for special cases (like remote deployment) then a new class
/// can be derived from this class instead.  
/// </summary>
/// <param name="startAction">The action for the actor to run.</param>
type EventSourcedActor(startAction: EventSourcedAction<unit, NoSnapshotExtra>, ?persistenceId) =
    inherit Internal.EventSourcedActorBase<NoSnapshotExtra>(persistenceId, NoSnapshotHandler startAction)

type private SnapshotHandler<'Snapshot> (startAction, snapshotHandler) =
    interface Internal.IActionHandler<SnapshotExtra<'Snapshot>> with
        member _.StartAction = startAction
        member _.SnapshotHandler snapshot = snapshotHandler (snapshot :?> 'Snapshot)
        member _.HandleSnapshotExtra extra act next =
            match extra with
            | GetLastSequenceNumber ->
                next act.LastSequenceNr
            | DeleteEvents sequenceNr ->
                act.DeleteMessages sequenceNr
                next ()
            | SaveSnapshot snapshot ->
                act.SaveSnapshot snapshot
                next ()
            | DeleteSnapshot sequenceNr ->
                act.DeleteSnapshot sequenceNr
                next ()
            | DeleteSnapshots criteria ->
                act.DeleteSnapshots criteria
                next ()

/// <summary>
/// Class that can be used to spawn event sourced actors. Usually, spawnSnapshots should be used to spawn this type of actor, but if
/// you need a class type to use with Akka.Actor.Props.Create for special cases (like remote deployment) then a new class
/// can be derived from this class instead.  
/// </summary>
/// <param name="startAction">The action for the actor to run.</param>
/// <param name="snapshotHandler">If a snapshot is offered by the persistence system then it will be passed to this function to generate and initial action to use instead of startAction.</param>
type EventSourcedSnapshotActor<'Snapshot>(
    startAction: EventSourcedAction<unit, SnapshotExtra<'Snapshot>>,
    snapshotHandler: 'Snapshot -> EventSourcedAction<unit, SnapshotExtra<'Snapshot>>,
    ?persistenceId
) =
    inherit Internal.EventSourcedActorBase<SnapshotExtra<'Snapshot>>(persistenceId, SnapshotHandler(startAction, snapshotHandler))

/// <summary>
/// The properties for an event sourced actor.
/// </summary>
type Props = {
    /// The persistence id for the actor. If None then the actor's path will be used.
    persistenceId: Option<string>
    /// The common properties for the actor.
    common: Common.Props
}
with
    /// <summary>
    /// Creates a Props object with empty persistence id and the given name.
    /// </summary>
    /// <param name="name">The name for the actor.</param>
    static member Named name = {
        persistenceId = None
        common = Common.Props.Named name
    }
    
    /// <summary>
    /// Creates a Props object with empty persistence id and no name.
    /// </summary>
    static member Anonymous = {
        persistenceId = None
        common = Common.Props.Anonymous
    }
    
    /// <summary>
    /// Creates a Props object with the given persistence id and name.
    /// </summary>
    /// <param name="id">The persistence id for the actor.</param>
    /// <param name="actorName">The name for the actor (default produces an anonymous actor).</param>
    static member PersistenceId(id, ?actorName) = {
        persistenceId = Some id
        common =
            match actorName with
            | Some n -> Common.Props.Named n
            | None -> Common.Props.Anonymous 
    }
    
/// <summary>
/// Spawns an event sourced actor. This variant does not support snapshots.
/// </summary>
/// <param name="parent">The parent for the new actor.</param>
/// <param name="props">The actor's properties.</param>
/// <param name="action">The action to run.</param>
let spawnNoSnapshots (parent: Akka.Actor.IActorRefFactory) (props: Props) (action: EventSourcedAction<unit, NoSnapshotExtra>) =
    let applyMod arg modifier current =
        match arg with
        | Some a -> modifier a current
        | None -> current        
    let actProps =
        Akka.Actor.Props.Create(fun () -> EventSourcedActor(action, ?persistenceId = props.persistenceId))
        |> applyMod props.common.dispatcher (fun d a -> a.WithDispatcher d)
        |> applyMod props.common.deploy (fun d a -> a.WithDeploy d)
        |> applyMod props.common.mailbox (fun d a -> a.WithMailbox d)
        |> applyMod props.common.router (fun d a -> a.WithRouter d)
        |> applyMod props.common.supervisionStrategy (fun d a -> a.WithSupervisorStrategy d)
    let act = parent.ActorOf(actProps, ?name = props.common.name)
    Akkling.ActorRefs.typed act

/// <summary>
/// Spawns an event sourced actor. This variant supports snapshots.
/// </summary>
/// <param name="parent">The parent for the new actor.</param>
/// <param name="props">The actor's properties.</param>
/// <param name="action">The action to run if no snapshot is available.</param>
/// <param name="snapshotHandler">Called to generate the action to run based on the given snapshot.</param>
let spawnSnapshots
    (parent: Akka.Actor.IActorRefFactory)
    (props: Props)
    (action: EventSourcedAction<unit, SnapshotExtra<'Snapshot>>)
    (snapshotHandler: 'Snapshot -> EventSourcedAction<unit, SnapshotExtra<'Snapshot>>)
    =
    let applyMod arg modifier current =
        match arg with
        | Some a -> modifier a current
        | None -> current        
    let actProps =
        Akka.Actor.Props.Create(fun () -> EventSourcedSnapshotActor(action, snapshotHandler, ?persistenceId = props.persistenceId))
        |> applyMod props.common.dispatcher (fun d a -> a.WithDispatcher d)
        |> applyMod props.common.deploy (fun d a -> a.WithDeploy d)
        |> applyMod props.common.mailbox (fun d a -> a.WithMailbox d)
        |> applyMod props.common.router (fun d a -> a.WithRouter d)
        |> applyMod props.common.supervisionStrategy (fun d a -> a.WithSupervisorStrategy d)
    let act = parent.ActorOf(actProps, ?name = props.common.name)
    Akkling.ActorRefs.typed act

[<AutoOpen>]
module Actions =

    let private persistObj (action: Simple.SimpleAction<obj>): EventSourcedAction<obj, 's> =
        Extra (RunAction action, Done)        
    
    /// The result of applying persist to a SimpleAction.
    type PersistResult<'Result> =
        /// The action was executed and produced the given result, or the result was read from the event log if recovering.
        | ActionResult of 'Result
        /// The action was executed, but the result was rejected for the given reason by the persistence system.
        | ActionResultRejected of result:'Result * reason:exn * sequenceNr:int64
        /// Recovery was running, but has now finished. The action was not executed and should be repeated if its
        /// result is needed.  
        | RecoveryDone
        /// Recovery was running, but has failed due to the given reason while processing the given message. The action
        /// was not executed.
        | RecoveryFailed of reason:exn * message:obj
    
    /// Runs the given SimpleAction and then persists the result. If the actor crashes, this action will skip running the
    /// action and return the persisted result instead. This version of persist will return persistence lifecycle events
    /// in addition to the results of the action passed to persist. If something other than ActionResult is returned then
    /// the action was not executed. The action will also not be executed if the actor is recovering, instead the
    /// ActionExecuted values will be read from the event log until it runs out, at which point persist will return a
    /// RecoveryDone value (the action will not have been executed, if it's result is needed then call persist again
    /// to execute the action). If the persistence system rejects a result, then ActionResultRejected will be returned. 
    let persist (action: Simple.SimpleAction<'Result>): EventSourcedAction<PersistResult<'Result>, 'Snapshot> =
        let rec getEvt () = actor {
            let! evt = persistObj (Simple.actor {
                let! res = action
                return (res :> obj)
            })
            match evt with
            | :? 'Result ->
                return ActionResult (evt :?> 'Result)
            | :? PersistenceMsg as pMsg ->
                match pMsg with
                | Completed ->
                    return RecoveryDone
                | Failed(reason, msg) ->
                    //already logged the error in OnRecoveryFailed
                    return RecoveryFailed (reason, msg)
                | Rejected(result, reason, sequenceNr) ->
                    return ActionResultRejected (result :?> 'Result, reason, sequenceNr)
            | _ ->
                return! getEvt ()
        }
        getEvt ()

    /// Runs the given SimpleAction and then persists the result. If the actor crashes, this action will skip running the
    /// action and return the persisted result instead. This version of persist will not return persistence lifecycle
    /// events and if recovery fails or a result is rejected by the persistence system , then it will stop the actor.
    /// If a result is produced then it was either read from the event log if recovering or the product of executing the
    /// action if not recovering. Unlike persist, persistSimple will always execute its action.
    let persistSimple (action: Simple.SimpleAction<'Result>): EventSourcedAction<'Result, 'Snapshot> =
        let rec getEvt () = actor {
            let! evt = persistObj (Simple.actor {
                let! res = action
                return (res :> obj)
            })
            match evt with
            | :? 'Result ->
                return (evt :?> 'Result)
            | :? PersistenceMsg as recovery ->
                match recovery with
                | Completed ->
                    return! getEvt ()
                | Failed _
                | Rejected _ ->
                    //already logged the error in OnRecoveryFailed
                    return! stop ()
            | _ ->
                return! getEvt ()
        }
        getEvt ()

    /// Gets the recovery state of the actor.
    let isRecovering () : EventSourcedAction<bool, 'Snapshot> = actor {
        let! res = Extra(GetRecovering, Done)
        return (res :?> bool)
    }
    
    type SnapshotAction<'Result, 'SnapShot> = EventSourcedAction<'Result, SnapshotExtra<'SnapShot>>
    
    /// Gets the sequence number of the last persistence operation.
    let getLastSequenceNumber () : SnapshotAction<int64, 'SnapShot> = actor {
        let! res = Extra(Snapshot GetLastSequenceNumber, Done)
        return (res :?> int64)
    }
    
    /// Deletes events up to the given sequence number. If you are interested in success/failure then watch for
    /// Akka.Persistence.DeleteMessagesSuccess and/or Akka.Persistence.DeleteMessagesFailure messages.
    let deleteEvents (sequenceNr: int64) : SnapshotAction<unit, 'SnapShot> = actor {
        let! _ = Extra(Snapshot (DeleteEvents sequenceNr), Done)
        return ()
    }

    /// Saves a snapshot of the actor's state. If you are interested in success/failure then watch for
    /// Akka.Persistence.SaveSnapshotSuccess and/or Akka.Persistence.SaveSnapshotFailure messages.  
    let saveSnapshot (snapshot: 'SnapShot) : SnapshotAction<unit, 'SnapShot> = actor {
        let! _ = Extra(Snapshot (SaveSnapshot snapshot), Done)
        return ()
    }

    /// Deletes snapshots up to the given sequence number. If you are interested in success/failure then watch for
    /// Akka.Persistence.DeleteSnapshotSuccess and/or Akka.Persistence.DeleteSnapshotFailure messages.
    let deleteSnapshot (sequenceNr: int64) : SnapshotAction<unit, 'SnapShot> = actor {
        let! _ = Extra(Snapshot (DeleteSnapshot sequenceNr), Done)
        return ()
    }
    
    /// Deletes snapshots that satisfy the given criteria. If you are interested in success/failure then watch for
    /// Akka.Persistence.DeleteSnapshotsSuccess and/or Akka.Persistence.DeleteSnapshotsFailure messages.
    let deleteSnapshots (criteria: Akka.Persistence.SnapshotSelectionCriteria) : SnapshotAction<unit, 'SnapShot> = actor {
        let! _ = Extra(Snapshot (DeleteSnapshots criteria), Done)
        return ()
    }
    
///Maps the given function over the given array within an actor expression.
let mapArray (func: 'a -> EventSourcedAction<'b, 's>) (values: 'a []) : EventSourcedAction<'b [], 's> =
    let rec loop (results: 'b []) i = ActorBuilder () {
        if i < values.Length then
            let! res = func values[i]
            results[i] <- res
            return! loop results (i + 1)
        else
            return results
    }
    loop (Array.zeroCreate values.Length) 0

///Maps the given function over the given list within an actor expression.
let mapList (func: 'a -> EventSourcedAction<'b, 's>) (values: List<'a>) : EventSourcedAction<List<'b>, 's> =
    let rec loop (left: List<'a>) (results: List<'b>) = ActorBuilder () {
        match left with
        | head::tail ->
            let! res = func head
            return! loop tail (res :: results)
        | [] ->
            return List.rev results
    }
    loop values []

///Folds the given function over the given sequence of actions within an actor expression.
let foldActions (func: 'a -> 'res -> EventSourcedAction<'res, 's>) (init: 'res) (values: seq<EventSourcedAction<'a, 's>>) : EventSourcedAction<'res, 's> =
    let rec loop left cur = actor {
        if Seq.isEmpty left then
            return cur
        else
            let! value = Seq.head left
            let! res = func value cur
            return! loop (Seq.tail left) res
    }
    loop values init

///Folds the given function over the given sequence within an actor expression.
let foldValues (func: 'a -> 'res -> EventSourcedAction<'res, 's>) (init: 'res) (values: seq<'a>) : EventSourcedAction<'res, 's> =
    let rec loop left cur = ActorBuilder () {
        if Seq.isEmpty left then
            return cur
        else
            let! res = func (Seq.head left) cur
            return! loop (Seq.tail left) res
    }
    loop values init
