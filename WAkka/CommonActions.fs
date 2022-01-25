module WAkka.CommonActions

open Context

type ActionBase<'Result, 'ExtraArg> =
    internal
    | Done of 'Result
    | Simple of (IActionContext -> ActionBase<'Result, 'ExtraArg>)
    | Stop of (unit -> ActionBase<'Result, 'ExtraArg>)
    | Extra of 'ExtraArg * (obj -> ActionBase<'Result, 'ExtraArg>)

let rec internal bindBase (f: 'a -> ActionBase<'b, 't>) (op: ActionBase<'a, 't>) : ActionBase<'b, 't> =
    match op with
    | Done res -> f res
    | Simple cont -> Simple (cont >> bindBase f)
    | Stop cont -> Stop (cont >> bindBase f)
    | Extra (extra, cont) -> Extra (extra, cont >> bindBase f)

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

let getActor () = Simple (fun ctx -> Done (Akkling.ActorRefs.typed ctx.Self))
let unsafeGetActorCtx () = Simple (fun ctx -> Done (ctx :> IActorContext))

let getLogger () = Simple (fun ctx -> Done ctx.Logger)

let stop () = Stop Done

let createChild (make: Akka.Actor.IActorRefFactory -> Akkling.ActorRefs.IActorRef<'Msg>) =
    Simple (fun ctx -> Done (make ctx.ActorFactory))

let send (recv: Akkling.ActorRefs.IActorRef<'Msg>) msg = Simple (fun ctx -> Done (recv.Tell (msg, ctx.Self)))

let watch (act: Akkling.ActorRefs.IActorRef<'msg>) = Simple (fun ctx -> Done (ctx.Watch (Akkling.ActorRefs.untyped act)))
let unwatch (act: Akkling.ActorRefs.IActorRef<'msg>) = Simple (fun ctx -> Done (ctx.Unwatch (Akkling.ActorRefs.untyped act)))

let schedule delay receiver msg = doSchedule delay receiver msg
let scheduleRepeatedly delay interval receiver msg = doScheduleRepeatedly delay interval receiver msg

let select (path: string) = Simple (fun ctx -> Done (ctx.ActorSelection path))
let selectPath (path: Akka.Actor.ActorPath) = Simple (fun ctx -> Done (ctx.ActorSelection path))

let setRestartHandler handler = Simple (fun ctx -> Done (ctx.SetRestartHandler handler))
let clearRestartHandler () = Simple (fun ctx -> Done (ctx.ClearRestartHandler ()))

[<AutoOpen>]
module Operators =
    let inline (<!) (recv: Akkling.ActorRefs.IActorRef<'Msg>) msg : ActionBase<unit, 'Extra> = send recv msg
