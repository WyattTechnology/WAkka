module BetterAkkling.Core

open Akkling

type SimpleActor = class end
//type EventSourcedActor<'Snapshotting> = class end
//type NoSnapshotting = class end
//type WithSnapshotting<'SnapshotType> = class end

type Action<'Type, 'Result> =
    internal
    | Done of 'Result
    | Simple of (Actors.Actor<obj> -> Action<'Type, 'Result>)
    | Msg of (obj -> Action<'Type, 'Result>)
    | RestartHandlerUpdate of Option<RestartHandler> * (unit -> Action<'Type, 'Result>)
    | Persist of Action<SimpleActor, obj> * (obj -> Action<'Type, 'Result>)
    | Stop of (unit -> Action<'Type, 'Result>)

let rec private bind (f: 'a -> Action<'t, 'b>) (op: Action<'t, 'a>) : Action<'t, 'b> =
    match op with
    | Done res -> f res
    | Simple cont -> Simple (cont >> bind f)
    | Msg cont -> Msg (cont >> bind f)
    | RestartHandlerUpdate (update, cont) -> RestartHandlerUpdate (update, cont >> bind f)
    | Persist (sub, cont) -> Persist (sub, cont >> bind f)
    | Stop cont -> Stop (cont >> bind f)

type ActorBuilder () =
    member this.Bind (x, f) = bind f x
    member this.Return x = Done x
    member this.ReturnFrom x = x
    member this.Zero () = Done ()
    member this.Combine(m1, m2) = this.Bind (m1, fun _ -> m2)
    member this.Delay f = f ()

let actor = ActorBuilder ()

