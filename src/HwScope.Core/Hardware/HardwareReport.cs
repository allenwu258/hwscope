namespace HwScope.Core.Hardware;

public sealed record HardwareReport(
    string Processor,
    string Motherboard,
    string Memory,
    string Graphics,
    string Display,
    string Disk,
    IReadOnlyList<string> Audio,
    string Network,
    DateTimeOffset GeneratedAt);

