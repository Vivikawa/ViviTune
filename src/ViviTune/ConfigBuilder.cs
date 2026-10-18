using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ViviTune
{
    public static class ConfigBuilder
    {
        static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        static Dictionary<string, object> D(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        public static string Build(Settings s)
        {
            var err = s.Validate();
            if (err != null) throw new InvalidOperationException(err);

            var names = s.ProcessNames().ToArray();
            var ip = IPAddress.Parse(s.Ip);
            var cidr = ip + (ip.AddressFamily == AddressFamily.InterNetworkV6 ? "/128" : "/32");

            var config = D(
                "log", D("level", "warn", "timestamp", true),
                "dns", D(
                    "servers", new object[]
                    {
                        D("type", "local", "tag", "local"),
                        D("type", "tcp", "tag", "proxy-dns", "server", "1.1.1.1", "detour", "proxy")
                    },
                    "rules", new object[] { D("process_name", names, "server", "proxy-dns") }),
                "inbounds", new object[]
                {
                    D("type", "tun", "tag", "tun-in", "address", new object[] { "172.19.0.1/30", "fdfe:dcba:9876::1/126" }, "auto_route", true, "strict_route", false)
                },
                "outbounds", new object[]
                {
                    D("type", "socks", "tag", "proxy", "server", ip.ToString(), "server_port", s.Port, "version", "5"),
                    D("type", "direct", "tag", "direct")
                },
                "route", D(
                    "rules", new object[]
                    {
                        D("action", "sniff"),
                        D("protocol", "dns", "action", "hijack-dns"),
                        D("ip_cidr", new object[] { cidr }, "outbound", "direct"),
                        D("process_name", names, "outbound", "proxy")
                    },
                    "final", "direct",
                    "auto_detect_interface", true,
                    "default_domain_resolver", "local"));

            return JsonSerializer.Serialize(config, Json);
        }
    }
}
