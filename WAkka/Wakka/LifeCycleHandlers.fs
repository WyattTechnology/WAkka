module internal WAkka.LifeCycleHandlers

type LifeCycleHandlers<'Args_t>() =
    
    let mutable nextId = 0
    let mutable handlers = Map.empty<int, 'Args_t -> unit>
    
    member _.AddHandler handler =
        let id = nextId
        nextId <- nextId + 1
        handlers <- handlers.Add(id, handler)
        id
    
    member _.RemoveHandler id =
        handlers <- handlers.Remove id

    member _.ExecuteHandlers args =
        for _id, handler in Map.toSeq handlers do
            handler args
    
