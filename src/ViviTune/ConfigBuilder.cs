using System;
using System.Collections.Generic;
using System.Net;
using System.Linq;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ViviTune
{
    // Какие выходы сейчас живы: прокси и «Мимо Podkop».
    public readonly record struct Exits(bool Proxy, bool Bypass);

    public static class ConfigBuilder
    {
        internal static readonly JsonCtx Json = new(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        internal static Dictionary<string, object> D(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        // Правила лежат в rule-set'ах (RuleFiles), конфиг только ссылается на них: их можно менять без перезапуска sing-box.
        static object LocalSet(string rulesDir, string tag, string format = "source") =>
            D("type", "local", "tag", tag, "format", format, "path", RuleFiles.PathOf(rulesDir, tag));

        public static string Build(Settings s, int apiPort, string apiSecret, string rulesDir, Exits? live = null)
        {
            var err = s.Validate();
            if (err != null) throw new InvalidOperationException(err);

            // null: прокси жив, bypass жив, если задан порт
            var l = live ?? new Exits(true, s.DirectPort != null);
            bool proxyRule = l.Proxy && s.ProcessNames().Count > 0;
            bool bypassRule = l.Bypass && s.DirectPort != null && s.BypassNames().Count > 0;
            if (!proxyRule && !bypassRule)
                throw new InvalidOperationException("Нет правил: ни одного живого выхода с программами");
            var ip = IPAddress.Parse(s.Ip);
            var cidr = ip + (ip.AddressFamily == AddressFamily.InterNetworkV6 ? "/128" : "/32");

            var outbounds = new List<object>
            {
                D("type", "socks", "tag", "proxy", "server", ip.ToString(), "server_port", s.Port, "version", "5"),
                D("type", "direct", "tag", "direct")
            };
            if (bypassRule)
                outbounds.Add(D("type", "socks", "tag", "bypass", "server", ip.ToString(), "server_port", s.DirectPort!.Value, "version", "5"));

            var rules = new List<object>
            {
                D("action", "sniff"),
                D("protocol", "dns", "action", "hijack-dns"),
                D("ip_cidr", new object[] { cidr }, "outbound", "direct")
            };
            var sets = new List<object>();
            if (proxyRule)
            {
                rules.Add(D("rule_set", new[] { RuleFiles.Apps }, "outbound", "proxy"));
                sets.Add(LocalSet(rulesDir, RuleFiles.Apps));
            }
            if (bypassRule)
            {
                rules.Add(D("rule_set", new[] { RuleFiles.Bypass }, "outbound", "bypass"));
                sets.Add(LocalSet(rulesDir, RuleFiles.Bypass));
            }

            var config = Head(apiPort, apiSecret);
            config["dns"] = D(
                "servers", new object[]
                {
                    D("type", "local", "tag", "local"),
                    D("type", "tcp", "tag", "proxy-dns", "server", "1.1.1.1", "detour", "proxy")
                },
                "rules", proxyRule ? new object[] { D("rule_set", new[] { RuleFiles.Apps }, "server", "proxy-dns") } : Array.Empty<object>());
            config["inbounds"] = Tun(false);
            config["outbounds"] = outbounds.ToArray();
            config["route"] = D(
                "rules", rules.ToArray(),
                "rule_set", sets.ToArray(),
                "final", "direct",
                "auto_detect_interface", true,
                "find_process", true,
                "default_domain_resolver", "local");

            return JsonSerializer.Serialize(config, Json.DictionaryStringObject);
        }

        // Общие куски конфига: лог, clash_api, TUN (strict_route и mtu только для VLESS).
        static Dictionary<string, object> Head(int apiPort, string apiSecret) => D(
            "log", D("level", "warn", "timestamp", true),
            "experimental", D("clash_api", D("external_controller", "127.0.0.1:" + apiPort, "secret", apiSecret)));

        // VLESS: mtu 9000, как у Hiddify (без него sing-box 1.12 на Windows берёт 65535); SOCKS5 не трогаем.
        static object[] Tun(bool vless)
        {
            var tun = D("type", "tun", "tag", "tun-in", "address", new object[] { "172.19.0.1/30", "fdfe:dcba:9876::1/126" }, "auto_route", true, "strict_route", vless);
            if (vless) tun["mtu"] = 9000;
            return new object[] { tun };
        }

        // proxy — ProxyOutbound, server — его IP; файлы правил (apps, sites, list-*) пишет RuleFiles.Write.
        public static string BuildVless(Settings s, int apiPort, string apiSecret, JsonObject proxy, IPAddress server, string rulesDir)
        {
            var err = s.Validate();
            if (err != null) throw new InvalidOperationException(err);
            var cidr = server + (server.AddressFamily == AddressFamily.InterNetworkV6 ? "/128" : "/32");

            // один набор на всё: каждый список каталога всегда в конфиге (выключенный — пустой файл)
            var sets = new List<object> { LocalSet(rulesDir, RuleFiles.Apps), LocalSet(rulesDir, RuleFiles.Sites) };
            sets.AddRange(Catalogs.Lists.Select(l => LocalSet(rulesDir, RuleFiles.ListTag(l.Id), "binary")));
            var tags = new[] { RuleFiles.Apps, RuleFiles.Sites }.Concat(Catalogs.Lists.Select(l => RuleFiles.ListTag(l.Id))).ToArray();
            var rules = new List<object>
            {
                D("action", "sniff"),
                D("protocol", "dns", "action", "hijack-dns"),
                D("ip_cidr", new object[] { cidr }, "outbound", "direct"),
                D("rule_set", tags, "outbound", "proxy")
            };

            // весь DNS компьютера — к 1.1.1.1 через туннель: провайдер его не видит и не подменяет.
            // Только IPv4: у многих VLESS-серверов нет IPv6, и соединение к AAAA-адресу молча глохнет (голос Discord).
            // ponytail: IPv6-литералы без DNS всё ещё уходят в IPv6; опция «есть IPv6», если найдётся сервер с ним
            var config = Head(apiPort, apiSecret);
            config["dns"] = D("servers", new object[] { D("type", "tcp", "tag", "dns", "server", "1.1.1.1", "detour", "proxy") }, "final", "dns", "strategy", "ipv4_only");
            config["inbounds"] = Tun(true);
            config["outbounds"] = new object[] { proxy, D("type", "direct", "tag", "direct") };
            config["route"] = D("rules", rules.ToArray(), "rule_set", sets.ToArray(), "final", "direct", "auto_detect_interface", true, "find_process", true, "default_domain_resolver", "dns");
            return JsonSerializer.Serialize(config, Json.DictionaryStringObject);
        }

        // Временный sing-box, чтобы скачать списки через VLESS-сервер (провайдер может резать GitHub):
        // только локальный HTTP/SOCKS-прокси на 127.0.0.1:port, без TUN — системный трафик не трогает.
        public static string BuildFetch(JsonObject proxy, int port) => JsonSerializer.Serialize(D(
            "log", D("level", "warn"),
            "inbounds", new object[] { D("type", "mixed", "tag", "in", "listen", "127.0.0.1", "listen_port", port) },
            "outbounds", new object[] { proxy.DeepClone() }), Json.DictionaryStringObject);

        // Outbound "proxy" от движка: адрес — уже разрешённый IP сервера, поправки как у Hiddify (ray2sing).
        public static JsonObject ProxyOutbound(Server s, IPAddress ip)
        {
            var o = (JsonObject)s.Outbound.DeepClone();
            o["tag"] = "proxy";
            o["server"] = ip.ToString();
            if (s.Type == "vless" && !o.ContainsKey("packet_encoding")) o["packet_encoding"] = "xudp";
            var tls = o["tls"] as JsonObject;
            bool tlsOn = tls?["enabled"]?.GetValue<bool>() == true;
            if (tlsOn && !tls!.ContainsKey("server_name") && !IPAddress.TryParse(s.Host, out _)) tls["server_name"] = s.Host;
            if (o["transport"] is JsonObject tr && tr["type"]?.GetValue<string>() == "grpc")
            {
                if (tlsOn && !tls!.ContainsKey("alpn")) tls["alpn"] = new JsonArray("h2"); // gRPC — это HTTP/2
                // ping HTTP/2 после 15 с тишины, нет ответа 15 с — новое соединение
                if (!tr.ContainsKey("idle_timeout")) tr["idle_timeout"] = "15s";
                if (!tr.ContainsKey("ping_timeout")) tr["ping_timeout"] = "15s";
            }
            return o;
        }
    }
}
