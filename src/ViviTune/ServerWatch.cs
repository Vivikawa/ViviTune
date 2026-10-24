namespace ViviTune;

// Проверка текущего сервера подписки: каждые interval — Delay("proxy"). Три неудачи подряд: с галкой «самый быстрый» —
// пингуем остальные и переключаемся (Switched), иначе или если никто не ответил — Down. После Down повторно не сообщаем,
// пока не будет успеха, Reset() или переключения. Работает в своём потоке пула; события приходят оттуда же.
// Все обращения к Clash API идут через pingGate (его делит Session с PingConnected): пинги не пересекаются.
// Dispose не ждёт: отмена уходит в пул (колбэки отмены не должны выполняться под чужим lock), события после него не приходят.
public sealed class ServerWatch : IDisposable
{
    const int Fails = 3;
    const int ProxyMs = 5000;

    readonly IClashApi api;
    readonly IReadOnlyList<string> tags;
    readonly Func<string> current;
    readonly Func<bool> auto;
    readonly TimeSpan interval;
    readonly int pingMs;
    readonly TimeSpan cap;
    readonly SemaphoreSlim pingGate;
    readonly Func<IReadOnlyDictionary<string, int?>>? lastPings; // последние пинги по тегам (без галки Down предлагает лучший)
    readonly CancellationTokenSource cts = new();
    readonly object sync = new();
    int fails;
    bool reported;
    volatile bool disposed;

    public event Action<string, string?, int?>? Down;
    public event Action<string, string, int>? Switched;

    public ServerWatch(IClashApi api, IReadOnlyList<string> tags, Func<string> current, Func<bool> auto, TimeSpan interval, int pingMs, TimeSpan cap,
        SemaphoreSlim? pingGate = null, Func<IReadOnlyDictionary<string, int?>>? lastPings = null)
    {
        this.api = api; this.tags = tags; this.current = current; this.auto = auto;
        this.interval = interval; this.pingMs = pingMs; this.cap = cap;
        this.pingGate = pingGate ?? new SemaphoreSlim(1, 1);
        this.lastPings = lastPings;
    }

    public void Reset()
    {
        lock (sync) { fails = 0; reported = false; }
    }

    public void Start() => _ = Loop(cts.Token);

    async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try { await Task.Run(() => Tick(ct), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch { } // обработчик события не должен ронять проверку
        }
    }

    void Tick(CancellationToken ct)
    {
        int? d;
        pingGate.Wait(ct);
        try { d = api.Delay("proxy", ProxyMs, ct); }
        catch { d = null; }
        finally { pingGate.Release(); }
        if (disposed) return;
        lock (sync)
        {
            if (d != null) { fails = 0; reported = false; return; }
            if (++fails < Fails || reported) return;
        }
        var cur = current();
        if (auto())
        {
            IReadOnlyDictionary<string, int?> pings;
            pingGate.Wait(ct);
            try { pings = Pinger.Ping(api, tags.Where(t => t != cur).ToList(), pingMs, cap, null, ct); }
            finally { pingGate.Release(); }
            if (disposed) return;
            var best = Pinger.Fastest(pings, cur);
            bool ok;
            try { ok = best != null && api.Select("proxy", best); }
            catch { ok = false; }
            if (disposed) return;
            if (ok)
            {
                lock (sync) { fails = 0; reported = false; }
                Switched?.Invoke(cur, best!, pings[best!]!.Value);
                return;
            }
            Report(cur, null, null);
        }
        else
        {
            var last = lastPings?.Invoke();
            var best = last == null ? null : Pinger.Fastest(last, cur);
            Report(cur, best, best == null ? null : last![best]);
        }
    }

    void Report(string cur, string? best, int? ms)
    {
        lock (sync) reported = true;
        Down?.Invoke(cur, best, ms);
    }

    public void Dispose()
    {
        disposed = true;
        ThreadPool.QueueUserWorkItem(_ => cts.Cancel());
    }
}
