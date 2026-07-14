using HwScope.Core.Hardware.Storage;

namespace HwScope.Core.Tests.Hardware.Storage;

public sealed class StorageQueryTimeoutPolicyTests
{
    [Fact]
    public async Task WaitAsync_TimesOutWithoutCancellingUnderlyingOperation()
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            StorageQueryTimeoutPolicy.WaitAsync(completion.Task, TimeSpan.FromMilliseconds(50), CancellationToken.None));

        Assert.False(completion.Task.IsCompleted);
        completion.SetResult(42);
        Assert.Equal(42, await completion.Task);
    }

    [Fact]
    public async Task WaitAsync_ReturnsCompletedResult()
    {
        var result = await StorageQueryTimeoutPolicy.WaitAsync(Task.FromResult(42), TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(42, result);
    }
}
