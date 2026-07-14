using System.Text.Json;

namespace HwScope.Core.Benchmark.Storage;

public sealed record StorageBenchmarkOrphan(
    string SessionId,
    string TargetRoot,
    string TestDirectory,
    string TestFilePath,
    long PlannedFileSizeBytes,
    DateTimeOffset CreatedAt,
    string ManifestPath);

public sealed class StorageBenchmarkSessionStore
{
    private readonly string _manifestDirectory;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public StorageBenchmarkSessionStore(string? manifestDirectory = null)
    {
        _manifestDirectory = manifestDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HwScope",
            "StorageBench",
            "Manifests");
    }

    public void Create(StorageBenchmarkPlan plan)
    {
        Directory.CreateDirectory(_manifestDirectory);
        var manifest = new SessionManifest(
            "HwScope.StorageBenchmark",
            plan.SessionId,
            plan.Target.RootPath,
            plan.Target.TestDirectory,
            plan.TestFilePath,
            plan.Options.FileSizeBytes,
            plan.CreatedAt);
        var path = GetManifestPath(plan.SessionId);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, manifest, _jsonOptions);
    }

    public void Complete(StorageBenchmarkPlan plan)
    {
        if (File.Exists(plan.TestFilePath))
        {
            return;
        }

        TryDeleteManifest(GetManifestPath(plan.SessionId));
    }

    public IReadOnlyList<StorageBenchmarkOrphan> FindOrphans()
    {
        if (!Directory.Exists(_manifestDirectory))
        {
            return [];
        }

        var orphans = new List<StorageBenchmarkOrphan>();
        foreach (var manifestPath in Directory.EnumerateFiles(_manifestDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var stream = File.OpenRead(manifestPath);
                var manifest = JsonSerializer.Deserialize<SessionManifest>(stream, _jsonOptions);
                if (manifest is null || !TryValidate(manifest, manifestPath, out var orphan))
                {
                    continue;
                }

                if (!File.Exists(orphan.TestFilePath))
                {
                    TryDeleteManifest(manifestPath);
                    continue;
                }
                orphans.Add(orphan);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        return orphans.OrderBy(orphan => orphan.CreatedAt).ToList();
    }

    public StorageBenchmarkCleanupResult TryCleanup(StorageBenchmarkOrphan orphan)
    {
        try
        {
            if (!TryValidate(
                    new SessionManifest("HwScope.StorageBenchmark", orphan.SessionId, orphan.TargetRoot, orphan.TestDirectory,
                        orphan.TestFilePath, orphan.PlannedFileSizeBytes, orphan.CreatedAt),
                    orphan.ManifestPath,
                    out var validated))
            {
                return new StorageBenchmarkCleanupResult(false, false, "manifestRejected");
            }

            if (!File.Exists(validated.TestFilePath))
            {
                TryDeleteManifest(validated.ManifestPath);
                return new StorageBenchmarkCleanupResult(true, true, "notFound");
            }

            var info = new FileInfo(validated.TestFilePath);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > validated.PlannedFileSizeBytes)
            {
                return new StorageBenchmarkCleanupResult(true, false, "fileValidationRejected");
            }

            info.Delete();
            TryDeleteManifest(validated.ManifestPath);
            return new StorageBenchmarkCleanupResult(true, true, "orphanDeleted");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new StorageBenchmarkCleanupResult(true, false, ex.GetType().Name);
        }
    }

    private bool TryValidate(SessionManifest manifest, string manifestPath, out StorageBenchmarkOrphan orphan)
    {
        orphan = default!;
        if (!string.Equals(manifest.CreatedBy, "HwScope.StorageBenchmark", StringComparison.Ordinal)
            || !Guid.TryParseExact(manifest.SessionId, "N", out _)
            || manifest.PlannedFileSizeBytes is < StorageBenchmarkPlanner.MinimumFileSizeBytes or > StorageBenchmarkPlanner.MaximumFileSizeBytes)
        {
            return false;
        }

        var expectedManifest = GetManifestPath(manifest.SessionId);
        var expectedFileName = $"hwscope-storagebench-{manifest.SessionId}.tmp";
        var fullFilePath = Path.GetFullPath(manifest.TestFilePath);
        var fullDirectory = Path.GetFullPath(manifest.TestDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var targetRoot = Path.GetPathRoot(Path.GetFullPath(manifest.TargetRoot));
        if (!string.Equals(Path.GetFullPath(manifestPath), expectedManifest, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(fullFilePath), expectedFileName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(fullFilePath), fullDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetPathRoot(fullFilePath), targetRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        orphan = new StorageBenchmarkOrphan(
            manifest.SessionId,
            manifest.TargetRoot,
            fullDirectory,
            fullFilePath,
            manifest.PlannedFileSizeBytes,
            manifest.CreatedAt,
            expectedManifest);
        return true;
    }

    private string GetManifestPath(string sessionId) => Path.GetFullPath(Path.Combine(_manifestDirectory, $"{sessionId}.json"));

    private static void TryDeleteManifest(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record SessionManifest(
        string CreatedBy,
        string SessionId,
        string TargetRoot,
        string TestDirectory,
        string TestFilePath,
        long PlannedFileSizeBytes,
        DateTimeOffset CreatedAt);
}
