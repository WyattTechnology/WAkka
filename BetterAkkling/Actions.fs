module BetterAkkling.Actions

open Akkling

open Core
open Props

let getActor () = Simple (fun ctx -> Done (retype ctx.Self))
let unsafeGetActorCtx () = Simple (fun ctx -> Done ctx)

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

let getLogger () = Simple (fun ctx -> Done (Logger ctx))

let stop () = Stop Done

let receiveAny () : Action<SimpleActor, obj> = Msg Done
let rec receiveOnly<'Msg> () : Action<SimpleActor, 'Msg> = actor {
    match! receiveAny () with
    | :? 'Msg as m -> return m
    | _ -> return! receiveOnly<'Msg> ()
}
let getSender () = Simple (fun ctx -> Done (ctx.Sender ()))

let createChild (make: Akka.Actor.IActorRefFactory -> ActorRefs.IActorRef<'Msg>) =
    Simple (fun ctx -> Done (make ctx))

let stash () = Simple (fun ctx -> Done (ctx.Stash ()))
let unstashOne () = Simple (fun ctx -> Done (ctx.Unstash ()))
let unstashAll () = Simple (fun ctx -> Done (ctx.UnstashAll ()))

let watch act = Simple (fun ctx -> Done (ctx.Watch (ActorRefs.untyped act) |> ignore))
let unwatch act = Simple (fun ctx -> Done (ctx.Unwatch (ActorRefs.untyped act) |> ignore))

let schedule delay receiver msg = Simple (fun ctx -> Done (ctx.Schedule delay receiver msg))
let scheduleRepeatedly delay interval receiver msg =
    Simple (fun ctx -> Done (ctx.ScheduleRepeatedly delay interval receiver msg))

let select (path: string) = Simple (fun ctx -> Done (ctx.ActorSelection path))

let setRestartHandler handler = RestartHandlerUpdate (Some handler, Done)
let clearRestartHandler () = RestartHandlerUpdate (None, Done)

type PersistResult<'Result> =
    | Persist of 'Result
    | NoPersist of 'Result

let persist (_action: Action<SimpleActor, PersistResult<'Result>>) : Action<PersistentActor, 'Result> = actor {
    return Unchecked.defaultof<_>
}
