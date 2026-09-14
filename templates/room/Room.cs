using GnsNet;

RoomLifecycle<string> CreateRoom()
{
    var room = new RoomLifecycle<string>(16) { AllowLateJoin = true };
    room.SceneChanged += transition => Console.WriteLine($"Scene {transition.From} -> {transition.To} at tick {transition.Tick}");
    return room;
}
