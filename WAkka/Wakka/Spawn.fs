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

module WAkka.Spawn

/// Specifies how an actor responds to restarts.
type ActorType =
    private
    | NotPersisted of Simple.SimpleAction<unit>
    | Checkpointed of Simple.SimpleAction<unit>
    | EventSourced of EventSourced.EventSourcedActionBase<unit, EventSourced.NoSnapshotExtra>

/// Creates an actor that goes back to the given action if it restarts. NOTE: This function is deprecated,
/// use Simple.Spawn.NotPersisted instead.
let notPersisted action = NotPersisted action
/// Creates and actor that runs the given action. If the actor crashes then it restarts from the last point where it
/// was waiting for a message. NOTE: This function is deprecated, use Simple.Spawn.Checkpointed instead.
let checkpointed action = Checkpointed action
/// Creates an actor that uses the Akka.NET persistence event sourcing mechanism. In the event of a restart, the actor
/// will replay events that were stored using the EventSourced.Actions.persist action. NOTE: This function is deprecated,
/// use EventSourced.Spawn.NoSnapshots or EventSourced.Spawn.Snapshots instead.
let eventSourced action = EventSourced action

/// Creates a new actor that is a child of the given parent. The actor will be created using the given properties and
/// actor type. A reference to the new actor will be returned. NOTE: this function is deprecated, use the spawn* functions
/// in the Simple and EventSourced modules instead.
let spawn (parent: Akka.Actor.IActorRefFactory) (props: Common.Props) (actorType: ActorType) =

    match actorType with
    | NotPersisted action ->
        Simple.spawn parent props false (fun () -> action)
    | Checkpointed action ->
        Simple.spawn parent props true (fun () -> action)
    | EventSourced action ->
        EventSourced.spawnNoSnapshots parent {persistenceId = None; common = props} action
