module  BetterAkkling.Better


type RestartHandler = Core.RestartHandler
let actor = Core.actor

type ActorType<'Snapshot> = Props.ActorType<'Snapshot>
let notPersisted = Props.notPersisted
let checkpointed = Props.checkpointed
let eventSourced = Props.eventSourced
let eventSourcedWithSnapshots =  Props.eventSourcedWithSnapshots

type Props = Props.Props

let spawn = Spawn.spawn
