module BetterAkklingTests.NotPersistentTests

open System

open Akkling

open NUnit.Framework
open FsUnitTyped

open BetterAkkling

type Msg = {value: int}

[<Test>]
let ``spawn with name`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Better.actor {
                let! msg = Actions.receiveOnly<Msg> ()
                typed probe <! msg
                return! handle ()
            }
        let act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.NotPersistent <| handle ())

        let m1 = {value = 1234}
        act <! m1
        probe.ExpectMsg m1 |> ignore

        retype act <! "testing 1 2 3"
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 12345}
        act <! m2
        probe.ExpectMsg m2 |> ignore

[<Test>]
let ``spawn with no name`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Better.actor {
                let! msg = Actions.receiveOnly<Msg> ()
                typed probe <! msg
                return! handle ()
            }
        let act = Better.spawn tk.Sys Better.Props.Anonymous (Better.NotPersistent <| handle ())

        let m1 = {value = 1234}
        act <! m1
        probe.ExpectMsg m1 |> ignore

        retype act <! "testing 1 2 3"
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 12345}
        act <! m2
        probe.ExpectMsg m2 |> ignore

[<Test>]
let ``get actor gives correct actor ref`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Better.actor {
                let! act = Actions.getActor ()
                typed probe <! act
            }
        let act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.NotPersistent <| handle ())

        probe.ExpectMsg act |> ignore

[<Test>]
let ``get actor context gives correct actor`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Better.actor {
                let! act = Actions.unsafeGetActorCtx ()
                typed probe <! act.Self
            }
        let act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.NotPersistent <| handle ())

        probe.ExpectMsg act |> ignore


[<Test>]
let ``stop action stops the actor`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Better.actor {
                let! _msg = Actions.receiveOnly<Msg> ()
                do! Actions.stop ()
                // The actor should stop on the previous line so this message should never be sent
                typed probe <! "should not get this"
            }
        let act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.NotPersistent <| handle ())

        tk.Watch (untyped act) |> ignore
        let m1 = {value = 1234}
        act <! m1
        tk.ExpectTerminated (untyped act) |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``create actor can create an actor`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Better.actor {
                let! self = Actions.getActor ()
                let! newAct = Actions.createChild (fun parent ->
                    let ctx = parent :?> Actor<obj>
                    typed probe <! ctx.Self
                    let typed : IActorRef<Msg> = retype self
                    typed
                )
                typed probe <! newAct
            }
        let act : IActorRef<Msg> =
            Better.spawn tk.Sys (Better.Props.Named "test") (Better.NotPersistent <| handle ())
        let actObj : IActorRef<obj> = retype act

        probe.ExpectMsg actObj |> ignore
        probe.ExpectMsg act |> ignore

[<Test>]
let ``unstash one only unstashes one message at a time`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle unstashed =
            Better.actor {
                let! msg = Actions.receiveOnly<Msg> ()
                if msg.value > 100 then
                    typed probe <! msg
                    do! Actions.unstashOne ()
                    return! handle true
                elif unstashed then
                    typed probe <! msg
                    return! handle true
                else
                    do! Actions.stash ()
                    return! handle false
            }
        let act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.NotPersistent <| handle false)

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
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle unstashed =
            Better.actor {
                let! msg = Actions.receiveOnly<Msg> ()
                if msg.value > 100 then
                    typed probe <! msg
                    do! Actions.unstashAll ()
                    return! handle true
                elif unstashed then
                    typed probe <! msg
                    return! handle true
                else
                    do! Actions.stash ()
                    return! handle false
            }
        let act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.NotPersistent <| handle false)

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
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = Better.actor {
            let! _ = Actions.receiveOnly<string> ()
            return ()
        }
        let watched = Better.spawn tk.Sys (Better.Props.Named "watched") (Better.NotPersistent <| otherActor ())

        let rec handle () =
            Better.actor {
                match! Actions.receiveAny () with
                | MessagePatterns.Terminated (act, _, _) ->
                    typed probe <!  act
                    return! Actions.stop ()
                | _msg ->
                    return! handle ()
            }
        let start = Better.actor {
            do! Actions.watch watched
            typed probe <! ""
            return! handle ()
        }
        let _act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.NotPersistent start)

        probe.ExpectMsg "" |> ignore
        retype watched <! ""
        probe.ExpectMsg watched |> ignore

[<Test>]
let ``unwatch works`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = Better.actor {
            let! _ = Actions.receiveOnly<string> ()
            return ()
        }
        let watched = Better.spawn tk.Sys (Better.Props.Named "watched") (Better.NotPersistent <| otherActor ())

        let rec handle () =
            Better.actor {
                match! Actions.receiveAny () with
                | MessagePatterns.Terminated (act, _, _) ->
                    typed probe <!  act
                    return! Actions.stop ()
                | :? string ->
                    do! Actions.unwatch watched
                    typed probe <! "unwatched"
                    return! handle ()
                | _msg ->
                    return! handle ()
            }
        let start = Better.actor {
            do! Actions.watch watched
            typed probe <! "watched"
            return! handle ()
        }
        let act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.NotPersistent start)

        probe.ExpectMsg "watched" |> ignore
        retype act <! ""
        probe.ExpectMsg "unwatched" |> ignore
        retype watched <! ""
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``schedule works`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Better.actor {
                let! _msg = Actions.receiveAny ()
                return! handle ()
            }
        let start = Better.actor {
            let! _cancel = Actions.schedule (TimeSpan.FromMilliseconds 100.0) (typed probe) "message"
            typed probe <! "scheduled"
            return! handle ()
        }
        let _act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.NotPersistent start)

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
            Better.actor {
                let! _msg = Actions.receiveAny ()
                return! handle ()
            }
        let start = Better.actor {
            let! cancel = Actions.schedule (TimeSpan.FromMilliseconds 100.0) (typed probe) "message"
            cancel.Cancel ()
            typed probe <! "scheduled"
            return! handle ()
        }
        let _act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.NotPersistent start)

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
            Better.actor {
                let! _msg = Actions.receiveAny ()
                return! handle ()
            }
        let start = Better.actor {
            let! _cancel = Actions.scheduleRepeatedly delay interval (typed probe) "message"
            typed probe <! "scheduled"
            return! handle ()
        }
        let _act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.NotPersistent start)

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
            Better.actor {
                let! _msg = Actions.receiveOnly<string> ()
                let! sender = Actions.getSender ()
                typed probe <! untyped sender
                return! handle ()
            }
        let act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.NotPersistent <| handle ())

        act.Tell("message", probe)
        probe.ExpectMsg probe |> ignore

[<Test>]
let ``select get's the correct selection`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let probeAct = probe :> Akka.Actor.IActorRef
        let path = probeAct.Path.ToString()

        let rec handle () =
            Better.actor {
                let! _msg = Actions.receiveOnly<string> ()
                return! handle ()
            }
        let start = Better.actor {
            let! selection = Actions.select path
            typed probe <! selection
        }
        let _act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.NotPersistent start)

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

        let rec handle () = Better.actor {
            let! _  = Actions.receiveAny ()
            return! handle ()
        }

        let rec crashHandle () = Better.actor {
            let! _  = Actions.receiveOnly<string> ()
            failwith "crashed"
            return! handle ()
        }
        let crashStart = Better.actor {
            do! Actions.setRestartHandler (fun msg err ->
                typed probe <! {msg = msg; err = err}
            )
            return! crashHandle ()
        }
        let start = Better.actor {
            let! crasher =
                Actions.createChild (fun f ->
                    Better.spawn f (Better.Props.Named "crasher") (Better.NotPersistent crashStart)
                )
            typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Better.Props.Named "parent" with
                supervisionStrategy = Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = Better.spawn tk.Sys parentProps (Better.NotPersistent start)

        let crasher = probe.ExpectMsg<IActorRef<obj>> ()
        retype crasher <! "crash it"
        let res = probe.ExpectMsg<CrashMsg>()
        res.msg :?> string |> ignore
        res.err :?> Exception |> ignore

[<Test>]
let ``crash handler is invoked if actor crashes before calling receive`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let mutable alreadyCrashed = 0

        let rec checkCrash () =
            if alreadyCrashed = 0 then
                Threading.Interlocked.Increment(&alreadyCrashed) |> ignore
                failwith "Initial crash"

        let rec handle () = Better.actor {
            let! _  = Actions.receiveAny ()
            return! handle ()
        }

        let crashStart = Better.actor {
            do! Actions.setRestartHandler (fun msg err ->
                typed probe <! {msg = msg; err = err}
            )
            checkCrash ()
            return! handle()
        }
        let start = Better.actor {
            let! crasher =
                Actions.createChild (fun f ->
                    Better.spawn f (Better.Props.Named "crasher") (Better.NotPersistent crashStart)
                )
            typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Better.Props.Named "parent" with
                supervisionStrategy = Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = Better.spawn tk.Sys parentProps (Better.NotPersistent start)

        let _crasher = probe.ExpectMsg<IActorRef<obj>> ()
        let _res = probe.ExpectMsg<CrashMsg>()
        ignore _res

[<Test>]
let ``crash handler is not invoked if handler is cleared`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () = Better.actor {
            let! _  = Actions.receiveAny ()
            return! handle ()
        }

        let crashStart = Better.actor {
            do! Actions.setRestartHandler (fun msg err ->
                typed probe <! {msg = msg; err = err}
            )
            do! Actions.clearRestartHandler ()
            return! handle()
        }

        let start = Better.actor {
            let! crasher =
                Actions.createChild (fun f ->
                    Better.spawn f (Better.Props.Named "crasher") (Better.NotPersistent crashStart)
                )
            typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Better.Props.Named "parent" with
                supervisionStrategy = Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = Better.spawn tk.Sys parentProps (Better.NotPersistent start)

        let crasher = probe.ExpectMsg<IActorRef<obj>> ()
        retype crasher <! Akka.Actor.Kill.Instance
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

