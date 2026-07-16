using System.Text.Json;
using HwScope.Core.Hardware.DeviceTopology.Usb;

var options = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
};

try
{
    var input = await Console.In.ReadToEndAsync().ConfigureAwait(false);
    var target = JsonSerializer.Deserialize<UsbDeviceDetailTarget>(input, options)
        ?? throw new InvalidDataException("USB descriptor worker received an empty target.");
    var detail = new UsbDeviceDetailCollector().Collect(target);
    await Console.Out.WriteAsync(JsonSerializer.Serialize(detail, options)).ConfigureAwait(false);
    return 0;
}
catch (Exception ex)
{
    await Console.Error.WriteAsync(ex.ToString()).ConfigureAwait(false);
    return 1;
}
