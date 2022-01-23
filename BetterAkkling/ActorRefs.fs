module BetterAkkling.ActorRefs

type IActorRef<'Msg> =
    abstract member Path: Akka.Actor.ActorPath

    abstract member Tell: 'Msg -> unit
    abstract member Tell: 'Msg * Akka.Actor.IActorRef -> unit

    abstract member Untyped: Akka.Actor.IActorRef

module internal Internal =
    type ActorRef<'Msg> (akkaRef: Akka.Actor.IActorRef) =
        interface IActorRef<'Msg> with
            member _.Path = akkaRef.Path

            member _.Tell (msg: 'Msg) = akkaRef.Tell(msg :> obj, Akka.Actor.ActorCell.GetCurrentSelfOrNoSender ())
            member _.Tell (msg: 'Msg, sender) = akkaRef.Tell(msg :> obj, sender)

            member _.Untyped = akkaRef

let retype (ref: IActorRef<'Msg>) : IActorRef<'NewMsg> = Internal.ActorRef<'NewMsg>(ref.Untyped) :> IActorRef<'NewMsg>
let typed (ref: Akka.Actor.IActorRef) : IActorRef<'NewMsg> = Internal.ActorRef<'NewMsg>(ref) :> IActorRef<'NewMsg>

