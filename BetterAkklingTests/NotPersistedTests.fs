module BetterAkklingTests.NotPersistedTests

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
                let! msg =
                    let rec getMsg () = Better.actor {
                        match! Actions.receiveAny () with
                        | :? Msg as msg -> return msg
                        | _ -> return! getMsg ()
                    }
                    getMsg ()
                do! typed probe <! msg
                return! handle ()
            }
        let act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.notPersisted <| handle ())

        let m1 = {value = 1234}
        act.Tell(m1, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg m1 |> ignore

        (retype act).Tell("testing 1 2 3", Akka.Actor.ActorRefs.NoSender)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 12345}
        act.Tell(m2, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg m2 |> ignore

[<Test>]
let ``spawn with no name`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Better.actor {
                let! msg = Actions.receiveOnly<Msg> ()
                do! typed probe <! msg
                return! handle ()
            }
        let act = Better.spawn tk.Sys Better.Props.Anonymous (Better.notPersisted <| handle ())

        let m1 = {value = 1234}
        act.Tell(m1, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg m1 |> ignore

        (retype act).Tell("testing 1 2 3", Akka.Actor.ActorRefs.NoSender)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 12345}
        act.Tell(m2, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg m2 |> ignore

[<Test>]
let ``get actor gives correct actor ref`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Better.actor {
                let! act = Actions.getActor ()
                do! typed probe <! act
            }
        let act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.notPersisted <| handle ())

        probe.ExpectMsg act |> ignore

[<Test>]
let ``get actor context gives correct actor`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Better.actor {
                let! act = Actions.unsafeGetActorCtx ()
                do! typed probe <! act.Self
            }
        let act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.notPersisted <| handle ())

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
                do! typed probe <! "should not get this"
            }
        let act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.notPersisted <| handle ())

        tk.Watch (untyped act) |> ignore
        let m1 = {value = 1234}
        act.Tell(m1, Akka.Actor.ActorRefs.NoSender)
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
                    (typed probe).Tell(ctx.Self, untyped ctx.Self)
                    let typed : IActorRef<Msg> = retype self
                    typed
                )
                do! typed probe <! newAct
            }
        let act : IActorRef<Msg> =
            Better.spawn tk.Sys (Better.Props.Named "test") (Better.notPersisted <| handle ())
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
                    do! typed probe <! msg
                    do! Actions.unstashOne ()
                    return! handle true
                elif unstashed then
                    do! typed probe <! msg
                    return! handle true
                else
                    do! Actions.stash ()
                    return! handle false
            }
        let act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.notPersisted <| handle false)

        let m1 = {value = 1}
        act.Tell(m1, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 2}
        act.Tell(m2, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m3 = {value = 101}
        act.Tell(m3, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg m3 |> ignore
        probe.ExpectMsg m1 |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m4 = {value = 102}
        act.Tell(m4, Akka.Actor.ActorRefs.NoSender)
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
                    do! typed probe <! msg
                    do! Actions.unstashAll ()
                    return! handle true
                elif unstashed then
                    do! typed probe <! msg
                    return! handle true
                else
                    do! Actions.stash ()
                    return! handle false
            }
        let act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.notPersisted <| handle false)

        let m1 = {value = 1}
        act.Tell(m1, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 2}
        act.Tell(m2, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m3 = {value = 101}
        act.Tell(m3, Akka.Actor.ActorRefs.NoSender)
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
        let watched = Better.spawn tk.Sys (Better.Props.Named "watched") (Better.notPersisted <| otherActor ())

        let rec handle () =
            Better.actor {
                match! Actions.receiveAny () with
                | MessagePatterns.Terminated (act, _, _) ->
                    do! typed probe <!  act
                    return! Actions.stop ()
                | _msg ->
                    return! handle ()
            }
        let start = Better.actor {
            do! Actions.watch watched
            do! typed probe <! ""
            return! handle ()
        }
        let _act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.notPersisted start)

        probe.ExpectMsg "" |> ignore
        (retype watched).Tell("", Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg watched |> ignore

[<Test>]
let ``unwatch works`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = Better.actor {
            let! _ = Actions.receiveOnly<string> ()
            return ()
        }
        let watched = Better.spawn tk.Sys (Better.Props.Named "watched") (Better.notPersisted <| otherActor ())

        let rec handle () =
            Better.actor {
                match! Actions.receiveAny () with
                | MessagePatterns.Terminated (act, _, _) ->
                    do! typed probe <!  act
                    return! Actions.stop ()
                | :? string ->
                    do! Actions.unwatch watched
                    do! typed probe <! "unwatched"
                    return! handle ()
                | _msg ->
                    return! handle ()
            }
        let start = Better.actor {
            do! Actions.watch watched
            do! typed probe <! "watched"
            return! handle ()
        }
        let act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.notPersisted start)

        probe.ExpectMsg "watched" |> ignore
        (retype act).Tell("", Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg "unwatched" |> ignore
        (retype watched).Tell("", Akka.Actor.ActorRefs.NoSender)
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
            do! typed probe <! "scheduled"
            return! handle ()
        }
        let _act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.notPersisted start)

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
            do! typed probe <! "scheduled"
            return! handle ()
        }
        let _act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.notPersisted start)

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
            do! typed probe <! "scheduled"
            return! handle ()
        }
        let _act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.notPersisted start)

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
                do! typed probe <! untyped sender
                return! handle ()
            }
        let act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.notPersisted <| handle ())

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
            do! typed probe <! selection
        }
        let _act = Better.spawn tk.Sys (Better.Props.Named "test") (Better.notPersisted start)

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
                (typed probe).Tell ({msg = msg; err = err}, Akka.Actor.ActorRefs.NoSender)
            )
            return! crashHandle ()
        }
        let start = Better.actor {
            let! crasher =
                Actions.createChild (fun f ->
                    Better.spawn f (Better.Props.Named "crasher") (Better.notPersisted crashStart)
                )
            do! typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Better.Props.Named "parent" with
                supervisionStrategy = Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = Better.spawn tk.Sys parentProps (Better.notPersisted start)

        let crasher = probe.ExpectMsg<IActorRef<obj>> ()
        (retype crasher).Tell("crash it", Akka.Actor.ActorRefs.NoSender)
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
                (typed probe).Tell ({msg = msg; err = err}, Akka.Actor.ActorRefs.NoSender)
            )
            checkCrash ()
            return! handle()
        }
        let start = Better.actor {
            let! crasher =
                Actions.createChild (fun f ->
                    Better.spawn f (Better.Props.Named "crasher") (Better.notPersisted crashStart)
                )
            do! typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Better.Props.Named "parent" with
                supervisionStrategy = Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = Better.spawn tk.Sys parentProps (Better.notPersisted start)

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
                (typed probe).Tell ({msg = msg; err = err}, Akka.Actor.ActorRefs.NoSender)
            )
            do! Actions.clearRestartHandler ()
            return! handle()
        }

        let start = Better.actor {
            let! crasher =
                Actions.createChild (fun f ->
                    Better.spawn f (Better.Props.Named "crasher") (Better.notPersisted crashStart)
                )
            do! typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Better.Props.Named "parent" with
                supervisionStrategy = Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = Better.spawn tk.Sys parentProps (Better.notPersisted start)

        let crasher = probe.ExpectMsg<IActorRef<obj>> ()
        (retype crasher).Tell(Akka.Actor.Kill.Instance, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

