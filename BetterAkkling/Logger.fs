module BetterAkkling.Logger

type Logger internal (logger: Akka.Event.ILoggingAdapter) =
    member _.Log level msg = logger.Log (level, msg)
    member this.Logf level =
        if logger.IsEnabled level then
            Printf.kprintf (this.Log level)
        else
            fun _ -> ()

    member _.LogException (err: exn) = logger.Error err.Message

    member _.Debug msg = logger.Debug msg
    member this.Debugf =
        if logger.IsDebugEnabled then
            Printf.kprintf this.Debug
        else
            fun _ -> ()

    member _.Info msg = logger.Info msg
    member this.Infof =
        if logger.IsInfoEnabled then
            Printf.kprintf this.Info
        else
            fun _ -> ()

    member _.Warning msg = logger.Warning msg
    member this.Warningf =
        if logger.IsWarningEnabled then
            Printf.kprintf this.Warning
        else
            fun _ -> ()

    member _.Error msg = logger.Error msg
    member this.Errorf =
        if logger.IsErrorEnabled then
            Printf.kprintf this.Error
        else
            fun _ -> ()
