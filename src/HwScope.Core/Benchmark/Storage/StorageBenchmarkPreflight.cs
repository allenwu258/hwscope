namespace HwScope.Core.Benchmark.Storage;

public static class StorageBenchmarkPreflight
{
    public static long Prepare(StorageBenchmarkPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var root = Path.GetPathRoot(Path.GetFullPath(plan.Target.RootPath))
            ?? throw new InvalidOperationException("无法解析目标卷。");
        var fileRoot = Path.GetPathRoot(Path.GetFullPath(plan.TestFilePath));
        if (!string.Equals(root, fileRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("测试文件不在选定目标卷上。");
        }

        var drive = new DriveInfo(root);
        if (!drive.IsReady)
        {
            throw new IOException("目标卷当前不可用。");
        }

        if (drive.DriveType is DriveType.Network or DriveType.CDRom or DriveType.Ram)
        {
            throw new NotSupportedException($"不支持在 {drive.DriveType} 目标上运行存储跑分。");
        }

        var reserve = Math.Max(2L * 1024 * 1024 * 1024, drive.TotalSize / 20);
        if (drive.AvailableFreeSpace < plan.Options.FileSizeBytes + reserve)
        {
            throw new IOException("目标卷当前可用空间不足，可能在选择后发生了变化。");
        }

        Directory.CreateDirectory(plan.Target.TestDirectory);
        var directory = new DirectoryInfo(plan.Target.TestDirectory);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new NotSupportedException("测试目录不能是 reparse point。");
        }

        if (File.Exists(plan.TestFilePath))
        {
            throw new IOException("同名 HwScope 测试文件已经存在；不会覆盖该文件。");
        }

        return drive.AvailableFreeSpace;
    }
}
