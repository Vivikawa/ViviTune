using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace ViviTune
{
    // Данные подписки из заголовков ответа. Used = upload + download; Total 0 — без лимита; Expire null — без срока
    public sealed record SubInfo(string? Title, int IntervalHours, long Used, long Total, DateTimeOffset? Expire);
    public sealed record SubEntry(string Name, string Link);
    public sealed record SubSkip(string Name, string Reason);
    public sealed record SubParse(SubInfo Info, IReadOnlyList<SubEntry> Entries, IReadOnlyList<SubSkip> Skipped, string? Error);

    // Разбор ответа подписки: base64, ссылки как есть или JSON Xray. Ссылки и ключи в ошибки не попадают
    public static class Subscription
    {
        const string NotSub = "Это не подписка";
        const string Unsupported = "Формат подписки не поддерживается";

        public static SubParse Parse(IReadOnlyDictionary<string, string> headers, string body)
        {
            var info = ReadInfo(headers);
            var none = Array.Empty<SubSkip>();
            SubParse Fail(string e) => new(info, Array.Empty<SubEntry>(), none, e);
            var text = (body ?? "").Trim().TrimStart('﻿').Trim();
            if (text.Length == 0 || text[0] == '<') return Fail(NotSub);

            if (text[0] is '{' or '[')
            {
                JsonNode? root;
                try { root = JsonNode.Parse(text); }
                catch (Exception) { return Fail(Unsupported); }
                if (root == null || !HasOutbounds(root)) return Fail(Unsupported);
                var (e, s) = FromXray(root);
                return e.Count + s.Count == 0 ? Fail(NotSub) : new SubParse(info, e, s, null);
            }
            if (text.Contains("proxies:")) return Fail(Unsupported);

            var first = text.Split('\n')[0];
            var lines = first.Contains("://") ? text : Decode(text);
            var entries = (lines ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Contains("://") && !l.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("https://", StringComparison.OrdinalIgnoreCase)).Select(l => new SubEntry(LinkName(l), l)).ToList();
            return entries.Count == 0 ? Fail(NotSub) : new SubParse(info, entries, none, null);
        }

        static SubInfo ReadInfo(IReadOnlyDictionary<string, string> headers)
        {
            string? Get(string name) => headers.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

            string? title = Get("profile-title");
            if (title != null && title.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
            {
                try { title = Encoding.UTF8.GetString(Convert.FromBase64String(Pad(title[7..]))); }
                catch (FormatException) { title = null; }
            }
            if (string.IsNullOrWhiteSpace(title)) title = null;

            int interval = int.TryParse(Get("profile-update-interval"), out var iv) && iv > 0 ? iv : 12;

            long up = 0, down = 0, total = 0, expire = 0;
            foreach (var part in (Get("subscription-userinfo") ?? "").Split(';'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2 || !long.TryParse(kv[1].Trim(), out var n)) continue;
                switch (kv[0].Trim().ToLowerInvariant())
                {
                    case "upload": up = n; break;
                    case "download": down = n; break;
                    case "total": total = n; break;
                    case "expire": expire = n; break;
                }
            }
            return new SubInfo(title, interval, up + down, total, expire > 0 && expire <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(expire) : null);
        }

        // Обычный и URL-safe base64, с «=» и без
        static string Pad(string s)
        {
            s = new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace('-', '+').Replace('_', '/').TrimEnd('=');
            return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        }

        static string? Decode(string s)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(Pad(s))); }
            catch (FormatException) { return null; }
        }

        // Имя — фрагмент после «#», без него — адрес
        static string LinkName(string link)
        {
            int hash = link.IndexOf('#');
            if (hash >= 0 && hash + 1 < link.Length)
            {
                try { return Uri.UnescapeDataString(link[(hash + 1)..]); }
                catch (UriFormatException) { return link[(hash + 1)..]; }
            }
            var rest = link[(link.IndexOf("://") + 3)..];
            if (hash >= 0) rest = rest[..Math.Max(0, hash - link.IndexOf("://") - 3)];
            int end = rest.IndexOfAny(new[] { '/', '?' });
            if (end >= 0) rest = rest[..end];
            rest = rest[(rest.LastIndexOf('@') + 1)..];
            if (rest.StartsWith('[') && rest.IndexOf(']') > 0) return rest[1..rest.IndexOf(']')];
            int colon = rest.LastIndexOf(':');
            return colon > 0 ? rest[..colon] : rest;
        }

        static bool HasOutbounds(JsonNode root) =>
            root is JsonArray a ? a.Any(c => c is JsonObject o && o["outbounds"] is JsonArray) : root is JsonObject r && r["outbounds"] is JsonArray;

        // Конфиги Xray -> ссылки. Конфиг с balancers (группа «Auto») пропускается целиком
        public static (IReadOnlyList<SubEntry> Entries, IReadOnlyList<SubSkip> Skipped) FromXray(JsonNode root)
        {
            var entries = new List<SubEntry>();
            var skipped = new List<SubSkip>();
            var configs = root is JsonArray arr ? arr.OfType<JsonNode>().ToList() : new List<JsonNode> { root };
            foreach (var cfg in configs)
            {
                if (cfg is not JsonObject) continue;
                if (cfg["routing"] is JsonObject routing && routing["balancers"] is JsonArray { Count: > 0 }) continue;
                if (cfg["outbounds"] is not JsonArray outs) continue;
                var proxies = outs.OfType<JsonObject>().Where(o => Str(o["protocol"]) is not (null or "freedom" or "blackhole" or "dns" or "loopback")).ToList();
                var remarks = Str(cfg["remarks"]);
                for (int i = 0; i < proxies.Count; i++)
                {
                    var o = proxies[i];
                    var (link, host, reason) = Outbound(o);
                    var name = string.IsNullOrEmpty(remarks) ? (host ?? "") : remarks;
                    if (proxies.Count > 1) name += " #" + (i + 1);
                    if (link == null) skipped.Add(new SubSkip(name, "не поддерживается: " + reason));
                    else entries.Add(new SubEntry(name, link + "#" + Uri.EscapeDataString(name)));
                }
            }
            return (entries, skipped);
        }

        static string? Str(JsonNode? n)
        {
            try { return n is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToString()) : null; }
            catch (Exception) { return null; }
        }

        static string E(string s) => Uri.EscapeDataString(s);

        // (ссылка без #имени | null, адрес, причина отказа)
        static (string? Link, string? Host, string Reason) Outbound(JsonObject o)
        {
            var protocol = Str(o["protocol"]) ?? "";
            var settings = o["settings"] as JsonObject;
            var stream = o["streamSettings"] as JsonObject;
            // vnext/servers[0] или плоский вид (address/port/id в settings)
            var srv = (settings?["vnext"] as JsonArray)?.FirstOrDefault() as JsonObject
                ?? (settings?["servers"] as JsonArray)?.FirstOrDefault() as JsonObject ?? settings;
            var host = Str(srv?["address"]);
            var port = Str(srv?["port"]);
            if (host == null || port == null) return (null, host, protocol);
            var user = (srv?["users"] as JsonArray)?.FirstOrDefault() as JsonObject ?? srv;
            var authority = (host.Contains(':') ? "[" + host + "]" : host) + ":" + port;

            var q = new List<string>();
            void Add(string k, string? v) { if (!string.IsNullOrEmpty(v)) q.Add(k + "=" + E(v)); }

            string scheme, userinfo;
            switch (protocol)
            {
                case "vless":
                    scheme = "vless";
                    userinfo = Str(user?["id"]) ?? "";
                    Add("encryption", Str(user?["encryption"]));
                    Add("flow", Str(user?["flow"]));
                    break;
                case "trojan":
                    scheme = "trojan";
                    userinfo = Str(user?["password"]) ?? "";
                    break;
                case "hysteria":
                    var hy = stream?["hysteriaSettings"] as JsonObject;
                    if ((Str(settings?["version"]) ?? Str(hy?["version"])) != "2") return (null, host, "hysteria 1");
                    scheme = "hysteria2";
                    userinfo = Str(hy?["auth"]) ?? Str(settings?["auth"]) ?? Str(user?["auth"]) ?? "";
                    break;
                default:
                    return (null, host, protocol);
            }
            if (userinfo.Length == 0) return (null, host, protocol + " без ключа");

            var security = Str(stream?["security"]);
            if (string.IsNullOrEmpty(security)) security = "none";
            if (security is not ("none" or "tls" or "reality")) return (null, host, security);
            var tls = (security == "reality" ? stream?["realitySettings"] : stream?["tlsSettings"]) as JsonObject;

            if (scheme != "hysteria2")
            {
                Add("security", security);
                var network = Str(stream?["network"]);
                if (string.IsNullOrEmpty(network)) network = "tcp";
                if (network is not ("tcp" or "raw" or "ws" or "grpc" or "xhttp" or "httpupgrade")) return (null, host, network);
                Add("type", network == "raw" ? "tcp" : network);
                var ts = network switch
                {
                    "ws" => stream?["wsSettings"],
                    "grpc" => stream?["grpcSettings"],
                    "xhttp" => stream?["xhttpSettings"] ?? stream?["splithttpSettings"],
                    "httpupgrade" => stream?["httpupgradeSettings"],
                    _ => null
                } as JsonObject;
                if (network is "ws" or "xhttp" or "httpupgrade")
                {
                    Add("path", Str(ts?["path"]));
                    Add("host", Str(ts?["host"]) ?? Str((ts?["headers"] as JsonObject)?["Host"]));
                }
                if (network == "grpc")
                {
                    Add("serviceName", Str(ts?["serviceName"]));
                    if (Str(ts?["multiMode"]) == "True" || Str(ts?["multiMode"]) == "true") Add("mode", "multi");
                }
                if (network == "xhttp")
                {
                    Add("mode", Str(ts?["mode"]));
                    if (ts?["extra"] is JsonObject extra) Add("extra", extra.ToJsonString());
                }
            }
            if (tls != null)
            {
                Add("sni", Str(tls["serverName"]));
                Add("fp", Str(tls["fingerprint"]));
                if (tls["alpn"] is JsonArray alpn) Add("alpn", string.Join(",", alpn.Select(Str).Where(a => !string.IsNullOrEmpty(a))));
                if (Str(tls["allowInsecure"]) is "True" or "true") q.Add("allowInsecure=1");
                Add("pbk", Str(tls["publicKey"]));
                Add("sid", Str(tls["shortId"]));
                Add("spx", Str(tls["spiderX"]));
            }
            return (scheme + "://" + E(userinfo) + "@" + authority + (q.Count > 0 ? "?" + string.Join("&", q) : ""), host, "");
        }
    }
}
