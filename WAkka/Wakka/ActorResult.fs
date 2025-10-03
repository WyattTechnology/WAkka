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

module WAkka.ActorResult

open FsToolkit.ErrorHandling

open Common
open Simple

/// Combines a SimpleAction and a Result.
type ActorResult<'result, 'error, 'extra> = ActionBase<Result<'result, 'error>, 'extra>

[<RequireQualifiedAccess>]
module ActorResult =

    /// Wraps the given value in an ActorResult so that the ActorResult evaluates to "Ok value".
    let inline retn (value: 'res) : ActorResult<'res, 'err, 'extra> = value |> Ok |> actor.Return
    /// Wraps the given value in an ActorResult so that the ActorResult evaluates to "Ok value".
    let inline ok  (value: 'res) : ActorResult<'res, 'err, 'extra> = retn value
    /// Wraps the given value in an ActorResult so that the ActorResult evaluates to "Error value".
    let inline returnError (value: 'err) : ActorResult<'res, 'err, 'extra> = value |> Error |> actor.Return
    /// Wraps the given value in an ActorResult so that the ActorResult evaluates to "Error value".
    let inline error (value: 'err) : ActorResult<'res, 'err, 'extra> = returnError value

    /// Applies the given function to the value of the given ActorResult if the ActorResult evaluates to Ok.
    let inline map (f: 'res1 -> 'res2) (action: ActorResult<'res1, 'err, 'extra>) : ActorResult<'res2, 'err, 'extra> =
        action |> mapResult (Result.map f)
    /// Applies the given function to the value of the given ActorResult if the ActorResult evaluates to Error.
    let inline mapError (f: 'err1 -> 'err2) (action: ActorResult<'res, 'err1, 'extra>) : ActorResult<'res, 'err2, 'extra> =
        action |> mapResult (Result.mapError f)

    /// Binds two ActorResults together.
    let inline bind
        (binder: 'input -> ActorResult<'output, 'error, 'extra>)
        (input: ActorResult<'input, 'error, 'extra>)
        : ActorResult<'output, 'error, 'extra> =
        bindBase (Result.either binder returnError) input

    /// <summary>
    /// Returns <paramref name="input"/> if it is <c>Ok</c>, otherwise returns <paramref name="ifError"/>
    /// </summary>
    /// <param name="ifError">The value to use if <paramref name="input"/> is <c>Error</c></param>
    /// <param name="input">The input result.</param>
    /// <remarks>
    /// </remarks>
    /// <example>
    /// <code>
    ///     AsyncResult.error "First" |> AsyncResult.orElse (AsyncResult.error "Second") // evaluates to Error ("Second")
    ///     AsyncResult.error "First" |> AsyncResult.orElse (AsyncResult.ok "Second") // evaluates to Ok ("Second")
    ///     AsyncResult.ok "First" |> AsyncResult.orElse (AsyncResult.error "Second") // evaluates to Ok ("First")
    ///     AsyncResult.ok "First" |> AsyncResult.orElse (AsyncResult.ok "Second") // evaluates to Ok ("First")
    /// </code>
    /// </example>
    /// <returns>
    /// The result if the result is Ok, else returns <paramref name="ifError"/>.
    /// </returns>
    let inline orElse
        (ifError: ActorResult<'ok, 'errorOutput, 'extra>)
        (input: ActorResult<'ok, 'errorInput, 'extra>)
        : ActorResult<'ok, 'errorOutput, 'extra> =
        bindBase (Result.either ok (fun _ -> ifError)) input

    /// <summary>
    /// Returns <paramref name="input"/> if it is <c>Ok</c>, otherwise executes <paramref name="ifErrorFunc"/> and returns the result.
    /// </summary>
    /// <param name="ifErrorFunc">A function that provides an alternate result when evaluated.</param>
    /// <param name="input">The input result.</param>
    /// <remarks>
    /// <paramref name="ifErrorFunc"/>  is not executed unless <paramref name="input"/> is an <c>Error</c>.
    /// </remarks>
    /// <example>
    /// <code>
    ///     AsyncResult.error "First" |> AsyncResult.orElseWith (fun _ -> AsyncResult.error "Second") // evaluates to Error ("Second")
    ///     AsyncResult.error "First" |> AsyncResult.orElseWith (fun _ -> AsyncResult.ok "Second") // evaluates to Ok ("Second")
    ///     AsyncResult.ok "First" |> AsyncResult.orElseWith (fun _ -> AsyncResult.error "Second") // evaluates to Ok ("First")
    ///     AsyncResult.ok "First" |> AsyncResult.orElseWith (fun _ -> AsyncResult.ok "Second") // evaluates to Ok ("First")
    /// </code>
    /// </example>1
    /// <returns>
    /// The result if the result is Ok, else the result of executing <paramref name="ifErrorFunc"/>.
    /// </returns>
    let inline orElseWith
        (ifErrorFunc: 'errorInput -> ActorResult<'ok, 'errorOutput, 'extra>)
        (input: ActorResult<'ok, 'errorInput, 'extra>)
        : ActorResult<'ok, 'errorOutput, 'extra> =
        bindBase (Result.either ok ifErrorFunc) input

    /// Replaces the wrapped value with unit
    let inline ignore<'ok, 'error, 'extra> (value: ActorResult<'ok, 'error, 'extra>) : ActorResult<unit, 'error, 'extra> =
        value |> map ignore<'ok>

    /// Returns the specified error if the async-wrapped value is false.
    let inline requireTrue (err: 'err) (action: ActionBase<bool, 'extra>): ActorResult<unit, 'err, 'extra> =
        action |> mapResult (Result.requireTrue err)
    /// Returns the specified error if the async-wrapped value is true.
    let inline requireFalse (err: 'err) (action: ActionBase<bool, 'extra>): ActorResult<unit, 'err, 'extra> =
        action |> mapResult (Result.requireFalse err)

    /// Converts an async-wrapped Option to a Result, using the given error if None.
    let inline requireSome (err: 'err) (action: ActionBase<Option<'res>, 'extra>): ActorResult<'res, 'err, 'extra> =
        action |> mapResult (Result.requireSome err)
    /// Converts an async-wrapped Option to a Result, using the given error if Some.
    let inline requireNone (err: 'err) (action: ActionBase<Option<'res>, 'extra>): ActorResult<unit, 'err, 'extra> =
        action |> mapResult (Result.requireNone err)

    /// Returns Ok if the async-wrapped value and the provided value are equal, or the specified error if not.
    let inline requireEqual (value1: 'value) (value2: ActionBase<'value, 'extra>) (error: 'error) : ActorResult<unit, 'error, 'extra> =
        value2 |> mapResult (fun x2' -> Result.requireEqual value1 x2' error)

    /// Returns Ok if the two values are equal, or the specified error if not.
    let inline requireEqualTo (other: 'value) (error: 'error) (this: ActionBase<'value, 'extra>) : ActorResult<unit, 'error, 'extra> =
        this |> mapResult (Result.requireEqualTo other error)

    /// Returns Ok if the async-wrapped sequence is empty, or the specified error if not.
    let inline requireEmpty (error: 'error) (values: ActionBase<#seq<'ok>, 'extra>) : ActorResult<unit, 'error, 'extra> =
        values |> mapResult (Result.requireEmpty error)

    /// Returns Ok if the async-wrapped sequence is not-empty, or the specified error if not.
    let inline requireNotEmpty (error: 'error) (values: ActionBase<#seq<'ok>, 'extra>) : ActorResult<unit, 'error, 'extra> =
        values |> mapResult (Result.requireNotEmpty error)

    /// Returns the first item of the async-wrapped sequence if it exists, or the specified
    /// error if the sequence is empty
    let inline requireHead (error: 'error) (values: ActionBase<#seq<'ok>, 'extra>) : ActorResult<'ok, 'error, 'extra> =
        values |> mapResult (Result.requireHead error)

    /// Lift SimpleAction to ActorResult
    let inline ofActor (value: ActionBase<'ok, 'extra>) : ActorResult<'ok, 'error, 'extra> = value |> mapResult Ok

    /// Lift Result to ActorResult
    let inline ofResult (value: Result<'ok, 'error>) : ActorResult<'ok, 'error, 'extra> = actor.Return value


[<AutoOpen>]
module ActorResultCE =

    type ActorResultBuilder () =

        member inline _.Return (value: 'res) : ActionBase<Result<'res, 'err>, 'extra> = value |> result.Return |> actor.Return
        member inline _.ReturnFrom (value: ActionBase<Result<'ok, 'err>, 'extra>) = value
        member inline _.Zero() : ActionBase<Result<unit, 'error>, 'extra> = result.Zero() |> actor.Return
        member inline _.Bind(result: ActorResult<'okInput, 'error, 'extra>,
                             binder: 'okInput -> ActorResult<'okOutput, 'error, 'extra>
                             ) : ActionBase<Result<'okOutput, 'error>, 'extra> =
            ActorResult.bind binder result
        member inline _.Delay (generator: unit -> ActionBase<Result<'ok, 'error>, 'extra>) : ActionBase<Result<'ok, 'error>, 'extra> =
            generator ()
        member inline this.Combine
            (
                computation1: ActionBase<Result<unit, 'error>, 'extra>,
                computation2: ActionBase<Result<'ok, 'error>, 'extra>
            ) : ActionBase<Result<'ok, 'error>, 'extra> =
            this.Bind(computation1, (fun () -> computation2))

    /// A computation expression that allows mixing of actor actions and Result via the type ActorResult<'res, 'err, 'extra> which
    /// is equivalent to ActionBase<Result<'res, 'err>, 'extra>. The CE unwraps both types so that "let! value = f ()" where
    /// "f: unit -> ActionBase<Result<'res, 'err>, 'extra>" would evaluate the action returned by "f ()" and then unwrap the
    /// Result that the action evaluated to. If the result is "Ok okValue" then "okValue" is bound to "value". If the
    /// result is "Error err" then the "actorResult" CE terminates with a value of "Error err". Note that the right
    /// side of a "let! ... = ..." must evaluate to "ActionBase<Result<'res, 'err>, 'extra>". There are numerous functions in
    /// the ActorResult module that can help convert other types (booleans, options, etc.) into the proper type.
    /// "actorResult" computations can be directly run in actor expressions (e.g., "actor {let! res = actorResult { ... }; ...}").
    let actorResult = ActorResultBuilder()
    
    /// Evaluates an actorResult computation expression. This is only here for backwards compatibility, actorResult
    /// computations can now be directly run in actor expressions (e.g., "actor {let! res = actorResult { ... }; ...}").
    let runActorResult (res: SimpleAction<Result<'res, 'err>>) = res

