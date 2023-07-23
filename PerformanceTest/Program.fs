module Main

open Akkling
open Microsoft.FSharp.Core

type GetInfo = GetInfo

type StartMsg = {
    ready: System.Threading.Tasks.TaskCompletionSource<unit>
    start: System.Threading.Tasks.TaskCompletionSource<unit>
}

type TestMsg = {
    index: int
    stop: Option<System.Threading.Tasks.TaskCompletionSource<unit>>
}
   
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
            actor {
                let! msg = Receive.Only<StartMsg>()
                msg.ready.SetResult ()
                msg.start.Task.Wait()
                return! handle ()
            }
        ))

module WAkkaWhileTest =
    open WAkka.Common
    open WAkka.Spawn
    open WAkka.Simple

    let makeActor parent =
        spawn parent Props.Anonymous (notPersisted (
            let mutable state = 0
            actor {
                let! msg = Receive.Only<StartMsg>()
                msg.ready.SetResult ()
                msg.start.Task.Wait()
                while true do 
                    let! msg = Receive.Only<TestMsg>()
                    if msg.stop.IsSome then
                        msg.stop.Value.SetResult ()
                    state <- state + 1
            }
        ))

module WAkkaHandleTest =
    open WAkka.Common
    open WAkka.Spawn
    open WAkka.Simple

    let makeActor parent =
        spawn parent Props.Anonymous (notPersisted (
            let handle (msg:obj) =
                match msg with
                | :? TestMsg as testMsg -> 
                    if testMsg.stop.IsSome then
                        testMsg.stop.Value.SetResult ()
                | _ ->
                    ()
                Continue
            Receive.HandleMessages (fun (msg: obj) ->
                match msg with
                | :? StartMsg as startMsg ->
                    startMsg.ready.SetResult ()
                    startMsg.start.Task.Wait()
                    ContinueWith handle
                | _ ->
                    Continue
            )
        ))

module WAkkaClosureTest =
    open WAkka.Common
    open WAkka.Spawn
    open WAkka.Simple

    let makeActor parent =
        spawn parent Props.Anonymous (notPersisted (
            let rec handle state (msg: obj) =
                match msg with
                | :? TestMsg as testMsg -> 
                    if testMsg.stop.IsSome then
                        testMsg.stop.Value.SetResult ()
                | _ ->
                    ()
                ContinueWith (handle (state + 1))
            Receive.HandleMessages (fun (msg: obj) ->
                match msg with
                | :? StartMsg as startMsg ->
                    startMsg.ready.SetResult ()
                    startMsg.start.Task.Wait()
                    ContinueWith (handle 0)
                | _ ->
                    Continue
            )
        ))

module WAkkaMutableTest =
    open WAkka.Common
    open WAkka.Spawn
    open WAkka.Simple

    let makeActor parent =
        spawn parent Props.Anonymous (notPersisted (
            let mutable state = 0
            let handle (msg: obj) =
                match msg with
                | :? TestMsg as testMsg -> 
                    if testMsg.stop.IsSome then
                        testMsg.stop.Value.SetResult ()
                | _ ->
                    ()
                state <- state + 1
                Continue
            Receive.HandleMessages (fun (msg: obj) ->
                match msg with
                | :? StartMsg as startMsg ->
                    startMsg.ready.SetResult ()
                    startMsg.start.Task.Wait()
                    ContinueWith handle
                | _ ->
                    Continue
            )
        ))

module WAkkaSmTest =
    open WAkka.Common
    open WAkka.SimpleSM

    let makeActor parent =
        spawn parent Props.Anonymous (
            let rec handle () = smActor {
                let! msg = receive()
                if msg.stop.IsSome then
                    msg.stop.Value.SetResult ()
                return! handle ()
            }
            smActor {
                let! msg = receive()
                msg.ready.SetResult ()
                msg.start.Task.Wait()
                return! handle ()
            }
        )

module AkkaTest =
    
    type TestActor() =
        inherit Akka.Actor.UntypedActor ()
        
        let _ctx = Akka.Actor.UntypedActor.Context :> Akka.Actor.IActorContext
        
        override _.OnReceive (msg: obj) =
            
            match msg with
            | :? TestMsg as testMsg -> 
                if testMsg.stop.IsSome then
                    testMsg.stop.Value.SetResult ()
            | :? StartMsg as msg ->
                msg.ready.SetResult ()
                msg.start.Task.Wait()
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
            | :? StartMsg as msg ->
                msg.ready.SetResult ()
                msg.start.Task.Wait()
            | _ ->
                ()
            
    let makeActor (parent: Akka.Actor.IActorRefFactory) =
        let actProps = Akka.Actor.Props.Create TestActor
        parent.ActorOf actProps |> typed

module AkklingTest =
    
    let makeActor parent = 
            Spawn.spawnAnonymous parent (Props.props(fun _ctx ->
                let handle (msg:obj) = 
                    match msg with
                    | :? TestMsg as msg ->  
                        if msg.stop.IsSome then
                            msg.stop.Value.SetResult ()
                    | _ -> ()
                    ignored ()
                become (fun (msg: obj) ->
                    match msg with
                    | :? StartMsg as msg ->  
                        msg.ready.SetResult()
                        msg.start.Task.Wait()
                        become handle
                    | _ ->
                        ignored()
                )                
            ))            

module AkklingWithStateTest =
    
    let makeActor parent = 
            Spawn.spawnAnonymous parent (Props.props(fun _ctx ->
                let rec loop value (msg:obj) = 
                    match msg with
                    | :? TestMsg as msg ->  
                        if msg.stop.IsSome then
                            msg.stop.Value.SetResult ()
                            become (loop 0)
                        else
                            become (loop (value + 1))
                    | _ ->
                        ignored()
                become (fun (msg: obj) ->
                    match msg with
                    | :? StartMsg as msg ->  
                        msg.ready.SetResult()
                        msg.start.Task.Wait()
                        become (loop 0)
                    | _ ->
                        ignored ()
                )                
            ))
            
module AkklingWithMutableStateTest =
    
    let makeActor parent = 
            Spawn.spawnAnonymous parent (Props.props(fun _ctx ->
                let mutable state = 0
                let rec loop (msg:obj) = 
                    match msg with
                    | :? TestMsg as msg ->  
                        if msg.stop.IsSome then
                            msg.stop.Value.SetResult ()
                            state <- 0
                        else
                            state <- state + 1
                    | _ ->
                        ()
                    ignored()
                become (fun (msg: obj) ->
                    match msg with
                    | :? StartMsg as msg ->  
                        msg.ready.SetResult()
                        msg.start.Task.Wait()
                        become loop
                    | _ ->
                        ignored ()
                )                
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

type StartStop = {
    start: System.Threading.Tasks.TaskCompletionSource<unit>
    stop: System.Threading.Tasks.TaskCompletionSource<unit>
}

[<BenchmarkDotNet.Attributes.MemoryDiagnoser>]
type ActorBenchmarks () =
    
    let numMessages = 3000000
    let msgs = [|
        for i in 1..numMessages do
            {index = i; stop = None}
    |]
    
    let mutable sys = Unchecked.defaultof<_>
    let mutable startStop = Unchecked.defaultof<StartStop>
    
    let prepTest (testActor: IActorRef<obj>) =
        let ready = System.Threading.Tasks.TaskCompletionSource<unit>()
        let start = System.Threading.Tasks.TaskCompletionSource<unit>()
        testActor <! {start = start; ready = ready}
        for msg in msgs do
            (retype testActor) <! msg
        let stop = System.Threading.Tasks.TaskCompletionSource<unit>()
        (retype testActor) <! {index = 0; stop = Some stop}
        ready.Task.Wait()
        {start = start; stop = stop}        
    
    [<BenchmarkDotNet.Attributes.GlobalSetup>]
    member _.Setup () =
        sys <- System.create "perf-test" (Configuration.defaultConfig ())
        
    [<BenchmarkDotNet.Attributes.Benchmark(Baseline = true)>]
    member _.Akka () =
        startStop.start.SetResult()
        startStop.stop.Task.Wait()

    [<BenchmarkDotNet.Attributes.IterationSetup(Target = nameof Unchecked.defaultof<ActorBenchmarks>.Akka)>]
    member _.AkkaSetup () =
        startStop <- prepTest (AkkaTest.makeActor sys)
        
    [<BenchmarkDotNet.Attributes.Benchmark>]
    member _.AkkaWithState () =
        startStop.start.SetResult()
        startStop.stop.Task.Wait()

    [<BenchmarkDotNet.Attributes.IterationSetup(Target = nameof Unchecked.defaultof<ActorBenchmarks>.AkkaWithState)>]
    member _.AkkaWithStateSetup () =
        startStop <- prepTest (AkkaWithStateTest.makeActor sys)
            
    [<BenchmarkDotNet.Attributes.Benchmark>]
    member _.Akkling () =
        startStop.start.SetResult()
        startStop.stop.Task.Wait()
        
    [<BenchmarkDotNet.Attributes.IterationSetup(Target = nameof Unchecked.defaultof<ActorBenchmarks>.Akkling)>]
    member _.AkklingSetup () =
        startStop <- prepTest (AkklingTest.makeActor sys)

    [<BenchmarkDotNet.Attributes.Benchmark>]
    member _.AkklingWithState () =
        startStop.start.SetResult()
        startStop.stop.Task.Wait()
        
    [<BenchmarkDotNet.Attributes.IterationSetup(Target = nameof Unchecked.defaultof<ActorBenchmarks>.AkklingWithState)>]
    member _.AkklingWithStateSetup () =
        startStop <- prepTest (AkklingWithStateTest.makeActor sys)

    [<BenchmarkDotNet.Attributes.Benchmark>]
    member _.AkklingWithMutableState () =
        startStop.start.SetResult()
        startStop.stop.Task.Wait()
        
    [<BenchmarkDotNet.Attributes.IterationSetup(Target = nameof Unchecked.defaultof<ActorBenchmarks>.AkklingWithMutableState)>]
    member _.AkklingWithMutableStateSetup () =
        startStop <- prepTest (AkklingWithMutableStateTest.makeActor sys)

    [<BenchmarkDotNet.Attributes.Benchmark>]
    member _.WAkka () =
        startStop.start.SetResult()
        startStop.stop.Task.Wait()
        
    [<BenchmarkDotNet.Attributes.IterationSetup(Target = nameof Unchecked.defaultof<ActorBenchmarks>.WAkka)>]
    member _.WAkkaSetup () =
        startStop <- prepTest (WAkkaTest.makeActor sys)

    [<BenchmarkDotNet.Attributes.Benchmark>]
    member _.WAkkaWhile () =
        startStop.start.SetResult()
        startStop.stop.Task.Wait()

    [<BenchmarkDotNet.Attributes.IterationSetup(Target = nameof Unchecked.defaultof<ActorBenchmarks>.WAkkaWhile)>]
    member _.WAkkaWhileSetup () =
        startStop <- prepTest (WAkkaWhileTest.makeActor sys)
        
    [<BenchmarkDotNet.Attributes.Benchmark>]
    member _.WAkkaHandle () =
        startStop.start.SetResult()
        startStop.stop.Task.Wait()

    [<BenchmarkDotNet.Attributes.IterationSetup(Target = nameof Unchecked.defaultof<ActorBenchmarks>.WAkkaHandle)>]
    member _.WAkkaHandleSetup () =
        startStop <- prepTest (WAkkaHandleTest.makeActor sys)
        
    [<BenchmarkDotNet.Attributes.Benchmark>]
    member _.WAkkaClosure () =
        startStop.start.SetResult()
        startStop.stop.Task.Wait()

    [<BenchmarkDotNet.Attributes.IterationSetup(Target = nameof Unchecked.defaultof<ActorBenchmarks>.WAkkaClosure)>]
    member _.WAkkaClosureSetup () =
        startStop <- prepTest (WAkkaClosureTest.makeActor sys)
        
    [<BenchmarkDotNet.Attributes.Benchmark>]
    member _.WAkkaMutable () =
        startStop.start.SetResult()
        startStop.stop.Task.Wait()

    [<BenchmarkDotNet.Attributes.IterationSetup(Target = nameof Unchecked.defaultof<ActorBenchmarks>.WAkkaMutable)>]
    member _.WAkkaMutableSetup () =
        startStop <- prepTest (WAkkaMutableTest.makeActor sys)

    [<BenchmarkDotNet.Attributes.Benchmark>]
    member _.WAkkaSm () =
        startStop.start.SetResult()
        startStop.stop.Task.Wait()

    [<BenchmarkDotNet.Attributes.IterationSetup(Target = nameof Unchecked.defaultof<ActorBenchmarks>.WAkkaSm)>]
    member _.WAkkaSmSetup () =
        startStop <- prepTest (WAkkaSmTest.makeActor sys)
        
let runNonBenchmarkTests () = 
    let numMessages = args.GetResult(Args.NumMessages, 1000000)
    let tests = args.GetResult(Args.Tests, ["all"])
    
    let sys = System.create "perf-test" (Configuration.defaultConfig ())
    
    let runTest (mode: string) (testActor: IActorRef<obj>) =
        if
            tests |> List.contains (mode.ToLower())
            || tests |> List.contains "all"
        then 
            let startTcs = System.Threading.Tasks.TaskCompletionSource<unit>()
            let readyTcs = System.Threading.Tasks.TaskCompletionSource<unit>()
            let stopTcs = System.Threading.Tasks.TaskCompletionSource<unit>()
            let msgs = [|
                for i in 1..numMessages do
                    {index = i; stop = None}
                {index = 0; stop = Some stopTcs}
            |]
            testActor <! {start = startTcs; ready = readyTcs}
            for msg in msgs do
                testActor <! msg
            readyTcs.Task.Wait()
            let start = System.DateTime.Now
            startTcs.SetResult()
            stopTcs.Task.Wait()
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
    let akklingWithMutableState = runTest "AkklingWithMutableState" (AkklingWithMutableStateTest.makeActor sys)
    let wakka = runTest "WAkka" (WAkkaTest.makeActor sys)
    let wakkaWhile = runTest "WAkkaWhile" (WAkkaWhileTest.makeActor sys)
    let wakkaHandle = runTest "WAkkaHandle" (WAkkaHandleTest.makeActor sys)
    let wakkaClosure = runTest "WAkkaClosure" (WAkkaClosureTest.makeActor sys)
    let wakkaMutable = runTest "WAkkaMutable" (WAkkaMutableTest.makeActor sys)
    let wakkaSm = runTest "WAkkaSm" (WAkkaSmTest.makeActor sys)
    
    akka |> Option.iter (fun akkaRate ->
        let diff name rate = 
            rate |> Option.iter (fun rate -> 
                let akkaDiff = rate/akkaRate * 100.0
                printfn $"{name}: {akkaDiff}%%"
            )
        diff "AkkaWithState" akkaWithState
        diff "Akkling" akkling
        diff "AkklingWithState" akklingWithState
        diff "AkklingWithMutableState" akklingWithMutableState
        diff "WAkka" wakka
        diff "WAkkaWhile" wakkaWhile
        diff "WAkkaHandle" wakkaHandle
        diff "WAkkaClosure" wakkaClosure
        diff "WAkkaMutable" wakkaMutable
        diff "WAkkaSm" wakkaSm
    )

if args.Contains NoBenchmarks then
    runNonBenchmarkTests ()
else
    BenchmarkDotNet.Running.BenchmarkRunner.Run<ActorBenchmarks> () |> ignore    
        
System.Environment.Exit 0

    
    
    