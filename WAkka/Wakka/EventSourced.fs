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

open Akka.Event
open Common
open WAkka.LifeCycleHandlers

type NoSnapshotExtra = NoSnapshotExtra

type SnapshotExtra<'Snapshot> =
    | GetLastSequenceNumber
    | DeleteEvents of sequenceNr:int64
    | SaveSnapshot of snapshot:'Snapshot
    | DeleteSnapshot of sequenceNr:int64
    | DeleteSnapshots of criteria:Akka.Persistence.SnapshotSelectionCriteria
    
/// The result of saving a persistence snapshot.
type SnapshotResult =
    /// Snapshot save was successful.
    | SnapshotSuccess of meta:Akka.Persistence.SnapshotMetadata
    /// Snapshot save failed.
    | SnapshotFailure of meta:Akka.Persistence.SnapshotMetadata * cause:exn

/// Interface that allows deletion of persistence messages and snapshots.
type ISnapshotControl =
    /// Delete all persisted messages up to the given sequence number (inclusive).
    abstract member DeleteMessages: seqNum:int64 -> unit
    /// Delete the snapshot with the given sequence number.
    abstract member DeleteSnapshot: seqNum:int64 -> unit
    /// Delete all snapshots that satisfy the given criteria.
    abstract member DeleteSnapshots: criteria:Akka.Persistence.SnapshotSelectionCriteria -> unit
    /// The actor's logger.
    abstract member Logger: ILoggingAdapter
    
type internal ISkippableEvent =
    abstract member Skip: bool
    abstract member Value: obj    

type EventSourcedExtra<'Snapshot> = 
    internal
    | RunAction of action:Simple.SimpleAction<obj>
    | RunSkippableAction of action:Simple.SimpleAction<ISkippableEvent>
    | GetRecovering
    | Snapshot of snapshot:'Snapshot
    | AddSnapshotResultHandler of handler:(ISnapshotControl * SnapshotResult -> bool)
    | RemoveSnapshotResultHandler of index:int
    
/// An action that can only be used directly in an event-sourced actor (e.g., started using eventSourced).
type EventSourcedActionBase<'Result, 'Snapshot> = ActionBase<'Result, EventSourcedExtra<'Snapshot>>
type NoSnapshotsAction<'Result> = EventSourcedActionBase<'Result, NoSnapshotExtra>
type SnapshotAction<'Result, 'Snapshot> = EventSourcedActionBase<'Result, SnapshotExtra<'Snapshot>>

/// Backwards compatible type alias for EventSourcedActionBase<'Result, NoSnapshotExtra>. Use NoSnapshotAction in
/// new code instead of this.
type EventSourcedAction<'Result> = EventSourcedActionBase<'Result, NoSnapshotExtra>

let rec private bind (f: 'a -> EventSourcedActionBase<'b, 's>) (op: EventSourcedActionBase<'a, 's>) : EventSourcedActionBase<'b, 's> =
    bindBase f op

type ActorBuilder () =
    member this.Bind (x, f) = bind f x
    member this.Return x = Done x
    member this.ReturnFrom x = x
    member this.Zero () = Done ()
    member this.Combine(m1, m2) = this.Bind (m1, m2)
    member this.Delay (f: unit -> EventSourcedActionBase<'t, 's>): (unit -> EventSourcedActionBase<'t, 's>) = f
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

    type PersistenceMsg =
        | Completed
        | Rejected of result:obj * reason:exn * sequenceNr:int64

open EventSourcedActorPrivate
    
type SkippableEvent<'T> =
    | Persist of value:'T
    | SkipPersist of value:'T
with 
    interface ISkippableEvent with
        member this.Skip =
            match this with
            | Persist _ -> false
            | SkipPersist _ -> true
        member this.Value =
            match this with
            | Persist value -> value :> obj
            | SkipPersist value -> value :> obj

module Internal = 
    type IActionHandler<'Snapshot> =
        abstract member SnapshotHandler: Option<obj> -> EventSourcedActionBase<unit, 'Snapshot>
        abstract member HandleSnapshotExtra: 'Snapshot -> Akka.Persistence.UntypedPersistentActor -> (obj -> EventSourcedActionBase<'a, 'Snapshot>) -> EventSourcedActionBase<'a, 'Snapshot>
    
    type internal SnapshotResultsHandlers() =
        inherit LifeCycleHandlersWithResult<ISnapshotControl * SnapshotResult, bool>(true, (&&))
        
    type EventSourcedActorBase<'Snapshot>(
        persistenceId: Option<string>,
        handler: IActionHandler<'Snapshot>
    ) as this =

        inherit Akka.Persistence.UntypedPersistentActor ()

        let ctx = Akka.Persistence.Eventsourced.Context
        let logger = ctx.GetLogger()
        
        let mutable restartHandlers = LifeCycleHandlers.LifeCycleHandlers<IActorContext * obj * exn>()
        let mutable stopHandlers = LifeCycleHandlers.LifeCycleHandlers<IActorContext>()
        let mutable snapshotResultsHandlers = SnapshotResultsHandlers()
        let snapshotControl = {
            new ISnapshotControl with
                member _.DeleteMessages seqNum = this.DeleteMessages seqNum
                member _.DeleteSnapshot seqNum = this.DeleteSnapshot seqNum
                member _.DeleteSnapshots criteria = this.DeleteSnapshots criteria
                member _.Logger = logger
        }

        let mutable msgHandler = fun (_recovering: bool) _msg -> ()
        let updateMsgHandler newMsgHandler =
            msgHandler <- (fun (recovering: bool) (msg: obj) ->
                if snapshotResultsHandlers.HasHandlers then 
                    match msg with
                    | :? Akka.Persistence.SaveSnapshotSuccess as success ->
                        if not (snapshotResultsHandlers.ExecuteHandlers(snapshotControl, SnapshotSuccess success.Metadata)) then
                            ctx.Stop ctx.Self
                        else 
                            newMsgHandler recovering msg
                    | :? Akka.Persistence.SaveSnapshotFailure as failure ->
                        if not (snapshotResultsHandlers.ExecuteHandlers(snapshotControl, SnapshotFailure (failure.Metadata, failure.Cause))) then 
                            ctx.Stop ctx.Self
                        else 
                            newMsgHandler recovering msg
                    | _ ->
                        newMsgHandler recovering msg
                else
                    newMsgHandler recovering msg
            )
        do updateMsgHandler msgHandler
        
        let mutable rejectionHandler = fun (_result:obj, _reasons: exn, _sequenceNr: int64)  -> ()
        let simpleActionCtx = {
            new Simple.ISimpleActionsContext with
                member _.Ctx = ctx
                member _.ActionCtx = this :> IActionContext
                member _.SetMsgHandler newHandler = updateMsgHandler (fun _ m -> newHandler.HandleMessage m)
        }
        
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
                        updateMsgHandler (fun stillRecovering msg -> handleActions stillRecovering (next msg))
                    else
                        let onDone (_, res: obj) =
                            rejectionHandler <- (fun (result, reason, sn) -> handleActions false (next (Rejected (result, reason, sn) :> obj)))
                            this.Persist (res, fun evt -> handleActions false (next evt))
                        Simple.handleSimpleActions(simpleActionCtx, onDone, subAction)
                | RunSkippableAction subAction ->
                    if recovering then
                        updateMsgHandler (fun stillRecovering msg -> handleActions stillRecovering (next msg))
                    else
                        let onDone (_, res: obj) =
                            rejectionHandler <- (fun (result, reason, sn) -> handleActions false (next (Rejected (result, reason, sn) :> obj)))
                            match res with
                            | :? ISkippableEvent as p ->
                                if p.Skip then
                                    handleActions false (next p.Value)
                                else
                                    this.Persist (p.Value, fun evt ->
                                        handleActions false (next evt)
                                    )
                            | _ -> 
                                failwith "Result was not ISkippableEvent"
                        let subAction' = Simple.actor {
                            let! r = subAction
                            return r :> obj
                        }
                        Simple.handleSimpleActions(simpleActionCtx, onDone, subAction')
                | GetRecovering ->
                    handleActions recovering (next recovering)
                | Snapshot snapshot ->
                    handleActions recovering (handler.HandleSnapshotExtra snapshot this next)
                | AddSnapshotResultHandler handler ->
                    let index = snapshotResultsHandlers.AddHandler handler
                    handleActions recovering (next index)
                | RemoveSnapshotResultHandler index ->
                    snapshotResultsHandlers.RemoveHandler index

        do updateMsgHandler (fun recovering msg ->
            logger.Error("Got msg before first receive(recovering = {0}): {1}", recovering, msg)
        )
        
        let mutable actionsInitialized = false

        override this.PersistenceId = defaultArg persistenceId (ctx.Self.Path.ToString())

        override _.OnCommand (msg: obj) =
            msgHandler false msg

        override _.OnPersistRejected(cause, event, sequenceNr) =
            logger.Error(cause, "rejected event [sequence number {0}] {1}", sequenceNr, event)
            rejectionHandler (event, cause, sequenceNr)

        override this.OnPersistFailure(cause, event, sequenceNr) =
            logger.Error(cause, "failure persisting event, actor will stop [sequence number {0}] {1}", sequenceNr, event)
            base.OnPersistFailure(cause, event, sequenceNr)

        override _.OnRecover (msg: obj) =
            match msg with
            | :? Akka.Persistence.SnapshotOffer as snapshot ->
                handleActions true (handler.SnapshotHandler (Some snapshot.Snapshot))
                actionsInitialized <- true
            | :? Akka.Persistence.RecoveryCompleted ->
                if not actionsInitialized then
                    handleActions true (handler.SnapshotHandler None)
                    actionsInitialized <- true
                msgHandler false (Completed :> obj)
            | _ ->
                if not actionsInitialized then
                    handleActions true (handler.SnapshotHandler None)
                    actionsInitialized <- true
                msgHandler true msg

        override _.OnRecoveryFailure(reason, message) =
            logger.Error(reason, "recovery failed on message {0}", message)

        override this.PreRestart(reason, message) =
            logger.Error(reason, "Actor crashed on {0}", message)
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
        member _.SnapshotHandler snapshot =
            match snapshot with
            | Some snap -> 
                actor {
                    let! logger = getAkkaLogger()
                    logger.Error("Got snapshot offer without handler (stopping actor): {0}", snap) 
                    return! stop ()            
                }
            | None ->
                startAction ()
        member _.HandleSnapshotExtra extra _act _next = actor {
            let! logger = getAkkaLogger()
            logger.Error("Got snapshot extra without handler (stopping actor): {0}", extra)
            return! stop ()            
        }
        
/// <summary>
/// Class that can be used to spawn eventSourced actors that do not support snapshots. Usually, Spawn.NoSnapshots should
/// be used to spawn this type of actor, but if you need a class type to use with Akka.Actor.Props.Create for special 
/// cases (like remote deployment), then a new class can be derived from this class instead.  
/// </summary>
/// <param name="startAction">The action for the actor to run.</param>
/// <param name="persistenceId">The persistence id to use. If not given, the actor path will be used.</param>
type EventSourcedActor(startAction: unit -> EventSourcedActionBase<unit, NoSnapshotExtra>, ?persistenceId) =
    inherit Internal.EventSourcedActorBase<NoSnapshotExtra>(persistenceId, NoSnapshotHandler startAction)

    new (startAction: EventSourcedActionBase<unit, NoSnapshotExtra>, ?persistenceId) =
        EventSourcedActor((fun () -> startAction), ?persistenceId = persistenceId)
        
type private SnapshotHandler<'Snapshot> (snapshotHandler) =
    interface Internal.IActionHandler<SnapshotExtra<'Snapshot>> with
        member _.SnapshotHandler snapshot = snapshotHandler (snapshot |> Option.map(fun s -> s :?> 'Snapshot))
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
/// Class that can be used to spawn event sourced actors. Usually, Spawn.WithSnapshots should be used to spawn this type
/// of actor, but if you need a class type to use with Akka.Actor.Props.Create for special cases (like remote
/// deployment), then a new class can be derived from this class instead.  
/// </summary>
/// <param name="action">
/// Function that generates the initial action for the actor. The function will be passed a persistence snapshot if
/// one is available, else None.
/// </param>
/// <param name="persistenceId">The persistence id to use. If not given, the actor path will be used.</param>
type EventSourcedSnapshotActor<'Snapshot>(
    action: Option<'Snapshot> -> EventSourcedActionBase<unit, SnapshotExtra<'Snapshot>>,
    ?persistenceId
) =
    inherit Internal.EventSourcedActorBase<SnapshotExtra<'Snapshot>>(persistenceId, SnapshotHandler action)

    /// <summary>
    /// Creates a new EventSourcedSnapshotActor, this constructor exists for backwards compatibility.
    /// </summary>
    /// <param name="startAction">The action to start with if there is no snapshot available.</param>
    /// <param name="snapshotHandler">Called to generate the initial action if there is a snapshot available.</param>
    /// <param name="persistenceId">The persistence id to use. If not given, the actor path will be used.</param>
    new (
        startAction: EventSourcedActionBase<unit, SnapshotExtra<'Snapshot>>,
        snapshotHandler: 'Snapshot -> EventSourcedActionBase<unit, SnapshotExtra<'Snapshot>>,
        ?persistenceId
    ) =
        let handler snap =
            match snap with
            | Some s -> snapshotHandler s
            | None -> startAction
        EventSourcedSnapshotActor(handler, ?persistenceId = persistenceId)
    
/// <summary>
/// The properties for an event-sourced actor.
/// </summary>
type EventSourcedProps = {
    /// The persistence id for the actor. If None, then the actor's path will be used.
    persistenceId: Option<string>
    /// The common properties for the actor.
    common: Props
}
with
    /// <summary>
    /// Creates a Props object with empty persistence id and the given name.
    /// </summary>
    /// <param name="name">The name for the actor.</param>
    static member Named name = {
        persistenceId = None
        common = Props.Named name
    }
    
    /// <summary>
    /// Creates a Props object with empty persistence id and no name.
    /// </summary>
    static member Anonymous = {
        persistenceId = None
        common = Props.Anonymous
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
            | Some n -> Props.Named n
            | None -> Props.Anonymous 
    }

type Spawn =
    
    /// <summary>
    /// Spawns an event-sourced actor. This variant does not support snapshots.
    /// </summary>
    /// <param name="parent">The actor's parent.</param>
    /// <param name="props">The actor's properties.</param>
    /// <param name="action">Function that generates the initial action for the actor</param>
    static member NoSnapshots (parent: Akka.Actor.IActorRefFactory, props: EventSourcedProps, action: unit -> NoSnapshotsAction<unit>) =
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
    /// Spawns an event-sourced actor. This variant supports snapshots.
    /// </summary>
    /// <param name="parent">The actor's parent.</param>
    /// <param name="props">The actor's properties.</param>
    /// <param name="action">
    /// Function that generates the initial action for the actor. The function will be passed a persistence snapshot if
    /// one is available, else None.
    /// </param>
    static member WithSnapshots (
        parent: Akka.Actor.IActorRefFactory,
        props: EventSourcedProps,
        action: Option<'Snapshot> -> SnapshotAction<unit, 'Snapshot>) =
        
        let applyMod arg modifier current =
            match arg with
            | Some a -> modifier a current
            | None -> current        
        let actProps =
            Akka.Actor.Props.Create(fun () -> EventSourcedSnapshotActor(
                action,
                ?persistenceId = props.persistenceId
            ))
            |> applyMod props.common.dispatcher (fun d a -> a.WithDispatcher d)
            |> applyMod props.common.deploy (fun d a -> a.WithDeploy d)
            |> applyMod props.common.mailbox (fun d a -> a.WithMailbox d)
            |> applyMod props.common.router (fun d a -> a.WithRouter d)
            |> applyMod props.common.supervisionStrategy (fun d a -> a.WithSupervisorStrategy d)
        let act = parent.ActorOf(actProps, ?name = props.common.name)
        Akkling.ActorRefs.typed act

/// <summary>
/// Spawns an event-sourced actor. This variant does not support snapshots. This exists for backwards compatibility, use
/// Spawn.NoSnapshots instead.
/// </summary>
/// <param name="parent">The parent for the new actor.</param>
/// <param name="props">The actor's properties.</param>
/// <param name="action">The action to run.</param>
let spawnNoSnapshots (parent: Akka.Actor.IActorRefFactory) (props: EventSourcedProps) (action: NoSnapshotsAction<unit>) =
    Spawn.NoSnapshots (parent, props, (fun () -> action))

/// <summary>
/// Spawns an event-sourced actor. This variant supports snapshots. This exists for backwards compatibility, use
/// Spawn.WithSnapshots instead.
/// </summary>
/// <param name="parent">The parent for the new actor.</param>
/// <param name="props">The actor's properties.</param>
/// <param name="action">The action to run if no snapshot is available.</param>
/// <param name="snapshotHandler">Called to generate the action to run based on the given snapshot.</param>
let spawnSnapshots
    (parent: Akka.Actor.IActorRefFactory)
    (props: EventSourcedProps)
    (action: EventSourcedActionBase<unit, SnapshotExtra<'Snapshot>>)
    (snapshotHandler: 'Snapshot -> SnapshotAction<unit, 'Snapshot>)
    =

    let handler snap =
        match snap with
        | Some snap -> snapshotHandler snap
        | None -> action
    Spawn.WithSnapshots(parent, props, handler)
    
[<AutoOpen>]
module Actions =

    let private persistObj (action: Simple.SimpleAction<obj>): EventSourcedActionBase<obj, 's> =
        Extra (RunAction action, Done)        
    let private persistObjSkippable (action: Simple.SimpleAction<ISkippableEvent>): EventSourcedActionBase<obj, 's> =
        Extra (RunSkippableAction action, Done)        
    
    /// The result of applying "persist" to a SimpleAction.
    type PersistResult<'Result> =
        /// The action was executed and produced the given result, or the result was read from the event log if recovering.
        | ActionResult of 'Result
        /// The action was executed, but the result was rejected for the given reason by the persistence system.
        | ActionResultRejected of result:'Result * reason:exn * sequenceNr:int64
        /// Recovery was running but has now finished. The action was not executed and should be repeated if its
        /// result is needed.  
        | RecoveryDone
    
    /// <summary>
    /// <para>
    /// Runs the given SimpleAction and then persists the result. If the actor crashes, this action will skip running the
    /// action and return the persisted result instead.
    /// </para>
    /// <para>
    /// This version of persist will return persistence lifecycle events
    /// in addition to the results of the action passed to persist. If something other than ActionResult is returned, then
    /// the action was not executed. The action will also not be executed if the actor is recovering, instead the
    /// ActionExecuted values will be read from the event log until it runs out, at which point persist will return a
    /// RecoveryDone value (the action will not have been executed, if its result is needed, then call persist again
    /// to execute the action). If the persistence system rejects a result, then ActionResultRejected will be returned.
    /// If persisting an event fails, then the failure will be logged and the actor will stop.  
    /// </para>
    /// </summary>
    let persist (action: Simple.SimpleAction<'Result>): EventSourcedActionBase<PersistResult<'Result>, 'Snapshot> =
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
                | Rejected(result, reason, sequenceNr) ->
                    return ActionResultRejected (result :?> 'Result, reason, sequenceNr)
            | _ ->
                return! getEvt ()
        }
        getEvt ()

    /// <summary>
    /// <para>
    /// Runs the given SimpleAction and if it evaluates to `SkippableEvent.Persist value`, then `value` is persisted and
    /// becomes the value of the persistSkippable action. If action evaluates to `Skippable.SkipPersist value`, then
    /// `value` becomes the result of the persistSkippable action but is not persisted. If the actor crashes, this
    /// action will skip running the action and return the persisted result instead. Note that if events were skipped
    /// in the initial run, they will not be present in the stream of events during recovery. It is up to the caller
    /// to structure things so that this will not be a problem. 
    /// </para>
    /// <para>
    /// This version of persist will return persistence lifecycle events
    /// in addition to the results of the action passed to persist. If something other than ActionResult is returned, then
    /// the action was not executed. The action will also not be executed if the actor is recovering, instead the
    /// ActionExecuted values will be read from the event log until it runs out, at which point persist will return a
    /// RecoveryDone value (the action will not have been executed, if its result is needed, then call persist again
    /// to execute the action). If the persistence system rejects a result, then ActionResultRejected will be returned.
    /// If persisting an event fails, then the failure will be logged and the actor will stop.  
    /// </para>
    /// </summary>
    let persistSkippable (action: Simple.SimpleAction<SkippableEvent<'Result>>): EventSourcedActionBase<PersistResult<'Result>, 'Snapshot> =
        let rec getEvt () = actor {
            let! evt = persistObjSkippable (Simple.actor {
                let! res = action
                return (res :> ISkippableEvent)
            })
            match evt with
            | :? 'Result as res ->
                return ActionResult res //(evt :?> 'Result)
            | :? PersistenceMsg as pMsg ->
                match pMsg with
                | Completed ->
                    return RecoveryDone
                | Rejected(result, reason, sequenceNr) ->
                    return ActionResultRejected (result :?> 'Result, reason, sequenceNr)
            | _ ->
                return! getEvt ()
        }
        getEvt ()

    /// <summary>
    /// <para>
    /// Runs the given SimpleAction and then persists the result. If the actor crashes, this action will skip running the
    /// action and return the persisted result instead.
    /// </para>
    /// <para>
    /// This version of persist will not return persistence lifecycle
    /// events, and if recovery fails or a result is rejected by the persistence system, then it will stop the actor.
    /// If a result is produced, then it was either read from the event log if recovering or the product of executing the
    /// action if not recovering. Unlike persist, persistSimple will always execute its action.
    /// </para>
    /// </summary>
    let persistSimple (action: Simple.SimpleAction<'Result>): EventSourcedActionBase<'Result, 'Snapshot> =
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
                | Rejected _ ->
                    //already logged the error in OnRecoveryFailed
                    return! stop ()
            | _ ->
                return! getEvt ()
        }
        getEvt ()

    /// <summary>
    /// <para>
    /// Runs the given SimpleAction and if it evaluates to `SkippableEvent.Persist value`, then `value` is persisted and
    /// becomes the value of the persistSkippable action. If action evaluates to `Skippable.SkipPersist value`, then
    /// `value` becomes the result of the persistSkippable action but is not persisted. If the actor crashes, this
    /// action will skip running the action and return the persisted result instead. Note that if events were skipped
    /// in the initial run, they will not be present in the stream of events during recovery. It is up to the caller
    /// to structure things so that this will not be a problem. 
    /// </para>
    /// <para>
    /// This version of persist will not return persistence lifecycle
    /// events, and if recovery fails or a result is rejected by the persistence system, then it will stop the actor.
    /// If a result is produced, then it was either read from the event log if recovering or the product of executing the
    /// action if not recovering. Unlike persist, persistSimple will always execute its action.
    /// </para>
    /// </summary>
    let persistSkippableSimple (action: Simple.SimpleAction<SkippableEvent<'Result>>): EventSourcedActionBase<'Result, 'Snapshot> =
        let rec getEvt () = actor {
            let! evt = persistObjSkippable (Simple.actor {
                let! res = action
                return (res :> ISkippableEvent)
            })
            match evt with
            | :? 'Result ->
                return (evt :?> 'Result)
            | :? PersistenceMsg as recovery ->
                match recovery with
                | Completed ->
                    return! getEvt ()
                | Rejected _ ->
                    //already logged the error in OnRecoveryFailed
                    return! stop ()
            | _ ->
                return! getEvt ()
        }
        getEvt ()

    /// Gets the recovery state of the actor.
    let isRecovering () : EventSourcedActionBase<bool, 'Snapshot> = actor {
        let! res = Extra(GetRecovering, Done)
        return (res :?> bool)
    }
    
    /// Gets the sequence number of the last persistence operation.
    let getLastSequenceNumber () : SnapshotAction<int64, 'SnapShot> = actor {
        let! res = Extra(Snapshot GetLastSequenceNumber, Done)
        return (res :?> int64)
    }
    
    /// Saves a snapshot of the actor's state. If you are interested in success or failure, then watch for
    /// Akka.Persistence.SaveSnapshotSuccess and/or Akka.Persistence.SaveSnapshotFailure messages or provide
    /// a snapshot result handler when starting the actor.  
    let saveSnapshot (snapshot: 'SnapShot) : SnapshotAction<unit, 'SnapShot> = actor {
        let! _ = Extra(Snapshot (SaveSnapshot snapshot), Done)
        return ()
    }

    /// Deletes events up to the given sequence number. If you are interested in success or failure, then watch for
    /// Akka.Persistence.DeleteMessagesSuccess and/or Akka.Persistence.DeleteMessagesFailure messages.
    let deleteEvents (sequenceNr: int64) : SnapshotAction<unit, 'SnapShot> = actor {
        let! _ = Extra(Snapshot (DeleteEvents sequenceNr), Done)
        return ()
    }

    /// Deletes snapshots up to the given sequence number. If you are interested in success or failure, then watch for
    /// Akka.Persistence.DeleteSnapshotSuccess and/or Akka.Persistence.DeleteSnapshotFailure messages.
    let deleteSnapshot (sequenceNr: int64) : SnapshotAction<unit, 'SnapShot> = actor {
        let! _ = Extra(Snapshot (DeleteSnapshot sequenceNr), Done)
        return ()
    }
    
    /// Deletes snapshots that satisfy the given criteria. If you are interested in success or failure, then watch for
    /// Akka.Persistence.DeleteSnapshotsSuccess and/or Akka.Persistence.DeleteSnapshotsFailure messages.
    let deleteSnapshots (criteria: Akka.Persistence.SnapshotSelectionCriteria) : SnapshotAction<unit, 'SnapShot> = actor {
        let! _ = Extra(Snapshot (DeleteSnapshots criteria), Done)
        return ()
    }
    
    /// Adds a snapshot result handler that will be called whenever a snapshot result is available. An IPersistenceControl
    /// object will be passed to the handler along with the snapshot result. If the handler returns false, the actor
    /// will be stopped.
    let addSnapshotResultHandler handler : SnapshotAction<int, 'SnapShot> = actor {
        let! res = Extra(AddSnapshotResultHandler handler, Done)
        return (res :?> int)
    }
    
    /// <summary>
    /// A snapshot result handler for use with addSnapshotResultHandler. Create an instance and pass its Handle method
    /// to addSnapshotResultHandler.
    /// </summary>
    /// <param name="deletePriorMessages">
    /// If true, then on snapshot success, all persisted messages prior to the snapshot are deleted.
    /// </param>
    /// <param name="deletePriorSnapshots">
    /// If true, then on snapshot success, all snapshots prior to the most recent are deleted.
    /// </param>
    /// <param name="errorHandler">
    /// If given, it will be called if there is a snapshot save error. If it returns false, then the actor will be
    /// stopped. If not given, snapshot save errors will be logged only.
    /// </param>
    type SnapshotResultHandler(deletePriorMessages, deletePriorSnapshots, ?errorHandler) =
        member _.Handle (control: ISnapshotControl, result) = 
            match result with
            | SnapshotSuccess metadata ->
                if deletePriorMessages then 
                    control.DeleteMessages metadata.SequenceNr
                if deletePriorSnapshots then
                    control.DeleteSnapshots (Akka.Persistence.SnapshotSelectionCriteria (metadata.SequenceNr - 1L))
                true
            | SnapshotFailure (metadata, cause) ->
                match errorHandler with
                | Some errHandler ->
                    errHandler metadata cause
                | None ->
                    control.Logger.Error(
                        cause,
                        "Snapshot save failed({0}, {1})",
                        metadata.PersistenceId,
                        metadata.SequenceNr
                    )
                    true

    /// Removes a snapshot result handler registered using addSnapshotResultHandler. 
    let removeSnapshotResultHandler index : SnapshotAction<unit, 'Snapshot> = actor {
        let! _ = Extra(RemoveSnapshotResultHandler index, Done)
        return ()
    }
    
///Maps the given function over the given array within an actor expression.
let mapArray (func: 'a -> EventSourcedActionBase<'b, 's>) (values: 'a []) : EventSourcedActionBase<'b [], 's> =
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
let mapList (func: 'a -> EventSourcedActionBase<'b, 's>) (values: List<'a>) : EventSourcedActionBase<List<'b>, 's> =
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
let foldActions (func: 'a -> 'res -> EventSourcedActionBase<'res, 's>) (init: 'res) (values: seq<EventSourcedActionBase<'a, 's>>) : EventSourcedActionBase<'res, 's> =
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
let foldValues (func: 'a -> 'res -> EventSourcedActionBase<'res, 's>) (init: 'res) (values: seq<'a>) : EventSourcedActionBase<'res, 's> =
    let rec loop left cur = ActorBuilder () {
        if Seq.isEmpty left then
            return cur
        else
            let! res = func (Seq.head left) cur
            return! loop (Seq.tail left) res
    }
    loop values init
