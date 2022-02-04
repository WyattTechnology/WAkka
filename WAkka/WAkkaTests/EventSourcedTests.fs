module WAkkaTests.EventSourcedTests

open System

open NUnit.Framework
open FsUnitTyped

open Akkling

open WAkka
open WAkka.Common
open WAkka.Simple.Actions
open WAkka.EventSourced
open WAkka.Spawn

type Msg = {value: int}

[<Test>]
let ``spawn with name`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec start =
            actor {
                do! persist (
                    let rec handle () = Simple.actor {
                        let! msg = receiveOnly<Msg> ()
                        do! (typed probe) <! msg
                        return! handle()
                    }
                    handle ()
                )
            }
        let act = spawn tk.Sys (Props.Named "test") (eventSourced start)

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

        let rec start =
            actor {
                do! persist (
                    let rec handle () =
                        Simple.actor {
                            let! msg = receiveOnly<Msg> ()
                            do! typed probe <! msg
                            return! handle ()
                        }
                    handle ()
                )
            }
        let act = spawn tk.Sys Props.Anonymous (eventSourced start)

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
            actor {
                let! act = getActor ()
                do! typed probe <! act
            }
        let act = spawn tk.Sys (Props.Named "test") (eventSourced <| handle ())

        probe.ExpectMsg act |> ignore

[<Test>]
let ``get actor context gives correct actor`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! act = unsafeGetActorCtx ()
                do! typed probe <! act.Self
            }
        let act = spawn tk.Sys (Props.Named "test") (eventSourced <| handle ())

        probe.ExpectMsg (untyped act) |> ignore

[<Test>]
let ``stop action stops the actor`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                do! persist( Simple.actor {
                    let! _msg = receiveOnly<Msg> ()
                    return ()
                })
                do! stop ()
                // The actor should stop on the previous line so this message should never be sent
                do! typed probe <! "should not get this"
            }
        let act = spawn tk.Sys (Props.Named "test") (eventSourced <| handle ())

        tk.Watch (untyped act) |> ignore
        let m1 = {value = 1234}
        act.Tell(m1, Akka.Actor.ActorRefs.NoSender)
        tk.ExpectTerminated (untyped act) |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``stop action in perist stops the actor`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                do! persist (Simple.actor {
                    let! _msg = receiveOnly<Msg> ()
                    do! stop ()
                    do! typed probe <! "should not get this"
                    return ()
                })
                // The actor should stop on the previous line so this message should never be sent
            }
        let act = spawn tk.Sys (Props.Named "test") (eventSourced <| handle ())

        tk.Watch (untyped act) |> ignore
        let m1 = {value = 1234}
        act.Tell(m1, Akka.Actor.ActorRefs.NoSender)
        tk.ExpectTerminated (untyped act) |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``restart after stop results in stopped actor`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let sent num = $"should get this first time({num})"

        let rec handle num =
            actor {
                do! persist (Simple.actor {
                        do! typed probe <! sent num
                        let! _ = receiveAny ()
                        do! stop ()
                        return ()
                    })
                do! typed probe <! $"should not get this ({num})"
                // The actor should stop on the previous line so this message should never be sent
            }
        let act = spawn tk.Sys (Props.Named "test") (eventSourced <| handle 1)

        tk.Watch (untyped act) |> ignore
        probe.ExpectMsg (sent 1) |> ignore
        let m1 = {value = 1234}
        act.Tell(m1, Akka.Actor.ActorRefs.NoSender)
        tk.ExpectTerminated (untyped act) |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let act2 = spawn tk.Sys (Props.Named "test") (eventSourced <| handle 2)
        tk.Watch (untyped act2) |> ignore
        tk.ExpectTerminated (untyped act2, timeout = TimeSpan.FromSeconds 30.0) |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``create actor can create an actor`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec child = actor {
            do! (typed probe) <! "created"
        }

        let rec handle () =
            actor {
                let! _newAct = createChild (fun parent ->
                    spawn parent Props.Anonymous (eventSourced child)
                )
                let! _ = persist(receiveAny ())
                return ()
            }
        let _act : ActorRefs.IActorRef<Msg> =
            spawn tk.Sys (Props.Named "test") (eventSourced <| handle ())

        probe.ExpectMsg "created" |> ignore

[<Test>]
let ``watch works`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = actor {
            let! _ = persist(receiveOnly<string> ())
            return ()
        }
        let watched = spawn tk.Sys (Props.Named "watched") (eventSourced <| otherActor ())

        let rec handle () =
            actor {
                match! persist(receiveAny ()) with
                | MessagePatterns.Terminated (act, _, _) ->
                    do! typed probe <!  act
                    return! stop ()
                | _msg ->
                    return! handle ()
            }
        let start = actor {
            do! watch watched
            do! typed probe <! ""
            return! handle ()
        }
        let _act = spawn tk.Sys (Props.Named "test") (eventSourced start)

        probe.ExpectMsg "" |> ignore
        (retype watched).Tell("", Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg watched |> ignore

[<Test>]
let ``unwatch works`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = actor {
            let! _ = persist(receiveOnly<string> ())
            return ()
        }
        let watched = spawn tk.Sys (Props.Named "watched") (eventSourced <| otherActor ())

        let rec handle () =
            actor {
                let! msg = persist(receiveAny ())
                match msg :> obj with
                | :? string ->
                    do! unwatch watched
                    do! typed probe <! "unwatched"
                    return! handle ()
                | MessagePatterns.Terminated (act, _, _) ->
                    do! typed probe <!  act
                    return! stop ()
                | _msg ->
                    return! handle ()
            }
        let start = actor {
            do! watch watched
            do! typed probe <! "watched"
            return! handle ()
        }
        let act = spawn tk.Sys (Props.Named "test") (eventSourced start)

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
            actor {
                let! _msg = persist(receiveAny ())
                return! handle ()
            }
        let start = actor {
            let! _cancel = schedule (TimeSpan.FromMilliseconds 100.0) (typed probe) "message"
            do! typed probe <! "scheduled"
            return! handle ()
        }
        let _act = spawn tk.Sys (Props.Named "test") (eventSourced start)

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
                let! _msg = persist(receiveAny ())
                return! handle ()
            }
        let start = actor {
            let! cancel = schedule (TimeSpan.FromMilliseconds 100.0) (typed probe) "message"
            cancel.Cancel ()
            do! typed probe <! "scheduled"
            return! handle ()
        }
        let _act = spawn tk.Sys (Props.Named "test") (eventSourced start)

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
                let! _msg = persist(receiveAny ())
                return! handle ()
            }
        let start = actor {
            let! _cancel = scheduleRepeatedly delay interval (typed probe) "message"
            do! typed probe <! "scheduled"
            return! handle ()
        }
        let _act = spawn tk.Sys (Props.Named "test") (eventSourced start)

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
                let! sender = persist(Simple.actor {
                    let! _ = receiveOnly<string> ()
                    return! getSender ()
                })
                do! typed probe <! ActorRefs.untyped sender
                return! handle ()
            }
        let act = spawn tk.Sys (Props.Named "test") (eventSourced <| handle ())

        act.Tell("message", probe)
        probe.ExpectMsg probe |> ignore

[<Test>]
let ``select get's the correct selection`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let probeAct = probe :> Akka.Actor.IActorRef
        let path = probeAct.Path.ToString()

        let start = actor {
            let! selection = select path
            do! typed probe <! selection
        }
        let _act = spawn tk.Sys (Props.Named "test") (eventSourced start)

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

        let rec crashHandle crash = actor {
            let! _msg = persist(receiveOnly<string> ())
            if crash then
                failwith "crashed"
            return! crashHandle false
        }
        let crashStart = actor {
            do! setRestartHandler (fun _ctx msg err ->
                (typed probe).Tell({msg = msg; err = err}, Akka.Actor.ActorRefs.NoSender)
            )
            return! crashHandle true
        }

        let start = Simple.actor {
            let! crasher =
                createChild (fun f ->
                    spawn f (Props.Named "crasher") (eventSourced crashStart)
                )
            do! typed probe <! crasher
            do! receiveOnly<unit>()
        }
        let parentProps = {
            Props.Named "parent" with
                supervisionStrategy = Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = spawn tk.Sys parentProps (notPersisted start)

        let crasher = probe.ExpectMsg<ActorRefs.IActorRef<obj>> ()
        (retype crasher).Tell("crash it", Akka.Actor.ActorRefs.NoSender)
        let res = probe.ExpectMsg<CrashMsg>()
        res.err :?> Exception |> ignore

[<Test>]
let ``crash handler is not invoked if handler is cleared`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () = actor {
            let! _  = persist(receiveAny ())
            return! handle ()
        }

        let crashStart = actor {
            do! setRestartHandler (fun _ctx msg err ->
                (typed probe).Tell({msg = msg; err = err}, Akka.Actor.ActorRefs.NoSender)
            )
            do! clearRestartHandler ()
            return! handle()
        }

        let start = actor {
            let! crasher =
                createChild (fun f ->
                    spawn f (Props.Named "crasher") (eventSourced crashStart)
                )
            do! typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Props.Named "parent" with
                supervisionStrategy = Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = spawn tk.Sys parentProps (eventSourced start)

        let crasher = probe.ExpectMsg<ActorRefs.IActorRef<obj>> ()
        (retype crasher).Tell(Akka.Actor.Kill.Instance, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

type CrashIt = CrashIt
[<Test>]
let ``state is recovered after a crash`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec crashHandle recved = actor {
            let! newRecved = persist(Simple.actor{
                match! receiveAny() with
                | :? string as msg ->
                    let newRecved = msg :: recved
                    do! typed probe <! String.concat "," newRecved
                    return newRecved
                | :? CrashIt ->
                    return failwith "Crashing"
                | _ ->
                    return recved
            })
            return! crashHandle newRecved
        }
        let crashStart = actor {
            do! setRestartHandler (fun _ctx msg err ->
                (typed probe).Tell({msg = msg; err = err}, Akka.Actor.ActorRefs.NoSender)
            )
            return! crashHandle []
        }

        let start = Simple.actor {
            let! crasher =
                createChild (fun f ->
                    spawn f (Props.Named "crasher") (eventSourced crashStart)
                )
            do! typed probe <! crasher
            let! _ = receiveAny ()
            return ()
        }
        let parentProps = {
            Props.Named "parent" with
                supervisionStrategy = Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = spawn tk.Sys parentProps (notPersisted start)

        let crasher : ActorRefs.IActorRef<string> = retype (probe.ExpectMsg<ActorRefs.IActorRef<obj>> ())
        let msg1 = "1"
        crasher.Tell(msg1, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg msg1 |> ignore
        let msg2 = "2"
        crasher.Tell(msg2, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg $"{msg2},{msg1}"|> ignore
        (retype crasher).Tell(CrashIt, Akka.Actor.ActorRefs.NoSender)
        let _res = probe.ExpectMsg<CrashMsg>()
        let msg3 = "3"
        crasher.Tell(msg3, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg $"{msg3},{msg2},{msg1}"|> ignore


