using System.Globalization;
using System.ComponentModel;
using HwScope.Core.Windows.Usb;

namespace HwScope.Core.Hardware.DeviceTopology.Usb;

internal interface IUsbDeviceDetailSource
{
    UsbDeviceDetailSnapshot Collect(UsbDeviceDetailTarget target);
}

internal sealed class UsbDeviceDetailCollector : IUsbDeviceDetailSource
{
    private const byte ConfigurationDescriptorType = 0x02;
    private const byte StringDescriptorType = 0x03;
    private const byte BosDescriptorType = 0x0F;
    private const int MaximumConfigurationCount = 32;
    private const int MaximumDescriptorLength = ushort.MaxValue;
    private const int MaximumDescriptorBytesPerDevice = 64 * 1024;
    private const int MaximumStringDescriptorLength = 255;

    public UsbDeviceDetailSnapshot Collect(UsbDeviceDetailTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var diagnostics = new List<DeviceTopologyDiagnostic>();
        var languages = Array.Empty<ushort>();
        var configurations = new List<UsbConfigurationDescriptorInfo>();
        var strings = new Dictionary<byte, string>();
        var attemptedStrings = new HashSet<byte>();
        var budget = new DescriptorReadBudget(MaximumDescriptorBytesPerDevice);
        UsbBosDescriptorInfo? bos = null;

        try
        {
            using var hub = UsbHubIoControl.OpenHub(target.ParentHubSymbolicName);
            languages = ReadLanguages(hub, target, budget, diagnostics);
            var languageId = SelectLanguage(languages);

            if (languageId.HasValue)
            {
                ReadString(hub, target, target.DeviceDescriptor.ManufacturerStringIndex, languageId.Value, strings, attemptedStrings, budget, diagnostics);
                ReadString(hub, target, target.DeviceDescriptor.ProductStringIndex, languageId.Value, strings, attemptedStrings, budget, diagnostics);
                ReadString(hub, target, target.DeviceDescriptor.SerialNumberStringIndex, languageId.Value, strings, attemptedStrings, budget, diagnostics);
            }

            var configurationCount = Math.Min((int)target.DeviceDescriptor.ConfigurationCount, MaximumConfigurationCount);
            if (target.DeviceDescriptor.ConfigurationCount > MaximumConfigurationCount)
            {
                diagnostics.Add(Diagnostic(
                    target,
                    "usb.detail.configuration-limit",
                    $"Device reported {target.DeviceDescriptor.ConfigurationCount} configurations; only the first {MaximumConfigurationCount} were read."));
            }

            for (byte index = 0; index < configurationCount; index++)
            {
                try
                {
                    if (!budget.TryReserve(9))
                    {
                        diagnostics.Add(BudgetDiagnostic(target));
                        break;
                    }

                    var header = hub.QueryDescriptor(target.PortNumber, ConfigurationDescriptorType, index, 0, 9);
                    var totalLength = UsbDescriptorParser.ReadConfigurationTotalLength(header);
                    if (totalLength > MaximumDescriptorLength)
                    {
                        throw new InvalidDataException($"Configuration {index} exceeds the {MaximumDescriptorLength}-byte descriptor limit.");
                    }

                    if (!budget.TryReserve(totalLength))
                    {
                        diagnostics.Add(BudgetDiagnostic(target));
                        break;
                    }

                    var raw = hub.QueryDescriptor(target.PortNumber, ConfigurationDescriptorType, index, 0, totalLength);
                    var initial = UsbDescriptorParser.ParseConfiguration(raw, index, target.DeviceDescriptor.UsbVersionBcd);
                    if (languageId.HasValue)
                    {
                        foreach (var stringIndex in EnumerateStringIndices(initial).Distinct())
                        {
                            ReadString(hub, target, stringIndex, languageId.Value, strings, attemptedStrings, budget, diagnostics);
                        }
                    }

                    configurations.Add(UsbDescriptorParser.ParseConfiguration(
                        raw,
                        index,
                        target.DeviceDescriptor.UsbVersionBcd,
                        strings));
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    diagnostics.Add(Diagnostic(
                        target,
                        "usb.detail.configuration-failed",
                        $"Unable to read configuration {index}: {ex.Message}"));
                }
            }

            if (target.DeviceDescriptor.UsbVersionBcd >= 0x0201)
            {
                try
                {
                    if (!budget.TryReserve(5))
                    {
                        diagnostics.Add(BudgetDiagnostic(target));
                        return CreateSnapshot(target, languages, configurations, strings, bos, diagnostics);
                    }

                    var header = hub.QueryDescriptor(target.PortNumber, BosDescriptorType, 0, 0, 5);
                    var totalLength = UsbDescriptorParser.ReadBosTotalLength(header);
                    if (!budget.TryReserve(totalLength))
                    {
                        diagnostics.Add(BudgetDiagnostic(target));
                        return CreateSnapshot(target, languages, configurations, strings, bos, diagnostics);
                    }

                    var raw = hub.QueryDescriptor(target.PortNumber, BosDescriptorType, 0, 0, totalLength);
                    bos = UsbDescriptorParser.ParseBos(raw);
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    diagnostics.Add(Diagnostic(
                        target,
                        "usb.detail.bos-failed",
                        $"Unable to read BOS descriptor: {ex.Message}",
                        DeviceTopologyDiagnosticSeverity.Information));
                }
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            diagnostics.Add(Diagnostic(
                target,
                "usb.detail.open-failed",
                $"Unable to read deep USB descriptors: {ex.Message}",
                DeviceTopologyDiagnosticSeverity.Error));
        }

        return CreateSnapshot(target, languages, configurations, strings, bos, diagnostics);
    }

    private static UsbDeviceDetailSnapshot CreateSnapshot(
        UsbDeviceDetailTarget target,
        IReadOnlyList<ushort> languages,
        IReadOnlyList<UsbConfigurationDescriptorInfo> configurations,
        IReadOnlyDictionary<byte, string> strings,
        UsbBosDescriptorInfo? bos,
        IReadOnlyList<DeviceTopologyDiagnostic> diagnostics)
    {
        return new UsbDeviceDetailSnapshot(
            target.AttachmentId,
            target.DeviceNodeId,
            Lookup(strings, target.DeviceDescriptor.ManufacturerStringIndex),
            Lookup(strings, target.DeviceDescriptor.ProductStringIndex),
            Lookup(strings, target.DeviceDescriptor.SerialNumberStringIndex),
            languages.Select(CreateLanguageInfo).ToArray(),
            configurations.ToArray(),
            bos,
            new DeviceTopologyDiagnostics(diagnostics),
            DateTimeOffset.Now);
    }

    private static ushort[] ReadLanguages(
        UsbHubIoControl hub,
        UsbDeviceDetailTarget target,
        DescriptorReadBudget budget,
        ICollection<DeviceTopologyDiagnostic> diagnostics)
    {
        if (!budget.TryReserve(MaximumStringDescriptorLength))
        {
            diagnostics.Add(BudgetDiagnostic(target));
            return [];
        }

        try
        {
            var raw = hub.QueryDescriptor(
                target.PortNumber,
                StringDescriptorType,
                0,
                0,
                MaximumStringDescriptorLength);
            return UsbDescriptorParser.ParseLanguageIds(raw).Distinct().ToArray();
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            diagnostics.Add(Diagnostic(
                target,
                "usb.detail.languages-failed",
                $"Unable to read supported descriptor languages: {ex.Message}"));
            return [];
        }
    }

    private static void ReadString(
        UsbHubIoControl hub,
        UsbDeviceDetailTarget target,
        byte index,
        ushort languageId,
        IDictionary<byte, string> strings,
        ISet<byte> attemptedStrings,
        DescriptorReadBudget budget,
        ICollection<DeviceTopologyDiagnostic> diagnostics)
    {
        if (index == 0 || !attemptedStrings.Add(index))
        {
            return;
        }

        if (!budget.TryReserve(MaximumStringDescriptorLength))
        {
            diagnostics.Add(BudgetDiagnostic(target));
            return;
        }

        try
        {
            var raw = hub.QueryDescriptor(
                target.PortNumber,
                StringDescriptorType,
                index,
                languageId,
                MaximumStringDescriptorLength);
            strings[index] = UsbDescriptorParser.ParseString(raw);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            diagnostics.Add(Diagnostic(
                target,
                "usb.detail.string-failed",
                $"Unable to read string descriptor {index} for language 0x{languageId:X4}: {ex.Message}"));
        }
    }

    private static IEnumerable<byte> EnumerateStringIndices(UsbConfigurationDescriptorInfo configuration)
    {
        if (configuration.DescriptionStringIndex != 0)
        {
            yield return configuration.DescriptionStringIndex;
        }

        foreach (var association in configuration.InterfaceAssociations)
        {
            if (association.DescriptionStringIndex != 0)
            {
                yield return association.DescriptionStringIndex;
            }
        }

        foreach (var item in configuration.Interfaces)
        {
            if (item.DescriptionStringIndex != 0)
            {
                yield return item.DescriptionStringIndex;
            }
        }
    }

    private static ushort? SelectLanguage(IReadOnlyList<ushort> languages)
    {
        return languages.Contains((ushort)0x0409) ? (ushort)0x0409 : languages.FirstOrDefault() is var first && first != 0 ? first : null;
    }

    private static UsbLanguageInfo CreateLanguageInfo(ushort languageId)
    {
        string name;
        try
        {
            name = CultureInfo.GetCultureInfo(languageId).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            name = "Unknown language";
        }

        return new UsbLanguageInfo(languageId, $"0x{languageId:X4} ({name})");
    }

    private static string? Lookup(IReadOnlyDictionary<byte, string> strings, byte index)
    {
        return index != 0 && strings.TryGetValue(index, out var value) ? value : null;
    }

    private static bool IsRecoverable(Exception exception)
    {
        return exception is InvalidDataException
            or IOException
            or TimeoutException
            or UnauthorizedAccessException
            or Win32Exception;
    }

    private static DeviceTopologyDiagnostic Diagnostic(
        UsbDeviceDetailTarget target,
        string code,
        string message,
        DeviceTopologyDiagnosticSeverity severity = DeviceTopologyDiagnosticSeverity.Warning)
    {
        return new DeviceTopologyDiagnostic(severity, code, message, target.DeviceNodeId);
    }

    private static DeviceTopologyDiagnostic BudgetDiagnostic(UsbDeviceDetailTarget target)
    {
        return Diagnostic(
            target,
            "usb.detail.descriptor-budget-exhausted",
            $"The {MaximumDescriptorBytesPerDevice / 1024} KiB per-device descriptor read budget was exhausted.");
    }

    private sealed class DescriptorReadBudget(int bytes)
    {
        private int _remaining = bytes;

        public bool TryReserve(int bytesToRead)
        {
            if (bytesToRead <= 0 || bytesToRead > _remaining)
            {
                return false;
            }

            _remaining -= bytesToRead;
            return true;
        }
    }
}
