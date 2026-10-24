using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ViviTune;

// Что нужно Pinger'у и выбору сервера от Clash API sing-box; в тестах подменяется.
public interface IClashApi
{
    // задержка до DelayUrl через outbound tag, мс; null — не ответил
    int? Delay(string tag, int timeoutMs, CancellationToken ct);
    bool Select(string group, string tag);
}

public sealed class ClashApi(int port, string secret) : IClashApi, IDisposable
{
    // без системного прокси: запросы к 127.0.0.1 не должны уходить в прокси из настроек Windows
    readonly HttpClient http = Make(secret);

    static HttpClient Make(string secret)
    {
        var h = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(12) };
        h.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return h;
    }

    // (HTTP-код, тело) или null, если Clash API не ответил
    public (int Status, string Body)? Get(string path, CancellationToken ct)
    {
        try
        {
            using var resp = http.GetAsync("http://127.0.0.1:" + port + path, ct).GetAwaiter().GetResult();
            return ((int)resp.StatusCode, resp.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult());
        }
        catch { return null; }
    }

    public int? Delay(string tag, int timeoutMs, CancellationToken ct)
    {
        var r = Get("/proxies/" + Uri.EscapeDataString(tag) + "/delay?timeout=" + timeoutMs + "&url=" + Uri.EscapeDataString(VlessProbe.DelayUrl), ct);
        if (r is not { Status: 200 }) return null;
        try
        {
            using var doc = JsonDocument.Parse(r.Value.Body);
            return doc.RootElement.TryGetProperty("delay", out var d) && d.TryGetInt32(out var ms) ? ms : null;
        }
        catch { return null; }
    }

    public bool Select(string group, string tag)
    {
        try
        {
            using var body = new StringContent(new JsonObject { ["name"] = tag }.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = http.PutAsync("http://127.0.0.1:" + port + "/proxies/" + Uri.EscapeDataString(group), body).GetAwaiter().GetResult();
            return (int)resp.StatusCode is 204 or 200;
        }
        catch { return false; }
    }

    public void Dispose() => http.Dispose();
}

public static class Pinger
{
    // По очереди, по одному (ТСПУ не любит много соединений к серверу). После cap остальные не проверяются.
    public static IReadOnlyDictionary<string, int?> Ping(IClashApi api, IReadOnlyList<string> tags, int perMs, TimeSpan? cap, Action<string, int?>? onResult, CancellationToken ct)
    {
        var res = new Dictionary<string, int?>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var t in tags)
        {
            if (ct.IsCancellationRequested || (cap != null && sw.Elapsed >= cap)) break;
            var ms = api.Delay(t, perMs, ct);
            res[t] = ms;
            onResult?.Invoke(t, ms);
        }
        return res;
    }

    // минимальная задержка среди ответивших, кроме except
    public static string? Fastest(IReadOnlyDictionary<string, int?> pings, string? except = null) =>
        pings.Where(p => p.Value != null && p.Key != except).OrderBy(p => p.Value).Select(p => p.Key).FirstOrDefault();
}
