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

        ValidateCurrentTargetIdentity(plan.Target);

        var reserve = Math.Max(2L * 1024 * 1024 * 1024, drive.TotalSize / 20);
        if (drive.AvailableFreeSpace < plan.Options.FileSizeBytes + reserve)
        {
            throw new IOException("目标卷当前可用空间不足，可能在选择后发生了变化。");
        }

        Directory.CreateDirectory(plan.Target.TestDirectory);
        ValidateDirectoryAncestors(plan.Target.TestDirectory, root);
        ValidateCurrentTargetIdentity(plan.Target);

        if (File.Exists(plan.TestFilePath))
        {
            throw new IOException("同名 HwScope 测试文件已经存在；不会覆盖该文件。");
        }

        return drive.AvailableFreeSpace;
    }

    internal static void ValidateCurrentTargetIdentity(StorageBenchmarkTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.VolumeGuidPath)
            || target.VolumeSerialNumber is null
            || target.PhysicalDriveNumber is null)
        {
            throw new NotSupportedException("目标卷缺少可复核的 volume GUID、serial 或单一 physical extent。");
        }

        var currentIdentity = StorageBenchmarkVolumeIdentityQuery.TryQuery(target.RootPath)
            ?? throw new IOException("无法重新读取目标卷 identity。");
        if (!string.Equals(
                StorageBenchmarkVolumeIdentityQuery.NormalizeGuidPath(target.VolumeGuidPath),
                currentIdentity.GuidPath,
                StringComparison.OrdinalIgnoreCase)
            || target.VolumeSerialNumber.Value != currentIdentity.SerialNumber)
        {
            throw new IOException("目标卷 identity 在选择后发生了变化。");
        }

        var currentExtents = StorageBenchmarkVolumeExtentQuery.TryQuery(target.DriveLetter)
            ?? throw new IOException("无法重新读取目标卷 physical extents。");
        if (currentExtents.DiskNumbers.Count != 1
            || currentExtents.DiskNumbers[0] != target.PhysicalDriveNumber.Value)
        {
            throw new IOException("目标卷 physical extent 在选择后发生了变化，或不再是单盘卷。");
        }
    }

    internal static void ValidateDirectoryAncestors(string directoryPath, string rootPath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar);
        var current = new DirectoryInfo(Path.GetFullPath(directoryPath));
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new NotSupportedException($"测试目录祖先不能是 reparse point：{current.FullName}");
            }

            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            current = current.Parent;
        }

        throw new InvalidOperationException("测试目录无法回溯到目标卷根目录。");
    }
}
