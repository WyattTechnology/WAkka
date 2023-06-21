module Main

open Akkling
open Microsoft.FSharp.Core

type GetInfo = GetInfo

type TestMsg = {
    index: int
    stop: Option<System.Threading.Tasks.TaskCompletionSource<unit>>
}

type Sender(recv: IActorRef<TestMsg>, numMessages: int) =
    
    inherit Akka.Actor.UntypedActor ()
        
    let ctx = Akka.Actor.UntypedActor.Context :> Akka.Actor.IActorContext
    let mutable stash = Unchecked.defaultof<Akka.Actor.IStash>

    let start = System.DateTime.Now
    let mutable finish = Unchecked.defaultof<System.DateTime>
    
    do 
        for i in 1..numMessages do
            recv <! {index = i; stop = None}
            
    let mutable nextIndex = 1
    
    override _.OnReceive (msg: obj) =
        match msg with
        | :? TestMsg as testMsg ->
            if testMsg.index = nextIndex then
                if nextIndex = numMessages then
                    finish <- System.DateTime.Now
                    nextIndex <- -1
                else
                    nextIndex <- nextIndex + 1
                stash.UnstashAll ()
            else
                stash.Stash ()
        | :? GetInfo ->
            if nextIndex  = -1 then 
                typed ctx.Sender <! (finish - start)
            else
                stash.Stash ()
        | _ ->
            ()
    
    interface Akka.Actor.IWithUnboundedStash with
        member _.Stash
            with get () = stash
            and set newStash = stash <- newStash
    
module WAkkaTest =
    open WAkka.Common
    open WAkka.Spawn
    open WAkka.Simple

    let makeActor parent =
        spawn parent Props.Anonymous (notPersisted (
            let rec handle () = actor {
                let! msg = Receive.Only<TestMsg>()
                if msg.stop.IsSome then
                    msg.stop.Value.SetResult ()
                return! handle ()
            }
            handle ()
        ))

module WAkkaHandleTest =
    open WAkka.Common
    open WAkka.Spawn
    open WAkka.Simple

    let makeActor parent =
        spawn parent Props.Anonymous (notPersisted (
            Receive.HandleMessages (fun (msg: obj) ->
                match msg with
                | :? TestMsg as testMsg -> 
                    if testMsg.stop.IsSome then
                        testMsg.stop.Value.SetResult ()
                | _ ->
                    ()
                Continue
            )
        ))

module WAkkaClosureTest =
    open WAkka.Common
    open WAkka.Spawn
    open WAkka.Simple

    let makeActor parent =
        spawn parent Props.Anonymous (notPersisted (
            Receive.HandleMessages (
                let rec handle state (msg: obj) =
                    match msg with
                    | :? TestMsg as testMsg -> 
                        if testMsg.stop.IsSome then
                            testMsg.stop.Value.SetResult ()
                    | _ ->
                        ()
                    ContinueWith (handle (state + 1))
                handle 0
            )
        ))

module WAkkaMutableTest =
    open WAkka.Common
    open WAkka.Spawn
    open WAkka.Simple

    let makeActor parent =
        spawn parent Props.Anonymous (notPersisted (
            Receive.HandleMessages (
                let mutable state = 0
                fun (msg: obj) ->
                    match msg with
                    | :? TestMsg as testMsg -> 
                        if testMsg.stop.IsSome then
                            testMsg.stop.Value.SetResult ()
                    | _ ->
                        ()
                    state <- state + 1
                    Continue
            )
        ))

module AkkaTest =
    
    type TestActor() =
        inherit Akka.Actor.UntypedActor ()
        
        let _ctx = Akka.Actor.UntypedActor.Context :> Akka.Actor.IActorContext
        
        override _.OnReceive (msg: obj) =
            
            match msg with
            | :? TestMsg as testMsg -> 
                if testMsg.stop.IsSome then
                    testMsg.stop.Value.SetResult ()
            | _ ->
                ()
            
    let makeActor (parent: Akka.Actor.IActorRefFactory) =
        let actProps = Akka.Actor.Props.Create TestActor
        parent.ActorOf actProps |> typed

module AkkaWithStateTest =
    
    type TestActor() =
        inherit Akka.Actor.UntypedActor ()
        
        let _ctx = Akka.Actor.UntypedActor.Context :> Akka.Actor.IActorContext
        
        let mutable state = 0
        override _.OnReceive (msg: obj) =
            
            match msg with
            | :? TestMsg as testMsg -> 
                state <- state + 1
                if testMsg.stop.IsSome then
                    testMsg.stop.Value.SetResult ()
                    state <- 0
            | _ ->
                ()
            
    let makeActor (parent: Akka.Actor.IActorRefFactory) =
        let actProps = Akka.Actor.Props.Create TestActor
        parent.ActorOf actProps |> typed

module AkklingTest =
    
    let makeActor parent = 
            Spawn.spawnAnonymous parent (Props.props(fun _ctx ->
                become (fun (msg:TestMsg) -> 
                    if msg.stop.IsSome then
                        msg.stop.Value.SetResult ()
                    ignored ()
                )
            ))            

module AkklingWithStateTest =
    
    let makeActor parent = 
            Spawn.spawnAnonymous parent (Props.props(fun _ctx ->
                let rec loop value (msg:TestMsg) = 
                    if msg.stop.IsSome then
                        msg.stop.Value.SetResult ()
                        become (loop 0)
                    else
                        become (loop (value + 1))
                become (loop 0)
            ))
            
type Args =
    | NoBenchmarks
    | Tests of test:List<string> 
    | NumMessages of int
    with
        interface Argu.IArgParserTemplate with
            member this.Usage =
                match this with
                | NoBenchmarks -> "Run individual tests instead of the benchmark suite"
                | Tests _ -> "List of tests to run when not running the benchmark suite ('all', 'akka', 'akkaWithState', 'akkling', 'akklingWithState', 'wakka')"
                | NumMessages _ -> "Number of messages to us in each test"
                
let parser = Argu.ArgumentParser.Create<Args>()
let args =
    try 
        parser.Parse (System.Environment.GetCommandLineArgs () |> Array.skip 1)
    with
    | :? Argu.ArguParseException as msg ->
        printfn $"{msg.Message}"
        System.Environment.Exit 1
        Unchecked.defaultof<_>

[<BenchmarkDotNet.Attributes.MemoryDiagnoser>]
type ActorBenchmarks () =
    
    let numMessages = 10000
    let msgs = [|
        for i in 1..numMessages do
            {index = i; stop = None}
    |]
    
    let mutable sys = Unchecked.defaultof<_>
    let mutable akkaActor = Unchecked.defaultof<_>
    let mutable akkaWithStateActor = Unchecked.defaultof<_>
    let mutable akklingActor = Unchecked.defaultof<_>
    let mutable akklingWithStateActor = Unchecked.defaultof<_>
    let mutable wakkaActor = Unchecked.defaultof<_>
    let mutable wakkaHandleActor = Unchecked.defaultof<_>
    let mutable wakkaClosureActor = Unchecked.defaultof<_>
    let mutable wakkaMutableActor = Unchecked.defaultof<_>
    
    let runTest testActor =
        for msg in msgs do
            testActor <! msg
        let tcs = System.Threading.Tasks.TaskCompletionSource<unit>()
        testActor <! {index = 0; stop = Some tcs}
        tcs.Task.Wait()

    [<BenchmarkDotNet.Attributes.GlobalSetup>]
    member _.Setup () =
        sys <- System.create "perf-test" (Configuration.defaultConfig ())
        akkaActor <- AkkaTest.makeActor sys
        akkaWithStateActor <- AkkaWithStateTest.makeActor sys
        akklingActor <- AkklingTest.makeActor sys
        akklingWithStateActor <- AkklingWithStateTest.makeActor sys
        wakkaActor <- WAkkaTest.makeActor sys
        wakkaHandleActor <- WAkkaHandleTest.makeActor sys
        wakkaClosureActor <- WAkkaClosureTest.makeActor sys
        wakkaMutableActor <- WAkkaMutableTest.makeActor sys
        
    [<BenchmarkDotNet.Attributes.Benchmark(Baseline = true)>]
    member _.Akka () =
        runTest akkaActor
        
    [<BenchmarkDotNet.Attributes.Benchmark>]
    member _.AkkaWithState () =
        runTest akkaWithStateActor
        
    [<BenchmarkDotNet.Attributes.Benchmark>]
    member _.Akkling () =
        runTest akklingActor
        
    [<BenchmarkDotNet.Attributes.Benchmark>]
    member _.AkklingWithState () =
        runTest akklingWithStateActor
        
    [<BenchmarkDotNet.Attributes.Benchmark>]
    member _.WAkka () =
        runTest wakkaActor
    
    [<BenchmarkDotNet.Attributes.Benchmark>]
    member _.WAkkaHandle () =
        runTest wakkaHandleActor
    
    [<BenchmarkDotNet.Attributes.Benchmark>]
    member _.WAkkaClosure () =
        runTest wakkaClosureActor
    
    [<BenchmarkDotNet.Attributes.Benchmark>]
    member _.WAkkaMutable () =
        runTest wakkaMutableActor    
        
let runNonBenchmarkTests () = 
    let numMessages = args.GetResult(Args.NumMessages, 10000)
    let tests = args.GetResult(Args.Tests, ["all"])
    
    let sys = System.create "perf-test" (Configuration.defaultConfig ())
    
    let runTest (mode: string) testActor =
        if
            tests |> List.contains (mode.ToLower())
            || tests |> List.contains "all"
        then 
            let tcs = System.Threading.Tasks.TaskCompletionSource<unit>()
            let msgs = [|
                for i in 1..numMessages do
                    {index = i; stop = None}
                {index = 0; stop = Some tcs}
            |]
            let start = System.DateTime.Now
            for msg in msgs do
                testActor <! msg
            tcs.Task.Wait()
            let timeSpan = System.DateTime.Now - start
            let rate = float numMessages/timeSpan.TotalSeconds
            printfn $"{mode}: {rate} msgs/sec"
            Some rate
        else
            None
    
    let akka = runTest "Akka" (AkkaTest.makeActor sys)
    let akkaWithState = runTest "AkkaWithState" (AkkaWithStateTest.makeActor sys)
    let akkling = runTest "Akkling" (AkklingTest.makeActor sys)
    let akklingWithState = runTest "AkklingWithState" (AkklingWithStateTest.makeActor sys)
    let wakka = runTest "WAkka" (WAkkaTest.makeActor sys)
    let wakkaHandle = runTest "WAkkaHandle" (WAkkaHandleTest.makeActor sys)
    let wakkaClosure = runTest "WAkkaClosure" (WAkkaClosureTest.makeActor sys)
    let wakkaMutable = runTest "WAkkaMutable" (WAkkaMutableTest.makeActor sys)
    
    akka |> Option.iter (fun akkaRate ->
        let diff name rate = 
            rate |> Option.iter (fun rate -> 
                let akkaDiff = rate/akkaRate * 100.0
                printfn $"{name}: {akkaDiff}%%"
            )
        diff "AkkaWithState" akkaWithState
        diff "Akkling" akkling
        diff "AkklingWithState" akklingWithState
        diff "WAkka" wakka
        diff "WAkkaHandle" wakkaHandle
        diff "WAkkaClosure" wakkaClosure
        diff "WAkkaMutable" wakkaMutable
    )

if args.Contains NoBenchmarks then
    runNonBenchmarkTests ()
else
    BenchmarkDotNet.Running.BenchmarkRunner.Run<ActorBenchmarks> () |> ignore    
        
System.Environment.Exit 0

    
    
    