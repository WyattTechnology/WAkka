module BetterAkklingTests.CheckpointedTests

open System

open NUnit.Framework
open FsUnitTyped

open Akkling

open BetterAkkling
open BetterAkkling.CommonActions
open BetterAkkling.Simple
open BetterAkkling.Simple.Actions

type Msg = {value: int}

let tell (act: ActorRefs.IActorRef<'Msg>) (msg: 'Msg) =
    act.Tell(msg, Akka.Actor.ActorRefs.NoSender)

[<Test>]
let ``spawn with name`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! msg = receiveOnly<Msg> ()
                do! ActorRefs.typed probe <! msg
                return! handle ()
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (Checkpointed <| handle ())

        let m1 = {value = 1234}
        tell act m1
        probe.ExpectMsg m1 |> ignore

        tell (retype act) "testing 1 2 3"
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 12345}
        tell act m2
        probe.ExpectMsg m2 |> ignore

[<Test>]
let ``spawn with no name`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! msg = receiveOnly<Msg> ()
                do! ActorRefs.typed probe <! msg
                return! handle ()
            }
        let act = spawn tk.Sys Context.Props.Anonymous (Checkpointed <| handle ())

        let m1 = {value = 1234}
        tell act m1
        probe.ExpectMsg m1 |> ignore

        tell (retype act) "testing 1 2 3"
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 12345}
        tell act m2
        probe.ExpectMsg m2 |> ignore

[<Test>]
let ``get actor gives correct actor ref`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! act = getActor ()
                do! ActorRefs.typed probe <! (untyped act)
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (Checkpointed <| handle ())

        probe.ExpectMsg (untyped act) |> ignore

[<Test>]
let ``get actor context gives correct actor`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! act = unsafeGetActorCtx ()
                do! ActorRefs.typed probe <! act.Self
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (Checkpointed <| handle ())

        probe.ExpectMsg (untyped act) |> ignore


[<Test>]
let ``stop action stops the actor`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! _msg = receiveOnly<Msg> ()
                do! stop ()
                // The actor should stop on the previous line so this message should never be sent
                do! ActorRefs.typed probe <! "should not get this"
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (Checkpointed <| handle ())

        tk.Watch (untyped act) |> ignore
        let m1 = {value = 1234}
        tell act m1
        tk.ExpectTerminated (untyped act) |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``create actor can create an actor`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! self = getActor ()
                let! newAct = createChild (fun parent ->
                    let ctx = parent :?> Akka.Actor.IActorContext
                    (typed probe).Tell (ctx.Self, ctx.Self)
                    let typed : ActorRefs.IActorRef<Msg> = ActorRefs.retype self
                    typed
                )
                do! ActorRefs.typed probe <! (untyped newAct)
            }
        let act : ActorRefs.IActorRef<Msg> =
            spawn tk.Sys (Context.Props.Named "test") (Checkpointed <| handle ())

        probe.ExpectMsg (untyped act) |> ignore
        probe.ExpectMsg (untyped act) |> ignore

[<Test>]
let ``unstash one only unstashes one message at a time`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle unstashed =
            actor {
                let! msg = receiveOnly<Msg> ()
                if msg.value > 100 then
                    do! ActorRefs.typed probe <! msg
                    do! unstashOne ()
                    return! handle true
                elif unstashed then
                    do! ActorRefs.typed probe <! msg
                    return! handle true
                else
                    do! stash ()
                    return! handle false
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (Checkpointed <| handle false)

        let m1 = {value = 1}
        tell act m1
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 2}
        tell act m2
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m3 = {value = 101}
        tell act m3
        probe.ExpectMsg m3 |> ignore
        probe.ExpectMsg m1 |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m4 = {value = 102}
        tell act m4
        probe.ExpectMsg m4 |> ignore
        probe.ExpectMsg m2 |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``unstash all unstashes all the messages`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle unstashed =
            actor {
                let! msg = receiveOnly<Msg> ()
                if msg.value > 100 then
                    do! ActorRefs.typed probe <! msg
                    do! unstashAll ()
                    return! handle true
                elif unstashed then
                    do! ActorRefs.typed probe <! msg
                    return! handle true
                else
                    do! stash ()
                    return! handle false
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (Checkpointed <| handle false)

        let m1 = {value = 1}
        tell act m1
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 2}
        tell act m2
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m3 = {value = 101}
        tell act m3
        probe.ExpectMsg m3 |> ignore
        probe.ExpectMsg m1 |> ignore
        probe.ExpectMsg m2 |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``watch works`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = actor {
            let! _ = receiveOnly<string> ()
            return ()
        }
        let watched = spawn tk.Sys (Context.Props.Named "watched") (Checkpointed <| otherActor ())

        let rec handle () =
            actor {
                match! receiveAny () with
                | MessagePatterns.Terminated (act, _, _) ->
                    do! ActorRefs.typed probe <!  (untyped act)
                    return! stop ()
                | _msg ->
                    return! handle ()
            }
        let start = actor {
            do! watch watched
            do! ActorRefs.typed probe <! ""
            return! handle ()
        }
        let _act = spawn tk.Sys (Context.Props.Named "test") (Checkpointed start)

        probe.ExpectMsg "" |> ignore
        tell (retype watched) ""
        probe.ExpectMsg (untyped watched) |> ignore

[<Test>]
let ``unwatch works`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = actor {
            let! _ = receiveOnly<string> ()
            return ()
        }
        let watched = spawn tk.Sys (Context.Props.Named "watched") (Checkpointed <| otherActor ())

        let rec handle () =
            actor {
                match! receiveAny () with
                | MessagePatterns.Terminated (act, _, _) ->
                    do! ActorRefs.typed probe <!  act
                    return! stop ()
                | :? string ->
                    do! unwatch watched
                    do! ActorRefs.typed probe <! "unwatched"
                    return! handle ()
                | _msg ->
                    return! handle ()
            }
        let start = actor {
            do! watch watched
            do! ActorRefs.typed probe <! "watched"
            return! handle ()
        }
        let act = spawn tk.Sys (Context.Props.Named "test") (Checkpointed start)

        probe.ExpectMsg "watched" |> ignore
        tell (retype act) ""
        probe.ExpectMsg "unwatched" |> ignore
        tell (retype watched) ""
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``schedule works`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! _msg = receiveAny ()
                return! handle ()
            }
        let start = actor {
            let! _cancel = schedule (TimeSpan.FromMilliseconds 100.0) (typed probe) "message"
            do! ActorRefs.typed probe <! "scheduled"
            return! handle ()
        }
        let _act = spawn tk.Sys (Context.Props.Named "test") (Checkpointed start)

        probe.ExpectMsg "scheduled" |> ignore
        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 99.0)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 1.0)
        probe.ExpectMsg "message" |> ignore

        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 100.0)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``scheduled messages can be cancelled`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! _msg = receiveAny ()
                return! handle ()
            }
        let start = actor {
            let! cancel = schedule (TimeSpan.FromMilliseconds 100.0) (typed probe) "message"
            cancel.Cancel ()
            do! ActorRefs.typed probe <! "scheduled"
            return! handle ()
        }
        let _act = spawn tk.Sys (Context.Props.Named "test") (Checkpointed start)

        probe.ExpectMsg "scheduled" |> ignore
        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 100.0)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)
        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 100.0)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``schedule repeatedly works`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let delay = TimeSpan.FromMilliseconds 100.0
        let interval = TimeSpan.FromMilliseconds 50.0

        let rec handle () =
            actor {
                let! _msg = receiveAny ()
                return! handle ()
            }
        let start = actor {
            let! _cancel = scheduleRepeatedly delay interval (typed probe) "message"
            do! ActorRefs.typed probe <! "scheduled"
            return! handle ()
        }
        let _act = spawn tk.Sys (Context.Props.Named "test") (Checkpointed start)

        probe.ExpectMsg "scheduled" |> ignore
        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 99.0)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 1.0)
        probe.ExpectMsg "message" |> ignore

        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 49.0)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 1.0)
        probe.ExpectMsg "message" |> ignore

        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 49.0)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 1.0)
        probe.ExpectMsg "message" |> ignore

[<Test>]
let ``get sender get's the correct actor`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! _msg = receiveOnly<string> ()
                let! sender = getSender ()
                do! ActorRefs.typed probe <! (untyped sender)
                return! handle ()
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (Checkpointed <| handle ())

        act.Tell("message", probe)
        probe.ExpectMsg probe |> ignore

[<Test>]
let ``select get's the correct selection`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let probeAct = probe :> Akka.Actor.IActorRef
        let path = probeAct.Path.ToString()

        let rec handle () =
            actor {
                let! _msg = receiveOnly<string> ()
                return! handle ()
            }
        let start = actor {
            let! selection = select path
            do! ActorRefs.typed probe <! selection
        }
        let _act = spawn tk.Sys (Context.Props.Named "test") (Checkpointed start)

        let msg = probe.ExpectMsg<Akka.Actor.ActorSelection> ()
        msg.PathString |> shouldEqual (probeAct.Path.ToStringWithoutAddress())

type CrashMsg = {
    msg: obj
    err: obj
}

[<Test>]
let ``crash handler is invoked if actor crashes`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () = actor {
            let! _  = receiveAny ()
            return! handle ()
        }

        let rec crashHandle () = actor {
            let! _  = receiveOnly<string> ()
            failwith "crashed"
            return! handle ()
        }
        let crashStart = actor {
            do! setRestartHandler (fun _ctx msg err ->
                tell (typed probe) {msg = msg; err = err}
            )
            return! crashHandle ()
        }
        let start = actor {
            let! crasher =
                createChild (fun f ->
                    spawn f (Context.Props.Named "crasher") (Checkpointed crashStart)
                )
            do! ActorRefs.typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Context.Props.Named "parent" with
                supervisionStrategy = Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = spawn tk.Sys parentProps (Checkpointed start)

        let crasher = probe.ExpectMsg<ActorRefs.IActorRef<obj>> ()
        tell (retype crasher) "crash it"
        let res = probe.ExpectMsg<CrashMsg>()
        res.msg :?> string |> ignore
        res.err :?> Exception |> ignore

[<Test>]
let ``crash handler is not invoked if handler is cleared`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () = actor {
            let! _  = receiveAny ()
            return! handle ()
        }

        let crashStart = actor {
            do! setRestartHandler (fun _ctx msg err ->
                tell (typed probe) {msg = msg; err = err}
            )
            do! clearRestartHandler ()
            return! handle()
        }

        let start = actor {
            let! crasher =
                createChild (fun f ->
                    spawn f (Context.Props.Named "crasher") (Checkpointed crashStart)
                )
            do! ActorRefs.typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Context.Props.Named "parent" with
                supervisionStrategy = Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = spawn tk.Sys parentProps (Checkpointed start)

        let crasher = probe.ExpectMsg<ActorRefs.IActorRef<obj>> ()
        tell (retype crasher) Akka.Actor.Kill.Instance
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

type CrashIt = CrashIt

[<Test>]
let ``state is recovered after a crash`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec crashHandle recved = actor {
            match! receiveAny () with
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
            let! _  = receiveAny ()
            return! handle ()
        }

        let start = actor {
            let! crasher =
                createChild (fun f ->
                    spawn f (Context.Props.Named "crasher") (Checkpointed crashStart)
                )
            do! ActorRefs.typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Context.Props.Named "parent" with
                supervisionStrategy = Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = spawn tk.Sys parentProps (Checkpointed start)

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
        probe.ExpectMsg $"{msg3},{msg2},{msg1}"|> ignore

