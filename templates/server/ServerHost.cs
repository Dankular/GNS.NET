using GnsNet;

// Construct a GnsServer after GnsRuntime.Initialize(), then compose the authoritative facade.
// The game owns applyInput; clients never submit state, only serialized input.
GnsAuthoritativeServer<string, WorldState, PlayerInput> CreateServer(GnsServer transport, ConnectionAdmission admission)
    => new(transport, new WorldState(0, 0),
        (state, _, input) => state with { X = state.X + input.MoveX, Y = state.Y + input.MoveY },
        new ServerInputGuard<string, PlayerInput>(), TimeSpan.FromSeconds(30), admission);
