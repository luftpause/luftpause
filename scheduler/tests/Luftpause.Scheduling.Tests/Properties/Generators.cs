using FsCheck;
using FsCheck.Fluent;

namespace Luftpause.Scheduling.Tests.Properties;

public static class Generators
{
    private const int Runs = 4;

    private static readonly Gen<long> Advance = Gen.Frequency(
        (6, Gen.Constant(0L)),
        (6, Gen.Constant(1_000L)),
        (2, Gen.Constant(60_000L)),
        (1, Gen.Constant(11 * 60_000L)),
        (1, Gen.Constant(31 * 60_000L)));

    private static readonly Gen<int> Pick = Gen.Choose(0, 7);

    private static Gen<Replica> ReplicaAt(int index) =>
        from engine in Gen.Frequency((5, Gen.Constant(EngineClass.Generative)), (1, Gen.Constant(EngineClass.SinglePass)))
        from models in Gen.Elements<string[]?>(null, ["m1"], ["m2"], ["m1", "m2"])
        select new Replica($"r{index}", $"http://r{index}", engine, Models: models);

    private static readonly Gen<Command> Request =
        from advance in Advance
        from run in Gen.Choose(0, Runs - 1)
        from engine in Gen.Frequency((5, Gen.Constant(EngineClass.Generative)), (1, Gen.Constant(EngineClass.SinglePass)))
        from service in Gen.Elements(ServiceClass.Interactive, ServiceClass.Batch)
        from model in Gen.Elements("m1", "m2")
        from caller in Gen.Elements("c0", "c1", "c2")
        from resumed in Gen.Frequency((3, Gen.Constant(false)), (1, Gen.Constant(true)))
        select (Command)new RequestTurn(advance, run, engine, service, model, caller, resumed);

    private static readonly Gen<Command> Complete =
        from advance in Advance
        from pick in Pick
        from toolCalls in Gen.Elements(true, false)
        from prompt in Gen.Choose(0, 3000)
        from completion in Gen.Choose(0, 500)
        select (Command)new CompleteTurn(advance, pick, toolCalls, prompt, completion);

    private static readonly Gen<Command> Abandon =
        from advance in Advance
        from pick in Pick
        from held in Gen.Elements(true, false)
        select (Command)new AbandonTurn(advance, pick, held);

    private static readonly Gen<Command> Load =
        from advance in Advance
        from replica in Gen.Choose(0, 2)
        from running in Gen.Choose(0, 6)
        from waiting in Gen.Choose(0, 6)
        select (Command)new ReportLoad(advance, replica, running, waiting);

    private static readonly Gen<Command> Health =
        from advance in Advance
        from replica in Gen.Choose(0, 2)
        from healthy in Gen.Frequency((2, Gen.Constant(true)), (1, Gen.Constant(false)))
        select (Command)new SetHealth(advance, replica, healthy);

    private static readonly Gen<Command> End =
        from advance in Advance
        from run in Gen.Choose(0, Runs - 1)
        select (Command)new EndRun(advance, run);

    private static readonly Gen<Command> Tick = Advance.Select(a => (Command)new TickClock(a));

    private static readonly Gen<Command> Command = Gen.Frequency(
        (8, Request),
        (6, Complete),
        (2, Abandon),
        (3, Load),
        (1, Health),
        (1, End),
        (2, Tick));

    private static Gen<int[]?> Seed(int replicas, Gen<int> each) => each.ArrayOf(replicas).Select(a => (int[]?)a);

    private static Gen<Scenario> Scenario(Func<int, Gen<int[]?>> capacity) =>
        from count in Gen.Choose(1, 3)
        from replicas in Replicas(count)
        from cap in capacity(count)
        from length in Gen.Choose(1, 40)
        from commands in Command.ArrayOf(length)
        select new Scenario(replicas, cap, commands);

    private static Gen<Replica[]> Replicas(int count) =>
        Enumerable.Range(0, count).Aggregate(
            Gen.Constant(Array.Empty<Replica>()),
            (soFar, index) => from replicas in soFar from replica in ReplicaAt(index) select (Replica[])[.. replicas, replica]);

    private static Gen<int[]?> None(int _) => Gen.Constant<int[]?>(null);

    public static Arbitrary<Scenario> Any() =>
        Arb.From(Scenario(None), Shrinker.Shrink);

    public static Arbitrary<Scenario> Capped() =>
        Arb.From(Scenario(n => Seed(n, Gen.Choose(1, 3))), Shrinker.Shrink);
}

public static class AnyScenario
{
    public static Arbitrary<Scenario> Scenario() => Generators.Any();
}

public static class CappedScenario
{
    public static Arbitrary<Scenario> Scenario() => Generators.Capped();
}
