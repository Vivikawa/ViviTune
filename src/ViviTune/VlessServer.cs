using System;
using System.Linq;
using System.Net;
using System.Web;

namespace ViviTune
{
    public sealed record VlessServer(
        string Uuid, string Host, int Port,
        string Security,      // "none" | "tls" | "reality"
        string Transport,     // "tcp" | "ws" | "grpc" | "httpupgrade" | "http"
        string? Flow,         // null | "xtls-rprx-vision"
        string? Sni, string? Fingerprint, string? PublicKey, string? ShortId,
        string[] Alpn, string? HostHeader, string? Path, string? ServiceName,
        bool AllowInsecure, string Name)
    {
        // null и текст ошибки, если ссылка не разбирается
        public static VlessServer? Parse(string? link, out string? error)
        {
            error = null;
            link = link?.Trim();
            if (string.IsNullOrEmpty(link)) { error = "Вставьте VLESS-ссылку в настройках"; return null; }
            if (!link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) { error = "Ссылка должна начинаться с vless://"; return null; }
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) { error = "Ссылка не разбирается"; return null; }

            var uuid = Uri.UnescapeDataString(uri.UserInfo);
            if (uuid.Length == 0) { error = "В ссылке нет UUID"; return null; }
            var host = uri.Host.Trim('[', ']');
            if (host.Length == 0) { error = "В ссылке нет адреса сервера"; return null; }
            // порт в ссылке не указан: Uri подставляет -1 для неизвестной схемы
            if (uri.IsDefaultPort || uri.Port <= 0) { error = "В ссылке нет порта сервера"; return null; }

            var hash = link.IndexOf('#');
            var rawQuery = uri.Query.TrimStart('?');
            var q = HttpUtility.ParseQueryString(rawQuery);
            string? P(string k) => string.IsNullOrEmpty(q[k]) ? null : q[k];

            var type = (P("type") ?? "tcp").ToLowerInvariant();
            string transport;
            switch (type)
            {
                case "tcp": case "raw": transport = "tcp"; break;
                case "ws": case "grpc": case "httpupgrade": case "http": transport = type; break;
                case "h2": transport = "http"; break;
                case "xhttp": case "splithttp": error = "XHTTP пока не поддерживается"; return null;
                default: error = "Неизвестный тип транспорта: " + type; return null;
            }

            var sec = (P("security") ?? "none").ToLowerInvariant();
            if (sec != "none" && sec != "tls" && sec != "reality") { error = "Неизвестный security: " + sec; return null; }
            if (sec == "reality" && P("pbk") == null) { error = "Для Reality в ссылке нужен pbk"; return null; }

            var flow = P("flow");
            if (flow != null && flow != "xtls-rprx-vision") { error = "Неизвестный flow: " + flow; return null; }

            var hostHeader = P("host");
            string? sni = null, fp = null;
            if (sec != "none")
            {
                sni = P("sni") ?? hostHeader ?? (IPAddress.TryParse(host, out _) ? null : host);
                fp = P("fp") ?? (sec == "reality" ? "chrome" : null);
            }
            var alpn = (P("alpn") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var ai = P("allowInsecure");
            var name = hash >= 0 ? Uri.UnescapeDataString(link[(hash + 1)..]) : "";
            if (name.Length == 0) name = host;

            return new VlessServer(uuid, host, uri.Port, sec, transport, flow, sni, fp, P("pbk"), P("sid"),
                alpn, hostHeader, P("path"), P("serviceName"), ai == "1" || ai == "true", name);
        }

        public string Summary
        {
            get
            {
                var h = Host.Contains(':') ? $"[{Host}]" : Host;
                var s = Security switch { "tls" => "TLS", "reality" => "Reality", _ => "без TLS" };
                var t = Transport switch { "ws" => "WebSocket", "grpc" => "gRPC", "httpupgrade" => "HTTPUpgrade", "http" => "HTTP", _ => "TCP" };
                return $"{h}:{Port} · {s} · {t}" + (Flow != null ? " · Vision" : "");
            }
        }
    }
}
