using GnsNet;

NetworkObjectDescriptor SpawnPlayer(NetworkObjectRegistry<string> registry, string session, uint tick)
    => registry.Spawn(typeId: 1, owner: session, tick: tick);
