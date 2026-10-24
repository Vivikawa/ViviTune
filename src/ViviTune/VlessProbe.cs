using System.Diagnostics;
using System.Text.Json;

namespace ViviTune;

// Проверки после запуска sing-box в режиме VLESS через его Clash API.
public static class VlessProbe
{
    public const string DelayUrl = "https://www.gstatic.com/generate_204";
    public const string DnsProbeName = "example.com";

    // get(путь с запросом, отмена) -> (HTTP-код, тело) или null, если Clash API не ответил.
    // Не бросает исключений, на отмену сразу возвращает текст ошибки (Session его игнорирует).
    public static string? AfterStart(Func<string, CancellationToken, (int Status, string Body)?> get, CancellationToken ct, TimeSpan readyTimeout, TimeSpan? retryPause = null, Action<string>? log = null)
    {
        var pause = retryPause ?? TimeSpan.FromSeconds(1.5);
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
            // две попытки: сразу после старта TUN sing-box может сбросить сеть и оборвать первое соединение
            for (var i = 0; ; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                var delay = get("/proxies/proxy/delay?timeout=5000&url=" + Uri.EscapeDataString(DelayUrl), ct);
                if (delay is { Status: 200 }) break;
                Fail(log, "Проверка сервера", i, delay, t0);
                if (i == 1) return "Сервер не отвечает";
                if (ct.WaitHandle.WaitOne(pause)) return notStarted;
            }
            for (var i = 0; ; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                var dns = get("/dns/query?name=" + DnsProbeName + "&type=A", ct);
                if (dns is { Status: 200 } && DnsAnswered(dns.Value.Body)) break;
                Fail(log, "Проверка DNS", i, dns, t0);
                if (i == 1) return "DNS через сервер не отвечает";
                if (ct.WaitHandle.WaitOne(pause)) return notStarted;
            }
            return null;
        }
        catch { return notStarted; }
    }

    // Строка в лог разработчика: что ответил Clash API на неудавшейся попытке и сколько она длилась.
    static void Fail(Action<string>? log, string what, int attempt, (int Status, string Body)? resp, long t0)
    {
        if (log == null) return;
        var body = resp?.Body ?? "";
        log($"{what}, попытка {attempt + 1}: {(resp == null ? "нет ответа" : resp.Value.Status.ToString())} {(body.Length > 200 ? body[..200] : body)} ({Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} мс)");
    }

    public static string? AfterStart(int apiPort, string secret, CancellationToken ct, Action<string>? log = null)
    {
        using var api = new ClashApi(apiPort, secret);
        return AfterStart(api.Get, ct, TimeSpan.FromSeconds(5), null, log);
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
