#if NETFRAMEWORK
using System;
#endif
using System.Text;

namespace OpenSkiTime.TimyUsbHost;

// Kept independent of WinForms/native loading so the exact SDK event boundary can be tested.
internal static class TimySdkEvent
{
    public static string DeviceId(object e)
    {
        var device = Property(e, "Device");
        // ALGE exposes TimyUsbDevice.Id as a public field, not a property.
        var id = device.GetType().GetField("Id") ?? throw new MissingFieldException(device.GetType().FullName, "Id");
        return id.GetValue(device)!.ToString()!;
    }

    public static byte[] Capture(object e)
    {
        // The current vendor x64 SDK's Data array copy is defective. Text contains the
        // untrimmed native ASCII chunk. Preserve BOTH fields before any interpretation.
        var bytes = Convert.ToBase64String((byte[])Property(e, "Data"));
        var text = Convert.ToBase64String(Encoding.UTF8.GetBytes((string)Property(e, "Text")));
        return Encoding.UTF8.GetBytes("{\"sdkBytes\":\"" + bytes + "\",\"sdkTextUtf8\":\"" + text + "\"}");
    }

    private static object Property(object value, string name)
    {
        var property = value.GetType().GetProperty(name) ?? throw new MissingMemberException(value.GetType().FullName, name);
        return property.GetValue(value, null) ?? throw new InvalidOperationException("Missing Timy SDK event value: " + name);
    }
}
