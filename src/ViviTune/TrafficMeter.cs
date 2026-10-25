using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ViviTune;

public readonly record struct ConnSample(string Id, long Upload, long Download, bool ViaProxy);

// UpBytes/DownBytes — байты через прокси за интервал опроса, для итога трафика
public readonly record struct Speed(double UpBps, double DownBps, long UpBytes = 0, long DownBytes = 0);

public static class TrafficMath
{
    public static List<ConnSample> Parse(string json)
    {
        var list = new List<ConnSample>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("connections", out var conns) ||
                conns.ValueKind != JsonValueKind.Array) return list;
            foreach (var c in conns.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object) continue;
                var id = c.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
                long up = c.TryGetProperty("upload", out var u) && u.TryGetInt64(out var uv) ? uv : 0;
                long down = c.TryGetProperty("download", out var d) && d.TryGetInt64(out var dv) ? dv : 0;
                bool proxy = false;
                if (c.TryGetProperty("chains", out var ch) && ch.ValueKind == JsonValueKind.Array)
                    foreach (var x in ch.EnumerateArray())
                        if (x.ValueKind == JsonValueKind.String && x.GetString() is { } t && (t == "proxy" || t.StartsWith("proxy@", StringComparison.Ordinal))) { proxy = true; break; }
                list.Add(new ConnSample(id, up, down, proxy));
            }
        }
        catch { list.Clear(); }
        return list;
    }

    public static Speed Delta(IReadOnlyDictionary<string, ConnSample> prev, IReadOnlyList<ConnSample> now, double seconds)
    {
        long up = 0, down = 0;
        foreach (var c in now)
        {
            if (!c.ViaProxy) continue;
            long pu = 0, pd = 0;
            if (prev.TryGetValue(c.Id, out var p)) { pu = p.Upload; pd = p.Download; }
            up += Math.Max(0, c.Upload - pu);
            down += Math.Max(0, c.Download - pd);
        }
        // байты не теряем и при seconds<=0: они идут в итог трафика
        return seconds > 0 ? new Speed(up / seconds, down / seconds, up, down) : new Speed(0, 0, up, down);
    }
}

public sealed class TrafficMeter : IDisposable
{
    readonly int port;
    // без системного прокси: запросы к 127.0.0.1 не должны уходить в прокси из настроек Windows
    readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(1) };
    readonly CancellationTokenSource cts = new();

    public event Action<Speed>? Sample;

    public TrafficMeter(int port, string secret)
    {
        this.port = port;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }

    // Один раз: Session запускает счётчик сразу после создания и только освобождает
    public void Start() => Task.Run(() => Loop(cts.Token));

    // Повторный вызов безопасен: Cancel и HttpClient.Dispose идемпотентны (cts без таймера освобождать незачем)
    public void Dispose()
    {
        cts.Cancel();
        http.Dispose();
    }

    async Task Loop(CancellationToken token)
    {
        Dictionary<string, ConnSample>? prev = null;
        var sw = new Stopwatch();
        while (!token.IsCancellationRequested)
        {
            Speed speed = default;
            try
            {
                var json = await http.GetStringAsync($"http://127.0.0.1:{port}/connections", token);
                var now = TrafficMath.Parse(json);
                double secs = sw.Elapsed.TotalSeconds;
                // первый удачный опрос только фиксирует базу, без всплеска от уже идущих соединений
                if (prev != null) speed = TrafficMath.Delta(prev, now, secs);
                prev = new Dictionary<string, ConnSample>();
                foreach (var c in now) prev[c.Id] = c;
                sw.Restart();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch { prev = null; }
            try { Sample?.Invoke(speed); } catch { }
            try { await Task.Delay(1000, token); }
            catch (OperationCanceledException) { return; }
        }
    }
}
