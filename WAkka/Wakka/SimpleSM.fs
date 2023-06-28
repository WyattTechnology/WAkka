module WAkka.SimpleSM

open System.Runtime.CompilerServices
open FSharp.Core.CompilerServices
open FSharp.Core.CompilerServices.StateMachineHelpers
open FSharp.Core.LanguagePrimitives.IntrinsicOperators

let mutable private traceMsgs = System.Collections.Concurrent.ConcurrentQueue<string>()
let clearTraces() = traceMsgs <- System.Collections.Concurrent.ConcurrentQueue<string>()
let getTraces() = traceMsgs |> Seq.toList

let enableTrace = false
let trace  =
    if enableTrace then 
        traceMsgs.Enqueue
    else
        ignore

/// Helpers to do zero-allocation call to interface methods on structs
[<AutoOpen>]
module internal Helpers =
    let inline MoveNext(x: byref<'T> when 'T :> IAsyncStateMachine) = x.MoveNext()
    let inline SetStateMachine(x: byref<'T> when 'T :> IAsyncStateMachine, state) = x.SetStateMachine(state)
    let inline GetResumptionPoint(x: byref<'T> when 'T :> IResumableStateMachine<'Data>) = x.ResumptionPoint

/// This is the type of SmActors
[<AbstractClass; NoEquality; NoComparison>] 
type SmActor<'Result>() =
    
    /// Checks if the SmActor is completed
    abstract IsCompleted: bool

    /// Executes the SmActor until the next 'yield'
    abstract MoveNext: unit -> unit

    abstract SetNextMessage: obj -> unit
    
    abstract SetResult: 'Result -> unit
    abstract Result: 'Result
    
    /// Gets the tail-call target if the SmActor has executed a `return!`
    abstract TailCallTarget: SmActor<'Result> option
    
/// This is the implementation of SmActor with respect to a particular struct state machine type.
and [<NoEquality; NoComparison>] 
    SmActor<'Machine, 'Result
                when 'Machine : struct
                and 'Machine :> IAsyncStateMachine 
                and 'Machine :> ISmActorStateMachine<'Result>>() =
    inherit SmActor<'Result>()

    // The state machine struct
    [<DefaultValue(false)>]
    val mutable Machine: 'Machine

    override this.IsCompleted =
        match this.TailCallTarget with 
        | None ->
            //TODO: Resumption point isn't being set properly when doing a tail call
            let resumptionPoint = GetResumptionPoint(&this.Machine)
            //if enableTrace then trace $"SmActor({this}).IsCompleted: res point = {resumptionPoint}"
            let complete = (resumptionPoint = -1)
            complete
        | Some tg -> 
            //if enableTrace then trace $"SmActor({this}).IsCompleted: have tail-call"
            tg.IsCompleted

    override this.SetNextMessage msg =
        //if enableTrace then trace $"SmActor({this}).SetNextMessage: {msg}"
        SmActorStateMachineData<'Result>.SetMessage(&this.Machine, msg)

    override this.TailCallTarget = 
        SmActorStateMachineData<'Result>.GetHijackTarget(&this.Machine)

    override this.MoveNext() =
        match this.TailCallTarget with 
        | None ->
            //if enableTrace then trace $"SmActor({this}).MoveNext: no tail-call"
            MoveNext(&this.Machine)
        | Some tg -> 
            //if enableTrace then trace $"SmActor({this}).MoveNext: have tail-call"
            match tg.TailCallTarget with 
            | None ->
                tg.SetNextMessage this.Machine.Data.Message
                tg.MoveNext()
            | Some tg2 -> 
                // Cut out chains of tail-calls
                SmActorStateMachineData<'Result>.SetHijackTarget(&this.Machine, tg2)
                tg2.SetNextMessage this.Machine.Data.Message
                tg2.MoveNext()
    
    override this.SetResult result =
        //if enableTrace then trace $"SmActor({this}).SetResult: {result}"
        SmActorStateMachineData<'Result>.SetResult(&this.Machine, result)
        
    override this.Result = this.Machine.Data.Result    
        
/// This extra data stored in ResumableStateMachine (and it's templated copies using __stateMachine) 
/// It only contains one field, the hijack target for tail-calls.
and [<Struct; NoComparison; NoEquality>]
    SmActorStateMachineData<'Result> =

    /// This is used for tail-calls using 'return!'
    [<DefaultValue(false)>]
    val mutable TailCallTarget: SmActor<'Result> option 

    [<DefaultValue(false)>]
    val mutable Message: obj
    
    [<DefaultValue(false)>]
    val mutable Result: 'Result
    
    static member GetHijackTarget(x: byref<'Machine> when 'Machine :> IResumableStateMachine<SmActorStateMachineData<'Result>>) = 
        x.Data.TailCallTarget

    static member SetHijackTarget(x: byref<'Machine>, tg: SmActor<'Result>) : unit when 'Machine :> IResumableStateMachine<SmActorStateMachineData<'Result>> = 
        let mutable newData = SmActorStateMachineData()
        newData.TailCallTarget <- Some tg
        newData.Message <- x.Data.Message
        x.Data <- newData
        
    static member SetMessage(x: byref<'Machine>, msg: obj) : unit when 'Machine :> IResumableStateMachine<SmActorStateMachineData<'Result>> = 
        let mutable newData = SmActorStateMachineData()
        newData.Message <- msg
        newData.TailCallTarget <- x.Data.TailCallTarget
        x.Data <- newData

    static member SetResult(x: byref<'Machine>, res: 'Result) : unit when 'Machine :> IResumableStateMachine<SmActorStateMachineData<'Result>> = 
        let mutable newData = SmActorStateMachineData()
        newData.Result <- res
        newData.Message <- x.Data.Message
        newData.TailCallTarget <- x.Data.TailCallTarget
        x.Data <- newData
        
and ISmActorStateMachine<'Result> = IResumableStateMachine<SmActorStateMachineData<'Result>>
and SmActorStateMachine<'Result> = ResumableStateMachine<SmActorStateMachineData<'Result>>
and SmActorResumptionFunc<'Result> = ResumptionFunc<SmActorStateMachineData<'Result>>
and SmActorResumptionDynamicInfo<'Result> = ResumptionDynamicInfo<SmActorStateMachineData<'Result>>
and SmActorCode<'Result> = ResumableCode<SmActorStateMachineData<'Result>, unit>

type ReceiveAction<'Msg> () = class end

type SmActorBuilder() =
    
    member inline _.Delay(f : unit -> SmActorCode<'Result>) : SmActorCode<'Result> = ResumableCode.Delay(f)

    /// Create the state machine and outer execution logic
    member inline _.Run(code : SmActorCode<'Result>) : SmActor<'Result> = 
        if __useResumableCode then 
            __stateMachine<SmActorStateMachineData<'Result>, SmActor<'Result>>
    
                // IAsyncStateMachine.MoveNext
                (MoveNextMethodImpl<_>(fun sm -> 
                        //-- RESUMABLE CODE START
                        __resumeAt sm.ResumptionPoint 
                        //if enableTrace then trace $"SmActorBuilder({sm}).MoveNext: after resumeAt {sm.ResumptionPoint}"
                        let __stack_code_fin = code.Invoke(&sm)
                        //if enableTrace then trace $"SmActorBuilder({sm}).MoveNext: after code.Invoke, fin = {__stack_code_fin}, res point = {sm.ResumptionPoint}"
                        if __stack_code_fin then
                            sm.ResumptionPoint  <- -1 // indicates complete
                        else
                            // Goto request
                            match sm.Data.TailCallTarget with 
                            | Some tg ->
                                tg.SetNextMessage sm.Data.Message
                                tg.MoveNext() // recurse
                            | None -> ()
                        //-- RESUMABLE CODE END
                    ))
    
                // IAsyncStateMachine.SetStateMachine
                (SetStateMachineMethodImpl<_>(fun sm state -> SetStateMachine(&sm, state)))
    
                // Box the SmActor.  In this example we don't start execution of the SmActor.
                (AfterCode<_,_>(fun sm -> 
                    let mutable cr = SmActor<SmActorStateMachine<'Result>, 'Result>()
                    cr.Machine <- sm
                    cr :> SmActor<'Result>))
        else
            failwith "no dynamic"
            // // The dynamic implementation
            // let initialResumptionFunc = SmActorResumptionFunc(fun sm -> code.Invoke(&sm))
            // let resumptionInfo =
            //     { new SmActorResumptionDynamicInfo<'Result>(initialResumptionFunc) with 
            //         member info.MoveNext(sm) = 
            //             if info.ResumptionFunc.Invoke(&sm) then
            //                 sm.ResumptionPoint <- -1
            //         member info.SetStateMachine(sm, state) = ()
            //      }
            // let mutable cr = SmActor<SmActorStateMachine<'Result>, 'Result>()
            // cr.Machine.ResumptionDynamicInfo <- resumptionInfo
            // cr :> SmActor<'Result>

    /// Used to represent no-ops like the implicit empty "else" branch of an "if" expression.
    [<DefaultValue>]
    member inline _.Zero() : SmActorCode<'Result> = ResumableCode.Zero()

    /// Chains together a step with its following step.
    /// Note that this requires that the first step has no result.
    /// This prevents constructs like `task { return 1; return 2; }`.
    member inline _.Combine(code1: SmActorCode<'Result>, code2: SmActorCode<'Result>) : SmActorCode<'Result> =
        ResumableCode.Combine(code1, code2)

    member inline _.Bind(_: ReceiveAction<'Msg>, cont: 'Msg -> SmActorCode<'Result>) : SmActorCode<'Result> =
        //if enableTrace then trace "SmActorBuilder.BindRecv"
        SmActorCode<'Result>(fun sm ->
            //if enableTrace then trace $"SmActorBuilder({sm}).BindRecv: enter"
            let mutable __stack_fin = true
            if isNull sm.Data.Message then
                //if enableTrace then trace $"SmActorBuilder({sm}).BindRecv: no message"
                // This will yield with __stack_fin2 = false
                // This will resume with __stack_fin2 = true
                let __stack_fin2 = ResumableCode.Yield().Invoke(&sm)
                //if enableTrace then trace $"SmActorBuilder({sm}).BindRecv: after yield, fin = {__stack_fin2}"
                __stack_fin <- __stack_fin2

            if __stack_fin then
                //if enableTrace then trace $"SmActorBuilder({sm}).BindRecv: after yield, fin = true"
                match sm.Data.Message with
                | :? 'Msg as msg ->
                    //if enableTrace then trace $"SmActorBuilder({sm}).BindRecv: good message {msg}"
                    sm.Data.Message <- null
                    (cont msg).Invoke(&sm)
                | _ ->
                    //if enableTrace then trace $"SmActorBuilder({sm}).BindRecv: bad message '{sm.Data.Message}'"
                    false
            else
                //if enableTrace then trace $"SmActorBuilder({sm}).BindRecv: after yield, fin = false"
                false
        )

    member inline _.Bind(action: SmActor<'Inter>, cont: 'Inter -> SmActorCode<'Result>) : SmActorCode<'Result> =
        //if enableTrace then trace "SmActorBuilder.Bind"
        SmActorCode<'Result>(fun sm ->
            //if enableTrace then trace $"SmActorBuilder({sm}).Bind: enter, msg = {sm.Data.Message}"
            action.SetNextMessage null
            //if enableTrace then trace $"SmActorBuilder({sm}).Bind: before initial MoveNext"
            action.MoveNext()
            //if enableTrace then trace $"SmActorBuilder({sm}).Bind: after initial MoveNext"
            let mutable __stack_fin = true
            if not action.IsCompleted then
                let __stack_fin2 = ResumableCode.Yield().Invoke(&sm)
                //if enableTrace then trace $"SmActorBuilder({sm}).Bind: after yield, fin = {__stack_fin2}"
                __stack_fin <- __stack_fin2
                
            if __stack_fin then
                //if enableTrace then trace $"SmActorBuilder({sm}).Bind: after yield check, fin = true"
                action.SetNextMessage sm.Data.Message
                action.MoveNext ()
                if action.IsCompleted then
                    //if enableTrace then trace $"SmActorBuilder({sm}).Bind: after action check, completed, result = {action.Result}"
                    (cont action.Result).Invoke(&sm)
                else
                    //if enableTrace then trace $"SmActorBuilder({sm}).Bind: after action check, not completed, message = {sm.Data.Message}"
                    false
            else
                //if enableTrace then trace $"SmActorBuilder({sm}).Bind: after yield check, fin = false"
                false
        )
        
    member inline _.Return(result: 'Result) : SmActorCode<'Result> = 
        //if enableTrace then trace $"SmActorBuilder.Return: {result}"
        ResumableCode<_,_>(fun sm ->
            match sm.Data.TailCallTarget with
            | Some tg ->
                //if enableTrace then trace $"SmActorBuilder({sm}).Return: tail call"
                tg.SetResult result
                true
            | None ->
                //if enableTrace then trace $"SmActorBuilder({sm}).Return: normal return"
                sm.Data.Result <- result
                sm.ResumptionPoint <- -1
                true
        )
        
    // The implementation of `return!`, non-standard for tail-calls
    member inline _.ReturnFrom (other: SmActor<'Result>) : SmActorCode<'Result> = 
        //if enableTrace then trace "SmActorBuilder.ReturnFrom"
        ResumableCode<_,_>(fun sm ->
            //if enableTrace then trace $"SmActorBuilder({sm}).ReturnFrom: setting target"
            sm.Data.TailCallTarget <- Some other
            false
        )

let smActor = SmActorBuilder()

let inline receive () = ReceiveAction<'Msg>()

type SmActorImpl (action: SmActor<unit>) =

    inherit Akka.Actor.UntypedActor ()

    let ctx = Akka.Actor.UntypedActor.Context :> Akka.Actor.IActorContext
    
    do
        //if enableTrace then trace "SmActorImpl.ctor"
        action.MoveNext()
        //if enableTrace then trace "SmActorImpl.ctor: after MoveNext"
        if action.IsCompleted then
            //if enableTrace then trace "SmActorImpl.ctor: completed"
            ctx.Stop ctx.Self
    
    override this.OnReceive msg =
        //if enableTrace then trace $"SmActorImpl.OnReceive: {msg}"
        action.SetNextMessage msg
        //if enableTrace then trace "SmActorImpl.OnReceive: before MoveNext"
        action.MoveNext()
        //if enableTrace then trace "SmActorImpl.OnReceive: after MoveNext"
        if action.IsCompleted then
            //if enableTrace then trace "SmActorImpl.OnReceive: completed"
            ctx.Stop ctx.Self

let spawn (parent: Akka.Actor.IActorRefFactory) (props: Common.Props) (action: SmActor<unit>) =
    let applyMod arg modifier current =
        match arg with
        | Some a -> modifier a current
        | None -> current    
    let actProps =
        Akka.Actor.Props.Create(fun () -> SmActorImpl(action))
        |> applyMod props.dispatcher (fun d a -> a.WithDispatcher d)
        |> applyMod props.deploy (fun d a -> a.WithDeploy d)
        |> applyMod props.mailbox (fun d a -> a.WithMailbox d)
        |> applyMod props.router (fun d a -> a.WithRouter d)
        |> applyMod props.supervisionStrategy (fun d a -> a.WithSupervisorStrategy d)
    let act =
        match props.name with
        | Some name ->
            parent.ActorOf(actProps, name)
        | None ->
            parent.ActorOf(actProps)
    Akkling.ActorRefs.typed act
