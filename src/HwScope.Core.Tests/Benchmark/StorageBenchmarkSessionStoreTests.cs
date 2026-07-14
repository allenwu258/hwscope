using HwScope.Core.Benchmark.Storage;

namespace HwScope.Core.Tests.Benchmark;

public sealed class StorageBenchmarkSessionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"HwScope-SessionStoreTests-{Guid.NewGuid():N}");

    [Fact]
    public void FindsAndDeletesOnlyValidatedOrphan()
    {
        var manifests = Path.Combine(_root, "manifests");
        var sessions = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(sessions);
        var sessionId = Guid.NewGuid().ToString("N");
        var plan = CreatePlan(sessionId, sessions);
        var store = new StorageBenchmarkSessionStore(manifests);
        store.Create(plan);
        File.WriteAllBytes(plan.TestFilePath, new byte[4096]);
        var unrelated = Path.Combine(sessions, "user-data.bin");
        File.WriteAllText(unrelated, "keep");

        var orphan = Assert.Single(store.FindOrphans());
        var cleanup = store.TryCleanup(orphan);

        Assert.True(cleanup.Deleted);
        Assert.False(File.Exists(plan.TestFilePath));
        Assert.True(File.Exists(unrelated));
        Assert.Empty(store.FindOrphans());
    }

    [Fact]
    public void RejectsOrphanLargerThanPlannedFile()
    {
        var manifests = Path.Combine(_root, "manifests");
        var sessions = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(sessions);
        var plan = CreatePlan(Guid.NewGuid().ToString("N"), sessions);
        var store = new StorageBenchmarkSessionStore(manifests);
        store.Create(plan);
        using (var stream = new FileStream(plan.TestFilePath, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength(plan.Options.FileSizeBytes + 1);
        }

        var cleanup = store.TryCleanup(Assert.Single(store.FindOrphans()));

        Assert.False(cleanup.Deleted);
        Assert.Equal("fileValidationRejected", cleanup.Status);
        Assert.True(File.Exists(plan.TestFilePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private StorageBenchmarkPlan CreatePlan(string sessionId, string sessions)
    {
        var root = Path.GetPathRoot(_root) ?? throw new InvalidOperationException();
        var target = new StorageBenchmarkTarget(
            root, root, sessions, root.TrimEnd('\\'), "Test", "NTFS",
            100L * 1024 * 1024 * 1024, 80L * 1024 * 1024 * 1024,
            string.Empty, null, "", "Fixed", "", 4096, false, false);
        var options = new StorageBenchmarkOptions { Runs = 1, FileSizeBytes = 64L * 1024 * 1024 };
        return StorageBenchmarkPlanner.CreatePlan(target, options, [StorageBenchmarkWorkloads.Sequential1MiBQ1T1], sessionId);
    }
}
