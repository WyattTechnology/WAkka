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

module WAkkaTests.SimpleTests

open System

open NUnit.Framework
open FsUnitTyped

open Akkling

open WAkka.Common
open WAkka.Simple
open WAkka.Spawn

let actorFunctions = [|notPersisted; checkpointed|]

type Msg = {value: int}

let tell (act: ActorRefs.IActorRef<'Msg>) (msg: 'Msg) =
    act.Tell(msg, Akka.Actor.ActorRefs.NoSender)

[<Test>]
let ``spawn with name`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! msg =
                    let rec getMsg () = actor {
                        match! Receive.Any () with
                        | :? Msg as msg -> return msg
                        | _ -> return! getMsg ()
                    }
                    getMsg ()
                do! ActorRefs.typed probe <! msg
                return! handle ()
            }
        let act = spawn tk.Sys (Props.Named "test") (makeActor <| handle ())

        let m1 = {value = 1234}
        tell act m1
        probe.ExpectMsg m1 |> ignore

        tell (retype act) "testing 1 2 3"
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 12345}
        tell act m2
        probe.ExpectMsg m2 |> ignore

[<Test>]
let ``spawn with no name`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! msg = Receive.Only<Msg> ()
                do! ActorRefs.typed probe <! msg
                return! handle ()
            }
        let act = spawn tk.Sys Props.Anonymous (makeActor <| handle ())

        let m1 = {value = 1234}
        tell act m1
        probe.ExpectMsg m1 |> ignore

        tell (retype act) "testing 1 2 3"
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        let m2 = {value = 12345}
        tell act m2
        probe.ExpectMsg m2 |> ignore

[<Test>]
let ``receive only ignores other messages by default`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! msg = Receive.Only<Msg> ()
                do! ActorRefs.typed probe <! msg
                let! other = Receive.Any (TimeSpan.FromMilliseconds 50.0)
                if other.IsSome then
                    do! ActorRefs.typed probe <! $"Got other message: {other.Value}"
                return! handle ()
            }
        let act = spawn tk.Sys Props.Anonymous (makeActor <| handle ())

        tell (retype act) "This should be ignored"
        let m1 = {value = 1234}
        tell (retype act) m1
        probe.ExpectMsg m1 |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``receive only with stash strategy stashes other messages`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! msg = Receive.Only<Msg> stashOthers
                do! ActorRefs.typed probe <! msg
                let! other = Receive.Any ()
                do! ActorRefs.typed probe <! other
                return! handle ()
            }
        let act = spawn tk.Sys Props.Anonymous (makeActor <| handle ())

        let otherMsg = "This should be stash"
        tell (retype act) otherMsg
        let m1 = {value = 1234}
        tell (retype act) m1
        probe.ExpectMsg m1 |> ignore
        probe.ExpectMsg otherMsg |> ignore

[<Test>]
let ``receive filter only uses the filter`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! msg = Receive.FilterOnly<Msg>(fun msg -> msg.value % 2 = 0)
                do! ActorRefs.typed probe <! msg
                return! handle ()
            }
        let act = spawn tk.Sys Props.Anonymous (makeActor <| handle ())

        let otherMsg = "This should be ignored"
        tell (retype act) otherMsg
        let m0 = {value = 1}
        tell (retype act) m0
        let m1 = {value = 2}
        tell (retype act) m1
        probe.ExpectMsg m1 |> ignore        

[<Test>]
let ``receive any with timeout will timeout`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let expected = "test"
        
        let _act = spawn tk.Sys (Props.Named "test") (makeActor (actor {
            let! msg = Receive.Any(TimeSpan.FromMilliseconds 100.0)
            match msg with
            | Some _ -> do! ActorRefs.typed probe <! msg
            | None -> do! ActorRefs.typed probe <! expected
        }))
        probe.ExpectNoMsg(TimeSpan.FromMilliseconds 100.0)
        let mutable tries = 0
        let mutable gotMsg = false
        while not gotMsg && tries < 5 do
            (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 100.0)
            try
                probe.ExpectMsg expected |> ignore
                gotMsg <- true
            with
            | _err ->
                tries <- tries + 1
        gotMsg |> shouldEqual true

[<Test>]
let ``get actor gives correct actor ref`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! act = getActor ()
                do! ActorRefs.typed probe <! (untyped act)
            }
        let act = spawn tk.Sys (Props.Named "test") (makeActor <| handle ())

        probe.ExpectMsg (untyped act) |> ignore

[<Test>]
let ``map gives the correct result`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                do! getActor() |> ignoreResult // just here to make sure that this type checks
                let! act = getActor () |> mapResult (fun a -> Result<IActorRef<obj>, unit>.Ok a)
                do! ActorRefs.typed probe <! act
            }
        let act = spawn tk.Sys (Props.Named "test") (makeActor <| handle ())

        let expected : Result<IActorRef<obj>, unit> = Ok act
        probe.ExpectMsg expected |> ignore

[<Test>]
let ``get actor context gives correct actor`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! act = unsafeGetActorCtx ()
                do! ActorRefs.typed probe <! act.Self
            }
        let act = spawn tk.Sys (Props.Named "test") (makeActor <| handle ())

        probe.ExpectMsg (untyped act) |> ignore


[<Test>]
let ``stop action stops the actor`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! _msg = Receive.Only<Msg> ()
                do! stop ()
                // The actor should stop on the previous line so this message should never be sent
                do! ActorRefs.typed probe <! "should not get this"
            }
        let act = spawn tk.Sys (Props.Named "test") (makeActor <| handle ())

        tk.Watch (untyped act) |> ignore
        let m1 = {value = 1234}
        tell act m1
        tk.ExpectTerminated (untyped act) |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``create actor can create an actor`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! self = getActor ()
                let! newAct = createChild (fun parent ->
                    let ctx = parent :?> Akka.Actor.IActorContext
                    (typed probe).Tell(ctx.Self, ctx.Self)
                    let typed : ActorRefs.IActorRef<Msg> = ActorRefs.retype self
                    typed
                )
                do! ActorRefs.typed probe <! (untyped newAct)
            }
        let act : ActorRefs.IActorRef<Msg> =
            spawn tk.Sys (Props.Named "test") (makeActor <| handle ())

        probe.ExpectMsg (untyped act) |> ignore
        probe.ExpectMsg (untyped act) |> ignore

[<Test>]
let ``unstash one only unstashes one message at a time`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle unstashed =
            actor {
                let! msg = Receive.Only<Msg> ()
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
        let act = spawn tk.Sys (Props.Named "test") (makeActor <| handle false)

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
let ``unstash all unstashes all the messages`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle unstashed =
            actor {
                let! msg = Receive.Only<Msg> ()
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
        let act = spawn tk.Sys (Props.Named "test") (makeActor <| handle false)

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
let ``watch works`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = actor {
            let! _ = Receive.Only<string> ()
            return ()
        }
        let watched = spawn tk.Sys (Props.Named "watched") (makeActor <| otherActor ())

        let rec handle () =
            actor {
                match! Receive.Any () with
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
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg "" |> ignore
        tell (retype watched) ""
        probe.ExpectMsg (untyped watched) |> ignore

[<Test>]
let ``unwatch works`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = actor {
            let! _ = Receive.Only<string> ()
            return ()
        }
        let watched = spawn tk.Sys (Props.Named "watched") (makeActor <| otherActor ())

        let rec handle () =
            actor {
                match! Receive.Any () with
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
        let act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg "watched" |> ignore
        tell (retype act) ""
        probe.ExpectMsg "unwatched" |> ignore
        tell (retype watched) ""
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``termination wait works`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = actor {
            do! Receive.Only<string> () |> ignoreResult
        }
        let watched = spawn tk.Sys (Props.Named "watched") (makeActor <| otherActor ())

        let start = actor {
            do! ActorRefs.typed probe <! ""
            do! Termination.Wait(watched, stashOthers)            
            do! ActorRefs.typed probe <! (untyped watched)
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg "" |> ignore
        tell (retype watched) ""
        probe.ExpectMsg (untyped watched) |> ignore

[<Test>]
let ``termination wait timeout works`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec otherActor () = actor {
            do! Receive.Only<string> () |> ignoreResult
        }
        let watched = spawn tk.Sys (Props.Named "watched") (makeActor <| otherActor ())

        let readyMsg = "ready"
        let doneMsg = "OnDone called"
        let others = {
            new IOtherMsgStrategy with
                member _.OtherMsg _ = actor {
                    let! sender = getSender()
                    do! sender <! readyMsg
                }
                member _.OnDone () = actor {do! ActorRefs.typed probe <! doneMsg}
        }
        let timeoutMsg = "time out"
        let start = actor {
            do! ActorRefs.typed probe <! ""
            let! res = Termination.Wait(watched, TimeSpan.FromMilliseconds 100.0, others)
            if res then
                do! ActorRefs.typed probe <! "Didn't time out"
            else
                do! ActorRefs.typed probe <! timeoutMsg
        }
        let act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg "" |> ignore
        let _msg = act.Ask("", None) |> Async.RunSynchronously
        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 99.0)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)
        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 1.0)
        probe.ExpectMsg doneMsg |> ignore
        probe.ExpectMsg timeoutMsg |> ignore

[<Test>]
let ``schedule works`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! _msg = Receive.Any ()
                return! handle ()
            }
        let start = actor {
            let! _cancel = schedule (TimeSpan.FromMilliseconds 100.0) (typed probe) "message"
            do! ActorRefs.typed probe <! "scheduled"
            return! handle ()
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg "scheduled" |> ignore
        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 99.0)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 1.0)
        probe.ExpectMsg "message" |> ignore

        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 100.0)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``scheduled messages can be cancelled`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! _msg = Receive.Any ()
                return! handle ()
            }
        let start = actor {
            let! cancel = schedule (TimeSpan.FromMilliseconds 100.0) (typed probe) "message"
            cancel.Cancel ()
            do! ActorRefs.typed probe <! "scheduled"
            return! handle ()
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg "scheduled" |> ignore
        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 100.0)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)
        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance (TimeSpan.FromMilliseconds 100.0)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``schedule repeatedly works`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let delay = TimeSpan.FromMilliseconds 100.0
        let interval = TimeSpan.FromMilliseconds 50.0

        let rec handle () =
            actor {
                let! _msg = Receive.Any ()
                return! handle ()
            }
        let start = actor {
            let! _cancel = scheduleRepeatedly delay interval (typed probe) "message"
            do! ActorRefs.typed probe <! "scheduled"
            return! handle ()
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

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
let ``get sender get's the correct actor`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () =
            actor {
                let! _msg = Receive.Only<string> ()
                let! sender = getSender ()
                do! ActorRefs.typed probe <! (untyped sender)
                return! handle ()
            }
        let act = spawn tk.Sys (Props.Named "test") (makeActor <| handle ())

        act.Tell("message", probe)
        probe.ExpectMsg probe |> ignore

[<Test>]
let ``select get's the correct selection`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let probeAct = probe :> Akka.Actor.IActorRef
        let path = probeAct.Path.ToString()

        let rec handle () =
            actor {
                let! _msg = Receive.Only<string> ()
                return! handle ()
            }
        let start = actor {
            let! selection = select path
            do! ActorRefs.typed probe <! selection
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        let msg = probe.ExpectMsg<Akka.Actor.ActorSelection> ()
        msg.PathString |> shouldEqual (probeAct.Path.ToStringWithoutAddress())

[<Test>]
let ``try without error gives correct results`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let msg = "testing 1 2 3"
        let start = actor {
            let! res = actor {
                try
                    return msg
                with
                | err ->
                    return $"got an error: {err}"
            }
            do! typed probe <! res
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg msg |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``nested try without error gives correct results`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let msg = "testing 1 2 3"
        let start = actor {
            let! res = actor {
                try
                    try
                        return msg
                    with
                    | err ->
                        return $"nested: got an error: {err}"
                with
                | err ->
                    return $"got an error: {err}"
            }
            do! typed probe <! res
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg msg |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``try with error after action gives correct results`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let msg = "testing 1 2 3"
        let doFail () = failwith msg
        let start = actor {
            let! res = actor {
                try
                    let! _ = getActor ()
                    doFail ()
                    return "missing error"
                with
                | err ->
                    return err.Message
            }
            do! typed probe <! res
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg msg |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``nested try with error after action gives correct results`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let msg = "testing 1 2 3"
        let doFail () = failwith msg
        let start = actor {
            let! res = actor {
                try
                    try
                        let! _ = getActor ()
                        doFail ()
                        return "missing error"
                    with
                    | err ->
                        return err.Message
                with
                | err ->
                    return $"Error caught at wrong level: {err}"
            }
            do! typed probe <! res
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg msg |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``try with error before action gives correct results`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let msg = "testing 1 2 3"
        let doFail () = failwith msg
        let start = actor {
            let! res = actor {
                try
                    doFail ()
                    return "missing error"
                with
                | err ->
                    return err.Message
            }
            do! typed probe <! res
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg msg |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``nested try with error before action gives correct results`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let msg = "testing 1 2 3"
        let doFail () = failwith msg
        let start = actor {
            let! res = actor {
                try
                    try
                        doFail ()
                        return "missing error"
                    with
                    | err ->
                        return err.Message
                with
                | err ->
                    return $"Error caught at wrong level: {err}"
            }
            do! typed probe <! res
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg msg |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``nested try with error after action and error in handler after action gives correct results`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let msg = "testing 1 2 3"
        let doFail () = failwith msg
        let start = actor {
            let! res = actor {
                try
                    try
                        let! _ = getActor ()
                        doFail ()
                        return "missing error"
                    with
                    | err ->
                        let! _ = getActor ()
                        failwith err.Message
                        return "missing error error"
                with
                | err ->
                    return err.Message
            }
            do! typed probe <! res
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg msg |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``nested try with error after action and error in handler before action gives correct results`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let msg = "testing 1 2 3"
        let doFail () = failwith msg
        let start = actor {
            let! res = actor {
                try
                    try
                        let! _ = getActor ()
                        doFail ()
                        return "missing error"
                    with
                    | err ->
                        failwith err.Message
                        return "missing error error"
                with
                | err ->
                    return err.Message
            }
            do! typed probe <! res
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg msg |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``nested try with error before action and error in handler after action gives correct results`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let msg = "testing 1 2 3"
        let doFail () = failwith msg
        let start = actor {
            let! res = actor {
                try
                    try
                        doFail ()
                        return "missing error"
                    with
                    | err ->
                        let! _ = getActor ()
                        failwith err.Message
                        return "missing error error"
                with
                | err ->
                    return err.Message
            }
            do! typed probe <! res
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg msg |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``nested try with error before action and error in handler before action gives correct results`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let msg = "testing 1 2 3"
        let doFail () = failwith msg
        let start = actor {
            let! res = actor {
                try
                    try
                        doFail ()
                        return "missing error"
                    with
                    | err ->
                        failwith err.Message
                        return "missing error error"
                with
                | err ->
                    return err.Message
            }
            do! typed probe <! res
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg msg |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)


[<Test>]
let ``finally without error calls handler`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"
        let final = tk.CreateTestProbe "final"

        let msg = "testing 1 2 3"
        let start = actor {
            let! res = actor {
                try
                    return msg
                finally
                    tellNow (typed final) "finally"
            }
            do! typed probe <! res
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg msg |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)
        final.ExpectMsg "finally" |> ignore

[<Test>]
let ``nested finally without error calls all handlers`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"
        let final = tk.CreateTestProbe "final"

        let msg = "testing 1 2 3"
        let start = actor {
            let! res = actor {
                try
                    try
                        return msg
                    finally
                        tellNow (typed final) "finally1"
                finally
                    tellNow (typed final) "finally2"
            }
            do! typed probe <! res
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg msg |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)
        final.ExpectMsg "finally1" |> ignore
        final.ExpectMsg "finally2" |> ignore

[<Test>]
let ``finally with error calls handler`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"
        let final = tk.CreateTestProbe "final"

        let msg = "testing 1 2 3"
        let start = actor {
            let! res = actor {
                try
                    try
                        failwith msg
                        return "missing error"
                    finally
                        tellNow (typed final) "finally"
                with
                | err ->
                    return err.Message
            }
            do! typed probe <! res
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg msg |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)
        final.ExpectMsg "finally" |> ignore

[<Test>]
let ``nested finally with error before actions calls all handlers`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"
        let final = tk.CreateTestProbe "final"

        let msg = "testing 1 2 3"
        let start = actor {
            let! res = actor {
                try
                    try
                        try
                            failwith msg
                            return "missing error"
                        finally
                            tellNow (typed final) "finally1"
                    finally
                        tellNow (typed final) "finally2"
                with
                | err ->
                    return err.Message
            }
            do! typed probe <! res
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg msg |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)
        final.ExpectMsg "finally1" |> ignore
        final.ExpectMsg "finally2" |> ignore

[<Test>]
let ``nested finally with error after actions calls all handlers`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"
        let final = tk.CreateTestProbe "final"

        let msg = "testing 1 2 3"
        let start = actor {
            let! res = actor {
                try
                    try
                        try
                            let! _ = getLogger()
                            failwith msg
                            return "missing error"
                        finally
                            tellNow (typed final) "finally1"
                    finally
                        tellNow (typed final) "finally2"
                with
                | err ->
                    return err.Message
            }
            do! typed probe <! res
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg msg |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)
        final.ExpectMsg "finally1" |> ignore
        final.ExpectMsg "finally2" |> ignore

[<Test>]
let ``finally in with calls handler`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"
        let final = tk.CreateTestProbe "final"

        let msg = "testing 1 2 3"
        let start = actor {
            let! res = actor {
                try
                    failwith msg
                    return "missing error"
                with
                | err ->
                    try
                        return ()
                    finally
                        tellNow (typed final) "finally"
                    return err.Message
            }
            do! typed probe <! res
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg msg |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)
        final.ExpectMsg "finally" |> ignore


[<Test>]
let ``using calls dispose`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"
        let final = tk.CreateTestProbe "final"

        let getDisposable () = actor {
            return {
                new IDisposable with
                    member _.Dispose () = tellNow (typed final) "disposed"
            }
        }

        let start = actor {
            use! disp = getDisposable ()
            do! typed probe <! disp
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg<IDisposable> () |> ignore
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)
        final.ExpectMsg "disposed" |> ignore

[<Test>]
let ``for loop runs expected number of times`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let indexes = [1..10]
        let start = actor {
            for i in indexes do
                do! typed probe <! $"{i}"
            do! typed probe <! "done"
        }
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        for i in indexes do
            probe.ExpectMsg $"{i}" |> ignore
        probe.ExpectMsg "done" |> ignore

[<Test>]
let ``while loop runs as long as expected`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"
        let mutable keepGoing = 0
        let mutable count = 0
        let act = spawn tk.Sys (Props.Named "test") (makeActor (actor {
             while keepGoing = 0 do
                 let! msg = Receive.Only<string> ()
                 System.Threading.Interlocked.Increment &count |> ignore
                 do! typed probe <! $"Got {msg}"
        }))
        tk.Watch (untyped act) |> ignore
        for i in 1..10 do
            tellNow act $"{i}"
            probe.ExpectMsg $"Got {i}" |> ignore
        
        System.Threading.Interlocked.Increment &keepGoing |> ignore
        tellNow act "stop"
        probe.ExpectMsg "Got stop" |> ignore
        count |> shouldEqual 11
        tk.ExpectTerminated (untyped act) |> ignore

type WhileMsg =
    | WhileValue of int
    | WhileDone

type WhileResult =
    | WhileResult of int
    | WhileDoneResult

[<Test>]
let ``execute while continues until done`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let result = tk.CreateTestProbe "result"

        let start = actor {
            do!
                (fun i -> actor {
                    do! typed result <! WhileResult i
                })
                |> executeWhile (actor {
                    match! Receive.Only<WhileMsg> () with
                    | WhileValue i -> return Some i
                    | WhileDone -> return None
                })
            do! typed result <! WhileDoneResult
        }
        let act = spawn tk.Sys (Props.Named "test") (makeActor start)

        for i in 0 .. 10 do
            tellNow act (WhileValue i)
            result.ExpectMsg (WhileResult i) |> ignore
        tellNow act WhileDone
        result.ExpectMsg WhileDoneResult |> ignore

[<Test>]
let ``map array process all elements`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let start = actor {
            let! res =  [|1; 2; 3|] |> mapArray (fun i -> actor{return (i + 1)})
            do! typed probe <! res
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg [|2; 3; 4|] |> ignore

[<Test>]
let ``map list process all elements`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let start = actor {
            let! res =  [1; 2; 3] |> mapList (fun i -> actor{return (i + 1)})
            do! typed probe <! res
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg [2; 3; 4] |> ignore

[<Test>]
let ``foldActions processes all elements`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let values = [1; 2; 3]
        let start = actor {
            let actions = values |> List.map actor.Return
            let! res =  (0, actions) ||> foldActions (fun i r -> actor{return (r + i)})
            do! typed probe <! res
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg (List.sum values) |> ignore

[<Test>]
let ``foldValues processes all elements`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let values = [1; 2; 3]
        let start = actor {
            let! res =  (0, values) ||> foldValues (fun i r -> actor{return (r + i)})
            do! typed probe <! res
        }
        let _act = spawn tk.Sys (Props.Named "test") (makeActor start)

        probe.ExpectMsg (List.sum values) |> ignore

[<Test>]
let ``sleep with stashing stashes messages`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let start = actor {
            do! sleep (TimeSpan.FromSeconds 1.0) stashOthers
            let! msg = Receive.Only<string>()
            do! typed probe <! $"got: {msg}"
        }
        let act = spawn tk.Sys (Props.Named "test") (makeActor start)

        let msg = "testing 1 2 3"
        tellNow act msg
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)
        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance(TimeSpan.FromSeconds 1.0)
        probe.ExpectMsg $"got: {msg}" |> ignore

[<Test>]
let ``sleep with ignoring ignores messages`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let start = actor {
            do! sleep (TimeSpan.FromSeconds 1.0) ignoreOthers
            let! msg = Receive.Only<string>()
            do! typed probe <! $"got: {msg}"
        }
        let act = spawn tk.Sys (Props.Named "test") (makeActor start)

        let msg = "testing 1 2 3"
        tellNow act msg
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)
        (tk.Sys.Scheduler :?> Akka.TestKit.TestScheduler).Advance(TimeSpan.FromSeconds 1.0)
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

type CrashMsg = {
    id: int
    msg: obj
    err: obj
}

[<Test>]
let ``crash handlers are invoked if actor crashes`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () = actor {
            let! _  = Receive.Any ()
            return! handle ()
        }

        let rec crashHandle () = actor {
            let! _  = Receive.Only<string> ()
            failwith "crashed"
            return! handle ()
        }
        let crashStart = actor {
            let! _ = setRestartHandler (fun (_ctx, msg, err) ->
                tell (typed probe) {id = 1; msg = msg; err = err}
            )
            let! _ = setRestartHandler (fun (_ctx, msg, err) ->
                tell (typed probe) {id = 2; msg = msg; err = err}
            )
            return! crashHandle ()
        }
        let start = actor {
            let! crasher =
                createChild (fun f ->
                    spawn f (Props.Named "crasher") (makeActor crashStart)
                )
            do! ActorRefs.typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Props.Named "parent" with
                supervisionStrategy = Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = spawn tk.Sys parentProps (makeActor start)

        let crasher = probe.ExpectMsg<ActorRefs.IActorRef<obj>> ()
        tell (retype crasher) "crash it"
        let res = probe.ExpectMsg<CrashMsg>()
        res.id |> shouldEqual 1
        let res = probe.ExpectMsg<CrashMsg>()
        res.id |> shouldEqual 2

[<Test>]
let ``crash handler is not invoked if handler is cleared`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () = actor {
            let! _  = Receive.Any ()
            return! handle ()
        }

        let rec crashHandle () = actor {
            let! _  = Receive.Only<string> ()
            failwith "crashed"
            return! handle ()
        }
        let crashStart = actor {
            let! id = setRestartHandler (fun (_ctx, msg, err) ->
                tell (typed probe) {id = 1; msg = msg; err = err}
            )
            let! _ = setRestartHandler (fun (_ctx, msg, err) ->
                tell (typed probe) {id = 2; msg = msg; err = err}
            )
            do! clearRestartHandler id
            return! crashHandle()
        }

        let start = actor {
            let! crasher =
                createChild (fun f ->
                    spawn f (Props.Named "crasher") (makeActor crashStart)
                )
            do! ActorRefs.typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Props.Named "parent" with
                supervisionStrategy = Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = spawn tk.Sys parentProps (makeActor start)

        let crasher = probe.ExpectMsg<ActorRefs.IActorRef<obj>> ()
        tell (retype crasher) "crash it"
        let res = probe.ExpectMsg<CrashMsg>()
        res.id |> shouldEqual 2
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

type StopMsg = {id: int}

[<Test>]
let ``stop handlers are invoked if actor stops`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () = actor {
            let! _  = Receive.Only<string> ()
            return! stop ()
        }
        let start = actor {
            let! _ = setStopHandler (fun _ctx ->
                tell (typed probe) {id = 1}
            )
            let! _ = setStopHandler (fun _ctx ->
                tell (typed probe) {id = 2}
            )
            return! handle ()
        }
        let actor = spawn tk.Sys (Props.Named "stopper") (makeActor start)

        tell (retype actor) "stop it"
        let res = probe.ExpectMsg<StopMsg>()
        res.id |> shouldEqual 1
        let res = probe.ExpectMsg<StopMsg>()
        res.id |> shouldEqual 2

[<Test>]
let ``stop handler is not invoked if handler is cleared`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle () = actor {
            let! _  = Receive.Only<string> ()
            return! stop ()
        }
        let start = actor {
            let! id = setStopHandler (fun _ctx ->
                tell (typed probe) {id = 1}
            )
            let! _ = setStopHandler (fun _ctx ->
                tell (typed probe) {id = 2}
            )
            do! clearStopHandler id
            return! handle ()
        }
        let actor = spawn tk.Sys (Props.Named "stopper") (makeActor start)

        tell (retype actor) "stop it"
        let res = probe.ExpectMsg<StopMsg>()
        res.id |> shouldEqual 2
        probe.ExpectNoMsg (TimeSpan.FromMilliseconds 100.0)

[<Test>]
let ``use HandleMessages at top-level`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let start = Receive.HandleMessages (fun msg ->
            tell (typed probe) msg
            HandleMessagesResult.IsDone ()
        )
        let actor = spawn tk.Sys (Props.Named "stopper") (makeActor start)
        tk.Watch (untyped actor) |> ignore
        
        tell (retype actor) "stop it"
        probe.ExpectMsg "stop it" |> ignore
        tk.ExpectTerminated (untyped actor) |> ignore

[<Test>]
let ``use HandleMessages with context at top-level`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let start = Receive.HandleMessages (fun ctx msg ->
            tell (typed probe) $"act: {ctx.GetSelf()}"
            tell (typed probe) msg
            HandleMessagesResult.IsDone ()
        )
        let actor = spawn tk.Sys (Props.Named "stopper") (makeActor start)
        tk.Watch (untyped actor) |> ignore
        
        tell (retype actor) "stop it"
        probe.ExpectMsg $"act: {actor}" |> ignore
        probe.ExpectMsg "stop it" |> ignore
        tk.ExpectTerminated (untyped actor) |> ignore

[<Test>]
let ``use HandleMessages, continue with no state changes`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let start = Receive.HandleMessages (fun msg ->
            tell (typed probe) msg
            HandleMessagesResult.Continue
        )
        let actor = spawn tk.Sys (Props.Named "stopper") (makeActor start)

        for i in 0 .. 10 do    
            tell (retype actor) $"{i}"
        for i in 0 .. 10 do           
            probe.ExpectMsg $"{i}" |> ignore

type TestMsg = {i: int}

[<Test>]
let ``use HandleMessages, continue with state changes`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle acc (msg: TestMsg) =
            let newAcc = acc + msg.i
            tell (typed probe) $"{msg.i}/{newAcc}"
            HandleMessagesResult.ContinueWith (handle newAcc)
            
        let actor = spawn tk.Sys (Props.Named "stopper") (makeActor (Receive.HandleMessages (handle 0)))

        for i in 0 .. 10 do    
            tell (retype actor) {i = i}
        let mutable acc = 0
        for i in 0 .. 10 do           
            acc <- acc + i
            probe.ExpectMsg $"{i}/{acc}" |> ignore

[<Test>]
let ``use HandleMessages, continue with action`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let handle (msg1: TestMsg) =
            HandleMessagesResult.ContinueWithAction (actor {
                let! msg2 = Receive.Only<TestMsg>()
                do! typed probe <! {i = msg1.i + msg2.i}
            }) 
            
        let actor = spawn tk.Sys (Props.Named "stopper") (makeActor (Receive.HandleMessages handle))
        tk.Watch (untyped actor) |> ignore

        let i1 = 12
        tell (retype actor) {i = i1}
        let i2 = 13
        tell (retype actor) {i = i2}
        probe.ExpectMsg {i = i1 + i2}  |> ignore

        tk.ExpectTerminated (untyped actor) |> ignore

[<Test>]
let ``HandleMessages ignores incorrect message type`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle acc (msg: TestMsg) =
            let newAcc = acc + msg.i
            tell (typed probe) $"{msg.i}/{newAcc}"
            HandleMessagesResult.ContinueWith (handle newAcc)
            
        let actor = spawn tk.Sys (Props.Named "stopper") (makeActor (Receive.HandleMessages (handle 0)))

        for i in 0 .. 10 do    
            tell (retype actor) {i = i}
            tell (retype actor) "should be ignored"
        let mutable acc = 0
        for i in 0 .. 10 do           
            acc <- acc + i
            probe.ExpectMsg $"{i}/{acc}" |> ignore
        
[<Test>]
let ``HandleMessages gives correct result when used in actor expression`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec handle prevMsg (msg: TestMsg) =
            match prevMsg with
            | Some prev -> HandleMessagesResult.IsDone (prev.i + msg.i)
            | None -> HandleMessagesResult.ContinueWith (handle (Some msg))
            
        let actor = spawn tk.Sys (Props.Named "stopper") (makeActor (actor {
            let! res = Receive.HandleMessages (handle None)
            do! typed probe <! {i = res}
        }))
        tk.Watch (untyped actor) |> ignore

        let i1 = 12
        tell (retype actor) {i = i1}
        let i2 = 13
        tell (retype actor) {i = i2}
        probe.ExpectMsg {i = i1 + i2}  |> ignore

        tk.ExpectTerminated (untyped actor) |> ignore
