using System.Net.WebSockets;
using System.Reflection;
using Xunit;

namespace DuLowAllocWebSocket.Tests;

public sealed class DuLowAllocWebSocketClientLifecycleTests
{
    [Fact]
    public void Dispose_LeavesSendSemaphoreUsableForAlreadyAdmittedWaiters()
    {
        using var client = new DuLowAllocWebSocketClient(new WebSocketClientOptions
        {
            EnablePerMessageDeflate = false,
            KeepAliveInterval = TimeSpan.Zero,
        });
        SemaphoreSlim sendLock = GetField<SemaphoreSlim>(client, "_sendLock");

        client.Dispose();

        Assert.True(sendLock.Wait(millisecondsTimeout: 0));
        sendLock.Release();
    }

    [Fact]
    public async Task ManagedCleanup_DoesNotStrandAlreadyAdmittedSendWaiters()
    {
        using var client = new DuLowAllocWebSocketClient(new WebSocketClientOptions
        {
            EnablePerMessageDeflate = false,
            KeepAliveInterval = TimeSpan.Zero,
        });
        SemaphoreSlim sendLock = GetField<SemaphoreSlim>(client, "_sendLock");
        await sendLock.WaitAsync();
        Task firstWaiter = sendLock.WaitAsync();
        Task secondWaiter = sendLock.WaitAsync();
        bool firstOwnsLock = false;
        bool secondOwnsLock = false;

        SetField(client, "_disposeStarted", 1);
        SetField(client, "_closing", 2);
        SetField(client, "_receivePumpExited", 1);
        SetField(client, "_state", (int)WebSocketState.Closed);

        MethodInfo cleanup = typeof(DuLowAllocWebSocketClient).GetMethod(
            "TryDisposeManagedResources",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TryDisposeManagedResources was not found.");

        try
        {
            // teardown owner가 lock을 놓으면 첫 waiter만 깨어나 아직 lock을 소유한다.
            sendLock.Release();
            await firstWaiter.WaitAsync(TimeSpan.FromSeconds(5));
            firstOwnsLock = true;

            cleanup.Invoke(client, null);

            // 첫 waiter의 finally Release가 두 번째 waiter까지 반드시 깨워야 한다.
            sendLock.Release();
            firstOwnsLock = false;
            await secondWaiter.WaitAsync(TimeSpan.FromSeconds(5));
            secondOwnsLock = true;
        }
        finally
        {
            if (firstOwnsLock)
            {
                try { sendLock.Release(); } catch { }
            }
            if (secondOwnsLock)
            {
                try { sendLock.Release(); } catch { }
            }
        }
    }

    [Fact]
    public async Task Dispose_WhenAnotherThreadOwnsTeardown_DoesNotDisposeSendLockEarly()
    {
        using var receiveThreadStarted = new ManualResetEventSlim();
        using var releaseReceiveThread = new ManualResetEventSlim();
        var receiveThread = new Thread(() =>
        {
            receiveThreadStarted.Set();
            releaseReceiveThread.Wait();
        })
        {
            IsBackground = true,
            Name = "DuLowAllocWebSocket.TestReceivePump"
        };

        using var client = new DuLowAllocWebSocketClient(new WebSocketClientOptions
        {
            EnablePerMessageDeflate = false,
            KeepAliveInterval = TimeSpan.Zero,
        });

        SetField(client, "_unsafeReceivePumpThread", receiveThread);
        SetField(client, "_state", (int)WebSocketState.Open);
        receiveThread.Start();
        Assert.True(receiveThreadStarted.Wait(TimeSpan.FromSeconds(5)));

        MethodInfo closeTransport = typeof(DuLowAllocWebSocketClient).GetMethod(
            "CloseTransport",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CloseTransport was not found.");

        Task<bool> teardownOwner = Task.Run(() => (bool)closeTransport.Invoke(client, null)!);
        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => GetField<int>(client, "_closing") == 1,
                TimeSpan.FromSeconds(5)));

            // 기존 구현은 다른 teardown이 진행 중인데도 성공으로 간주하고 여기서
            // send lock을 Dispose하여, join 중인 소유자가 재개될 때 예외를 냈다.
            client.Dispose();
            Assert.False(teardownOwner.IsCompleted);

            releaseReceiveThread.Set();
            Assert.True(await teardownOwner.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, GetField<int>(client, "_managedResourcesDisposed"));
        }
        finally
        {
            releaseReceiveThread.Set();
            receiveThread.Join(millisecondsTimeout: 5_000);
            try { await teardownOwner.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    private static void SetField<T>(DuLowAllocWebSocketClient client, string name, T value)
    {
        FieldInfo field = typeof(DuLowAllocWebSocketClient).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{name} was not found.");
        field.SetValue(client, value);
    }

    private static T GetField<T>(DuLowAllocWebSocketClient client, string name)
    {
        FieldInfo field = typeof(DuLowAllocWebSocketClient).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{name} was not found.");
        return (T)field.GetValue(client)!;
    }
}
