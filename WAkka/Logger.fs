module WAkka.Logger

/// A logger tied to an actor.
type Logger internal (logger: Akka.Event.ILoggingAdapter) =
    /// Log the given message at the given level.
    member _.Log level msg = logger.Log (level, msg)
    /// Log the given message at the given level.
    member this.Logf level =
        if logger.IsEnabled level then
            Printf.kprintf (this.Log level)
        else
            fun _ -> ()

    ///Log the given exception.
    member _.LogException (err: exn) = logger.Error err.Message

    ///Log the given message at the debug level.
    member _.Debug msg = logger.Debug msg
    ///Log the given message at the debug level.
    member this.Debugf =
        if logger.IsDebugEnabled then
            Printf.kprintf this.Debug
        else
            fun _ -> ()

    ///Log the given message at the info level.
    member _.Info msg = logger.Info msg
    ///Log the given message at the info level.
    member this.Infof =
        if logger.IsInfoEnabled then
            Printf.kprintf this.Info
        else
            fun _ -> ()

    ///Log the given message at the warning level.
    member _.Warning msg = logger.Warning msg
    ///Log the given message at the warning level.
    member this.Warningf =
        if logger.IsWarningEnabled then
            Printf.kprintf this.Warning
        else
            fun _ -> ()

    ///Log the given message at the error level.
    member _.Error msg = logger.Error msg
    ///Log the given message at the error level.
    member this.Errorf =
        if logger.IsErrorEnabled then
            Printf.kprintf this.Error
        else
            fun _ -> ()
