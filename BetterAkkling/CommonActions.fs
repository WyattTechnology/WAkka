module BetterAkkling.CommonActions

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

type CommonActions<'Extra> internal () =

    static member getActor () = Simple (fun ctx -> Done (Akkling.ActorRefs.typed ctx.Self))
    static member unsafeGetActorCtx () = Simple (fun ctx -> Done (ctx :> IActorContext))

    static member getLogger () = Simple (fun ctx -> Done ctx.Logger)

    static member stop () = Stop Done

    static member getSender () = Simple (fun ctx -> Done (Akkling.ActorRefs.typed ctx.Sender))

    static member createChild (make: Akka.Actor.IActorRefFactory -> Akkling.ActorRefs.IActorRef<'Msg>) =
        Simple (fun ctx -> Done (make ctx.ActorFactory))

    static member send (recv: Akkling.ActorRefs.IActorRef<'Msg>) msg = Simple (fun ctx -> Done (recv.Tell (msg, ctx.Self)))

    static member stash () : ActionBase<unit, 'Extra> = Simple (fun ctx -> Done (ctx.Stash.Stash ()))
    static member unstashOne () : ActionBase<unit, 'Extra> = Simple (fun ctx -> Done (ctx.Stash.Unstash ()))
    static member unstashAll () : ActionBase<unit, 'Extra> = Simple (fun ctx -> Done (ctx.Stash.UnstashAll ()))

    static member watch (act: Akkling.ActorRefs.IActorRef<'msg>) = Simple (fun ctx -> Done (ctx.Watch (Akkling.ActorRefs.untyped act)))
    static member watch (act: Akka.Actor.IActorRef) = Simple (fun ctx -> Done (ctx.Watch act))
    static member unwatch (act: Akkling.ActorRefs.IActorRef<'msg>) = Simple (fun ctx -> Done (ctx.Unwatch (Akkling.ActorRefs.untyped act)))
    static member unwatch (act: Akka.Actor.IActorRef) = Simple (fun ctx -> Done (ctx.Unwatch act))

    static member schedule delay receiver msg = doSchedule delay receiver msg
    static member scheduleRepeatedly delay interval receiver msg = doScheduleRepeatedly delay interval receiver msg

    static member select (path: string) = Simple (fun ctx -> Done (ctx.ActorSelection path))
    static member select (path: Akka.Actor.ActorPath) = Simple (fun ctx -> Done (ctx.ActorSelection path))

    static member setRestartHandler handler = Simple (fun ctx -> Done (ctx.SetRestartHandler handler))
    static member clearRestartHandler () = Simple (fun ctx -> Done (ctx.ClearRestartHandler ()))
