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
        public string Error;
    }

    public static class ProxyProbe
    {
        const int timeoutMs = 3000;

        public static ProbeResult Probe(string ip, int port)
        {
            var r = new ProbeResult();
            try
            {
                IPAddress addr;
                if (!IPAddress.TryParse(ip, out addr)) { r.Error = "некорректный IP"; return r; }
                using (var c = new TcpClient(addr.AddressFamily))
                {
                    if (!c.ConnectAsync(addr, port).Wait(timeoutMs)) { r.Error = "таймаут подключения"; return r; }
                    c.ReceiveTimeout = c.SendTimeout = timeoutMs;
                    var st = c.GetStream();

                    st.Write(new byte[] { 5, 1, 0 }, 0, 3); // SOCKS5, один метод: без авторизации
                    var rep = ReadN(st, 2);
                    if (rep[0] != 5 || rep[1] != 0) { r.Error = "это не SOCKS5 без авторизации"; return r; }
                    r.Reachable = true;

                    st.Write(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 10); // UDP ASSOCIATE 0.0.0.0:0
                    var u = ReadN(st, 4); // VER REP RSV ATYP
                    r.UdpSupported = u[0] == 5 && u[1] == 0;
                }
            }
            catch (Exception e)
            {
                if (!r.Reachable) r.Error = e.GetBaseException().Message; // ошибка после рукопожатия = просто нет UDP
            }
            return r;
        }

        static byte[] ReadN(Stream s, int n)
        {
            var b = new byte[n];
            int o = 0;
            while (o < n)
            {
                int k = s.Read(b, o, n - o);
                if (k == 0) throw new IOException("соединение закрыто");
                o += k;
            }
            return b;
        }
    }
}
