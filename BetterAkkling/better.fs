module  BetterAkkling.Better


type RestartHandler = Core.RestartHandler
let actor = Core.actor

type ActorType = Props.ActorType
let NotPersistent = Props.NotPersistent
let InMemoryPersistent = Props.InMemoryPersistent
let ProcessPersistent = Props.ProcessPersistent

type Props = Props.Props

let spawn = Spawn.spawn
