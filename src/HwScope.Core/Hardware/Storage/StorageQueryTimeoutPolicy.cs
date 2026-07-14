namespace HwScope.Core.Hardware.Storage;

public static class StorageQueryTimeoutPolicy
{
    public static Task<T> WaitAsync<T>(Task<T> operation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return operation.WaitAsync(timeout, cancellationToken);
    }
}
