using GnsNet;

GnsPredictedClient<PlayerInput, WorldState> CreateClient(GnsClientHost transport)
    => new(transport, new WorldState(0, 0),
        (state, input) => state with { X = state.X + input.MoveX, Y = state.Y + input.MoveY },
        (from, to, amount) => new WorldState((int)(from.X + (to.X - from.X) * amount), (int)(from.Y + (to.Y - from.Y) * amount)));
