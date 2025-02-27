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

module WAkkaTests.CheckpointedTests

open NUnit.Framework

open Akkling

open WAkka.Common
open WAkka.Simple

let tell (act: IActorRef<'Msg>) (msg: 'Msg) =
    act.Tell(msg, Akka.Actor.ActorRefs.NoSender)

type CrashMsg = {
    msg: obj
    err: obj
}

type CrashIt = CrashIt

[<Test>]
let ``state is recovered after a crash`` () =
    TestKit.testDefault <| fun tk ->
        let probe = tk.CreateTestProbe "probe"

        let rec crashHandle recved = actor {
            match! Receive.Any () with
            | :? string as msg ->
                let newRecved = msg :: recved
                do! typed probe <! String.concat "," newRecved
                return! crashHandle newRecved
            | :? CrashIt ->
                failwith "crashing"
            | _ ->
                return! crashHandle recved
        }
        let crashStart () = actor {
            let! _ = setRestartHandler (fun (_ctx, msg, err) ->
                tell (typed probe) {msg = msg; err = err}
            )
            return! crashHandle []
        }

        let rec handle () = actor {
            let! _  = Receive.Any ()
            return! handle ()
        }

        let start () = actor {
            let! crasher =
                createChild (fun f ->
                    Spawn.Checkpointed(f, Props.Named "crasher", crashStart)
                )
            do! typed probe <! crasher
            return! handle ()
        }
        let parentProps = {
            Props.Named "parent" with
                supervisionStrategy = Strategy.OneForOne (fun _err -> Akka.Actor.Directive.Restart) |> Some
        }
        let _parent = Spawn.Checkpointed(tk.Sys, parentProps, start)

        let crasher : IActorRef<string> = retype (probe.ExpectMsg<IActorRef<obj>> ())
        let msg1 = "1"
        tell crasher msg1
        probe.ExpectMsg msg1 |> ignore
        let msg2 = "2"
        tell crasher msg2
        probe.ExpectMsg $"{msg2},{msg1}"|> ignore
        tell (retype crasher) CrashIt
        let _res = probe.ExpectMsg<CrashMsg>()
        let msg3 = "3"
        tell crasher msg3
        probe.ExpectMsg $"{msg3},{msg2},{msg1}"|> ignore

