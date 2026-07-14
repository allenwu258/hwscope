using System.Text;

namespace HwScope.Core.Hardware.Storage;

public static class StorageDetailReportFormatter
{
    public static string Format(StorageDetailReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Storage Device");
        builder.AppendLine();

        builder.AppendLine("Identity");
        Append(builder, "Disk", report.Identity.PhysicalDriveNumber);
        Append(builder, "Model", report.Identity.Model);
        Append(builder, "Firmware", report.Identity.Firmware);
        Append(builder, "Serial", report.Identity.SerialNumber);
        Append(builder, "Capacity", report.Identity.Capacity);
        Append(builder, "Media Type", report.Identity.MediaType);
        Append(builder, "Device Path", report.Identity.DevicePath);
        builder.AppendLine();

        builder.AppendLine("Interface");
        Append(builder, "Bus", report.Interface.BusType);
        Append(builder, "Standard", report.Interface.Standard);
        Append(builder, "Current Link", report.Interface.CurrentLink);
        Append(builder, "Maximum Link", report.Interface.MaximumLink);
        Append(builder, "Logical Sector", report.Interface.LogicalSectorSize);
        Append(builder, "Physical Sector", report.Interface.PhysicalSectorSize);
        builder.AppendLine($"Features: {(report.Interface.Features.Count == 0 ? StorageField.UnknownText : string.Join(", ", report.Interface.Features))}");
        builder.AppendLine();

        builder.AppendLine("Health");
        builder.AppendLine($"Status: {report.Health.StatusText}");
        builder.AppendLine($"Reason: {report.Health.StatusReason}");
        Append(builder, "Temperature", report.Health.TemperatureCelsius);
        Append(builder, "Remaining Life", report.Health.RemainingLifePercent);
        builder.AppendLine();

        builder.AppendLine("Lifetime");
        Append(builder, "Host Reads", report.Lifetime.HostReads);
        Append(builder, "Host Writes", report.Lifetime.HostWrites);
        Append(builder, "Power Cycles", report.Lifetime.PowerCycles);
        Append(builder, "Power-On Hours", report.Lifetime.PowerOnHours);
        Append(builder, "Unsafe Shutdowns", report.Lifetime.UnsafeShutdowns);
        Append(builder, "Media Errors", report.Lifetime.MediaErrors);
        Append(builder, "Error Log Entries", report.Lifetime.ErrorLogEntries);
        builder.AppendLine();

        builder.AppendLine("Protocol Attributes");
        foreach (var attribute in report.Attributes)
        {
            var unit = string.IsNullOrWhiteSpace(attribute.Unit) ? string.Empty : $" {attribute.Unit}";
            builder.AppendLine($"{attribute.Id} {attribute.Name}: {attribute.DisplayValue}{unit} [raw {attribute.RawValue}] [{FormatSource(attribute.Source)}]");
        }
        builder.AppendLine();

        builder.AppendLine("Partitions");
        foreach (var partition in report.Partitions)
        {
            builder.AppendLine($"Partition {partition.PartitionNumber}: {StorageField.FormatBinaryBytes(partition.SizeBytes)}, {partition.Style}, {string.Join(", ", partition.AccessPaths)}");
        }
        builder.AppendLine();

        builder.AppendLine("Volumes");
        foreach (var volume in report.Volumes)
        {
            var name = string.IsNullOrWhiteSpace(volume.DriveLetter) ? volume.Path : volume.DriveLetter;
            var separator = string.IsNullOrWhiteSpace(volume.DriveLetter) ? ":" : string.Empty;
            builder.AppendLine($"{name}{separator} {volume.Label} {volume.FileSystem}, {StorageField.FormatBinaryBytes(volume.SizeBytes)}, free {StorageField.FormatBinaryBytes(volume.FreeBytes)}, {string.Join(", ", volume.Roles)}");
        }

        if (report.Notes.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Notes");
            foreach (var note in report.Notes)
            {
                builder.AppendLine($"- {note.Message} [{FormatSource(note.Source)}]");
            }
        }

        builder.AppendLine();
        builder.AppendLine($"Generated At: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
        return builder.ToString().TrimEnd();
    }

    private static void Append<T>(StringBuilder builder, string label, StorageFieldValue<T> field)
    {
        builder.AppendLine($"{label}: {field.DisplayText} [{FormatSource(field.Source, field.IsEstimated)}]");
    }

    private static string FormatSource(StorageDataSource source, bool estimated = false)
    {
        var value = source switch
        {
            StorageDataSource.Wmi => "WMI",
            StorageDataSource.WindowsStorage => "Windows Storage",
            StorageDataSource.StorageApi => "Storage API",
            StorageDataSource.Nvme => "NVMe",
            StorageDataSource.AtaSmart => "ATA SMART",
            StorageDataSource.Scsi => "SCSI",
            StorageDataSource.StorageSpaces => "Storage Spaces",
            StorageDataSource.Computed => "Computed",
            StorageDataSource.Placeholder => "Pending",
            _ => "Unknown"
        };
        return estimated ? $"{value}/estimated" : value;
    }
}
