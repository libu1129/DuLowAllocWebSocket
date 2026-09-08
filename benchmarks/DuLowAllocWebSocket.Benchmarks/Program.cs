using BenchmarkDotNet.Running;
using DuLowAllocWebSocket.Benchmarks;

if (args.Length > 0 && args[0] == "--verify-fixtures")
{
    BenchmarkFixtureVerify.Run();
    return;
}

if (args.Length > 0 && args[0] == "manual")
{
    ManualBench.Run();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
