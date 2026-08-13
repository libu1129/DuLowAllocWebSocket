using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace DuLowAllocWebSocket.Tests;

public sealed class RuntimeAsyncCompilationTests
{
    [Fact]
    public void AsyncMethods_UseExpectedCompilationModelForTargetFramework()
    {
        var methods = new[]
        {
            typeof(FrameWriter).GetMethod(nameof(FrameWriter.SendAsync)),
            typeof(DuLowAllocWebSocketClient).GetMethod(nameof(DuLowAllocWebSocketClient.CloseAsync)),
        };

        foreach (var method in methods)
        {
            Assert.NotNull(method);
            var stateMachine = method!.GetCustomAttribute<AsyncStateMachineAttribute>();
#if RUNTIME_ASYNC_ENABLED
            Assert.Null(stateMachine);
#else
            Assert.NotNull(stateMachine);
#endif
        }
    }
}
