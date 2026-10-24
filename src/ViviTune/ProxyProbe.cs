using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace ViviTune
{
    public class ProbeResult
    {
        public bool Reachable;
        public bool UdpSupported;
        public string? Error;
    }

    public static class ProxyProbe
    {
        const int timeoutMs = 3000;

        public static ProbeResult Probe(string ip, int port)
        {
            var r = new ProbeResult();
            try
            {
                IPAddress? addr;
                if (!IPAddress.TryParse(ip, out addr)) { r.Error = "некорректный IP"; return r; }
                using (var c = new TcpClient(addr.AddressFamily))
                {
                    if (!c.ConnectAsync(addr, port).Wait(timeoutMs)) { r.Error = "таймаут подключения"; return r; }
                    c.ReceiveTimeout = c.SendTimeout = timeoutMs;
                    var st = c.GetStream();

                    st.Write(new byte[] { 5, 1, 0 }, 0, 3); // SOCKS5, один метод: без авторизации
                    var rep = new byte[2];
                    st.ReadExactly(rep);
                    if (rep[0] != 5 || rep[1] != 0) { r.Error = "это не SOCKS5 без авторизации"; return r; }
                    r.Reachable = true;

                    st.Write(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 10); // UDP ASSOCIATE 0.0.0.0:0
                    var u = new byte[4]; // VER REP RSV ATYP
                    st.ReadExactly(u);
                    r.UdpSupported = u[0] == 5 && u[1] == 0;
                }
            }
            catch (Exception e)
            {
                // ошибка после рукопожатия = просто нет UDP
                if (!r.Reachable) r.Error = e is EndOfStreamException ? "соединение закрыто" : e.GetBaseException().Message;
            }
            return r;
        }

        // TCP-подключение за 3 с: null — ответил, иначе причина
        public static string? Reach(IPAddress ip, int port)
        {
            try
            {
                using var c = new TcpClient(ip.AddressFamily);
                return c.ConnectAsync(ip, port).Wait(timeoutMs) ? null : "таймаут подключения";
            }
            catch (Exception e) { return e.GetBaseException().Message; }
        }
    }
}
