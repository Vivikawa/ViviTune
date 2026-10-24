using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace ViviTune
{
    // Сервер из ссылки: outbound sing-box (без tag) и предупреждения движка
    public sealed record Server(string Name, JsonObject Outbound, IReadOnlyList<string> Warnings)
    {
        public string Type => Outbound["type"]?.GetValue<string>() ?? "";
        public string Host => Outbound["server"]?.GetValue<string>() ?? "";
        public int Port => Outbound["server_port"]?.GetValue<int>() ?? 0;
        public bool Udp => Type is "hysteria2" or "hysteria" or "tuic";

        public string Summary
        {
            get
            {
                var parts = new List<string> { (Host.Contains(':') ? "[" + Host + "]" : Host) + ":" + Port };
                switch (Type)
                {
                    case "vless":
                    case "trojan":
                        if (Type == "trojan") parts.Add("Trojan");
                        parts.Add(Outbound["tls"]?["reality"]?["enabled"]?.GetValue<bool>() == true ? "Reality"
                            : Outbound["tls"]?["enabled"]?.GetValue<bool>() == true ? "TLS" : "без TLS");
                        parts.Add(Outbound["transport"]?["type"]?.GetValue<string>() switch
                        {
                            "ws" => "WebSocket",
                            "grpc" => "gRPC",
                            "httpupgrade" => "HTTPUpgrade",
                            "http" => "HTTP",
                            "xhttp" => "xHTTP",
                            _ => "TCP",
                        });
                        if (Outbound["flow"]?.GetValue<string>() is { Length: > 0 }) parts.Add("Vision");
                        break;
                    case "hysteria2": parts.Add("Hysteria2"); break;
                    default: parts.Add(Type); break;
                }
                return string.Join(" · ", parts);
            }
        }
    }

    // Разбор ссылки движком: sing-box tools decode-link
    public static class Links
    {
        public static Server? Decode(string? link, Func<string, (int Code, string Out, string Err)> run, out string? error)
        {
            error = null;
            link = link?.Trim().Trim('﻿').Trim();
            if (string.IsNullOrEmpty(link)) { error = "Вставьте ссылку в настройках"; return null; }
            if (!link.Contains("://")) { error = "Это не ссылка"; return null; }
            try
            {
                var (code, o, err) = run(link);
                var lines = err.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (code == 2)
                {
                    var l = Array.Find(lines, x => x.StartsWith("error: ", StringComparison.Ordinal));
                    error = "Ссылка не подходит: " + (l == null ? "неизвестная ошибка" : l["error: ".Length..]);
                    return null;
                }
                if (code != 0) { error = $"sing-box не разобрал ссылку (код {code})"; return null; }
                if (JsonNode.Parse(o) is not JsonObject ob) { error = "sing-box не разобрал ссылку: пустой ответ"; return null; }
                var tag = ob["tag"]?.GetValue<string>();
                ob.Remove("tag");
                var warnings = new List<string>();
                foreach (var l in lines)
                    if (l.StartsWith("warning: ", StringComparison.Ordinal)) warnings.Add(l["warning: ".Length..]);
                var srv = new Server("", ob, warnings);
                return srv with { Name = string.IsNullOrEmpty(tag) ? srv.Host : tag };
            }
            catch (Exception ex)
            {
                error = "sing-box не разобрал ссылку: " + ex.Message;
                return null;
            }
        }

        // Ссылка уходит в stdin (секрет, не в командную строку), UTF-8 без BOM
        public static Func<string, (int Code, string Out, string Err)> Engine(string singBoxExe) => link =>
        {
            var utf8 = new UTF8Encoding(false);
            var psi = new ProcessStartInfo(singBoxExe)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                StandardInputEncoding = utf8, StandardOutputEncoding = utf8, StandardErrorEncoding = utf8,
            };
            psi.ArgumentList.Add("tools");
            psi.ArgumentList.Add("decode-link");
            psi.ArgumentList.Add("--compact");
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            p.StandardInput.Write(link);
            p.StandardInput.Close();
            p.WaitForExit();
            return (p.ExitCode, o.Result, e.Result);
        };
    }
}
