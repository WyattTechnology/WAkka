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
type ActorResult<'result, 'error> = SimpleAction<Result<'result, 'error>>

[<RequireQualifiedAccess>]
module ActorResult =

    /// Wraps the given value in an ActorResult so that the ActorResult evaluates to "Ok value".
    let inline retn (value: 'res) : ActorResult<'res, 'err> = value |> Ok |> actor.Return
    /// Wraps the given value in an ActorResult so that the ActorResult evaluates to "Ok value".
    let inline ok  (value: 'res) : ActorResult<'res, 'err> = retn value
    /// Wraps the given value in an ActorResult so that the ActorResult evaluates to "Error value".
    let inline returnError (value: 'err) : ActorResult<'res, 'err> = value |> Error |> actor.Return
    /// Wraps the given value in an ActorResult so that the ActorResult evaluates to "Error value".
    let inline error (value: 'err) : ActorResult<'res, 'err> = returnError value

    /// Applies the given function to the value of the given ActorResult if the ActorResult evaluates to Ok.
    let inline map (f: 'res1 -> 'res2) (action: ActorResult<'res1, 'err>) : ActorResult<'res2, 'err> =
        action |> mapResult (Result.map f)
    /// Applies the given function to the value of the given ActorResult if the ActorResult evaluates to Error.
    let inline mapError (f: 'err1 -> 'err2) (action: ActorResult<'res, 'err1>) : ActorResult<'res, 'err2> =
        action |> mapResult (Result.mapError f)

    /// Binds two ActorResults together.
    let inline bind
        (binder: 'input -> ActorResult<'output, 'error>)
        (input: ActorResult<'input, 'error>)
        : ActorResult<'output, 'error> =
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
        (ifError: ActorResult<'ok, 'errorOutput>)
        (input: ActorResult<'ok, 'errorInput>)
        : ActorResult<'ok, 'errorOutput> =
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
        (ifErrorFunc: 'errorInput -> ActorResult<'ok, 'errorOutput>)
        (input: ActorResult<'ok, 'errorInput>)
        : ActorResult<'ok, 'errorOutput> =
        bindBase (Result.either ok ifErrorFunc) input

    /// Replaces the wrapped value with unit
    let inline ignore<'ok, 'error> (value: ActorResult<'ok, 'error>) : ActorResult<unit, 'error> =
        value |> map ignore<'ok>

    /// Returns the specified error if the async-wrapped value is false.
    let inline requireTrue (err: 'err) (action: SimpleAction<bool>): ActorResult<unit, 'err> =
        action |> mapResult (Result.requireTrue err)
    /// Returns the specified error if the async-wrapped value is true.
    let inline requireFalse (err: 'err) (action: SimpleAction<bool>): ActorResult<unit, 'err> =
        action |> mapResult (Result.requireFalse err)

    /// Converts an async-wrapped Option to a Result, using the given error if None.
    let inline requireSome (err: 'err) (action: SimpleAction<Option<'res>>): ActorResult<'res, 'err> =
        action |> mapResult (Result.requireSome err)
    /// Converts an async-wrapped Option to a Result, using the given error if Some.
    let inline requireNone (err: 'err) (action: SimpleAction<Option<'res>>): ActorResult<unit, 'err> =
        action |> mapResult (Result.requireNone err)

    /// Returns Ok if the async-wrapped value and the provided value are equal, or the specified error if not.
    let inline requireEqual (value1: 'value) (value2: SimpleAction<'value>) (error: 'error) : ActorResult<unit, 'error> =
        value2 |> mapResult (fun x2' -> Result.requireEqual value1 x2' error)

    /// Returns Ok if the two values are equal, or the specified error if not.
    let inline requireEqualTo (other: 'value) (error: 'error) (this: SimpleAction<'value>) : ActorResult<unit, 'error> =
        this |> mapResult (Result.requireEqualTo other error)

    /// Returns Ok if the async-wrapped sequence is empty, or the specified error if not.
    let inline requireEmpty (error: 'error) (values: SimpleAction<#seq<'ok>>) : ActorResult<unit, 'error> =
        values |> mapResult (Result.requireEmpty error)

    /// Returns Ok if the async-wrapped sequence is not-empty, or the specified error if not.
    let inline requireNotEmpty (error: 'error) (values: SimpleAction<#seq<'ok>>) : ActorResult<unit, 'error> =
        values |> mapResult (Result.requireNotEmpty error)

    /// Returns the first item of the async-wrapped sequence if it exists, or the specified
    /// error if the sequence is empty
    let inline requireHead (error: 'error) (values: SimpleAction<#seq<'ok>>) : ActorResult<'ok, 'error> =
        values |> mapResult (Result.requireHead error)

    /// Lift SimpleAction to ActorResult
    let inline ofActor (value: SimpleAction<'ok>) : ActorResult<'ok, 'error> = value |> mapResult Ok

    /// Lift Result to ActorResult
    let inline ofResult (value: Result<'ok, 'error>) : ActorResult<'ok, 'error> = actor.Return value


[<AutoOpen>]
module ActorResultCE =

    type ActorResultBuilder () =

        member inline _.Return (value: 'res) : SimpleAction<Result<'res, 'err>> = value |> result.Return |> actor.Return
        member inline _.ReturnFrom (value: SimpleAction<Result<'ok, 'err>>) = value
        member inline _.Zero() : SimpleAction<Result<unit, 'error>> = result.Zero() |> actor.Return
        member inline _.Bind(result: ActorResult<'okInput, 'error>,
                             binder: 'okInput -> ActorResult<'okOutput, 'error>
                             ) : SimpleAction<Result<'okOutput, 'error>> =
            ActorResult.bind binder result
        member inline _.Delay (generator: unit -> SimpleAction<Result<'ok, 'error>>) : SimpleAction<Result<'ok, 'error>> =
            generator ()
        member inline this.Combine
            (
                computation1: SimpleAction<Result<unit, 'error>>,
                computation2: SimpleAction<Result<'ok, 'error>>
            ) : SimpleAction<Result<'ok, 'error>> =
            this.Bind(computation1, (fun () -> computation2))

    /// A computation expression that allows mixing of SimpleAction and Result via the type ActorResult<'res, 'err> which
    /// is equivalent to SimpleAction<Result<'res, 'err>>. The CE unwraps both types so that "let! value = f ()" where
    /// "f: unit -> SimpleAction<Result<'res, 'err>>" would evaluate the action returned by "f ()" and then unwrap the
    /// Result that the action evaluated to. If the result is "Ok okValue" then "okValue" is bound to "value". If the
    /// result is "Error err" then the "actorResult" CE terminates with a value of "Error err". Note that the right
    /// side of a "let! ... = ..." must evaluate to "SimpleAction<Result<'res, 'err>>". There are numerous functions in
    /// the ActorResult module that can help convert other types (booleans, options, etc.) into the proper type.
    /// "actorResult" computations can be directly run in actor expressions (e.g.,
    /// "actor {let! res = actorResult { ... }; ...}").
    let actorResult = ActorResultBuilder()
    
    /// Evaluates an actorResult computation expression. This is only here for backwards compatibility, actorResult
    /// computations can now be directly run in actor expressions (e.g., "actor {let! res = actorResult { ... }; ...}").
    let runActorResult (res: SimpleAction<Result<'res, 'err>>) = res

