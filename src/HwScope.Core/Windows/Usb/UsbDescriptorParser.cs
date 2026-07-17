using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using HwScope.Core.Hardware.DeviceTopology.Usb;

namespace HwScope.Core.Windows.Usb;

internal static class UsbDescriptorParser
{
    internal const int MaximumInterfacesPerConfiguration = 256;
    internal const int MaximumEndpointsPerConfiguration = 1_024;
    internal const int MaximumAssociationsPerConfiguration = 256;
    internal const int MaximumAdditionalDescriptorsPerConfiguration = 1_024;
    internal const int MaximumBosCapabilities = 256;
    private const byte ConfigurationDescriptorType = 0x02;
    private const byte StringDescriptorType = 0x03;
    private const byte InterfaceDescriptorType = 0x04;
    private const byte EndpointDescriptorType = 0x05;
    private const byte InterfaceAssociationDescriptorType = 0x0B;
    private const byte BosDescriptorType = 0x0F;
    private const byte DeviceCapabilityDescriptorType = 0x10;
    private const byte SuperSpeedEndpointCompanionDescriptorType = 0x30;
    private const byte HidDescriptorType = 0x21;
    private const byte ClassSpecificInterfaceDescriptorType = 0x24;
    private const byte ClassSpecificEndpointDescriptorType = 0x25;

    public static ushort ReadConfigurationTotalLength(ReadOnlySpan<byte> data)
    {
        ValidateDescriptorHeader(data, 9, ConfigurationDescriptorType, "USB configuration descriptor");
        var totalLength = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        if (totalLength < 9)
        {
            throw new InvalidDataException($"USB configuration descriptor reported invalid wTotalLength {totalLength}.");
        }

        return totalLength;
    }

    public static UsbConfigurationDescriptorInfo ParseConfiguration(
        ReadOnlySpan<byte> data,
        byte descriptorIndex,
        ushort usbVersionBcd,
        IReadOnlyDictionary<byte, string>? strings = null)
    {
        var totalLength = ReadConfigurationTotalLength(data);
        if (totalLength > data.Length)
        {
            throw new InvalidDataException($"USB configuration descriptor requires {totalLength} bytes, received {data.Length}.");
        }

        var bounded = data[..totalLength];
        var interfaces = new List<UsbInterfaceDescriptorInfo>();
        var associations = new List<UsbInterfaceAssociationInfo>();
        var additional = new List<UsbRawDescriptorInfo>();
        var ordered = new List<UsbConfigurationDescriptorEntryInfo>();
        var endpointCount = 0;
        int? currentInterfaceIndex = null;
        int? currentEndpointIndex = null;
        byte? previousDescriptorType = null;
        var offset = 0;
        while (offset < bounded.Length)
        {
            if (bounded.Length - offset < 2)
            {
                throw new InvalidDataException($"USB descriptor header is truncated at offset {offset}.");
            }

            var length = bounded[offset];
            var type = bounded[offset + 1];
            if (length < 2)
            {
                throw new InvalidDataException($"USB descriptor at offset {offset} reported invalid bLength {length}.");
            }

            if (offset + length > bounded.Length)
            {
                throw new InvalidDataException($"USB descriptor at offset {offset} exceeds configuration wTotalLength.");
            }

            var descriptor = bounded.Slice(offset, length);
            switch (type)
            {
                case ConfigurationDescriptorType when offset == 0:
                    if (length < 9)
                    {
                        throw new InvalidDataException("USB configuration descriptor is shorter than 9 bytes.");
                    }
                    break;
                case InterfaceDescriptorType:
                    EnsureCountWithinLimit(
                        interfaces.Count,
                        MaximumInterfacesPerConfiguration,
                        "interfaces");
                    currentInterfaceIndex = interfaces.Count;
                    currentEndpointIndex = null;
                    interfaces.Add(ParseInterface(descriptor, strings));
                    ordered.Add(OrderedEntry(
                        offset,
                        descriptor,
                        UsbConfigurationDescriptorEntryKind.Interface,
                        UsbConfigurationDescriptorOwnerKind.Configuration,
                        interfaceIndex: currentInterfaceIndex));
                    break;
                case EndpointDescriptorType:
                    if (interfaces.Count == 0)
                    {
                        throw new InvalidDataException($"USB endpoint descriptor at offset {offset} has no preceding interface.");
                    }

                    EnsureCountWithinLimit(
                        endpointCount,
                        MaximumEndpointsPerConfiguration,
                        "endpoints");

                    var currentInterface = interfaces[^1];
                    currentEndpointIndex = currentInterface.Endpoints.Length;
                    interfaces[^1] = currentInterface with
                    {
                        Endpoints = currentInterface.Endpoints.Add(ParseEndpoint(descriptor))
                    };
                    ordered.Add(OrderedEntry(
                        offset,
                        descriptor,
                        UsbConfigurationDescriptorEntryKind.Endpoint,
                        UsbConfigurationDescriptorOwnerKind.Interface,
                        interfaceIndex: currentInterfaceIndex,
                        endpointIndex: currentEndpointIndex));
                    endpointCount++;
                    break;
                case SuperSpeedEndpointCompanionDescriptorType:
                    if (previousDescriptorType != EndpointDescriptorType
                        || interfaces.Count == 0
                        || interfaces[^1].Endpoints.IsDefaultOrEmpty)
                    {
                        throw new InvalidDataException(
                            $"USB SuperSpeed endpoint companion at offset {offset} does not immediately follow an endpoint.");
                    }

                    var owner = interfaces[^1];
                    var endpointIndex = owner.Endpoints.Length - 1;
                    var endpoint = owner.Endpoints[endpointIndex];
                    if (endpoint.SuperSpeedCompanion is not null)
                    {
                        throw new InvalidDataException(
                            $"USB endpoint at offset {offset} has more than one SuperSpeed companion.");
                    }

                    interfaces[^1] = owner with
                    {
                        Endpoints = owner.Endpoints.SetItem(
                            endpointIndex,
                            endpoint with
                            {
                                SuperSpeedCompanion = ParseSuperSpeedEndpointCompanion(descriptor)
                            })
                    };
                    ordered.Add(OrderedEntry(
                        offset,
                        descriptor,
                        UsbConfigurationDescriptorEntryKind.SuperSpeedEndpointCompanion,
                        UsbConfigurationDescriptorOwnerKind.Endpoint,
                        interfaceIndex: currentInterfaceIndex,
                        endpointIndex: currentEndpointIndex));
                    break;
                case InterfaceAssociationDescriptorType:
                    EnsureCountWithinLimit(
                        associations.Count,
                        MaximumAssociationsPerConfiguration,
                        "interface associations");
                    currentInterfaceIndex = null;
                    currentEndpointIndex = null;
                    var associationIndex = associations.Count;
                    associations.Add(ParseInterfaceAssociation(descriptor, strings));
                    ordered.Add(OrderedEntry(
                        offset,
                        descriptor,
                        UsbConfigurationDescriptorEntryKind.InterfaceAssociation,
                        UsbConfigurationDescriptorOwnerKind.Configuration,
                        interfaceAssociationIndex: associationIndex));
                    break;
                default:
                    if (offset != 0)
                    {
                        EnsureCountWithinLimit(
                            additional.Count,
                            MaximumAdditionalDescriptorsPerConfiguration,
                            "additional descriptors");
                        var additionalIndex = additional.Count;
                        additional.Add(new UsbRawDescriptorInfo(type, length, descriptor.ToArray().ToImmutableArray()));
                        var ownerIsHeuristic = false;
                        UsbConfigurationDescriptorOwnerKind ownerKind;
                        if (type == ClassSpecificEndpointDescriptorType && currentEndpointIndex.HasValue)
                        {
                            ownerKind = UsbConfigurationDescriptorOwnerKind.Endpoint;
                        }
                        else if ((type == HidDescriptorType || type == ClassSpecificInterfaceDescriptorType)
                            && currentInterfaceIndex.HasValue)
                        {
                            ownerKind = UsbConfigurationDescriptorOwnerKind.Interface;
                        }
                        else if (currentInterfaceIndex.HasValue)
                        {
                            ownerKind = UsbConfigurationDescriptorOwnerKind.Interface;
                            ownerIsHeuristic = true;
                        }
                        else
                        {
                            ownerKind = UsbConfigurationDescriptorOwnerKind.Configuration;
                        }
                        ordered.Add(OrderedEntry(
                            offset,
                            descriptor,
                            UsbConfigurationDescriptorEntryKind.Additional,
                            ownerKind,
                            interfaceIndex: ownerKind == UsbConfigurationDescriptorOwnerKind.Configuration
                                ? null
                                : currentInterfaceIndex,
                            endpointIndex: ownerKind == UsbConfigurationDescriptorOwnerKind.Endpoint
                                ? currentEndpointIndex
                                : null,
                            additionalDescriptorIndex: additionalIndex,
                            ownerIsHeuristic: ownerIsHeuristic));
                    }
                    break;
            }

            previousDescriptorType = type;
            offset += length;
        }

        var attributes = bounded[7];
        var powerUnitMilliamps = usbVersionBcd >= 0x0300 ? 8 : 2;
        return new UsbConfigurationDescriptorInfo(
            descriptorIndex,
            totalLength,
            bounded[4],
            bounded[5],
            bounded[6],
            Lookup(strings, bounded[6]),
            attributes,
            (attributes & 0x40) != 0,
            (attributes & 0x20) != 0,
            bounded[8] * powerUnitMilliamps,
            associations.ToImmutableArray(),
            interfaces.ToImmutableArray(),
            additional.ToImmutableArray(),
            bounded.ToArray().ToImmutableArray())
        {
            OrderedDescriptors = ordered.ToImmutableArray()
        };
    }

    private static UsbConfigurationDescriptorEntryInfo OrderedEntry(
        int offset,
        ReadOnlySpan<byte> descriptor,
        UsbConfigurationDescriptorEntryKind kind,
        UsbConfigurationDescriptorOwnerKind ownerKind,
        int? interfaceAssociationIndex = null,
        int? interfaceIndex = null,
        int? endpointIndex = null,
        int? additionalDescriptorIndex = null,
        bool ownerIsHeuristic = false)
    {
        return new UsbConfigurationDescriptorEntryInfo(
            offset,
            descriptor[1],
            descriptor[0],
            kind,
            ownerKind,
            interfaceAssociationIndex,
            interfaceIndex,
            endpointIndex,
            additionalDescriptorIndex,
            ownerIsHeuristic,
            descriptor.ToArray().ToImmutableArray());
    }

    public static ushort ReadBosTotalLength(ReadOnlySpan<byte> data)
    {
        ValidateDescriptorHeader(data, 5, BosDescriptorType, "USB BOS descriptor");
        var totalLength = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        if (totalLength < 5)
        {
            throw new InvalidDataException($"USB BOS descriptor reported invalid wTotalLength {totalLength}.");
        }

        return totalLength;
    }

    public static UsbBosDescriptorInfo ParseBos(ReadOnlySpan<byte> data)
    {
        var totalLength = ReadBosTotalLength(data);
        if (totalLength > data.Length)
        {
            throw new InvalidDataException($"USB BOS descriptor requires {totalLength} bytes, received {data.Length}.");
        }

        var bounded = data[..totalLength];
        var capabilities = new List<UsbBosCapabilityInfo>();
        var offset = bounded[0];
        while (offset < bounded.Length)
        {
            if (bounded.Length - offset < 3)
            {
                throw new InvalidDataException($"USB BOS capability header is truncated at offset {offset}.");
            }

            var length = bounded[offset];
            if (length < 3 || offset + length > bounded.Length)
            {
                throw new InvalidDataException($"USB BOS capability at offset {offset} reported invalid bLength {length}.");
            }

            var descriptor = bounded.Slice(offset, length);
            if (descriptor[1] != DeviceCapabilityDescriptorType)
            {
                throw new InvalidDataException($"USB BOS child at offset {offset} has unexpected descriptor type 0x{descriptor[1]:X2}.");
            }

            EnsureCountWithinLimit(capabilities.Count, MaximumBosCapabilities, "BOS capabilities");
            capabilities.Add(new UsbBosCapabilityInfo(
                descriptor[2],
                FormatCapabilityName(descriptor[2]),
                descriptor.ToArray().ToImmutableArray()));
            offset += length;
        }

        return new UsbBosDescriptorInfo(
            totalLength,
            bounded[4],
            capabilities.ToImmutableArray(),
            bounded.ToArray().ToImmutableArray());
    }

    public static IReadOnlyList<ushort> ParseLanguageIds(ReadOnlySpan<byte> data)
    {
        ValidateDescriptorHeader(data, 2, StringDescriptorType, "USB language string descriptor");
        var length = data[0];
        if ((length & 1) != 0 || length > data.Length)
        {
            throw new InvalidDataException($"USB language string descriptor reported invalid bLength {length}.");
        }

        var languages = new List<ushort>();
        for (var offset = 2; offset + 1 < length; offset += 2)
        {
            languages.Add(BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]));
        }

        return languages;
    }

    public static string ParseString(ReadOnlySpan<byte> data)
    {
        ValidateDescriptorHeader(data, 2, StringDescriptorType, "USB string descriptor");
        var length = data[0];
        if ((length & 1) != 0 || length > data.Length)
        {
            throw new InvalidDataException($"USB string descriptor reported invalid bLength {length}.");
        }

        return length == 2 ? string.Empty : Encoding.Unicode.GetString(data.Slice(2, length - 2)).TrimEnd('\0');
    }

    private static UsbInterfaceDescriptorInfo ParseInterface(
        ReadOnlySpan<byte> descriptor,
        IReadOnlyDictionary<byte, string>? strings)
    {
        ValidateDescriptorHeader(descriptor, 9, InterfaceDescriptorType, "USB interface descriptor");
        return new UsbInterfaceDescriptorInfo(
            descriptor[2],
            descriptor[3],
            descriptor[4],
            descriptor[5],
            descriptor[6],
            descriptor[7],
            descriptor[8],
            Lookup(strings, descriptor[8]),
            ImmutableArray<UsbEndpointDescriptorInfo>.Empty);
    }

    private static UsbEndpointDescriptorInfo ParseEndpoint(ReadOnlySpan<byte> descriptor)
    {
        ValidateDescriptorHeader(descriptor, 7, EndpointDescriptorType, "USB endpoint descriptor");
        var address = descriptor[2];
        var attributes = descriptor[3];
        var rawMaximumPacketSize = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[4..]);
        return new UsbEndpointDescriptorInfo(
            address,
            (address & 0x80) != 0 ? UsbEndpointDirection.In : UsbEndpointDirection.Out,
            (UsbEndpointTransferType)(attributes & 0x03),
            (byte)((attributes >> 2) & 0x03),
            (byte)((attributes >> 4) & 0x03),
            rawMaximumPacketSize,
            rawMaximumPacketSize & 0x07FF,
            ((rawMaximumPacketSize >> 11) & 0x03) + 1,
            descriptor[6],
            null);
    }

    private static UsbSuperSpeedEndpointCompanionInfo ParseSuperSpeedEndpointCompanion(ReadOnlySpan<byte> descriptor)
    {
        ValidateDescriptorHeader(
            descriptor,
            6,
            SuperSpeedEndpointCompanionDescriptorType,
            "USB SuperSpeed endpoint companion descriptor");
        return new UsbSuperSpeedEndpointCompanionInfo(
            descriptor[2],
            descriptor[3],
            BinaryPrimitives.ReadUInt16LittleEndian(descriptor[4..]));
    }

    private static UsbInterfaceAssociationInfo ParseInterfaceAssociation(
        ReadOnlySpan<byte> descriptor,
        IReadOnlyDictionary<byte, string>? strings)
    {
        ValidateDescriptorHeader(
            descriptor,
            8,
            InterfaceAssociationDescriptorType,
            "USB interface association descriptor");
        return new UsbInterfaceAssociationInfo(
            descriptor[2],
            descriptor[3],
            descriptor[4],
            descriptor[5],
            descriptor[6],
            descriptor[7],
            Lookup(strings, descriptor[7]));
    }

    private static void ValidateDescriptorHeader(
        ReadOnlySpan<byte> data,
        int minimumLength,
        byte expectedType,
        string name)
    {
        if (data.Length < minimumLength)
        {
            throw new InvalidDataException($"{name} requires at least {minimumLength} bytes, received {data.Length}.");
        }

        if (data[0] < minimumLength || data[0] > data.Length)
        {
            throw new InvalidDataException($"{name} reported invalid bLength {data[0]} for {data.Length} bytes.");
        }

        if (data[1] != expectedType)
        {
            throw new InvalidDataException($"{name} has descriptor type 0x{data[1]:X2}, expected 0x{expectedType:X2}.");
        }
    }

    private static string? Lookup(IReadOnlyDictionary<byte, string>? strings, byte index)
    {
        return index != 0 && strings is not null && strings.TryGetValue(index, out var value) ? value : null;
    }

    private static void EnsureCountWithinLimit(int count, int maximum, string itemName)
    {
        if (count >= maximum)
        {
            throw new InvalidDataException(
                $"USB descriptor exceeds the limit of {maximum} {itemName}.");
        }
    }

    private static string FormatCapabilityName(byte capabilityType)
    {
        return capabilityType switch
        {
            0x02 => "USB 2.0 Extension",
            0x03 => "SuperSpeed USB",
            0x04 => "Container ID",
            0x05 => "Platform",
            0x06 => "Power Delivery",
            0x0A => "SuperSpeedPlus USB",
            0x0D => "Billboard",
            _ => $"Capability 0x{capabilityType.ToString("X2", CultureInfo.InvariantCulture)}"
        };
    }
}
