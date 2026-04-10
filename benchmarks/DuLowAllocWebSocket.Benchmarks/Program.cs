using BenchmarkDotNet.Running;
using DuLowAllocWebSocket.Benchmarks;

if (args.Length > 0 && args[0] == "manual")
{
    ManualBench.Run();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
