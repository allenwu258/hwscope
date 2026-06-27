using System.Runtime.InteropServices;

namespace HwScope.Core.Windows;

internal static class LogicalProcessorInformation
{
    private const int RelationProcessorCore = 0;
    private const int RelationNumaNode = 1;
    private const int RelationCache = 2;
    private const int RelationProcessorPackage = 3;
    private const int RelationGroup = 4;
    private const int RelationAll = 0xffff;

    public static unsafe LogicalProcessorTopology? TryCollect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        uint length = 0;
        _ = GetLogicalProcessorInformationEx(RelationAll, null, ref length);
        if (length == 0)
        {
            return null;
        }

        var buffer = new byte[length];
        fixed (byte* ptr = buffer)
        {
            if (!GetLogicalProcessorInformationEx(RelationAll, ptr, ref length))
            {
                return null;
            }

            var groups = new List<LogicalProcessorGroup>();
            var cores = new List<LogicalCoreInfo>();
            var caches = new List<LogicalCacheInfo>();
            var packages = new List<LogicalPackageInfo>();
            var numaNodes = new List<LogicalNumaNodeInfo>();

            byte* cursor = ptr;
            byte* end = ptr + length;
            var coreIndex = 0;
            var packageIndex = 0;

            while (cursor + 8 <= end)
            {
                var relationship = *(int*)cursor;
                var size = *(uint*)(cursor + 4);
                if (size < 8 || cursor + size > end)
                {
                    return null;
                }

                var body = cursor + 8;
                var bodySize = size - 8;

                switch (relationship)
                {
                    case RelationProcessorCore:
                        if (TryReadCore(body, bodySize, coreIndex, out var core))
                        {
                            cores.Add(core);
                            coreIndex++;
                        }
                        break;
                    case RelationNumaNode:
                        if (TryReadNumaNode(body, bodySize, out var numaNode))
                        {
                            numaNodes.Add(numaNode);
                        }
                        break;
                    case RelationCache:
                        if (TryReadCache(body, bodySize, out var cache))
                        {
                            caches.Add(cache);
                        }
                        break;
                    case RelationProcessorPackage:
                        if (TryReadPackage(body, bodySize, packageIndex, out var package))
                        {
                            packages.Add(package);
                            packageIndex++;
                        }
                        break;
                    case RelationGroup:
                        if (TryReadGroup(body, bodySize, groups))
                        {
                            break;
                        }
                        break;
                }

                cursor += size;
            }

            var activeGroupCount = groups.Count > 0 ? groups.Count : CountDistinctGroups(cores.SelectMany(c => c.Masks));
            var maximumGroupCount = groups.Count > 0 ? groups.Max(g => g.Group + 1) : activeGroupCount;
            var logicalProcessorCount = groups.Count > 0
                ? groups.Sum(g => g.ActiveProcessorCount)
                : cores.SelectMany(c => c.Masks).Sum(m => m.Count);

            return new LogicalProcessorTopology(
                PackageCount: packages.Count,
                PhysicalCoreCount: cores.Count,
                LogicalProcessorCount: logicalProcessorCount,
                ActiveGroupCount: activeGroupCount,
                MaximumGroupCount: maximumGroupCount,
                NumaNodeCount: numaNodes.Count,
                Groups: groups,
                Cores: cores,
                Caches: caches.OrderBy(c => c.Level).ThenBy(c => c.Mask.Group).ThenBy(c => c.Mask.FirstProcessor).ToList(),
                Packages: packages,
                NumaNodes: numaNodes);
        }
    }

    private static unsafe bool TryReadCore(byte* body, uint bodySize, int index, out LogicalCoreInfo core)
    {
        core = default!;
        if (bodySize < 24)
        {
            return false;
        }

        var flags = *body;
        var efficiencyClass = *(body + 1);
        var groupCount = *(ushort*)(body + 22);
        if (!CanReadGroupMasks(bodySize, 24, groupCount))
        {
            return false;
        }

        core = new LogicalCoreInfo(index, (flags & 0x1) != 0, efficiencyClass, ReadGroupMasks(body + 24, groupCount));
        return true;
    }

    private static unsafe bool TryReadNumaNode(byte* body, uint bodySize, out LogicalNumaNodeInfo node)
    {
        node = default!;
        if (bodySize < 24)
        {
            return false;
        }

        var nodeNumber = *(uint*)body;
        var mask = ReadGroupMask(body + 8);
        node = new LogicalNumaNodeInfo(nodeNumber, mask);
        return true;
    }

    private static unsafe bool TryReadCache(byte* body, uint bodySize, out LogicalCacheInfo cache)
    {
        cache = default!;
        if (bodySize < 48)
        {
            return false;
        }

        var level = *body;
        var associativity = *(body + 1);
        var lineSize = *(ushort*)(body + 2);
        var size = *(uint*)(body + 4);
        var type = *(int*)(body + 8);
        var mask = ReadGroupMask(body + 32);

        cache = new LogicalCacheInfo(level, ToCacheType(type), size, lineSize, associativity, mask);
        return true;
    }

    private static unsafe bool TryReadPackage(byte* body, uint bodySize, int index, out LogicalPackageInfo package)
    {
        package = default!;
        if (bodySize < 24)
        {
            return false;
        }

        var groupCount = *(ushort*)(body + 22);
        if (!CanReadGroupMasks(bodySize, 24, groupCount))
        {
            return false;
        }

        package = new LogicalPackageInfo(index, ReadGroupMasks(body + 24, groupCount));
        return true;
    }

    private static unsafe bool TryReadGroup(byte* body, uint bodySize, List<LogicalProcessorGroup> groups)
    {
        if (bodySize < 24)
        {
            return false;
        }

        var activeGroupCount = *(ushort*)(body + 2);
        var required = 24u + (uint)activeGroupCount * 48u;
        if (required > bodySize)
        {
            return false;
        }

        var cursor = body + 24;
        for (ushort i = 0; i < activeGroupCount; i++)
        {
            var maximumProcessorCount = *cursor;
            var activeProcessorCount = *(cursor + 1);
            var activeProcessorMask = (ulong)(*(nuint*)(cursor + 40));
            groups.Add(new LogicalProcessorGroup(i, maximumProcessorCount, activeProcessorCount, new LogicalProcessorMask(i, activeProcessorMask)));
            cursor += 48;
        }

        return true;
    }

    private static bool CanReadGroupMasks(uint bodySize, uint offset, ushort count)
    {
        return offset + (uint)count * 16u <= bodySize;
    }

    private static unsafe IReadOnlyList<LogicalProcessorMask> ReadGroupMasks(byte* ptr, int count)
    {
        var result = new List<LogicalProcessorMask>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(ReadGroupMask(ptr + i * 16));
        }

        return result;
    }

    private static unsafe LogicalProcessorMask ReadGroupMask(byte* ptr)
    {
        var mask = (ulong)(*(nuint*)ptr);
        var group = *(ushort*)(ptr + 8);
        return new LogicalProcessorMask(group, mask);
    }

    private static LogicalCacheType ToCacheType(int type)
    {
        return type switch
        {
            0 => LogicalCacheType.Unified,
            1 => LogicalCacheType.Instruction,
            2 => LogicalCacheType.Data,
            3 => LogicalCacheType.Trace,
            _ => LogicalCacheType.Unknown
        };
    }

    private static int CountDistinctGroups(IEnumerable<LogicalProcessorMask> masks)
    {
        return masks.Select(mask => mask.Group).Distinct().Count();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool GetLogicalProcessorInformationEx(int relationshipType, byte* buffer, ref uint returnedLength);
}

internal sealed record LogicalProcessorTopology(
    int PackageCount,
    int PhysicalCoreCount,
    int LogicalProcessorCount,
    int ActiveGroupCount,
    int MaximumGroupCount,
    int NumaNodeCount,
    IReadOnlyList<LogicalProcessorGroup> Groups,
    IReadOnlyList<LogicalCoreInfo> Cores,
    IReadOnlyList<LogicalCacheInfo> Caches,
    IReadOnlyList<LogicalPackageInfo> Packages,
    IReadOnlyList<LogicalNumaNodeInfo> NumaNodes);

internal sealed record LogicalProcessorGroup(
    ushort Group,
    int MaximumProcessorCount,
    int ActiveProcessorCount,
    LogicalProcessorMask ActiveMask);

internal sealed record LogicalCoreInfo(
    int Index,
    bool HasSmt,
    byte EfficiencyClass,
    IReadOnlyList<LogicalProcessorMask> Masks);

internal sealed record LogicalCacheInfo(
    byte Level,
    LogicalCacheType Type,
    long SizeBytes,
    int LineSizeBytes,
    int Associativity,
    LogicalProcessorMask Mask);

internal sealed record LogicalPackageInfo(
    int Index,
    IReadOnlyList<LogicalProcessorMask> Masks);

internal sealed record LogicalNumaNodeInfo(
    uint NodeNumber,
    LogicalProcessorMask Mask);

internal sealed record LogicalProcessorMask(ushort Group, ulong Mask)
{
    public int Count => LocalProcessorIndexes.Count;

    public int FirstProcessor => LocalProcessorIndexes.DefaultIfEmpty(int.MaxValue).First();

    public IReadOnlyList<int> LocalProcessorIndexes { get; } = EnumerateProcessors(Mask).ToList();

    public string ProcessorRange => CompressRanges(LocalProcessorIndexes);

    public string HexMask => $"0x{Mask:X}";

    public string DisplayText => $"group {Group} [{ProcessorRange}] mask={HexMask}";

    private static IEnumerable<int> EnumerateProcessors(ulong mask)
    {
        for (var i = 0; i < 64; i++)
        {
            if ((mask & (1UL << i)) != 0)
            {
                yield return i;
            }
        }
    }

    private static string CompressRanges(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
        {
            return string.Empty;
        }

        var ranges = new List<string>();
        var start = values[0];
        var previous = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] == previous + 1)
            {
                previous = values[i];
                continue;
            }

            ranges.Add(start == previous ? start.ToString() : $"{start}-{previous}");
            start = previous = values[i];
        }

        ranges.Add(start == previous ? start.ToString() : $"{start}-{previous}");
        return string.Join(",", ranges);
    }
}

internal enum LogicalCacheType
{
    Unified,
    Instruction,
    Data,
    Trace,
    Unknown
}
