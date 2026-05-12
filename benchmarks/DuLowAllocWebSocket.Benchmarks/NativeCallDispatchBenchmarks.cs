using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;

namespace DuLowAllocWebSocket.Benchmarks;

/// <summary>네이티브 함수 호출 시 delegate와 function pointer 호출 비용 비교.</summary>
[Config(typeof(DefaultConfig))]
public unsafe class NativeCallDispatchBenchmarks
{
    private delegate nint ZlibVersionDelegate();

    private nint _handle;
    private ZlibVersionDelegate _delegateCall = null!;
    private delegate* unmanaged[Cdecl]<nint> _functionPointerCall;

    [GlobalSetup]
    public void Setup()
    {
        if (!TryLoadZlib(out _handle))
        {
            throw new InvalidOperationException("zlib native library was not found.");
        }

        nint symbol = NativeLibrary.GetExport(_handle, "zlibVersion");
        _delegateCall = Marshal.GetDelegateForFunctionPointer<ZlibVersionDelegate>(symbol);
        _functionPointerCall = (delegate* unmanaged[Cdecl]<nint>)symbol;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_handle != 0)
        {
            NativeLibrary.Free(_handle);
            _handle = 0;
        }
    }

    [Benchmark(Baseline = true)]
    public nint DelegateCall()
    {
        return _delegateCall();
    }

    [Benchmark]
    public nint FunctionPointerCall()
    {
        return _functionPointerCall();
    }

    private static bool TryLoadZlib(out nint handle)
    {
        var assembly = typeof(DeflateInflater).Assembly;

        ReadOnlySpan<string> candidates =
        [
            "zlib1.dll",
            "libz.so.1",
            "libz.so",
            "libz.dylib"
        ];

        foreach (string candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, assembly, DllImportSearchPath.SafeDirectories, out handle))
            {
                return true;
            }
        }

        handle = 0;
        return false;
    }
}
