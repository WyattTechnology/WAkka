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
    
let phony = Unchecked.defaultof<IActorRef<TestMsg>>

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
                    return ()
                else 
                    return! handle ()
            }
            handle ()
        ))

module AkkaTest =
    
    type TestActor() =
        inherit Akka.Actor.UntypedActor ()
        
        let ctx = Akka.Actor.UntypedActor.Context :> Akka.Actor.IActorContext
        
        override _.OnReceive (msg: obj) =
            
            match msg with
            | :? TestMsg as testMsg -> 
                if testMsg.stop.IsSome then
                    testMsg.stop.Value.SetResult ()
                    ctx.Stop ctx.Self                    
            | _ ->
                ()
            
    let makeActor (parent: Akka.Actor.IActorRefFactory) =
        let actProps = Akka.Actor.Props.Create TestActor
        parent.ActorOf actProps |> typed

module AkklingTest =
    
    // let makeActor parent = 
    //         Spawn.spawnAnonymous parent (Props.props(fun ctx ->
    //             let rec loop value (msg:TestMsg) = 
    //                 if msg.stop.IsSome then
    //                     msg.stop.Value.SetResult ()
    //                     Spawn.stop ()                    
    //                 else
    //                     become (loop (value + 1))
    //             become (loop 0)
    //         ))
            
    let makeActor parent = 
            Spawn.spawnAnonymous parent (Props.props(fun ctx ->
                become (fun (msg:TestMsg) -> 
                    if msg.stop.IsSome then
                        msg.stop.Value.SetResult ()
                        Spawn.stop ()                    
                    else
                        ignored ()
                )
            ))
            
    let t = task {
        let! x = task {return 5}
        return 1 + x
    }
            
[<EntryPoint>]
let main args =
    
    let numMessages =
        args
        |> Array.tryHead
        |> Option.map int
        |> Option.defaultValue 1000000
        
    let sys = System.create "perf-test" (Configuration.defaultConfig ())

    let runTest mode testActor =         
        // let actProps = Akka.Actor.Props.Create(fun () -> Sender(testActor, numMessages))
        // let monitor = sys.ActorOf(actProps)
        // let timeSpan : System.TimeSpan = typed monitor <? GetInfo |> Async.RunSynchronously
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
        rate
    
    let mutable keepGoing = true
    while keepGoing do        
        let akka = runTest "Akka" (AkkaTest.makeActor sys)
        let akkling = runTest "Akkling" (AkklingTest.makeActor sys)
        let wakka = runTest "WAkka" (WAkkaTest.makeActor sys)
        let akklingDiff = akkling/akka * 100.0        
        let wakkaDiff = wakka/akka * 100.0        
        printfn $"Akkling: {akklingDiff}%%     WAkka: {wakkaDiff}%%"
        keepGoing <- false
    0
    
    
    