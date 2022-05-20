module WAkkaTests.NotPersistedTests

open NUnit.Framework

open Akkling

open WAkka.Common
open WAkka.Simple
open WAkka.Spawn

let tell (act: ActorRefs.IActorRef<'Msg>) (msg: 'Msg) =
    act.Tell(msg, Akka.Actor.ActorRefs.NoSender)


type CrashMsg = {
    msg: obj
    err: obj
}

type CrashIt = CrashIt

[<Test>]
let ``state is not recovered after a crash`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec crashHandle recved = actor {
            match! Receive.Any () with
            | :? string as msg ->
                let newRecved = msg :: recved
                do! ActorRefs.typed probe <! String.concat "," newRecved
                return! crashHandle newRecved
            | :? CrashIt ->
                failwith "crashing"
            | _ ->
                return! crashHandle recved
        }
        let crashStart = actor {
            do! setRestartHandler (fun _ctx msg err ->
                tell (typed probe) {msg = msg; err = err}
            )
            return! crashHandle []
        }

        let rec handle () = actor {
            let! _  = Receive.Any ()
            return! handle ()
        }

        let start = actor {
            let! crasher =
                createChild (fun f ->
                    spawn f (Props.Named "crasher") (notPersisted crashStart)
                )
            do! ActorRefs.typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Props.Named "parent" with
                supervisionStrategy = Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = spawn tk.Sys parentProps (notPersisted start)

        let crasher : ActorRefs.IActorRef<string> = ActorRefs.retype (probe.ExpectMsg<ActorRefs.IActorRef<obj>> ())
        let msg1 = "1"
        tell crasher msg1
        probe.ExpectMsg msg1 |> ignore
        let msg2 = "2"
        tell crasher msg2
        probe.ExpectMsg $"{msg2},{msg1}"|> ignore
        tell (retype crasher) CrashIt
        let _res = probe.ExpectMsg<CrashMsg>()
        let msg3 = "3"
        tell crasher msg3
        probe.ExpectMsg $"{msg3}"|> ignore

