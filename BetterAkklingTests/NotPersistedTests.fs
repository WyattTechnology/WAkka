module BetterAkklingTests.NotPersistedTests

open System

open NUnit.Framework
open FsUnitTyped

open BetterAkkling
open BetterAkkling.CommonActions
open BetterAkkling.Simple
open BetterAkkling.Simple.Actions

type Msg = {value: int}

let tell (act: Akkling.ActorRefs.IActorRef<'Msg>) (msg: 'Msg) =
    act.Tell(msg, Akka.Actor.ActorRefs.NoSender)

[<Test>]
let ``spawn with name`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! msg =
                    let rec getMsg () = actor {
                        match! receiveAny () with
                        | :? Msg as msg -> return msg
                        | _ -> return! getMsg ()
                    }
                    getMsg ()
                do! Akkling.ActorRefs.typed probe <! msg
                return! handle ()
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (NotPersisted <| handle ())

        let m1 = {value = 1234}
        tell act m1
        probe.ExpectMsg m1 |> ignore

        tell (Akkling.ActorRefs.retype act) "testing 1 2 3"
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 12345}
        tell act m2
        probe.ExpectMsg m2 |> ignore

[<Test>]
let ``spawn with no name`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! msg = receiveOnly<Msg> ()
                do! Akkling.ActorRefs.typed probe <! msg
                return! handle ()
            }
        let act = spawn tk.Sys Context.Props.Anonymous (NotPersisted <| handle ())

        let m1 = {value = 1234}
        tell act m1
        probe.ExpectMsg m1 |> ignore

        tell (Akkling.ActorRefs.retype act) "testing 1 2 3"
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 12345}
        tell act m2
        probe.ExpectMsg m2 |> ignore

[<Test>]
let ``get actor gives correct actor ref`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! act = getActor ()
                do! Akkling.ActorRefs.typed probe <! (Akkling.ActorRefs.untyped act)
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (NotPersisted <| handle ())

        probe.ExpectMsg (Akkling.ActorRefs.untyped act) |> ignore

[<Test>]
let ``get actor context gives correct actor`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! act = unsafeGetActorCtx ()
                do! Akkling.ActorRefs.typed probe <! act.Self
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (NotPersisted <| handle ())

        probe.ExpectMsg (Akkling.ActorRefs.untyped act) |> ignore


[<Test>]
let ``stop action stops the actor`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! _msg = receiveOnly<Msg> ()
                do! stop ()
                // The actor should stop on the previous line so this message should never be sent
                do! Akkling.ActorRefs.typed probe <! "should not get this"
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (NotPersisted <| handle ())

        tk.Watch (Akkling.ActorRefs.untyped act) |> ignore
        let m1 = {value = 1234}
        tell act m1
        tk.ExpectTerminated (Akkling.ActorRefs.untyped act) |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``create actor can create an actor`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! self = getActor ()
                let! newAct = createChild (fun parent ->
                    let ctx = parent :?> Akka.Actor.IActorContext
                    (Akkling.ActorRefs.typed probe).Tell(ctx.Self, ctx.Self)
                    let typed : Akkling.ActorRefs.IActorRef<Msg> = Akkling.ActorRefs.retype self
                    typed
                )
                do! Akkling.ActorRefs.typed probe <! (Akkling.ActorRefs.untyped newAct)
            }
        let act : Akkling.ActorRefs.IActorRef<Msg> =
            spawn tk.Sys (Context.Props.Named "test") (NotPersisted <| handle ())

        probe.ExpectMsg (Akkling.ActorRefs.untyped act) |> ignore
        probe.ExpectMsg (Akkling.ActorRefs.untyped act) |> ignore

[<Test>]
let ``unstash one only unstashes one message at a time`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle unstashed =
            actor {
                let! msg = receiveOnly<Msg> ()
                if msg.value > 100 then
                    do! Akkling.ActorRefs.typed probe <! msg
                    do! unstashOne ()
                    return! handle true
                elif unstashed then
                    do! Akkling.ActorRefs.typed probe <! msg
                    return! handle true
                else
                    do! stash ()
                    return! handle false
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (NotPersisted <| handle false)

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
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle unstashed =
            actor {
                let! msg = receiveOnly<Msg> ()
                if msg.value > 100 then
                    do! Akkling.ActorRefs.typed probe <! msg
                    do! unstashAll ()
                    return! handle true
                elif unstashed then
                    do! Akkling.ActorRefs.typed probe <! msg
                    return! handle true
                else
                    do! stash ()
                    return! handle false
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (NotPersisted <| handle false)

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
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = actor {
            let! _ = receiveOnly<string> ()
            return ()
        }
        let watched = spawn tk.Sys (Context.Props.Named "watched") (NotPersisted <| otherActor ())

        let rec handle () =
            actor {
                match! receiveAny () with
                | Akkling.MessagePatterns.Terminated (act, _, _) ->
                    do! Akkling.ActorRefs.typed probe <!  (Akkling.ActorRefs.untyped act)
                    return! stop ()
                | _msg ->
                    return! handle ()
            }
        let start = actor {
            do! watch watched
            do! Akkling.ActorRefs.typed probe <! ""
            return! handle ()
        }
        let _act = spawn tk.Sys (Context.Props.Named "test") (NotPersisted start)

        probe.ExpectMsg "" |> ignore
        tell (Akkling.ActorRefs.retype watched) ""
        probe.ExpectMsg (Akkling.ActorRefs.untyped watched) |> ignore

[<Test>]
let ``unwatch works`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = actor {
            let! _ = receiveOnly<string> ()
            return ()
        }
        let watched = spawn tk.Sys (Context.Props.Named "watched") (NotPersisted <| otherActor ())

        let rec handle () =
            actor {
                match! receiveAny () with
                | Akkling.MessagePatterns.Terminated (act, _, _) ->
                    do! Akkling.ActorRefs.typed probe <!  act
                    return! stop ()
                | :? string ->
                    do! unwatch watched
                    do! Akkling.ActorRefs.typed probe <! "unwatched"
                    return! handle ()
                | _msg ->
                    return! handle ()
            }
        let start = actor {
            do! watch watched
            do! Akkling.ActorRefs.typed probe <! "watched"
            return! handle ()
        }
        let act = spawn tk.Sys (Context.Props.Named "test") (NotPersisted start)

        probe.ExpectMsg "watched" |> ignore
        tell (Akkling.ActorRefs.retype act) ""
        probe.ExpectMsg "unwatched" |> ignore
        tell (Akkling.ActorRefs.retype watched) ""
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``schedule works`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! _msg = receiveAny ()
                return! handle ()
            }
        let start = actor {
            let! _cancel = schedule (TimeSpan.FromMilliseconds 100.0) (Akkling.ActorRefs.typed probe) "message"
            do! Akkling.ActorRefs.typed probe <! "scheduled"
            return! handle ()
        }
        let _act = spawn tk.Sys (Context.Props.Named "test") (NotPersisted start)

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
                let! _msg = receiveAny ()
                return! handle ()
            }
        let start = actor {
            let! cancel = schedule (TimeSpan.FromMilliseconds 100.0) (Akkling.ActorRefs.typed probe) "message"
            cancel.Cancel ()
            do! Akkling.ActorRefs.typed probe <! "scheduled"
            return! handle ()
        }
        let _act = spawn tk.Sys (Context.Props.Named "test") (NotPersisted start)

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
                let! _msg = receiveAny ()
                return! handle ()
            }
        let start = actor {
            let! _cancel = scheduleRepeatedly delay interval (Akkling.ActorRefs.typed probe) "message"
            do! Akkling.ActorRefs.typed probe <! "scheduled"
            return! handle ()
        }
        let _act = spawn tk.Sys (Context.Props.Named "test") (NotPersisted start)

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
                let! _msg = receiveOnly<string> ()
                let! sender = getSender ()
                do! Akkling.ActorRefs.typed probe <! (Akkling.ActorRefs.untyped sender)
                return! handle ()
            }
        let act = spawn tk.Sys (Context.Props.Named "test") (NotPersisted <| handle ())

        act.Tell("message", probe)
        probe.ExpectMsg probe |> ignore

[<Test>]
let ``select get's the correct selection`` () =
    Akkling.TestKit.testDefault <| fun tk ->
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
            do! Akkling.ActorRefs.typed probe <! selection
        }
        let _act = spawn tk.Sys (Context.Props.Named "test") (NotPersisted start)

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
                tell (Akkling.ActorRefs.typed probe) {msg = msg; err = err}
            )
            return! crashHandle ()
        }
        let start = actor {
            let! crasher =
                createChild (fun f ->
                    spawn f (Context.Props.Named "crasher") (NotPersisted crashStart)
                )
            do! Akkling.ActorRefs.typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Context.Props.Named "parent" with
                supervisionStrategy = Akkling.Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = spawn tk.Sys parentProps (NotPersisted start)

        let crasher = probe.ExpectMsg<Akkling.ActorRefs.IActorRef<obj>> ()
        tell (Akkling.ActorRefs.retype crasher) "crash it"
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

        let rec handle () = actor {
            let! _  = receiveAny ()
            return! handle ()
        }

        let crashStart = actor {
            do! setRestartHandler (fun _ctx msg err ->
                tell (Akkling.ActorRefs.typed probe) {msg = msg; err = err}
            )
            checkCrash ()
            return! handle()
        }
        let start = actor {
            let! crasher =
                createChild (fun f ->
                    spawn f (Context.Props.Named "crasher") (NotPersisted crashStart)
                )
            do! Akkling.ActorRefs.typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Context.Props.Named "parent" with
                supervisionStrategy = Akkling.Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = spawn tk.Sys parentProps (NotPersisted start)

        let _crasher = probe.ExpectMsg<Akkling.ActorRefs.IActorRef<obj>> ()
        let _res = probe.ExpectMsg<CrashMsg>()
        ignore _res

[<Test>]
let ``crash handler is not invoked if handler is cleared`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () = actor {
            let! _  = receiveAny ()
            return! handle ()
        }

        let crashStart = actor {
            do! setRestartHandler (fun _ctx msg err ->
                tell (Akkling.ActorRefs.typed probe) {msg = msg; err = err}
            )
            do! clearRestartHandler ()
            return! handle()
        }

        let start = actor {
            let! crasher =
                createChild (fun f ->
                    spawn f (Context.Props.Named "crasher") (NotPersisted crashStart)
                )
            do! Akkling.ActorRefs.typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Context.Props.Named "parent" with
                supervisionStrategy = Akkling.Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = spawn tk.Sys parentProps (NotPersisted start)

        let crasher = probe.ExpectMsg<Akkling.ActorRefs.IActorRef<obj>> ()
        tell (Akkling.ActorRefs.retype crasher) Akka.Actor.Kill.Instance
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

