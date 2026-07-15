using System.Buffers.Binary;
using System.Text;

namespace HwScope.Core.Windows.Usb;

internal static class UsbNativeBufferParser
{
    private const int NodeInformationSize = 76;
    private const int ConnectionInformationExSize = 35;
    private const int ConnectionInformationV2Size = 16;
    private const int ConnectorPropertiesFixedSize = 16;

    public static UsbNativeHubInformation ParseHubInformation(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < NodeInformationSize)
        {
            throw new InvalidDataException($"USB_NODE_INFORMATION requires {NodeInformationSize} bytes, received {buffer.Length}.");
        }

        var nodeType = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        if (nodeType != 0)
        {
            throw new InvalidDataException($"Expected UsbHub node type 0, received {nodeType}.");
        }

        return new UsbNativeHubInformation(buffer[6], buffer[75] != 0);
    }

    public static UsbNativePortConnection ParseConnectionInformation(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < ConnectionInformationExSize)
        {
            throw new InvalidDataException($"USB_NODE_CONNECTION_INFORMATION_EX requires {ConnectionInformationExSize} bytes, received {buffer.Length}.");
        }

        UsbNativeDeviceDescriptor? descriptor = null;
        if (buffer[4] >= 18 && buffer[5] == 1)
        {
            descriptor = new UsbNativeDeviceDescriptor(
                buffer[4],
                buffer[5],
                BinaryPrimitives.ReadUInt16LittleEndian(buffer[6..]),
                buffer[8],
                buffer[9],
                buffer[10],
                buffer[11],
                BinaryPrimitives.ReadUInt16LittleEndian(buffer[12..]),
                BinaryPrimitives.ReadUInt16LittleEndian(buffer[14..]),
                BinaryPrimitives.ReadUInt16LittleEndian(buffer[16..]),
                buffer[18],
                buffer[19],
                buffer[20],
                buffer[21]);
        }

        return new UsbNativePortConnection(
            BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            descriptor,
            buffer[22],
            buffer[23],
            buffer[24] != 0,
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[25..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[27..]),
            BinaryPrimitives.ReadInt32LittleEndian(buffer[31..]));
    }

    public static UsbNativeConnectionV2 ParseConnectionInformationV2(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < ConnectionInformationV2Size)
        {
            throw new InvalidDataException($"USB_NODE_CONNECTION_INFORMATION_EX_V2 requires {ConnectionInformationV2Size} bytes, received {buffer.Length}.");
        }

        return new UsbNativeConnectionV2(
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[12..]));
    }

    public static UsbNativeConnectorProperties ParseConnectorProperties(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < ConnectorPropertiesFixedSize)
        {
            throw new InvalidDataException($"USB_PORT_CONNECTOR_PROPERTIES requires {ConnectorPropertiesFixedSize} bytes, received {buffer.Length}.");
        }

        var actualLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
        if (actualLength != 0
            && (actualLength < ConnectorPropertiesFixedSize || actualLength > buffer.Length || (actualLength & 1) != 0))
        {
            throw new InvalidDataException($"USB_PORT_CONNECTOR_PROPERTIES reported invalid ActualLength {actualLength} for a {buffer.Length}-byte buffer.");
        }

        var boundedLength = actualLength == 0 ? buffer.Length : checked((int)actualLength);
        var name = boundedLength > ConnectorPropertiesFixedSize
            ? DecodeUnicode(buffer[ConnectorPropertiesFixedSize..boundedLength])
            : string.Empty;
        return new UsbNativeConnectorProperties(
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[12..]),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[14..]),
            name);
    }

    public static string ParseVariableLengthName(ReadOnlySpan<byte> buffer, string structureName)
    {
        return ParseVariableLengthName(buffer, structureName, 0, 4);
    }

    public static string ParseConnectionVariableLengthName(ReadOnlySpan<byte> buffer, string structureName)
    {
        return ParseVariableLengthName(buffer, structureName, 4, 8);
    }

    private static string ParseVariableLengthName(
        ReadOnlySpan<byte> buffer,
        string structureName,
        int lengthOffset,
        int nameOffset)
    {
        if (buffer.Length < nameOffset + 2)
        {
            throw new InvalidDataException($"{structureName} requires at least {nameOffset + 2} bytes, received {buffer.Length}.");
        }

        var actualLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer[lengthOffset..]);
        if (actualLength < nameOffset + 2 || actualLength > buffer.Length || (actualLength & 1) != 0)
        {
            throw new InvalidDataException($"{structureName} reported invalid ActualLength {actualLength} for a {buffer.Length}-byte buffer.");
        }

        return DecodeUnicode(buffer[nameOffset..(int)actualLength]);
    }

    private static string DecodeUnicode(ReadOnlySpan<byte> data)
    {
        var evenLength = data.Length - data.Length % 2;
        return evenLength == 0 ? string.Empty : Encoding.Unicode.GetString(data[..evenLength]).TrimEnd('\0');
    }
}
