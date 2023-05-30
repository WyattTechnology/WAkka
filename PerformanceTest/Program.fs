module Main

open Akkling

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
        
[<EntryPoint>]
let main _args =
    
    BenchmarkDotNet.Running.BenchmarkRunner.Run<ActorBenchmarks> () |> ignore    
        
    // let numMessages =
    //     args
    //     |> Array.tryHead
    //     |> Option.map int
    //     |> Option.defaultValue 1000000
    //
    // let sys = System.create "perf-test" (Configuration.defaultConfig ())
    //
    // let runTest mode testActor =
    //     // let actProps = Akka.Actor.Props.Create(fun () -> Sender(testActor, numMessages))
    //     // let monitor = sys.ActorOf(actProps)
    //     // let timeSpan : System.TimeSpan = typed monitor <? GetInfo |> Async.RunSynchronously
    //     let tcs = System.Threading.Tasks.TaskCompletionSource<unit>()
    //     let msgs = [|
    //         for i in 1..numMessages do
    //             {index = i; stop = None}
    //         {index = 0; stop = Some tcs}
    //     |]
    //     let start = System.DateTime.Now
    //     for msg in msgs do
    //         testActor <! msg
    //     tcs.Task.Wait()
    //     let timeSpan = System.DateTime.Now - start
    //     let rate = float numMessages/timeSpan.TotalSeconds
    //     printfn $"{mode}: {rate} msgs/sec"
    //     rate
    //
    // let mutable keepGoing = true
    // while keepGoing do        
    //     let akka = runTest "Akka" (AkkaTest.makeActor sys)
    //     let akkling = runTest "Akkling" (AkklingTest.makeActor sys)
    //     let wakka = runTest "WAkka" (WAkkaTest.makeActor sys)
    //     let akklingDiff = akkling/akka * 100.0        
    //     let wakkaDiff = wakka/akka * 100.0        
    //     printfn $"Akkling: {akklingDiff}%%     WAkka: {wakkaDiff}%%"
    //     keepGoing <- false

    0
    
    
    