namespace BetterAkkling

open System
open Akkling

open BetterAkkling.Props
open Core

type Logger internal (ctx:Actor<obj>) =
    member _.Log level msg = Logging.log level ctx msg
    member _.Logf level = Logging.logf level ctx
    member _.LogException err = Logging.logException ctx err
    member _.Debug msg = Logging.logDebug ctx msg
    member _.Debugf = Logging.logDebugf ctx
    member _.Info msg = Logging.logInfo ctx msg
    member _.Infof = Logging.logInfof ctx
    member _.Warning msg = Logging.logWarning ctx msg
    member _.Warningf = Logging.logWarningf ctx
    member _.Error msg = Logging.logError ctx msg
    member _.Errorf = Logging.logErrorf ctx

type private Timeout = {started: DateTime}

type Actions private () =
    static let doReceive () : Action<SimpleActor, obj> = Msg Done
    static let doSchedule delay receiver msg = Simple (fun ctx -> Done (ctx.Schedule delay receiver msg))

    static member  getActor () = Simple (fun ctx -> Done (retype ctx.Self))
    static member unsafeGetActorCtx () = Simple (fun ctx -> Done ctx)

    static member getLogger () = Simple (fun ctx -> Done (Logger ctx))

    static member stop () = Stop Done

    static member receive (choose: obj -> Option<'Msg>) =
        let rec recv () = actor {
            match! doReceive () with
            | :? Timeout ->
                return! recv ()
            | msg ->
                match choose msg with
                | Some res ->
                    return res
                | None ->
                    return! recv ()
        }
        recv ()

    static member receive (choose: obj -> Option<'Msg>, timeout: TimeSpan) =
        let rec recv started (cancel: Akka.Actor.ICancelable) = actor {
            match! doReceive () with
            | :? Timeout as timeout ->
                if timeout.started = started then
                    return None
                else
                    return! recv started cancel
            | msg ->
                match choose msg with
                | Some res ->
                    cancel.Cancel()
                    return (Some res)
                | None ->
                    return! recv started cancel
        }
        actor {
            let now = DateTime.Now
            let! self = Actions.getActor ()
            let! cancel = doSchedule timeout (retype self) {started = now}
            return! recv now cancel
        }

    static member receiveAny () = Actions.receive Some
    static member receiveAny (timeout: TimeSpan) = Actions.receive(Some, timeout)

    static member receiveOnly<'Msg> () : Action<SimpleActor, 'Msg> =
        Actions.receive (fun msg ->
            match msg with
            | :? 'Msg as m -> Some m
            | _ -> None
        )
    static member receiveOnly<'Msg> (timeout: TimeSpan) : Action<SimpleActor, Option<'Msg>> =
        Actions.receive (
            (fun msg ->
                match msg with
                | :? 'Msg as m -> Some m
                | _ -> None
            ),
            timeout
        )

    static member getSender () = Simple (fun ctx -> Done (ctx.Sender ()))

    static member createChild (make: Akka.Actor.IActorRefFactory -> ActorRefs.IActorRef<'Msg>) =
        Simple (fun ctx -> Done (make ctx))

    static member stash () = Simple (fun ctx -> Done (ctx.Stash ()))
    static member unstashOne () = Simple (fun ctx -> Done (ctx.Unstash ()))
    static member unstashAll () = Simple (fun ctx -> Done (ctx.UnstashAll ()))

    static member watch act = Simple (fun ctx -> Done (ctx.Watch (ActorRefs.untyped act) |> ignore))
    static member unwatch act = Simple (fun ctx -> Done (ctx.Unwatch (ActorRefs.untyped act) |> ignore))

    static member schedule delay receiver msg = doSchedule delay receiver msg
    static member scheduleRepeatedly delay interval receiver msg =
        Simple (fun ctx -> Done (ctx.ScheduleRepeatedly delay interval receiver msg))

    static member select (path: string) = Simple (fun ctx -> Done (ctx.ActorSelection path))

    static member setRestartHandler handler = RestartHandlerUpdate (Some handler, Done)
    static member clearRestartHandler () = RestartHandlerUpdate (None, Done)

    static member private persistObj (action: Action<SimpleActor, obj>): Action<EventSourcedActor<'Snapshotting>, obj> =
        Persist (action, Done)

    static member persist (action: Action<SimpleActor, 'Result>): Action<EventSourcedActor<'Snapshotting>, 'Result> = actor {
        let! evt = Actions.persistObj (actor {
            let! res = action
            return (res :> obj)
        })
        return (evt :?> 'Result)
    }

    static member snapshot (snapshot: 'Snapshot): Action<EventSourcedActor<WithSnapshotting<'Snapshot>>, 'Result> = actor {
        return Unchecked.defaultof<_>
    }

