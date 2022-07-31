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

module WAkkaTests.ActorResultTests


open NUnit.Framework

open Akkling

open WAkka.Common
open WAkka.Simple
open WAkka.Spawn
open WAkka.ActorResult

let actorFunctions = [|notPersisted; checkpointed|]

[<Test>]
let ``actor result: computation expression`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let act =
            spawn tk.Sys (Props.Named "ce-test") (
                makeActor (
                    let rec loop () =
                        actor {
                            let! res =
                                runActorResult (
                                    actorResult {
                                        let! msg1 = Receive.Only<Result<int, string>> ()
                                        do! ActorResult.ofActor ((typed probe) <! "intermediate message")
                                        let! msg2 = Receive.Only<Result<int, string>> ()
                                        return msg1 + msg2
                                    }
                                )
                            do! typed probe <! res
                            return! loop ()
                        }
                    loop ()
                )
            )

        tellNow (retype act) (Result<int, string>.Ok 1)
        probe.ExpectMsg "intermediate message" |> ignore
        tellNow (retype act) (Result<int, string>.Ok 2)
        probe.ExpectMsg (Result<int, string>.Ok 3) |> ignore

        tellNow (retype act) (Result<int, string>.Ok 1)
        probe.ExpectMsg "intermediate message" |> ignore
        tellNow (retype act) (Result<int, string>.Error "err1")
        probe.ExpectMsg (Result<int, string>.Error "err1") |> ignore

        tellNow (retype act) (Result<int, string>.Error "err2")
        probe.ExpectMsg (Result<int, string>.Error "err2") |> ignore

[<Test>]
let ``actor result: orElse`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let act =
            spawn tk.Sys (Props.Named "ce-test") (
                makeActor (
                    let rec loop () =
                        actor {
                            let! res =
                                Receive.Only<Result<int, string>> ()
                                |> ActorResult.orElse (Receive.Only<Result<int, string>> ())
                            do! typed probe <! res
                            return! loop ()
                        }
                    loop ()
                )
            )

        tellNow (retype act) (Result<int, string>.Ok 1)
        probe.ExpectMsg (Result<int, string>.Ok 1) |> ignore

        tellNow (retype act) (Result<int, string>.Error "err1")
        tellNow (retype act) (Result<int, string>.Ok 2)
        probe.ExpectMsg (Result<int, string>.Ok 2) |> ignore

        tellNow (retype act) (Result<int, string>.Error "err1")
        tellNow (retype act) (Result<int, string>.Error "err2")
        probe.ExpectMsg (Result<int, string>.Error "err2") |> ignore

[<Test>]
let ``actor result: orElseWith`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let act =
            spawn tk.Sys (Props.Named "ce-test") (
                makeActor (
                    let rec loop () = actor {
                        let! res =
                            Receive.Only<Result<int, string>> ()
                            |> ActorResult.orElseWith (fun err1 ->
                                tellNow (typed probe) err1
                                Receive.Only<Result<int, string>> ()
                            )
                        do! typed probe <! res
                        return! loop ()
                    }
                    loop ()
                )
            )

        tellNow (retype act) (Result<int, string>.Ok 1)
        probe.ExpectMsg (Result<int, string>.Ok 1) |> ignore

        tellNow (retype act) (Result<int, string>.Error "err1")
        probe.ExpectMsg "err1" |> ignore
        tellNow (retype act) (Result<int, string>.Ok 2)
        probe.ExpectMsg (Result<int, string>.Ok 2) |> ignore

        tellNow (retype act) (Result<int, string>.Error "err1")
        probe.ExpectMsg "err1" |> ignore
        tellNow (retype act) (Result<int, string>.Error "err2")
        probe.ExpectMsg (Result<int, string>.Error "err2") |> ignore

[<Test>]
let ``actor result: ignore`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let act =
            spawn tk.Sys (Props.Named "ce-test") (
                makeActor (
                    let rec loop () = actor {
                        let! msg = Receive.Only<Result<string, string>> () |> ActorResult.ignore
                        do! typed probe <! msg

                        return! loop ()
                    }
                    loop ()
                )
            )

        tellNow (retype act) (Result<string, string>.Ok "")
        probe.ExpectMsg (Result<unit, string>.Ok ()) |> ignore
        tellNow (retype act) (Result<string, string>.Error "err")
        probe.ExpectMsg (Result<unit, string>.Error "err") |> ignore


let testRequire makeActor require test =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let act =
            spawn tk.Sys (Props.Named "ce-test") (
                makeActor (
                    let rec loop () = actor {
                        let! msg = require
                        do! typed probe <! msg

                        return! loop ()
                    }
                    loop ()
                )
            )

        test act probe

[<Test>]
let ``actor result: requireTrue`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    testRequire
        makeActor
        (Receive.Only<bool> () |> ActorResult.requireTrue "failed to get true")
        (fun act probe ->
            tellNow (retype act) true
            probe.ExpectMsg (Result<unit, string>.Ok ()) |> ignore
            tellNow (retype act) false
            probe.ExpectMsg (Result<unit, string>.Error "failed to get true") |> ignore
        )

[<Test>]
let ``actor result: requireFalse`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    testRequire
        makeActor
        (Receive.Only<bool> () |> ActorResult.requireFalse "failed to get false")
        (fun act probe ->
            tellNow (retype act) false
            probe.ExpectMsg (Result<unit, string>.Ok ()) |> ignore
            tellNow (retype act) true
            probe.ExpectMsg (Result<unit, string>.Error "failed to get false") |> ignore
        )

type WrapOption<'a> = {
    value: Option<'a>
}

[<Test>]
let ``actor result: requireSome`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    testRequire
        makeActor
        (Receive.Only<WrapOption<int>> () |> mapResult (fun w -> w.value) |> ActorResult.requireSome "failed to get some")
        (fun act probe ->
            tellNow (retype act) {value = Some 1}
            probe.ExpectMsg (Result<int, string>.Ok 1) |> ignore
            tellNow (retype act) {value = Option<int>.None}
            probe.ExpectMsg (Result<int, string>.Error "failed to get some") |> ignore
        )

[<Test>]
let ``actor result: requireNone`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    testRequire
        makeActor
        (Receive.Only<WrapOption<int>> () |> mapResult (fun w -> w.value) |> ActorResult.requireNone "failed to get none")
        (fun act probe ->
            tellNow (retype act) {value = Option<int>.None}
            probe.ExpectMsg (Result<unit, string>.Ok ()) |> ignore
            tellNow (retype act) {value = Some 1}
            probe.ExpectMsg (Result<unit, string>.Error "failed to get none") |> ignore
        )

[<Test>]
let ``actor result: requireEqual`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    testRequire
        makeActor
        (ActorResult.requireEqual 1 (Receive.Only<int> ()) "failed to get equal")
        (fun act probe ->
            tellNow (retype act) 1
            probe.ExpectMsg (Result<unit, string>.Ok ()) |> ignore
            tellNow (retype act) 2
            probe.ExpectMsg (Result<unit, string>.Error "failed to get equal") |> ignore
        )

[<Test>]
let ``actor result: requireEqualTo`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    testRequire
        makeActor
        (ActorResult.requireEqualTo 1 "failed to get equal" (Receive.Only<int> ()))
        (fun act probe ->
            tellNow (retype act) 1
            probe.ExpectMsg (Result<unit, string>.Ok ()) |> ignore
            tellNow (retype act) 2
            probe.ExpectMsg (Result<unit, string>.Error "failed to get equal") |> ignore
        )

[<Test>]
let ``actor result: requireEmpty`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    testRequire
        makeActor
        (Receive.Only<int []> () |> ActorResult.requireEmpty "failed to get empty")
        (fun act probe ->
            tellNow (retype act) Array.empty<int>
            probe.ExpectMsg (Result<unit, string>.Ok ()) |> ignore
            tellNow (retype act) [|1|]
            probe.ExpectMsg (Result<unit, string>.Error "failed to get empty") |> ignore
        )

[<Test>]
let ``actor result: requireNotEmpty`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    testRequire
        makeActor
        (Receive.Only<int []> () |> ActorResult.requireNotEmpty "failed to get not empty")
        (fun act probe ->
            tellNow (retype act) [|1|]
            probe.ExpectMsg (Result<unit, string>.Ok ()) |> ignore
            tellNow (retype act) Array.empty<int>
            probe.ExpectMsg (Result<unit, string>.Error "failed to get not empty") |> ignore
        )

[<Test>]
let ``actor result: requireHead`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    testRequire
        makeActor
        (Receive.Only<int []> () |> ActorResult.requireHead "failed to get first")
        (fun act probe ->
            tellNow (retype act) [|1|]
            probe.ExpectMsg (Result<int, string>.Ok 1) |> ignore
            tellNow (retype act) Array.empty<int>
            probe.ExpectMsg (Result<int, string>.Error "failed to get first") |> ignore
        )

[<Test>]
let ``actor result: ofActor`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    testRequire
        makeActor
        (Receive.Only<int> () |> ActorResult.ofActor)
        (fun act probe ->
            tellNow (retype act) 1
            probe.ExpectMsg (Result<int, obj>.Ok 1) |> ignore
        )

[<Test>]
let ``actor result: ofResult`` ([<ValueSource("actorFunctions")>] makeActor: SimpleAction<unit> -> ActorType) =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let _act =
            spawn tk.Sys (Props.Named "ce-test") (
                makeActor (
                    actor {
                        let! msg = Ok 1 |> ActorResult.ofResult
                        do! typed probe <! msg
                    }
                )
            )

        probe.ExpectMsg (Result<int, obj>.Ok 1) |> ignore

