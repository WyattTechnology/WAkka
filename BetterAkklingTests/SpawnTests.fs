module BetterAkklingTests.SpawnTests

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
            Actor.actor {
                let! msg = Actor.receiveOnly<Msg> ()
                Akkling.ActorRefs.typed probe <! msg
                return! handle ()
            }
        let act = Actor.spawn tk.Sys "test" (Actor.props (handle ()))

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
            Actor.actor {
                let! msg = Actor.receiveOnly<Msg> ()
                Akkling.ActorRefs.typed probe <! msg
                return! handle ()
            }
        let act = Actor.spawnAnonymous tk.Sys (Actor.props <| handle ())

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
            Actor.actor {
                let! act = Actor.getActor ()
                Akkling.ActorRefs.typed probe <! act
            }
        let act = Actor.spawn tk.Sys "test" (Actor.props (handle ()))

        probe.ExpectMsg (Akkling.ActorRefs.untyped act) |> ignore

[<Test>]
let ``get actor context gives correct actor`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Actor.actor {
                let! act = Actor.unsafeGetActorCtx ()
                Akkling.ActorRefs.typed probe <! act.Self
            }
        let act = Actor.spawn tk.Sys "test" (Actor.props (handle ()))

        probe.ExpectMsg act |> ignore


[<Test>]
let ``stop action stops the actor`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let rec handle () =
            Actor.actor {
                let! _msg = Actor.receiveOnly<Msg> ()
                return! Actor.stop ()
            }
        let act = Actor.spawnAnonymous tk.Sys (Actor.props <| handle ())

        tk.Watch (Akkling.ActorRefs.untyped act) |> ignore
        let m1 = {value = 1234}
        act <! m1
        tk.ExpectTerminated (Akkling.ActorRefs.untyped act) |> ignore

[<Test>]
let ``create actor can create an actor`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            Actor.actor {
                let! self = Actor.getActor ()
                let! newAct = Actor.createChild (fun parent ->
                    let ctx = parent :?> Akkling.Actors.Actor<obj>
                    Akkling.ActorRefs.typed probe <! ctx.Self
                    let typed : Akkling.ActorRefs.IActorRef<Msg> = Akkling.ActorRefs.typed self
                    typed
                )
                Akkling.ActorRefs.typed probe <! newAct
            }
        let act : Akkling.ActorRefs.IActorRef<Msg> = Actor.spawnAnonymous tk.Sys (Actor.props <| handle ())
        let actObj : Akkling.ActorRefs.IActorRef<obj> = Akkling.ActorRefs.retype act

        probe.ExpectMsg actObj |> ignore
        probe.ExpectMsg act |> ignore

[<Test>]
let ``unstash one only unstashes one message at a time`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle unstashed =
            Actor.actor {
                let! msg = Actor.receiveOnly<Msg> ()
                if msg.value > 100 then
                    Akkling.ActorRefs.typed probe <! msg
                    do! Actor.unstashOne ()
                    return! handle true
                elif unstashed then
                    Akkling.ActorRefs.typed probe <! msg
                    return! handle true
                else
                    do! Actor.stash ()
                    return! handle false
            }
        let act = Actor.spawn tk.Sys "test" (Actor.props (handle false))

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
            Actor.actor {
                let! msg = Actor.receiveOnly<Msg> ()
                if msg.value > 100 then
                    Akkling.ActorRefs.typed probe <! msg
                    do! Actor.unstashAll ()
                    return! handle true
                elif unstashed then
                    Akkling.ActorRefs.typed probe <! msg
                    return! handle true
                else
                    do! Actor.stash ()
                    return! handle false
            }
        let act = Actor.spawn tk.Sys "test" (Actor.props (handle false))

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

        let rec otherActor () = Actor.actor {
            let! _ = Actor.receiveOnly<string> ()
            return ()
        }
        let watched = Actor.spawnAnonymous tk.Sys (Actor.props (otherActor ()))

        let rec handle () =
            Actor.actor {
                match! Actor.receive () with
                | Akkling.MessagePatterns.Terminated (act, _, _) ->
                    Akkling.ActorRefs.typed probe <!  act
                    return! Actor.stop ()
                | _msg ->
                    return! handle ()
            }
        let start = Actor.actor {
            do! Actor.watch watched
            Akkling.ActorRefs.typed probe <! ""
            return! handle ()
        }
        let _act = Actor.spawn tk.Sys "test" (Actor.props start)

        probe.ExpectMsg "" |> ignore
        Akkling.ActorRefs.retype watched <! ""
        probe.ExpectMsg watched |> ignore

[<Test>]
let ``unwatch works`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = Actor.actor {
            let! _ = Actor.receiveOnly<string> ()
            return ()
        }
        let watched = Actor.spawnAnonymous tk.Sys (Actor.props (otherActor ()))

        let rec handle () =
            Actor.actor {
                match! Actor.receive () with
                | Akkling.MessagePatterns.Terminated (act, _, _) ->
                    Akkling.ActorRefs.typed probe <!  act
                    return! Actor.stop ()
                | :? string ->
                    do! Actor.unwatch watched
                    Akkling.ActorRefs.typed probe <! "unwatched"
                    return! handle ()
                | _msg ->
                    return! handle ()
            }
        let start = Actor.actor {
            do! Actor.watch watched
            Akkling.ActorRefs.typed probe <! "watched"
            return! handle ()
        }
        let act = Actor.spawn tk.Sys "test" (Actor.props start)

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
            Actor.actor {
                let! _msg = Actor.receive ()
                return! handle ()
            }
        let start = Actor.actor {
            let! _cancel = Actor.schedule (TimeSpan.FromMilliseconds 100.0) (Akkling.ActorRefs.typed probe) "message"
            Akkling.ActorRefs.typed probe <! "scheduled"
            return! handle ()
        }
        let _act = Actor.spawn tk.Sys "test" (Actor.props start)

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
            Actor.actor {
                let! _msg = Actor.receive ()
                return! handle ()
            }
        let start = Actor.actor {
            let! cancel = Actor.schedule (TimeSpan.FromMilliseconds 100.0) (Akkling.ActorRefs.typed probe) "message"
            cancel.Cancel ()
            Akkling.ActorRefs.typed probe <! "scheduled"
            return! handle ()
        }
        let _act = Actor.spawn tk.Sys "test" (Actor.props start)

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
            Actor.actor {
                let! _msg = Actor.receive ()
                return! handle ()
            }
        let start = Actor.actor {
            let! _cancel = Actor.scheduleRepeatedly delay interval (Akkling.ActorRefs.typed probe) "message"
            Akkling.ActorRefs.typed probe <! "scheduled"
            return! handle ()
        }
        let _act = Actor.spawn tk.Sys "test" (Actor.props start)

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
            Actor.actor {
                let! _msg = Actor.receiveOnly<string> ()
                let! sender = Actor.getSender ()
                Akkling.ActorRefs.typed probe <! sender
                return! handle ()
            }
        let act = Actor.spawn tk.Sys "test" (Actor.props (handle ()))

        act.Tell("message", probe)
        probe.ExpectMsg probe |> ignore

[<Test>]
let ``select get's the correct selection`` () =
    Akkling.TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let probeAct = probe :> Akka.Actor.IActorRef
        let path = probeAct.Path.ToString()

        let rec handle () =
            Actor.actor {
                let! _msg = Actor.receiveOnly<string> ()
                return! handle ()
            }
        let start = Actor.actor {
            let! selection = Actor.select path
            Akkling.ActorRefs.typed probe <! selection
        }
        let _act = Actor.spawn tk.Sys "test" (Actor.props start)

        let msg = probe.ExpectMsg<Akka.Actor.ActorSelection> ()
        msg.PathString |> shouldEqual (probeAct.Path.ToStringWithoutAddress())
