using Microsoft.Win32;

namespace HydroHaptic;

internal static class DongleDiscovery
{
    // The shipped Hydro firmware uses Zephyr's laboratory USB VID/PID.
    // Both node and dongle can match; only the exact protocol PONG identifies the dongle.
    internal static bool IsCandidateDevice(string id) =>
        id.Equals("VID_2FE3&PID_0100&MI_00", StringComparison.OrdinalIgnoreCase);

    internal static string[] Ports()
    {
        var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb is null) return [];
        var present = System.IO.Ports.SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string device in usb.GetSubKeyNames().Where(IsCandidateDevice))
        {
            using var key = usb.OpenSubKey(device);
            if (key is null) continue;
            foreach (string instance in key.GetSubKeyNames())
            {
                using var parameters = key.OpenSubKey(instance + @"\Device Parameters");
                if (parameters?.GetValue("PortName") is string port && present.Contains(port)) ports.Add(port);
            }
        }
        return ports.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
