using MemoryPack;

[MemoryPackable]
public partial record WorldState(int X, int Y);

[MemoryPackable]
public partial record PlayerInput(int MoveX, int MoveY);
