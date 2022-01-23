module BetterAkklingTests.NotPersistedTests

open System

open NUnit.Framework
open FsUnitTyped

open BetterAkkling
open BetterAkkling.Simple.Ops
open BetterAkkling.ActorRefs

type Msg = {value: int}

[<Test>]
let ``spawn with name`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Simple.actor {
                let! msg =
                    let rec getMsg () = Simple.actor {
                        match! Simple.Actions.receiveAny () with
                        | :? Msg as msg -> return msg
                        | _ -> return! getMsg ()
                    }
                    getMsg ()
                do! typed probe <! msg
                return! handle ()
            }
        let act = Simple.spawn tk.Sys (Simple.Props.Named "test") (Simple.NotPersisted <| handle ())

        let m1 = {value = 1234}
        act.Tell(m1)
        probe.ExpectMsg m1 |> ignore

        (retype act).Tell("testing 1 2 3")
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 12345}
        act.Tell(m2)
        probe.ExpectMsg m2 |> ignore

[<Test>]
let ``spawn with no name`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Simple.actor {
                let! msg = Simple.Actions.receiveOnly<Msg> ()
                do! typed probe <! msg
                return! handle ()
            }
        let act = Simple.spawn tk.Sys Simple.Props.Anonymous (Simple.NotPersisted <| handle ())

        let m1 = {value = 1234}
        act.Tell(m1)
        probe.ExpectMsg m1 |> ignore

        (retype act).Tell("testing 1 2 3")
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 12345}
        act.Tell(m2)
        probe.ExpectMsg m2 |> ignore

[<Test>]
let ``get actor gives correct actor ref`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Simple.actor {
                let! act = Simple.Actions.getActor ()
                do! typed probe <! act.Untyped
            }
        let act = Simple.spawn tk.Sys (Simple.Props.Named "test") (Simple.NotPersisted <| handle ())

        probe.ExpectMsg act.Untyped |> ignore

[<Test>]
let ``get actor context gives correct actor`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Simple.actor {
                let! act = Simple.Actions.unsafeGetActorCtx ()
                do! typed probe <! act.Self
            }
        let act = Simple.spawn tk.Sys (Simple.Props.Named "test") (Simple.NotPersisted <| handle ())

        probe.ExpectMsg act.Untyped |> ignore


[<Test>]
let ``stop action stops the actor`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Simple.actor {
                let! _msg = Simple.Actions.receiveOnly<Msg> ()
                do! Simple.Actions.stop ()
                // The actor should stop on the previous line so this message should never be sent
                do! typed probe <! "should not get this"
            }
        let act = Simple.spawn tk.Sys (Simple.Props.Named "test") (Simple.NotPersisted <| handle ())

        tk.Watch act.Untyped |> ignore
        let m1 = {value = 1234}
        act.Tell(m1)
        tk.ExpectTerminated act.Untyped |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``create actor can create an actor`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Simple.actor {
                let! self = Simple.Actions.getActor ()
                let! newAct = Simple.Actions.createChild (fun parent ->
                    let ctx = parent :?> Akka.Actor.IActorContext
                    (typed probe).Tell(ctx.Self, ctx.Self)
                    let typed : IActorRef<Msg> = retype self
                    typed
                )
                do! typed probe <! newAct.Untyped
            }
        let act : IActorRef<Msg> =
            Simple.spawn tk.Sys (Simple.Props.Named "test") (Simple.NotPersisted <| handle ())

        probe.ExpectMsg act.Untyped |> ignore
        probe.ExpectMsg act.Untyped |> ignore

[<Test>]
let ``unstash one only unstashes one message at a time`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle unstashed =
            Simple.actor {
                let! msg = Simple.Actions.receiveOnly<Msg> ()
                if msg.value > 100 then
                    do! typed probe <! msg
                    do! Simple.Actions.unstashOne ()
                    return! handle true
                elif unstashed then
                    do! typed probe <! msg
                    return! handle true
                else
                    do! Simple.Actions.stash ()
                    return! handle false
            }
        let act = Simple.spawn tk.Sys (Simple.Props.Named "test") (Simple.NotPersisted <| handle false)

        let m1 = {value = 1}
        act.Tell(m1)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 2}
        act.Tell(m2)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m3 = {value = 101}
        act.Tell(m3)
        probe.ExpectMsg m3 |> ignore
        probe.ExpectMsg m1 |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m4 = {value = 102}
        act.Tell(m4)
        probe.ExpectMsg m4 |> ignore
        probe.ExpectMsg m2 |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``unstash all unstashes all the messages`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle unstashed =
            Simple.actor {
                let! msg = Simple.Actions.receiveOnly<Msg> ()
                if msg.value > 100 then
                    do! typed probe <! msg
                    do! Simple.Actions.unstashAll ()
                    return! handle true
                elif unstashed then
                    do! typed probe <! msg
                    return! handle true
                else
                    do! Simple.Actions.stash ()
                    return! handle false
            }
        let act = Simple.spawn tk.Sys (Simple.Props.Named "test") (Simple.NotPersisted <| handle false)

        let m1 = {value = 1}
        act.Tell(m1)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 2}
        act.Tell(m2)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m3 = {value = 101}
        act.Tell(m3)
        probe.ExpectMsg m3 |> ignore
        probe.ExpectMsg m1 |> ignore
        probe.ExpectMsg m2 |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``watch works`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = Simple.actor {
            let! _ = Simple.Actions.receiveOnly<string> ()
            return ()
        }
        let watched = Simple.spawn tk.Sys (Simple.Props.Named "watched") (Simple.NotPersisted <| otherActor ())

        let rec handle () =
            Simple.actor {
                match! Simple.Actions.receiveAny () with
                | Akkling.MessagePatterns.Terminated (act, _, _) ->
                    do! typed probe <!  (Akkling.ActorRefs.untyped act)
                    return! Simple.Actions.stop ()
                | _msg ->
                    return! handle ()
            }
        let start = Simple.actor {
            do! Simple.Actions.watch watched
            do! typed probe <! ""
            return! handle ()
        }
        let _act = Simple.spawn tk.Sys (Simple.Props.Named "test") (Simple.NotPersisted start)

        probe.ExpectMsg "" |> ignore
        (retype watched).Tell("")
        probe.ExpectMsg watched.Untyped |> ignore

[<Test>]
let ``unwatch works`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = Simple.actor {
            let! _ = Simple.Actions.receiveOnly<string> ()
            return ()
        }
        let watched = Simple.spawn tk.Sys (Simple.Props.Named "watched") (Simple.NotPersisted <| otherActor ())

        let rec handle () =
            Simple.actor {
                match! Simple.Actions.receiveAny () with
                | Akkling.MessagePatterns.Terminated (act, _, _) ->
                    do! typed probe <!  act
                    return! Simple.Actions.stop ()
                | :? string ->
                    do! Simple.Actions.unwatch watched
                    do! typed probe <! "unwatched"
                    return! handle ()
                | _msg ->
                    return! handle ()
            }
        let start = Simple.actor {
            do! Simple.Actions.watch watched
            do! typed probe <! "watched"
            return! handle ()
        }
        let act = Simple.spawn tk.Sys (Simple.Props.Named "test") (Simple.NotPersisted start)

        probe.ExpectMsg "watched" |> ignore
        (retype act).Tell("")
        probe.ExpectMsg "unwatched" |> ignore
        (retype watched).Tell("")
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``schedule works`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Simple.actor {
                let! _msg = Simple.Actions.receiveAny ()
                return! handle ()
            }
        let start = Simple.actor {
            let! _cancel = Simple.Actions.schedule (TimeSpan.FromMilliseconds 100.0) (typed probe) "message"
            do! typed probe <! "scheduled"
            return! handle ()
        }
        let _act = Simple.spawn tk.Sys (Simple.Props.Named "test") (Simple.NotPersisted start)

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
            Simple.actor {
                let! _msg = Simple.Actions.receiveAny ()
                return! handle ()
            }
        let start = Simple.actor {
            let! cancel = Simple.Actions.schedule (TimeSpan.FromMilliseconds 100.0) (typed probe) "message"
            cancel.Cancel ()
            do! typed probe <! "scheduled"
            return! handle ()
        }
        let _act = Simple.spawn tk.Sys (Simple.Props.Named "test") (Simple.NotPersisted start)

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
            Simple.actor {
                let! _msg = Simple.Actions.receiveAny ()
                return! handle ()
            }
        let start = Simple.actor {
            let! _cancel = Simple.Actions.scheduleRepeatedly delay interval (typed probe) "message"
            do! typed probe <! "scheduled"
            return! handle ()
        }
        let _act = Simple.spawn tk.Sys (Simple.Props.Named "test") (Simple.NotPersisted start)

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
            Simple.actor {
                let! _msg = Simple.Actions.receiveOnly<string> ()
                let! sender = Simple.Actions.getSender ()
                do! typed probe <! sender.Untyped
                return! handle ()
            }
        let act = Simple.spawn tk.Sys (Simple.Props.Named "test") (Simple.NotPersisted <| handle ())

        act.Tell("message", probe)
        probe.ExpectMsg probe |> ignore

[<Test>]
let ``select get's the correct selection`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let probeAct = probe :> Akka.Actor.IActorRef
        let path = probeAct.Path.ToString()

        let rec handle () =
            Simple.actor {
                let! _msg = Simple.Actions.receiveOnly<string> ()
                return! handle ()
            }
        let start = Simple.actor {
            let! selection = Simple.Actions.select path
            do! typed probe <! selection
        }
        let _act = Simple.spawn tk.Sys (Simple.Props.Named "test") (Simple.NotPersisted start)

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

        let rec handle () = Simple.actor {
            let! _  = Simple.Actions.receiveAny ()
            return! handle ()
        }

        let rec crashHandle () = Simple.actor {
            let! _  = Simple.Actions.receiveOnly<string> ()
            failwith "crashed"
            return! handle ()
        }
        let crashStart = Simple.actor {
            do! Simple.Actions.setRestartHandler (fun _ctx msg err ->
                (typed probe).Tell {msg = msg; err = err}
            )
            return! crashHandle ()
        }
        let start = Simple.actor {
            let! crasher =
                Simple.Actions.createChild (fun f ->
                    Simple.spawn f (Simple.Props.Named "crasher") (Simple.NotPersisted crashStart)
                )
            do! typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Simple.Props.Named "parent" with
                supervisionStrategy = Akkling.Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = Simple.spawn tk.Sys parentProps (Simple.NotPersisted start)

        let crasher = probe.ExpectMsg<IActorRef<obj>> ()
        (retype crasher).Tell("crash it")
        let res = probe.ExpectMsg<CrashMsg>()
        res.msg :?> string |> ignore
        res.err :?> Exception |> ignore

[<Test>]
let ``crash handler is invoked if actor crashes before calling receive`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let mutable alreadyCrashed = 0

        let rec checkCrash () =
            if alreadyCrashed = 0 then
                Threading.Interlocked.Increment(&alreadyCrashed) |> ignore
                failwith "Initial crash"

        let rec handle () = Simple.actor {
            let! _  = Simple.Actions.receiveAny ()
            return! handle ()
        }

        let crashStart = Simple.actor {
            do! Simple.Actions.setRestartHandler (fun _ctx msg err ->
                (typed probe).Tell {msg = msg; err = err}
            )
            checkCrash ()
            return! handle()
        }
        let start = Simple.actor {
            let! crasher =
                Simple.Actions.createChild (fun f ->
                    Simple.spawn f (Simple.Props.Named "crasher") (Simple.NotPersisted crashStart)
                )
            do! typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Simple.Props.Named "parent" with
                supervisionStrategy = Akkling.Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = Simple.spawn tk.Sys parentProps (Simple.NotPersisted start)

        let _crasher = probe.ExpectMsg<IActorRef<obj>> ()
        let _res = probe.ExpectMsg<CrashMsg>()
        ignore _res

[<Test>]
let ``crash handler is not invoked if handler is cleared`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () = Simple.actor {
            let! _  = Simple.Actions.receiveAny ()
            return! handle ()
        }

        let crashStart = Simple.actor {
            do! Simple.Actions.setRestartHandler (fun _ctx msg err ->
                (typed probe).Tell {msg = msg; err = err}
            )
            do! Simple.Actions.clearRestartHandler ()
            return! handle()
        }

        let start = Simple.actor {
            let! crasher =
                Simple.Actions.createChild (fun f ->
                    Simple.spawn f (Simple.Props.Named "crasher") (Simple.NotPersisted crashStart)
                )
            do! typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Simple.Props.Named "parent" with
                supervisionStrategy = Akkling.Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = Simple.spawn tk.Sys parentProps (Simple.NotPersisted start)

        let crasher = probe.ExpectMsg<IActorRef<obj>> ()
        (retype crasher).Tell(Akka.Actor.Kill.Instance)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

