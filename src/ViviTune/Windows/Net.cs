using System.Net.NetworkInformation;

namespace ViviTune.Windows;

public static class Net
{
    // Есть ли в системе хоть один работающий адаптер с IPv6 (кроме петли, туннелей и нашего TUN).
    // При ошибке — true: поведение как раньше (TUN с IPv6-адресом).
    public static bool HasIPv6()
    {
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (n.Name == "tun0" || n.Description == "sing-tun Tunnel") continue;
                if (n.Supports(NetworkInterfaceComponent.IPv6)) return true;
            }
            return false;
        }
        catch { return true; }
    }
}
