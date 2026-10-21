using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ViviTune;

// Проверки после запуска sing-box в режиме VLESS через его Clash API.
public static class VlessProbe
{
    public const string DelayUrl = "https://www.gstatic.com/generate_204";
    public const string DnsProbeName = "example.com";

    // get(путь с запросом, отмена) -> (HTTP-код, тело) или null, если Clash API не ответил.
    // Не бросает исключений, на отмену сразу возвращает текст ошибки (Session его игнорирует).
    public static string? AfterStart(Func<string, CancellationToken, (int Status, string Body)?> get, CancellationToken ct, TimeSpan readyTimeout)
    {
        const string notStarted = "sing-box не запустился, подробности в логе";
        try
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                if (get("/version", ct) is { Status: 200 }) break;
                if (sw.Elapsed >= readyTimeout) return notStarted;
                if (ct.WaitHandle.WaitOne(100)) return notStarted;
            }
            if (get("/proxies/proxy/delay?timeout=5000&url=" + Uri.EscapeDataString(DelayUrl), ct) is not { Status: 200 })
                return "VLESS-сервер не отвечает";
            if (get("/dns/query?name=" + DnsProbeName + "&type=A", ct) is not { Status: 200 } dns || !DnsAnswered(dns.Body))
                return "DNS через VLESS-сервер не отвечает";
            return null;
        }
        catch { return notStarted; }
    }

    public static string? AfterStart(int apiPort, string secret, CancellationToken ct)
    {
        // без системного прокси: запросы к 127.0.0.1 не должны уходить в прокси из настроек Windows
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        (int, string)? Get(string path, CancellationToken c)
        {
            try
            {
                using var resp = http.GetAsync("http://127.0.0.1:" + apiPort + path, c).GetAwaiter().GetResult();
                return ((int)resp.StatusCode, resp.Content.ReadAsStringAsync(c).GetAwaiter().GetResult());
            }
            catch { return null; }
        }
        return AfterStart(Get, ct, TimeSpan.FromSeconds(5));
    }

    // Status == 0 и непустой Answer
    public static bool DnsAnswered(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            // не тот вид JSON бросает InvalidOperationException: это тоже «нет ответа»
            return r.TryGetProperty("Status", out var st) && st.TryGetInt32(out var code) && code == 0
                && r.TryGetProperty("Answer", out var an) && an.GetArrayLength() > 0;
        }
        catch { return false; }
    }
}
