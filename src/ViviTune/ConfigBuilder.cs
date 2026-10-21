using System;
using System.Collections.Generic;
using System.Net;
using System.Linq;
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

            var config = Head(apiPort, apiSecret);
            config["dns"] = D(
                "servers", new object[]
                {
                    D("type", "local", "tag", "local"),
                    D("type", "tcp", "tag", "proxy-dns", "server", "1.1.1.1", "detour", "proxy")
                },
                "rules", proxyNames.Length > 0 ? new object[] { D("process_name", proxyNames, "server", "proxy-dns") } : Array.Empty<object>());
            config["inbounds"] = Tun(false);
            config["outbounds"] = outbounds.ToArray();
            config["route"] = D(
                "rules", rules.ToArray(),
                "final", "direct",
                "auto_detect_interface", true,
                "default_domain_resolver", "local");

            return JsonSerializer.Serialize(config, Json.DictionaryStringObject);
        }

        // Общие куски конфига: лог, clash_api, TUN (strict_route только для VLESS).
        static Dictionary<string, object> Head(int apiPort, string apiSecret) => D(
            "log", D("level", "warn", "timestamp", true),
            "experimental", D("clash_api", D("external_controller", "127.0.0.1:" + apiPort, "secret", apiSecret)));

        static object[] Tun(bool strict) => new object[]
        {
            D("type", "tun", "tag", "tun-in", "address", new object[] { "172.19.0.1/30", "fdfe:dcba:9876::1/126" }, "auto_route", true, "strict_route", strict)
        };

        // listFiles: id списка -> путь к локальному .srs; server — уже разрешённый IP сервера VLESS.
        public static string BuildVless(Settings s, int apiPort, string apiSecret, IPAddress server, IReadOnlyDictionary<string, string> listFiles)
        {
            var err = s.Validate();
            if (err != null) throw new InvalidOperationException(err);
            var names = s.ProcessNames().ToArray();
            var lists = Catalogs.Lists.Where(l => listFiles.ContainsKey(l.Id)).Select(l => l.Id).ToArray();
            if (names.Length == 0 && lists.Length == 0)
                throw new InvalidOperationException("Нет правил: ни одной программы и ни одного списка");
            var v = VlessServer.Parse(s.VlessLink, out _)!;
            var cidr = server + (server.AddressFamily == AddressFamily.InterNetworkV6 ? "/128" : "/32");

            var proxy = D("type", "vless", "tag", "proxy", "server", server.ToString(), "server_port", v.Port, "uuid", v.Uuid);
            if (!string.IsNullOrEmpty(v.Flow)) proxy["flow"] = v.Flow;
            proxy["packet_encoding"] = "xudp";
            if (v.Security != "none")
            {
                var tls = D("enabled", true);
                if (!string.IsNullOrEmpty(v.Sni)) tls["server_name"] = v.Sni;
                if (v.AllowInsecure) tls["insecure"] = true;
                if (v.Alpn.Length > 0) tls["alpn"] = v.Alpn;
                if (!string.IsNullOrEmpty(v.Fingerprint)) tls["utls"] = D("enabled", true, "fingerprint", v.Fingerprint);
                if (v.Security == "reality")
                {
                    var re = D("enabled", true, "public_key", v.PublicKey ?? "");
                    if (!string.IsNullOrEmpty(v.ShortId)) re["short_id"] = v.ShortId;
                    tls["reality"] = re;
                }
                proxy["tls"] = tls;
            }
            if (v.Transport != "tcp")
            {
                var tr = D("type", v.Transport);
                bool host = !string.IsNullOrEmpty(v.HostHeader), path = !string.IsNullOrEmpty(v.Path);
                switch (v.Transport)
                {
                    case "ws":
                        if (path) tr["path"] = v.Path!;
                        if (host) tr["headers"] = D("Host", v.HostHeader!);
                        break;
                    case "grpc":
                        if (!string.IsNullOrEmpty(v.ServiceName)) tr["service_name"] = v.ServiceName;
                        break;
                    case "httpupgrade":
                        if (host) tr["host"] = v.HostHeader!;
                        if (path) tr["path"] = v.Path!;
                        break;
                    default: // http
                        if (host) tr["host"] = new object[] { v.HostHeader! };
                        if (path) tr["path"] = v.Path!;
                        break;
                }
                proxy["transport"] = tr;
            }

            var rules = new List<object>
            {
                D("action", "sniff"),
                D("protocol", "dns", "action", "hijack-dns"),
                D("ip_cidr", new object[] { cidr }, "outbound", "direct")
            };
            if (names.Length > 0) rules.Add(D("process_name", names, "outbound", "proxy"));
            if (lists.Length > 0) rules.Add(D("rule_set", lists, "outbound", "proxy"));

            // весь DNS компьютера — к 1.1.1.1 через туннель: провайдер его не видит и не подменяет
            var config = Head(apiPort, apiSecret);
            config["dns"] = D("servers", new object[] { D("type", "tcp", "tag", "dns", "server", "1.1.1.1", "detour", "proxy") }, "final", "dns");
            config["inbounds"] = Tun(true);
            config["outbounds"] = new object[] { proxy, D("type", "direct", "tag", "direct") };
            var route = D("rules", rules.ToArray(), "final", "direct", "auto_detect_interface", true, "default_domain_resolver", "dns");
            if (lists.Length > 0)
                route["rule_set"] = lists.Select(id => (object)D("type", "local", "tag", id, "format", "binary", "path", listFiles[id])).ToArray();
            config["route"] = route;
            return JsonSerializer.Serialize(config, Json.DictionaryStringObject);
        }
    }
}
