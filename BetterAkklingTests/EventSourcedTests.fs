module BetterAkklingTests.EventSourcedTests

open System

open NUnit.Framework
open FsUnitTyped

open BetterAkkling
open BetterAkkling.CommonActions
open BetterAkkling.Simple.Actions
open BetterAkkling.EventSourced
open BetterAkkling.EventSourced.Actions

type Msg = {value: int}

[<Test>]
let ``spawn with name`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec start =
            actor {
                do! persist (
                    let rec handle () = Simple.actor {
                        let! msg = receiveOnly<Msg> ()
                        do! send (Akkling.ActorRefs.typed probe) msg
                        do! (Akkling.ActorRefs.typed probe) <! msg
                        return! handle()
                    }
                    handle ()
                )
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (eventSourced start)

        let m1 = {value = 1234}
        act.Tell(m1, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg m1 |> ignore

        (Akkling.ActorRefs.retype act).Tell("testing 1 2 3", Akka.Actor.ActorRefs.NoSender)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 12345}
        act.Tell(m2, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg m2 |> ignore

[<Test>]
let ``spawn with no name`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec start =
            actor {
                do! persist (
                    let rec handle () =
                        Simple.actor {
                            let! msg = receiveOnly<Msg> ()
                            do! Akkling.ActorRefs.typed probe <! msg
                            return! handle ()
                        }
                    handle ()
                )
            }
        let act = spawn tk.Sys Context.Props.Anonymous (eventSourced start)

        let m1 = {value = 1234}
        act.Tell(m1, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg m1 |> ignore

        (Akkling.ActorRefs.retype act).Tell("testing 1 2 3", Akka.Actor.ActorRefs.NoSender)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 12345}
        act.Tell(m2, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg m2 |> ignore

[<Test>]
let ``get actor gives correct actor ref`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! act = getActor ()
                do! Akkling.ActorRefs.typed probe <! act
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (eventSourced <| handle ())

        probe.ExpectMsg act |> ignore

[<Test>]
let ``get actor context gives correct actor`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! act = unsafeGetActorCtx ()
                do! Akkling.ActorRefs.typed probe <! act.Self
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (eventSourced <| handle ())

        probe.ExpectMsg act |> ignore

[<Test>]
let ``stop action stops the actor`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                do! persist( Simple.actor {
                    let! _msg = receiveOnly<Msg> ()
                    return ()
                })
                do! stop ()
                // The actor should stop on the previous line so this message should never be sent
                do! Akkling.ActorRefs.typed probe <! "should not get this"
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (eventSourced <| handle ())

        tk.Watch (Akkling.ActorRefs.untyped act) |> ignore
        let m1 = {value = 1234}
        act.Tell(m1, Akka.Actor.ActorRefs.NoSender)
        tk.ExpectTerminated (Akkling.ActorRefs.untyped act) |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``stop action in perist stops the actor`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                do! persist (Simple.actor {
                    let! _msg = receiveOnly<Msg> ()
                    do! stop ()
                    do! Akkling.ActorRefs.typed probe <! "should not get this"
                    return ()
                })
                // The actor should stop on the previous line so this message should never be sent
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (eventSourced <| handle ())

        tk.Watch (Akkling.ActorRefs.untyped act) |> ignore
        let m1 = {value = 1234}
        act.Tell(m1, Akka.Actor.ActorRefs.NoSender)
        tk.ExpectTerminated (Akkling.ActorRefs.untyped act) |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``restart after stop results in stopped actor`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let sent num = $"should get this first time({num})"

        let rec handle num =
            actor {
                do! persist (Simple.actor {
                        do! Akkling.ActorRefs.typed probe <! sent num
                        let! _ = receiveAny ()
                        do! stop ()
                        return ()
                    })
                do! Akkling.ActorRefs.typed probe <! $"should not get this ({num})"
                // The actor should stop on the previous line so this message should never be sent
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (eventSourced <| handle 1)

        tk.Watch (Akkling.ActorRefs.untyped act) |> ignore
        probe.ExpectMsg (sent 1) |> ignore
        let m1 = {value = 1234}
        act.Tell(m1, Akka.Actor.ActorRefs.NoSender)
        tk.ExpectTerminated (Akkling.ActorRefs.untyped act) |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let act2 = spawn tk.Sys (Context.Props.Named "test") (eventSourced <| handle 2)
        tk.Watch (Akkling.ActorRefs.untyped act2) |> ignore
        tk.ExpectTerminated (Akkling.ActorRefs.untyped act2, timeout = TimeSpan.FromSeconds 30.0) |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``create actor can create an actor`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec child = actor {
            do! (Akkling.ActorRefs.typed probe) <! "created"
        }

        let rec handle () =
            actor {
                let! _newAct = createChild (fun parent ->
                    spawn parent Context.Props.Anonymous (eventSourced child)
                )
                let! _ = persist(receiveAny ())
                return ()
            }
        let _act : Akkling.ActorRefs.IActorRef<Msg> =
            spawn tk.Sys (Context.Props.Named "test") (eventSourced <| handle ())

        probe.ExpectMsg "created" |> ignore

[<Test>]
let ``unstash one only unstashes one message at a time`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle unstashed = actor {
            let! newUnstashed = persist (Simple.actor {
                let! msg = receiveOnly<Msg> ()
                if msg.value > 100 then
                    do! Akkling.ActorRefs.typed probe <! msg
                    do! unstashOne ()
                    return true
                elif unstashed then
                    do! Akkling.ActorRefs.typed probe <! msg
                    return true
                else
                    do! stash ()
                    return false
            })
            return! handle newUnstashed
        }
        let act = spawn tk.Sys (Context.Props.Named "test") (eventSourced <| handle false)

        let m1 = {value = 1}
        act.Tell(m1, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 2}
        act.Tell(m2, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m3 = {value = 101}
        act.Tell(m3, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg (m3, TimeSpan.FromSeconds 30.0) |> ignore
        probe.ExpectMsg (m1, TimeSpan.FromSeconds 30.0) |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m4 = {value = 102}
        act.Tell(m4, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg m4 |> ignore
        probe.ExpectMsg m2 |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``unstash all unstashes all the messages`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle unstashed = actor {
            let! newUnstashed = persist (Simple.actor {
                let! msg = receiveOnly<Msg> ()
                if msg.value > 100 then
                    do! Akkling.ActorRefs.typed probe <! msg
                    do! unstashAll ()
                    return true
                elif unstashed then
                    do! Akkling.ActorRefs.typed probe <! msg
                    return true
                else
                    do! stash ()
                    return false
            })
            return! handle newUnstashed
        }
        let act = spawn tk.Sys (Context.Props.Named "test") (eventSourced <| handle false)

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
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = actor {
            let! _ = persist(receiveOnly<string> ())
            return ()
        }
        let watched = spawn tk.Sys (Context.Props.Named "watched") (eventSourced <| otherActor ())

        let rec handle () =
            actor {
                match! persist(receiveAny ()) with
                | Akkling.MessagePatterns.Terminated (act, _, _) ->
                    do! Akkling.ActorRefs.typed probe <!  act
                    return! stop ()
                | _msg ->
                    return! handle ()
            }
        let start = actor {
            do! watch watched
            do! Akkling.ActorRefs.typed probe <! ""
            return! handle ()
        }
        let _act = spawn tk.Sys (Context.Props.Named "test") (eventSourced start)

        probe.ExpectMsg "" |> ignore
        (Akkling.ActorRefs.retype watched).Tell("", Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg watched |> ignore

[<Test>]
let ``unwatch works`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = actor {
            let! _ = persist(receiveOnly<string> ())
            return ()
        }
        let watched = spawn tk.Sys (Context.Props.Named "watched") (eventSourced <| otherActor ())

        let rec handle () =
            actor {
                let! msg = persist(receiveAny ())
                match msg :> obj with
                | :? string ->
                    do! unwatch watched
                    do! Akkling.ActorRefs.typed probe <! "unwatched"
                    return! handle ()
                | Akkling.MessagePatterns.Terminated (act, _, _) ->
                    do! Akkling.ActorRefs.typed probe <!  act
                    return! stop ()
                | _msg ->
                    return! handle ()
            }
        let start = actor {
            do! watch watched
            do! Akkling.ActorRefs.typed probe <! "watched"
            return! handle ()
        }
        let act = spawn tk.Sys (Context.Props.Named "test") (eventSourced start)

        probe.ExpectMsg "watched" |> ignore
        (Akkling.ActorRefs.retype act).Tell("", Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg "unwatched" |> ignore
        (Akkling.ActorRefs.retype watched).Tell("", Akka.Actor.ActorRefs.NoSender)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``schedule works`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! _msg = persist(receiveAny ())
                return! handle ()
            }
        let start = actor {
            let! _cancel = schedule (TimeSpan.FromMilliseconds 100.0) (Akkling.ActorRefs.typed probe) "message"
            do! Akkling.ActorRefs.typed probe <! "scheduled"
            return! handle ()
        }
        let _act = spawn tk.Sys (Context.Props.Named "test") (eventSourced start)

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
            actor {
                let! _msg = persist(receiveAny ())
                return! handle ()
            }
        let start = actor {
            let! cancel = schedule (TimeSpan.FromMilliseconds 100.0) (Akkling.ActorRefs.typed probe) "message"
            cancel.Cancel ()
            do! Akkling.ActorRefs.typed probe <! "scheduled"
            return! handle ()
        }
        let _act = spawn tk.Sys (Context.Props.Named "test") (eventSourced start)

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
            actor {
                let! _msg = persist(receiveAny ())
                return! handle ()
            }
        let start = actor {
            let! _cancel = scheduleRepeatedly delay interval (Akkling.ActorRefs.typed probe) "message"
            do! Akkling.ActorRefs.typed probe <! "scheduled"
            return! handle ()
        }
        let _act = spawn tk.Sys (Context.Props.Named "test") (eventSourced start)

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
            actor {
                let! sender = persist(Simple.actor {
                    let! _ = receiveOnly<string> ()
                    return! getSender ()
                })
                do! Akkling.ActorRefs.typed probe <! Akkling.ActorRefs.untyped sender
                return! handle ()
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (eventSourced <| handle ())

        act.Tell("message", probe)
        probe.ExpectMsg probe |> ignore

[<Test>]
let ``select get's the correct selection`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let probeAct = probe :> Akka.Actor.IActorRef
        let path = probeAct.Path.ToString()

        let start = actor {
            let! selection = select path
            do! Akkling.ActorRefs.typed probe <! selection
        }
        let _act = spawn tk.Sys (Context.Props.Named "test") (eventSourced start)

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

        let rec crashHandle crash = actor {
            let! _msg = persist(receiveOnly<string> ())
            if crash then
                failwith "crashed"
            return! crashHandle false
        }
        let crashStart = actor {
            do! setRestartHandler (fun _ctx msg err ->
                (Akkling.ActorRefs.typed probe).Tell({msg = msg; err = err}, Akka.Actor.ActorRefs.NoSender)
            )
            return! crashHandle true
        }

        let start = Simple.actor {
            let! crasher =
                createChild (fun f ->
                    spawn f (Context.Props.Named "crasher") (eventSourced crashStart)
                )
            do! Akkling.ActorRefs.typed probe <! crasher
            do! receiveOnly<unit>()
        }
        let parentProps = {
            Context.Props.Named "parent" with
                supervisionStrategy = Akkling.Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = Simple.spawn tk.Sys parentProps (Simple.NotPersisted start)

        let crasher = probe.ExpectMsg<Akkling.ActorRefs.IActorRef<obj>> ()
        (Akkling.ActorRefs.retype crasher).Tell("crash it", Akka.Actor.ActorRefs.NoSender)
        let res = probe.ExpectMsg<CrashMsg>()
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

        let rec handle () = actor {
            let! _  = persist(receiveAny ())
            return! handle ()
        }

        let crashStart = actor {
            do! setRestartHandler (fun _ctx msg err ->
                (Akkling.ActorRefs.typed probe).Tell({msg = msg; err = err}, Akka.Actor.ActorRefs.NoSender)
            )
            checkCrash ()
            return! handle()
        }
        let start = Simple.actor {
            let! crasher =
                createChild (fun f ->
                    spawn f (Context.Props.Named "crasher") (eventSourced crashStart)
                )
            do! Akkling.ActorRefs.typed probe <! crasher
            return! receiveOnly<unit> ()
        }
        let parentProps = {
            Context.Props.Named "parent" with
                supervisionStrategy = Akkling.Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = Simple.spawn tk.Sys parentProps (Simple.NotPersisted start)

        let _crasher = probe.ExpectMsg<Akkling.ActorRefs.IActorRef<obj>> ()
        let _res = probe.ExpectMsg<CrashMsg>()
        ignore _res

[<Test>]
let ``crash handler is not invoked if handler is cleared`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () = actor {
            let! _  = persist(receiveAny ())
            return! handle ()
        }

        let crashStart = actor {
            do! setRestartHandler (fun _ctx msg err ->
                (Akkling.ActorRefs.typed probe).Tell({msg = msg; err = err}, Akka.Actor.ActorRefs.NoSender)
            )
            do! clearRestartHandler ()
            return! handle()
        }

        let start = actor {
            let! crasher =
                createChild (fun f ->
                    spawn f (Context.Props.Named "crasher") (eventSourced crashStart)
                )
            do! Akkling.ActorRefs.typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Context.Props.Named "parent" with
                supervisionStrategy = Akkling.Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = spawn tk.Sys parentProps (eventSourced start)

        let crasher = probe.ExpectMsg<Akkling.ActorRefs.IActorRef<obj>> ()
        (Akkling.ActorRefs.retype crasher).Tell(Akka.Actor.Kill.Instance, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``state is recovered after a crash`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec crashHandle recved = actor {
            let! newRecved = persist(Simple.actor{
                let! msg = receiveOnly<string>()
                let newRecved = msg :: recved
                do! Akkling.ActorRefs.typed probe <! String.concat "," newRecved
                return newRecved
            })
            return! crashHandle newRecved
        }
        let crashStart = actor {
            do! setRestartHandler (fun _ctx msg err ->
                (Akkling.ActorRefs.typed probe).Tell({msg = msg; err = err}, Akka.Actor.ActorRefs.NoSender)
            )
            return! crashHandle []
        }

        let rec handle () = Simple.actor {
            let! _  = receiveAny ()
            return! handle ()
        }

        let start = Simple.actor {
            let! crasher =
                createChild (fun f ->
                    spawn f (Context.Props.Named "crasher") (eventSourced crashStart)
                )
            do! Akkling.ActorRefs.typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Context.Props.Named "parent" with
                supervisionStrategy = Akkling.Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = Simple.spawn tk.Sys parentProps (Simple.NotPersisted start)

        let crasher : Akkling.ActorRefs.IActorRef<string> = Akkling.ActorRefs.retype (probe.ExpectMsg<Akkling.ActorRefs.IActorRef<obj>> ())
        let msg1 = "1"
        crasher.Tell(msg1, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg msg1 |> ignore
        let msg2 = "2"
        crasher.Tell(msg2, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg $"{msg2},{msg1}"|> ignore
        (Akkling.ActorRefs.retype crasher).Tell(Akka.Actor.Kill.Instance, Akka.Actor.ActorRefs.NoSender)
        let _res = probe.ExpectMsg<CrashMsg>()
        let msg3 = "3"
        crasher.Tell(msg3, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg $"{msg3},{msg2},{msg1}"|> ignore


