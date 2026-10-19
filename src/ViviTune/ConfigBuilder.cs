using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ViviTune
{
    // Какие выходы сейчас живы: прокси и «Мимо Podkop».
    public readonly record struct Exits(bool Proxy, bool Bypass);

    public static class ConfigBuilder
    {
        static readonly JsonCtx Json = new(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        static Dictionary<string, object> D(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        public static string Build(Settings s, int apiPort, string apiSecret, Exits? live = null)
        {
            var err = s.Validate();
            if (err != null) throw new InvalidOperationException(err);

            // null: прокси жив, bypass жив, если задан порт
            var l = live ?? new Exits(true, s.DirectPort != null);
            var proxyNames = l.Proxy ? s.ProcessNames().ToArray() : Array.Empty<string>();
            var bypassNames = l.Bypass && s.DirectPort != null ? s.BypassNames().ToArray() : Array.Empty<string>();
            if (proxyNames.Length == 0 && bypassNames.Length == 0)
                throw new InvalidOperationException("Нет правил: ни одного живого выхода с программами");
            var ip = IPAddress.Parse(s.Ip);
            var cidr = ip + (ip.AddressFamily == AddressFamily.InterNetworkV6 ? "/128" : "/32");

            var outbounds = new List<object>
            {
                D("type", "socks", "tag", "proxy", "server", ip.ToString(), "server_port", s.Port, "version", "5"),
                D("type", "direct", "tag", "direct")
            };
            if (bypassNames.Length > 0)
                outbounds.Add(D("type", "socks", "tag", "bypass", "server", ip.ToString(), "server_port", s.DirectPort!.Value, "version", "5"));

            var rules = new List<object>
            {
                D("action", "sniff"),
                D("protocol", "dns", "action", "hijack-dns"),
                D("ip_cidr", new object[] { cidr }, "outbound", "direct")
            };
            if (proxyNames.Length > 0) rules.Add(D("process_name", proxyNames, "outbound", "proxy"));
            if (bypassNames.Length > 0) rules.Add(D("process_name", bypassNames, "outbound", "bypass"));

            var config = D(
                "log", D("level", "warn", "timestamp", true),
                "experimental", D("clash_api", D("external_controller", "127.0.0.1:" + apiPort, "secret", apiSecret)),
                "dns", D(
                    "servers", new object[]
                    {
                        D("type", "local", "tag", "local"),
                        D("type", "tcp", "tag", "proxy-dns", "server", "1.1.1.1", "detour", "proxy")
                    },
                    "rules", proxyNames.Length > 0 ? new object[] { D("process_name", proxyNames, "server", "proxy-dns") } : Array.Empty<object>()),
                "inbounds", new object[]
                {
                    D("type", "tun", "tag", "tun-in", "address", new object[] { "172.19.0.1/30", "fdfe:dcba:9876::1/126" }, "auto_route", true, "strict_route", false)
                },
                "outbounds", outbounds.ToArray(),
                "route", D(
                    "rules", rules.ToArray(),
                    "final", "direct",
                    "auto_detect_interface", true,
                    "default_domain_resolver", "local"));

            return JsonSerializer.Serialize(config, Json.DictionaryStringObject);
        }
    }
}
