// Copyright (c) 2022, Wyatt Technology Corporation
// All rights reserved.

// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:

// 1. Redistributions of source code must retain the above copyright
// notice, this list of conditions and the following disclaimer.

// 2. Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.

// 3. Neither the name of the copyright holder nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission.

// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

module WAkkaTests.SnapshotTests

open System

open NUnit.Framework
open FsUnitTyped

open Akkling

open WAkka
open WAkka.Common
open WAkka.Simple.Actions
open WAkka.EventSourced

type Msg = {value: int}

let constAction action = fun _ -> action

[<Test>]
let ``spawn with name`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec start =
            actor {
                do!
                    persistSimple (
                        let rec handle () = Simple.actor {
                            let! msg = Receive.Only<Msg> ()
                            do! (typed probe) <! msg
                            return! handle()
                        }
                        handle ()
                    )
            }
        let act = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") start (constAction start)
        
        let m1 = {value = 1234}
        act.Tell(m1, Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg(m1, TimeSpan.FromSeconds 60.0) |> ignore

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
                do!
                    persistSimple (
                        let rec handle () =
                            Simple.actor {
                                let! msg = Receive.Only<Msg> ()
                                do! typed probe <! msg
                                return! handle ()
                            }
                        handle ()
                    )
            }
        let act = spawnSnapshots tk.Sys EventSourcedProps.Anonymous start (constAction start)

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
        let act = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") (handle ()) (constAction (handle ()))

        probe.ExpectMsg act |> ignore

[<Test>]
let ``map gives the correct result`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! act = getActor () |> mapResult (fun a -> Result<IActorRef<obj>, unit>.Ok a)
                do! ActorRefs.typed probe <! act
            }
        let act = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") (handle ())  (constAction (handle ()))

        let expected : Result<IActorRef<obj>, unit> = Ok act
        probe.ExpectMsg expected |> ignore

[<Test>]
let ``get actor context gives correct actor`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! act = unsafeGetActorCtx ()
                do! typed probe <! act.Self
            }
        let act = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") (handle ()) (constAction (handle ()))

        probe.ExpectMsg (untyped act) |> ignore

[<Test>]
let ``stop action calls stop handlers and stops the actor`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! id = setStopHandler(fun _ctx -> tellNow (typed probe) "message1")
                let! _ = setStopHandler(fun _ctx -> tellNow (typed probe) "message2")
                do! clearStopHandler id
                do! persistSimple( Simple.actor {
                    let! _msg = Receive.Only<Msg> ()
                    return ()
                })
                do! stop ()
                // The actor should stop on the previous line so this message should never be sent
                do! typed probe <! "should not get this"
            }
        let act = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") (handle ()) (constAction (handle ()))

        tk.Watch (untyped act) |> ignore
        let m1 = {value = 1234}
        act.Tell(m1, Akka.Actor.ActorRefs.NoSender)
        tk.ExpectTerminated (untyped act) |> ignore
        probe.ExpectMsg "message2" |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``stop action in perist stops the actor`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                do! persistSimple (Simple.actor {
                    let! _msg = Receive.Only<Msg> ()
                    do! stop ()
                    do! typed probe <! "should not get this"
                    return ()
                })
                // The actor should stop on the previous line so this message should never be sent
            }
        let act = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") (handle ()) (constAction (handle ()))

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
                do! persistSimple (Simple.actor {
                        do! typed probe <! sent num
                        let! _ = Receive.Any ()
                        return! stop ()
                    })
                do! typed probe <! $"should not get this ({num})"
                // The actor should stop on the previous line so this message should never be sent
            }
        let act = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") (handle 1) (constAction (handle 1))

        tk.Watch (untyped act) |> ignore
        probe.ExpectMsg (sent 1) |> ignore
        let m1 = {value = 1234}
        act.Tell(m1, Akka.Actor.ActorRefs.NoSender)
        tk.ExpectTerminated (untyped act) |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let act2 = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") (handle 2)  (constAction (handle 2))
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
                    spawnSnapshots parent EventSourcedProps.Anonymous child (constAction child)
                )
                let! _ = persistSimple(Receive.Any ())
                return! handle ()
            }
        let _act : ActorRefs.IActorRef<Msg> =
            spawnSnapshots tk.Sys (EventSourcedProps.Named "snap-test-1") (handle ()) (constAction (handle ()))

        probe.ExpectMsg("created") |> ignore

[<Test>]
let ``watch works`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = actor {
            let! _ = persistSimple(Receive.Only<string> ())
            return ()
        }
        let watched = spawnSnapshots tk.Sys (EventSourcedProps.Named "watched") (otherActor ())  (constAction (otherActor ()))

        let rec handle () =
            actor {
                match! persistSimple(Receive.Any ()) with
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
        let _act = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") start (constAction start)

        probe.ExpectMsg "" |> ignore
        (retype watched).Tell("", Akka.Actor.ActorRefs.NoSender)
        probe.ExpectMsg watched |> ignore

[<Test>]
let ``unwatch works`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = actor {
            let! _ = persistSimple(Receive.Only<string> ())
            return ()
        }
        let watched = spawnSnapshots tk.Sys (EventSourcedProps.Named "watched") (otherActor ()) (constAction (otherActor ()))

        let rec handle () =
            actor {
                let! msg = persistSimple(Receive.Any ())
                match msg with
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
        let act = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") start (constAction start)

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
                let! _msg = persistSimple(Receive.Any ())
                return! handle ()
            }
        let start = actor {
            let! _cancel = schedule (TimeSpan.FromMilliseconds 100.0) (typed probe) "message"
            do! typed probe <! "scheduled"
            return! handle ()
        }
        let _act = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") start (constAction start)

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
                let! _msg = persistSimple(Receive.Any ())
                return! handle ()
            }
        let start = actor {
            let! cancel = schedule (TimeSpan.FromMilliseconds 100.0) (typed probe) "message"
            cancel.Cancel ()
            do! typed probe <! "scheduled"
            return! handle ()
        }
        let _act = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") start (constAction start)

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
                let! _msg = persistSimple(Receive.Any ())
                return! handle ()
            }
        let start = actor {
            let! _cancel = scheduleRepeatedly delay interval (typed probe) "message"
            do! typed probe <! "scheduled"
            return! handle ()
        }
        let _act = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") start (constAction start)

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
                let! sender = persistSimple(Simple.actor {
                    let! _ = Receive.Only<string> ()
                    return! getSender ()
                })
                do! typed probe <! ActorRefs.untyped sender
                return! handle ()
            }
        let act = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") (handle ()) (constAction (handle ()))

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
        let _act = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") start (constAction start)

        let msg = probe.ExpectMsg<Akka.Actor.ActorSelection> ()
        msg.PathString |> shouldEqual (probeAct.Path.ToStringWithoutAddress())

[<Test>]
let ``for loop runs expected number of times`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let indexes = [1..10]
        let start = actor {
            for i in indexes do
                do! typed probe <! $"{i}"
            do! typed probe <! "done"
        }
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)
        let _act = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") start (constAction start)

        for i in indexes do
            probe.ExpectMsg $"{i}" |> ignore
        probe.ExpectMsg "done" |> ignore


[<Test>]
let ``map array process all elements`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let start = actor {
            let! res =  [|1; 2; 3|] |> mapArray (fun i -> actor{return (i + 1)})
            do! typed probe <! res
        }
        let _act = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") start (constAction start)

        probe.ExpectMsg [|2; 3; 4|] |> ignore

[<Test>]
let ``map list process all elements`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let start = actor {
            let! res =  [1; 2; 3] |> mapList (fun i -> actor{return (i + 1)})
            do! typed probe <! res
        }
        let _act = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") start (constAction start)

        probe.ExpectMsg [2; 3; 4] |> ignore

[<Test>]
let ``foldActions processes all elements`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let values = [1; 2; 3]
        let start = actor {
            let actions = values |> List.map actor.Return
            let! res =  (0, actions) ||> foldActions (fun i r -> actor{return (r + i)})
            do! typed probe <! res
        }
        let _act = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") start (constAction start)

        probe.ExpectMsg (List.sum values) |> ignore

[<Test>]
let ``foldValues processes all elements`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let values = [1; 2; 3]
        let start = actor {
            let! res =  (0, values) ||> foldValues (fun i r -> actor{return (r + i)})
            do! typed probe <! res
        }
        let _act = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") start (constAction start)

        probe.ExpectMsg (List.sum values) |> ignore

[<Test>]
let ``state is recovered when actor starts again using custom persistence id`` () =
    TestKit.testDefault <| fun tk ->
        let action = actor {
            let rec inner state = Simple.actor {
                match! Receive.Only<string>() with
                | "get" ->
                    let! sender = getSender()
                    do! sender <! state
                    return! inner state
                | add ->
                    return add
            }
            let rec outer state = actor {
                let! add = persistSimple (inner state)
                return! outer (state + add)
            }
            return! outer ""
        }
        
        let act1 = spawnSnapshots tk.Sys (EventSourcedProps.PersistenceId("test-id", actorName = "act1")) action  (constAction action)
        tellNow act1 "1"
        tellNow act1 "2"
        tellNow act1 "3"
        let res1 = (retype act1).Ask<string>("get", Some (TimeSpan.FromMilliseconds 500.0)) |> Async.RunSynchronously
        res1 |> shouldEqual "123"
        tk.Watch (untyped act1) |> ignore
        tellNow (retype act1) Akka.Actor.PoisonPill.Instance
        tk.ExpectTerminated (untyped act1) |> ignore //make sure actor stops before starting new one
        
        let act2 = spawnSnapshots tk.Sys (EventSourcedProps.PersistenceId("test-id", "act2")) action  (constAction action)
        let res2 = (retype act2).Ask<string>("get", Some (TimeSpan.FromMilliseconds 500.0)) |> Async.RunSynchronously
        res2 |> shouldEqual "123"
        
[<Test>]
let ``state is recovered when actor starts again using default persistence id`` () =
    TestKit.testDefault <| fun tk ->
        let action = actor {
            let rec inner state = Simple.actor {
                match! Receive.Only<string>() with
                | "get" ->
                    let! sender = getSender()
                    do! sender <! state
                    return! inner state
                | add ->
                    return add
            }
            let rec outer state = actor {
                let! add = persistSimple (inner state)
                return! outer (state + add)
            }
            return! outer ""
        }
        
        let act1 = spawnSnapshots tk.Sys (EventSourcedProps.Named "test-id") action (constAction action)
        tellNow act1 "1"
        tellNow act1 "2"
        tellNow act1 "3"
        let res1 = (retype act1).Ask<string>("get", Some (TimeSpan.FromMilliseconds 500.0)) |> Async.RunSynchronously
        res1 |> shouldEqual "123"
        tk.Watch (untyped act1) |> ignore
        tellNow (retype act1) Akka.Actor.PoisonPill.Instance
        tk.ExpectTerminated (untyped act1) |> ignore //make sure actor stops before starting new one
        
        let act2 = spawnSnapshots tk.Sys (EventSourcedProps.Named "test-id") action (constAction action)
        let res2 = (retype act2).Ask<string>("get", Some (TimeSpan.FromMilliseconds 500.0)) |> Async.RunSynchronously
        res2 |> shouldEqual "123"
        
type CrashMsg = {
    msg: obj
    err: obj
}

type CrashIt = CrashIt

[<Test>]
let ``state is recovered after a crash`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"
        let events = tk.CreateTestProbe ()
        
        let rec crashHandle recved = actor {
            let! res = persist(Simple.actor{
                match! Receive.Any() with
                | :? string as msg ->
                    let newRecved = msg :: recved
                    do! typed probe <! String.concat "," newRecved
                    return newRecved
                | :? CrashIt ->
                    return failwith "Crashing"
                | _ ->
                    return recved
            })
            match res with
            | ActionResult newRecved -> 
                return! crashHandle newRecved
            | other ->
                tellNow (typed events) other
                return! crashHandle recved
        }
        let crashStart = actor {
            let! _ = setRestartHandler (fun (_ctx, msg, err) ->
                (typed probe).Tell({msg = msg; err = err}, Akka.Actor.ActorRefs.NoSender)
            )
            return! crashHandle []
        }

        let start = Simple.actor {
            let! crasher =
                createChild (fun f ->
                    spawnSnapshots f (EventSourcedProps.Named "crasher") crashStart (constAction crashStart)
                )
            do! typed probe <! crasher
            let! _ = Receive.Any ()
            return ()
        }
        let parentProps = {
            Common.Props.Named "parent" with
                supervisionStrategy = Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = Simple.spawnNotPersisted tk.Sys parentProps start

        let crasher : ActorRefs.IActorRef<string> = retype (probe.ExpectMsg<ActorRefs.IActorRef<obj>> ())
        events.ExpectMsg PersistResult<List<string>>.RecoveryDone |> ignore
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
        events.ExpectMsg PersistResult<List<string>>.RecoveryDone |> ignore

[<Test>]
let ``state is recovered after a crash with simple persist`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec crashHandle recved = actor {
            let! newRecved = persistSimple(Simple.actor{
                match! Receive.Any() with
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
            let! _ = setRestartHandler (fun (_ctx, msg, err) ->
                (typed probe).Tell({msg = msg; err = err}, Akka.Actor.ActorRefs.NoSender)
            )
            return! crashHandle []
        }

        let start = Simple.actor {
            let! crasher =
                createChild (fun f ->
                    spawnSnapshots f (EventSourcedProps.Named "crasher") crashStart (constAction crashStart)
                )
            do! typed probe <! crasher
            let! _ = Receive.Any ()
            return ()
        }
        let parentProps = {
            Common.Props.Named "parent" with
                supervisionStrategy = Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = Simple.spawnNotPersisted tk.Sys parentProps start

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

//TODO: Needs tests for persistence rejection and recovery failure

let recoveryTestAction probe = actor {
    let! res1 = isRecovering ()
    if res1 then
        do! probe <! "Was recovering at start"
    let! recDone = persist(Simple.actor {return ()})
    if recDone = RecoveryDone then
        do! probe <! "Got RecoveryDone"
    let! res2 = isRecovering ()
    if not res2 then
        do! probe <! "Was not recovering after RecoveryDone"
}

[<Test>]
let ``isRecovering gives correct results`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"
        
        let action = recoveryTestAction (typed probe)
        let _act = spawnSnapshots tk.Sys (EventSourcedProps.Named "test") action (constAction action)

        probe.ExpectMsg "Was recovering at start" |> ignore
        probe.ExpectMsg "Got RecoveryDone" |> ignore
        probe.ExpectMsg "Was not recovering after RecoveryDone" |> ignore

type TestActorClass (recv: IActorRef<string>) = inherit EventSourcedSnapshotActor<obj> (recoveryTestAction recv, constAction (recoveryTestAction recv))

[<Test>]
let ``Actor class: isRecovering gives correct results`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"
        let recv : IActorRef<string> = typed probe
        
        let props = Akka.Actor.Props.Create<TestActorClass> recv
        let _act = tk.ActorOf props

        probe.ExpectMsg "Was recovering at start" |> ignore
        probe.ExpectMsg "Got RecoveryDone" |> ignore
        probe.ExpectMsg "Was not recovering after RecoveryDone" |> ignore
    
[<Test>]
let ``state is recovered when actor starts again with snapshot`` () =
    TestKit.testDefault <| fun tk ->
        let cur = Environment.CurrentDirectory
        IO.Directory.Delete (IO.Path.Combine(cur, "snapshots"), true)
        let action initState = actor {
            let rec inner state = Simple.actor {
                match! Receive.Only<string>() with
                | "get" ->
                    let! sender = getSender()
                    do! sender <! state
                    return! inner state
                | add ->
                    return add
            }
            let rec outer state = actor {
                let! add = persistSimple (inner state)
                let newState = state + add
                do! saveSnapshot newState
                return! outer newState
            }
            return! outer initState
        }
        
        let probe = tk.CreateTestProbe "probe"
        let handleSnapshot state =
            tellNow (typed probe) state
            action state
        let act1 = spawnSnapshots tk.Sys (EventSourcedProps.PersistenceId("snapshot-id", actorName = "act1")) (action "") handleSnapshot
        probe.ExpectNoMsg(TimeSpan.FromMilliseconds 200.0)
        tellNow act1 "1"
        tellNow act1 "2"
        tellNow act1 "3"
        let res1 = (retype act1).Ask<string>("get", Some (TimeSpan.FromMilliseconds 500.0)) |> Async.RunSynchronously
        res1 |> shouldEqual "123"
        tk.Watch (untyped act1) |> ignore
        tellNow (retype act1) Akka.Actor.PoisonPill.Instance
        tk.ExpectTerminated (untyped act1) |> ignore //make sure actor stops before starting new one
        
        let act2 = spawnSnapshots tk.Sys (EventSourcedProps.PersistenceId("snapshot-id", "act2")) (action "") handleSnapshot
        probe.ExpectMsg "123" |> ignore
        let res2 = (retype act2).Ask<string>("get", Some (TimeSpan.FromMilliseconds 500.0)) |> Async.RunSynchronously
        res2 |> shouldEqual "123"

[<Test>]
let ``message are deleted by delete messages`` () =
    TestKit.testDefault <| fun tk ->
        let cur = Environment.CurrentDirectory
        IO.Directory.Delete (IO.Path.Combine(cur, "snapshots"), true)
        let action initState = actor {
            let rec inner state = Simple.actor {
                match! Receive.Only<string>() with
                | "get" ->
                    let! sender = getSender()
                    do! sender <! state
                    return! inner state
                | add ->
                    return add
            }
            let rec outer state = actor {
                let! add = persistSimple (inner state)
                let newState = state + add
                let! seqNum = getLastSequenceNumber()
                do! deleteEvents seqNum
                return! outer newState
            }
            return! outer initState
        }
        
        let probe = tk.CreateTestProbe "probe"
        let handleSnapshot state =
            tellNow (typed probe) state
            action state
        let act1 = spawnSnapshots tk.Sys (EventSourcedProps.PersistenceId("delete-events-id", actorName = "act1")) (action "") handleSnapshot
        probe.ExpectNoMsg(TimeSpan.FromMilliseconds 200.0)
        tellNow act1 "1"
        tellNow act1 "2"
        tellNow act1 "3"
        let res1 = (retype act1).Ask<string>("get", Some (TimeSpan.FromMilliseconds 500.0)) |> Async.RunSynchronously
        res1 |> shouldEqual "123"
        tk.Watch (untyped act1) |> ignore
        tellNow (retype act1) Akka.Actor.PoisonPill.Instance
        tk.ExpectTerminated (untyped act1) |> ignore //make sure actor stops before starting new one
        
        let act2 = spawnSnapshots tk.Sys (EventSourcedProps.PersistenceId("delete-events-id", "act2")) (action "") handleSnapshot
        probe.ExpectNoMsg(TimeSpan.FromMilliseconds 200.0)
        let res2 = (retype act2).Ask<string>("get", Some (TimeSpan.FromMilliseconds 500.0)) |> Async.RunSynchronously
        res2 |> shouldEqual ""

type DeleteSnapMsg =
    | Add of string
    | DeleteSnap
    
[<Test>]
let ``snapshots are deleted by delete snapshot`` () =
    TestKit.testDefault <| fun tk ->
        let cur = Environment.CurrentDirectory
        IO.Directory.Delete (IO.Path.Combine(cur, "snapshots"), true)
        let del = tk.CreateTestProbe "del"
        let action initState = actor {
            let rec inner state = Simple.actor {
                match! Receive.Any() with
                | :? string as msg ->
                    match msg with 
                    | "get" ->
                        let! sender = getSender()
                        do! sender <! state
                        return! inner state
                    | add ->
                        return Add add
                | :? Akka.Persistence.SaveSnapshotSuccess ->
                    return DeleteSnap
                | _other ->
                    return! inner state
            }
            let rec outer state = actor {
                let! add = persistSimple (inner state)
                match add with
                | Add add -> 
                    let newState = state + add
                    do! saveSnapshot newState
                    return! outer newState
                | DeleteSnap ->
                    let! isRecovering = isRecovering()
                    if not isRecovering then 
                        let! seqNum = getLastSequenceNumber()
                        do! deleteSnapshot (seqNum - 1L)
                        do! typed del <! "deleted"
                    return! outer state
            }
            return! outer initState
        }
        
        let probe = tk.CreateTestProbe "probe"
        let handleSnapshot state =
            tellNow (typed probe) state
            action state
        let act1 = spawnSnapshots tk.Sys (EventSourcedProps.PersistenceId("delete-snapshot1-id", actorName = "act1-1")) (action "") handleSnapshot
        probe.ExpectNoMsg(TimeSpan.FromMilliseconds 200.0)
        tellNow act1 "1"
        let res1 = (retype act1).Ask<string>("get", Some (TimeSpan.FromMilliseconds 500.0)) |> Async.RunSynchronously
        res1 |> shouldEqual "1"
        del.ExpectMsg("deleted") |> ignore //have to wait for snapshot delete to be done
        tk.Watch (untyped act1) |> ignore
        tellNow (retype act1) Akka.Actor.PoisonPill.Instance
        tk.ExpectTerminated (untyped act1) |> ignore //make sure actor stops before starting new one
        
        let _act2 = spawnSnapshots tk.Sys (EventSourcedProps.PersistenceId("delete-snapshot1-id", "act2-1")) (action "") handleSnapshot
        probe.ExpectNoMsg(TimeSpan.FromMilliseconds 200.0)

[<Test>]
let ``snapshots are deleted by delete snapshots`` () =
    TestKit.testDefault <| fun tk ->
        let cur = Environment.CurrentDirectory
        IO.Directory.Delete (IO.Path.Combine(cur, "snapshots"), true)
        let del = tk.CreateTestProbe "del"
        let action initState = actor {
            let rec inner state = Simple.actor {
                match! Receive.Any() with
                | :? string as msg ->
                    match msg with 
                    | "get" ->
                        let! sender = getSender()
                        do! sender <! state
                        return! inner state
                    | add ->
                        return Add add
                | :? Akka.Persistence.SaveSnapshotSuccess ->
                    return DeleteSnap
                | _other ->
                    return! inner state
            }
            let rec outer state = actor {
                let! add = persistSimple (inner state)
                match add with
                | Add add -> 
                    let newState = state + add
                    do! saveSnapshot newState
                    return! outer newState
                | DeleteSnap ->
                    let! isRecovering = isRecovering()
                    if not isRecovering then 
                        let! seqNum = getLastSequenceNumber()
                        do! deleteSnapshots (Akka.Persistence.SnapshotSelectionCriteria seqNum)
                        do! typed del <! "deleted"
                    return! outer state
            }
            return! outer initState
        }
        
        let probe = tk.CreateTestProbe "probe"
        let handleSnapshot state =
            tellNow (typed probe) state
            action state
        let act1 = spawnSnapshots tk.Sys (EventSourcedProps.PersistenceId("delete-snapshot1-id", actorName = "act1-1")) (action "") handleSnapshot
        probe.ExpectNoMsg(TimeSpan.FromMilliseconds 200.0)
        tellNow act1 "1"
        let res1 = (retype act1).Ask<string>("get", Some (TimeSpan.FromMilliseconds 500.0)) |> Async.RunSynchronously
        res1 |> shouldEqual "1"
        del.ExpectMsg("deleted") |> ignore //have to wait for snapshot delete to be done
        tk.Watch (untyped act1) |> ignore
        tellNow (retype act1) Akka.Actor.PoisonPill.Instance
        tk.ExpectTerminated (untyped act1) |> ignore //make sure actor stops before starting new one
        
        let _act2 = spawnSnapshots tk.Sys (EventSourcedProps.PersistenceId("delete-snapshot1-id", "act2-1")) (action "") handleSnapshot
        probe.ExpectNoMsg(TimeSpan.FromMilliseconds 200.0)
