module BetterAkklingTests.BetterTests

open System

open NUnit.Framework
open FsUnitTyped

open BetterAkkling

let (<!) = Akkling.ActorRefs.(<!)

type Msg = {value: int}

[<Test>]
let ``spawn with name`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Better.actor {
                let! msg = Better.receiveOnly<Msg> ()
                Akkling.ActorRefs.typed probe <! msg
                return! handle ()
            }
        let act = Better.spawn tk.Sys "test" (Better.props (handle ()))

        let m1 = {value = 1234}
        act <! m1
        probe.ExpectMsg m1 |> ignore

        Akkling.ActorRefs.retype act <! "testing 1 2 3"
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 12345}
        act <! m2
        probe.ExpectMsg m2 |> ignore

[<Test>]
let ``spawn with no name`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Better.actor {
                let! msg = Better.receiveOnly<Msg> ()
                Akkling.ActorRefs.typed probe <! msg
                return! handle ()
            }
        let act = Better.spawnAnonymous tk.Sys (Better.props <| handle ())

        let m1 = {value = 1234}
        act <! m1
        probe.ExpectMsg m1 |> ignore

        Akkling.ActorRefs.retype act <! "testing 1 2 3"
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 12345}
        act <! m2
        probe.ExpectMsg m2 |> ignore

[<Test>]
let ``get actor gives correct actor ref`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Better.actor {
                let! act = Better.getActor ()
                Akkling.ActorRefs.typed probe <! act
            }
        let act = Better.spawn tk.Sys "test" (Better.props (handle ()))

        probe.ExpectMsg act |> ignore

[<Test>]
let ``get actor context gives correct actor`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Better.actor {
                let! act = Better.unsafeGetActorCtx ()
                Akkling.ActorRefs.typed probe <! act.Self
            }
        let act = Better.spawn tk.Sys "test" (Better.props (handle ()))

        probe.ExpectMsg act |> ignore


[<Test>]
let ``stop action stops the actor`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Better.actor {
                let! _msg = Better.receiveOnly<Msg> ()
                do! Better.stop ()
                // The actor should stop on the previous line so this message should never be sent
                Akkling.ActorRefs.typed probe <! "should not get this"
            }
        let act = Better.spawnAnonymous tk.Sys (Better.props <| handle ())

        tk.Watch (Akkling.ActorRefs.untyped act) |> ignore
        let m1 = {value = 1234}
        act <! m1
        tk.ExpectTerminated (Akkling.ActorRefs.untyped act) |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``create actor can create an actor`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Better.actor {
                let! self = Better.getActor ()
                let! newAct = Better.createChild (fun parent ->
                    let ctx = parent :?> Akkling.Actors.Actor<obj>
                    Akkling.ActorRefs.typed probe <! ctx.Self
                    let typed : Akkling.ActorRefs.IActorRef<Msg> = Akkling.ActorRefs.retype self
                    typed
                )
                Akkling.ActorRefs.typed probe <! newAct
            }
        let act : Akkling.ActorRefs.IActorRef<Msg> = Better.spawnAnonymous tk.Sys (Better.props <| handle ())
        let actObj : Akkling.ActorRefs.IActorRef<obj> = Akkling.ActorRefs.retype act

        probe.ExpectMsg actObj |> ignore
        probe.ExpectMsg act |> ignore

[<Test>]
let ``unstash one only unstashes one message at a time`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle unstashed =
            Better.actor {
                let! msg = Better.receiveOnly<Msg> ()
                if msg.value > 100 then
                    Akkling.ActorRefs.typed probe <! msg
                    do! Better.unstashOne ()
                    return! handle true
                elif unstashed then
                    Akkling.ActorRefs.typed probe <! msg
                    return! handle true
                else
                    do! Better.stash ()
                    return! handle false
            }
        let act = Better.spawn tk.Sys "test" (Better.props (handle false))

        let m1 = {value = 1}
        act <! m1
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 2}
        act <! m2
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m3 = {value = 101}
        act <! m3
        probe.ExpectMsg m3 |> ignore
        probe.ExpectMsg m1 |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m4 = {value = 102}
        act <! m4
        probe.ExpectMsg m4 |> ignore
        probe.ExpectMsg m2 |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``unstash all unstashes all the messages`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle unstashed =
            Better.actor {
                let! msg = Better.receiveOnly<Msg> ()
                if msg.value > 100 then
                    Akkling.ActorRefs.typed probe <! msg
                    do! Better.unstashAll ()
                    return! handle true
                elif unstashed then
                    Akkling.ActorRefs.typed probe <! msg
                    return! handle true
                else
                    do! Better.stash ()
                    return! handle false
            }
        let act = Better.spawn tk.Sys "test" (Better.props (handle false))

        let m1 = {value = 1}
        act <! m1
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 2}
        act <! m2
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m3 = {value = 101}
        act <! m3
        probe.ExpectMsg m3 |> ignore
        probe.ExpectMsg m1 |> ignore
        probe.ExpectMsg m2 |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``watch works`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = Better.actor {
            let! _ = Better.receiveOnly<string> ()
            return ()
        }
        let watched = Better.spawnAnonymous tk.Sys (Better.props (otherActor ()))

        let rec handle () =
            Better.actor {
                match! Better.receiveAny () with
                | Akkling.MessagePatterns.Terminated (act, _, _) ->
                    Akkling.ActorRefs.typed probe <!  act
                    return! Better.stop ()
                | _msg ->
                    return! handle ()
            }
        let start = Better.actor {
            do! Better.watch watched
            Akkling.ActorRefs.typed probe <! ""
            return! handle ()
        }
        let _act = Better.spawn tk.Sys "test" (Better.props start)

        probe.ExpectMsg "" |> ignore
        Akkling.ActorRefs.retype watched <! ""
        probe.ExpectMsg watched |> ignore

[<Test>]
let ``unwatch works`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = Better.actor {
            let! _ = Better.receiveOnly<string> ()
            return ()
        }
        let watched = Better.spawnAnonymous tk.Sys (Better.props (otherActor ()))

        let rec handle () =
            Better.actor {
                match! Better.receiveAny () with
                | Akkling.MessagePatterns.Terminated (act, _, _) ->
                    Akkling.ActorRefs.typed probe <!  act
                    return! Better.stop ()
                | :? string ->
                    do! Better.unwatch watched
                    Akkling.ActorRefs.typed probe <! "unwatched"
                    return! handle ()
                | _msg ->
                    return! handle ()
            }
        let start = Better.actor {
            do! Better.watch watched
            Akkling.ActorRefs.typed probe <! "watched"
            return! handle ()
        }
        let act = Better.spawn tk.Sys "test" (Better.props start)

        probe.ExpectMsg "watched" |> ignore
        Akkling.ActorRefs.retype act <! ""
        probe.ExpectMsg "unwatched" |> ignore
        Akkling.ActorRefs.retype watched <! ""
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``schedule works`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Better.actor {
                let! _msg = Better.receiveAny ()
                return! handle ()
            }
        let start = Better.actor {
            let! _cancel = Better.schedule (TimeSpan.FromMilliseconds 100.0) (Akkling.ActorRefs.typed probe) "message"
            Akkling.ActorRefs.typed probe <! "scheduled"
            return! handle ()
        }
        let _act = Better.spawn tk.Sys "test" (Better.props start)

        probe.ExpectMsg "scheduled" |> ignore
        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 99.0)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 1.0)
        probe.ExpectMsg "message" |> ignore

        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 100.0)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``scheduled messages can be cancelled`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Better.actor {
                let! _msg = Better.receiveAny ()
                return! handle ()
            }
        let start = Better.actor {
            let! cancel = Better.schedule (TimeSpan.FromMilliseconds 100.0) (Akkling.ActorRefs.typed probe) "message"
            cancel.Cancel ()
            Akkling.ActorRefs.typed probe <! "scheduled"
            return! handle ()
        }
        let _act = Better.spawn tk.Sys "test" (Better.props start)

        probe.ExpectMsg "scheduled" |> ignore
        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 100.0)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)
        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 100.0)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``schedule repeatedly works`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let delay = TimeSpan.FromMilliseconds 100.0
        let interval = TimeSpan.FromMilliseconds 50.0

        let rec handle () =
            Better.actor {
                let! _msg = Better.receiveAny ()
                return! handle ()
            }
        let start = Better.actor {
            let! _cancel = Better.scheduleRepeatedly delay interval (Akkling.ActorRefs.typed probe) "message"
            Akkling.ActorRefs.typed probe <! "scheduled"
            return! handle ()
        }
        let _act = Better.spawn tk.Sys "test" (Better.props start)

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
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Better.actor {
                let! _msg = Better.receiveOnly<string> ()
                let! sender = Better.getSender ()
                Akkling.ActorRefs.typed probe <! Akkling.ActorRefs.untyped sender
                return! handle ()
            }
        let act = Better.spawn tk.Sys "test" (Better.props (handle ()))

        act.Tell("message", probe)
        probe.ExpectMsg probe |> ignore

[<Test>]
let ``select get's the correct selection`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let probeAct = probe :> Akka.Actor.IActorRef
        let path = probeAct.Path.ToString()

        let rec handle () =
            Better.actor {
                let! _msg = Better.receiveOnly<string> ()
                return! handle ()
            }
        let start = Better.actor {
            let! selection = Better.select path
            Akkling.ActorRefs.typed probe <! selection
        }
        let _act = Better.spawn tk.Sys "test" (Better.props start)

        let msg = probe.ExpectMsg<Akka.Actor.ActorSelection> ()
        msg.PathString |> shouldEqual (probeAct.Path.ToStringWithoutAddress())

type CrashMsg = {
    msg: obj
    err: obj
}

[<Test>]
let ``crash handler is invoked if actor crashes`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () = Better.actor {
            let! _  = Better.receiveAny ()
            return! handle ()
        }

        let crashStart = Better.actor {
            do! Better.setOnRestart (fun msg err ->
                Akkling.ActorRefs.typed probe <! {msg = msg; err = err}
            )
            return! handle()
        }

        let start = Better.actor {
            let! crasher = Better.createChild (fun f -> Better.spawn f "crasher" (Better.props crashStart))
            Akkling.ActorRefs.typed probe <! crasher
            return! handle ()
        }
        let _parent = Better.spawn tk.Sys "parent" {
            Better.props start with
                supervisionStrategy = Akkling.Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }

        let crasher = probe.ExpectMsg<Akkling.ActorRefs.IActorRef<obj>> ()
        Akkling.ActorRefs.retype crasher <! Akka.Actor.Kill.Instance
        let res = probe.ExpectMsg<CrashMsg>()
        res.msg :?> Akka.Actor.Kill |> ignore
        res.err :?> Akka.Actor.ActorKilledException |> ignore

[<Test>]
let ``crash handler is not invoked if handler is cleared`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () = Better.actor {
            let! _  = Better.receiveAny ()
            return! handle ()
        }

        let crashStart = Better.actor {
            do! Better.setOnRestart (fun msg err ->
                Akkling.ActorRefs.typed probe <! {msg = msg; err = err}
            )
            do! Better.clearOnRestart ()
            return! handle()
        }

        let start = Better.actor {
            let! crasher = Better.createChild (fun f -> Better.spawn f "crasher" (Better.props crashStart))
            Akkling.ActorRefs.typed probe <! crasher
            return! handle ()
        }
        let _parent = Better.spawn tk.Sys "parent" {
            Better.props start with
                supervisionStrategy = Akkling.Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }

        let crasher = probe.ExpectMsg<Akkling.ActorRefs.IActorRef<obj>> ()
        Akkling.ActorRefs.retype crasher <! Akka.Actor.Kill.Instance
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

