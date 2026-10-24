using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using ViviTune;
using ViviTune.Ui;
using ViviTune.Windows;

static class Program
{
    static int failures;

    static void Check(bool ok, string name)
    {
        if (!ok) failures++;
        Console.WriteLine((ok ? "ok:   " : "FAIL: ") + name);
    }

    static int Main(string[] args)
    {
        // Зависший «движок» для проверки таймаута Links.Engine: сам тестовый exe с аргументами decode-link
        if (args.Length > 0 && args[0] == "tools") { Thread.Sleep(Timeout.Infinite); return 0; }
        SettingsTests();
        ConfigTests();
        ProbeTests();
        TrafficTests();
        EvaluateTests();
        SessionTests();
        CatalogTests();
        ViewStateTests();
        AppListTests();
        SettingsV4Tests();
        SiteEntryTests();
        SettingsV5Tests();
        CatalogsTests();
        VlessConfigTests();
        SubscriptionConfigTests();
        PingerTests();
        SettingsV6Tests();
        SubSessionTests();
        ServerWatchTests();
        RuleFilesTests();
        ListStoreTests();
        VlessProbeTests();
        ViewStateVlessTests();
        AutostartTests();
        LinksTests();
        SubscriptionTests();
        SubStoreTests();
        DevLogTests();
        return failures == 0 ? 0 : 1;
    }

    static void DevLogTests()
    {
        // Settings.SingBoxLog
        var dir = Path.Combine(Path.GetTempPath(), "vt-devlog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string? Load(string json) { var f = Path.Combine(dir, "s.json"); File.WriteAllText(f, json); return Settings.Load(f).SingBoxLog; }
            Check(Load("{\"SingBoxLog\":\"DEBUG\"}") == "debug", "SingBoxLog: DEBUG -> debug");
            Check(Load("{\"SingBoxLog\":\"bogus\"}") == null, "SingBoxLog: мусор -> null");
            Check(Load("{}") == null, "SingBoxLog: нет поля -> null");
            var sf = Path.Combine(dir, "save.json");
            new Settings().Save(sf);
            Check(!File.ReadAllText(sf).Contains("SingBoxLog"), "SingBoxLog: Save по умолчанию не пишет поле");
            Check(new Settings { SingBoxLog = "trace" }.Clone().SingBoxLog == "trace", "SingBoxLog: Clone копирует");

            // конфиг
            var rd = Path.Combine(dir, "rules");
            var s5 = new Settings { Ip = "192.168.1.1", Port = 1818, Apps = new List<AppEntry> { new AppEntry { Path = @"C:\a\app.exe" } } };
            string Lvl(string json) => JsonNode.Parse(json)!["log"]!["level"]!.GetValue<string>();
            Check(Lvl(ConfigBuilder.Build(s5, 9090, "s", rd)) == "warn", "SingBoxLog: SOCKS5 по умолчанию warn");
            s5.SingBoxLog = "debug";
            Check(Lvl(ConfigBuilder.Build(s5, 9090, "s", rd)) == "debug", "SingBoxLog: SOCKS5 debug");
            var srv = IPAddress.Parse("203.0.113.5");
            var link = new Server("n", JsonNode.Parse("{\"type\":\"vless\",\"server\":\"nl.example.com\",\"server_port\":443,\"uuid\":\"3f1c0a52-0000-4000-8000-000000000001\"}")!.AsObject(), []);
            var sv = new Settings { Mode = ProxyMode.Vless, VlessLink = "vless://x", Apps = new List<AppEntry> { new AppEntry { Path = @"C:\a\app.exe" } } };
            string Bv() => ConfigBuilder.BuildVless(sv, 9090, "t", Single(ConfigBuilder.ProxyOutbound(link, srv), srv), rd);
            Check(Lvl(Bv()) == "warn", "SingBoxLog: VLESS по умолчанию warn");
            sv.SingBoxLog = "debug";
            Check(Lvl(Bv()) == "debug", "SingBoxLog: VLESS debug");

            // LogFile
            var lf = Path.Combine(dir, "sing-box.log");
            using (var log = new LogFile(lf))
            {
                log.Write("первая"); log.Write("second");
                string txt;
                using (var fs = new FileStream(lf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs)) txt = sr.ReadToEnd();
                var ls = txt.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimEnd('\r')).ToArray();
                bool Pref(string l, string rest) => l.EndsWith(" " + rest) && System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d\d:\d\d:\d\d\.\d\d\d ");
                Check(ls.Length == 2 && Pref(ls[0], "первая") && Pref(ls[1], "second"), "LogFile: две строки с префиксом времени, читается при открытом");
            }
            var rf = Path.Combine(dir, "r.log");
            using (var log = new LogFile(rf, 200))
            {
                for (var i = 0; i < 20; i++) log.Write("строка номер " + i + " xxxxxxxxxx");
            }
            Check(File.Exists(Path.Combine(dir, "r.old.log")) && new FileInfo(rf).Length < 400 && new FileInfo(rf).Length > 0, "LogFile: ротация в .old.log, новый файл мал");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        Check(LogFile.IsVerbose("+0500 2026-10-24 19:37:55 INFO inbound/tun[tun-in]: started")
            && LogFile.IsVerbose("+0500 2026-10-24 19:37:55 DEBUG x") && LogFile.IsVerbose("+0500 2026-10-24 19:37:55 TRACE x")
            && LogFile.IsVerbose("INFO router: x") && LogFile.IsVerbose("DEBUG x"), "IsVerbose: INFO/DEBUG/TRACE -> true");
        Check(!LogFile.IsVerbose("+0500 2026-10-24 19:37:55 WARN x") && !LogFile.IsVerbose("+0500 2026-10-24 19:37:55 ERROR x")
            && !LogFile.IsVerbose("FATAL x") && !LogFile.IsVerbose("Сервер не отвечает") && !LogFile.IsVerbose(""), "IsVerbose: WARN/ERROR/FATAL и сообщения ViviTune -> false");

        // VlessProbe: лог неудачных попыток
        (int, string)? Ok(string path) =>
            path.StartsWith("/version") ? (200, "{}") :
            path.StartsWith("/proxies/proxy/delay") ? (200, "{\"delay\":50}") :
            path.StartsWith("/dns/query") ? (200, "{\"Status\":0,\"Answer\":[{\"data\":\"1.1.1.1\"}]}") : null;
        var lines = new List<string>();
        var n = 0;
        var r = VlessProbe.AfterStart((p, c) => p.StartsWith("/proxies") && ++n == 1 ? (504, "boom") : Ok(p), CancellationToken.None, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20), lines.Add);
        Check(r == null && lines.Count == 1 && lines[0].StartsWith("Проверка сервера, попытка 1:") && lines[0].Contains("504") && lines[0].Contains("boom"), "VlessProbe: один сбой delay -> одна строка лога");
        n = 0;
        Check(VlessProbe.AfterStart((p, c) => p.StartsWith("/proxies") && ++n == 1 ? (504, "") : Ok(p), CancellationToken.None, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20)) == null, "VlessProbe: без log всё работает");
    }

    static void RuleFilesTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "vivitune-rf-" + Guid.NewGuid().ToString("N"));
        string P(string d, string tag) => RuleFiles.PathOf(d, tag);
        string Text(string d, string tag) => File.ReadAllText(P(d, tag));
        try
        {
            var d1 = Path.Combine(root, "socks");
            RuleFiles.Write(d1, Both(), new Dictionary<string, string>());
            Check(Text(d1, "apps") == "{\"version\":3,\"rules\":[{\"process_name\":[\"chrome.exe\"]}]}" && Text(d1, "bypass") == "{\"version\":3,\"rules\":[{\"process_name\":[\"qbittorrent.exe\"]}]}"
                && !File.Exists(P(d1, "sites")) && Directory.GetFiles(d1, "list-*").Length == 0, "RuleFiles SOCKS5: apps.json и bypass.json, sites и list-* нет");

            var d2 = Path.Combine(root, "sites");
            var vs = new Settings { Mode = ProxyMode.Vless, VlessLink = Reality, Sites = new List<string> { "example.com", "1.2.3.4", "10.0.0.0/8" } };
            RuleFiles.Write(d2, vs, new Dictionary<string, string>());
            Check(Text(d2, "sites") == "{\"version\":3,\"rules\":[{\"domain_suffix\":[\"example.com\"]},{\"ip_cidr\":[\"1.2.3.4/32\",\"10.0.0.0/8\"]}]}"
                && Text(d2, "apps") == "{\"version\":3,\"rules\":[]}", "RuleFiles VLESS: sites.json (domain_suffix и ip_cidr), без программ пустой apps.json");
            Check(RuleFiles.SitesJson(new[] { "example.com" }) == "{\"version\":3,\"rules\":[{\"domain_suffix\":[\"example.com\"]}]}"
                && RuleFiles.SitesJson(Array.Empty<string>()) == "{\"version\":3,\"rules\":[]}", "RuleFiles.SitesJson: только непустые правила");

            var d3 = Path.Combine(root, "lists");
            var yt = Path.Combine(root, "yt.srs");
            var ytBytes = new byte[] { 0x53, 0x52, 0x53, 0x03, 0x01 };
            Directory.CreateDirectory(root);
            File.WriteAllBytes(yt, ytBytes);
            var ls = new Settings { Mode = ProxyMode.Vless, VlessLink = Reality, Lists = new List<string> { "youtube" } };
            var lf = new Dictionary<string, string> { ["youtube"] = yt, ["telegram"] = yt };
            RuleFiles.Write(d3, ls, lf);
            Check(File.ReadAllBytes(P(d3, "list-youtube")).SequenceEqual(ytBytes) && File.ReadAllBytes(P(d3, "list-telegram")).SequenceEqual(RuleFiles.EmptySrs)
                && Directory.GetFiles(d3, "list-*.srs").Length == Catalogs.Lists.Count, "RuleFiles VLESS: включённый список — копия, остальные — пустой SRS, по файлу на список каталога");
            Check(RuleFiles.EmptySrs.Length == 17, "RuleFiles.EmptySrs: 17 байт");

            // review focus 1: те же правила — файлы не переписываются
            var old = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            foreach (var f in Directory.GetFiles(d3)) File.SetLastWriteTimeUtc(f, old);
            RuleFiles.Write(d3, ls, lf);
            Check(Directory.GetFiles(d3).All(f => File.GetLastWriteTimeUtc(f) == old), "RuleFiles: те же аргументы -> ни один файл не переписан");
            ls.Lists.Clear();
            RuleFiles.Write(d3, ls, lf);
            Check(File.GetLastWriteTimeUtc(P(d3, "list-youtube")) != old && File.ReadAllBytes(P(d3, "list-youtube")).SequenceEqual(RuleFiles.EmptySrs)
                && Directory.GetFiles(d3).Where(f => f != P(d3, "list-youtube")).All(f => File.GetLastWriteTimeUtc(f) == old),
                "RuleFiles: выключили youtube -> изменился только его файл");

            // review focus 2: файл занят (sing-box его читает) — запись ждёт и проходит
            ls.Lists.Add("youtube");
            var held = new ManualResetEventSlim();
            var holder = Task.Run(() =>
            {
                using var fs = new FileStream(P(d3, "list-youtube"), FileMode.Open, FileAccess.Read, FileShare.None);
                held.Set();
                Thread.Sleep(300);
            });
            held.Wait(2000);
            Exception? ex = null;
            try { RuleFiles.Write(d3, ls, lf); } catch (Exception e) { ex = e; }
            holder.Wait(2000);
            Check(ex == null && File.ReadAllBytes(P(d3, "list-youtube")).SequenceEqual(ytBytes), "RuleFiles: файл занят на 300 мс -> запись дождалась, без исключения");

            // sing-box читает с общим доступом на чтение: чтение для сравнения проходит, повторять должен File.Move
            ls.Lists.Clear();
            RuleFiles.Write(d3, ls, lf);
            ls.Lists.Add("youtube");
            held.Reset();
            holder = Task.Run(() =>
            {
                using var fs = new FileStream(P(d3, "list-youtube"), FileMode.Open, FileAccess.Read, FileShare.Read);
                held.Set();
                Thread.Sleep(300);
            });
            held.Wait(2000);
            ex = null;
            try { RuleFiles.Write(d3, ls, lf); } catch (Exception e) { ex = e; }
            holder.Wait(2000);
            Check(ex == null && File.ReadAllBytes(P(d3, "list-youtube")).SequenceEqual(ytBytes), "RuleFiles: файл открыт на чтение 300 мс -> замена дождалась, без исключения");
            Check(Directory.GetFiles(d3, "*.tmp").Length == 0 && Directory.GetFiles(d1, "*.tmp").Length == 0, "RuleFiles: временных файлов не осталось");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static void ListStoreTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vivitune-lists-" + Guid.NewGuid().ToString("N"));
        var now = DateTime.UtcNow;
        var good = new byte[] { 0x53, 0x52, 0x53, 1, 2, 3 };
        var old = new byte[] { 0x53, 0x52, 0x53, 9 };
        string P(string id) => Path.Combine(dir, id + ".srs");
        void Seed(string id, bool stale) { Directory.CreateDirectory(dir); File.WriteAllBytes(P(id), old); File.SetLastWriteTimeUtc(P(id), stale ? now.AddDays(-2) : now); }
        try
        {
            {
                Seed("fresh", false);
                int calls = 0;
                var r = ListStore.Prepare(dir, new[] { "fresh" }, id => { Interlocked.Increment(ref calls); return good; }, now);
                Check(calls == 0 && r.Files["fresh"] == P("fresh") && r.Failed.Count == 0 && r.Stale.Count == 0, "ListStore: свежая копия не скачивается");
                var sb = new FakeSingBox();
                var rv = ListStore.PrepareVia(dir, new[] { "fresh" }, sb, new JsonObject(), now);
                Check(sb.Starts == 0 && rv.Files["fresh"] == P("fresh") && Directory.GetFiles(dir, "fetch-*").Length == 0,
                    "ListStore: всё свежее — временный sing-box не запускается");
            }
            {
                Seed("upd", true);
                var r = ListStore.Prepare(dir, new[] { "upd" }, id => good, now);
                Check(File.ReadAllBytes(P("upd")).SequenceEqual(good) && r.Files.ContainsKey("upd") && r.Stale.Count == 0 && r.Failed.Count == 0 && !File.Exists(P("upd") + ".tmp"),
                    "ListStore: старая копия обновлена");
            }
            {
                Seed("st", true);
                var r = ListStore.Prepare(dir, new[] { "st" }, id => throw new IOException("нет сети"), now);
                Check(r.Files.ContainsKey("st") && r.Stale.Contains("st") && r.Failed.Count == 0 && File.ReadAllBytes(P("st")).SequenceEqual(old),
                    "ListStore: ошибка скачивания, работаем со старой копией");
            }
            {
                // качает параллельно, а Failed и Stale отдаёт в порядке ids (в нём и предупреждения)
                Seed("s2", true);
                Seed("s1", true);
                var ids = new[] { "f3", "s2", "f1", "s1", "f2" };
                var r = ListStore.Prepare(dir, ids, id => { Thread.Sleep(id.EndsWith("1") ? 50 : 0); throw new IOException("нет сети"); }, now);
                Check(r.Failed.SequenceEqual(new[] { "f3", "f1", "f2" }) && r.Stale.SequenceEqual(new[] { "s2", "s1" }), "ListStore: Failed и Stale в порядке ids");
            }
            {
                var r = ListStore.Prepare(dir, new[] { "none" }, id => throw new IOException("нет сети"), now);
                Check(!r.Files.ContainsKey("none") && r.Failed.Contains("none") && !File.Exists(P("none")), "ListStore: нет копии и нет сети");
            }
            {
                var html = System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE html><html></html>");
                Seed("hs", true);
                var r1 = ListStore.Prepare(dir, new[] { "hs" }, id => html, now);
                var r2 = ListStore.Prepare(dir, new[] { "hn" }, id => html, now);
                Check(r1.Stale.Contains("hs") && r1.Files.ContainsKey("hs") && File.ReadAllBytes(P("hs")).SequenceEqual(old) &&
                      r2.Failed.Contains("hn") && !File.Exists(P("hn")), "ListStore: не SRS отвергается");
            }
            {
                var newer = new byte[] { 0x53, 0x52, 0x53, 6, 1 };
                Seed("vs", true);
                var r1 = ListStore.Prepare(dir, new[] { "vs" }, id => newer, now);
                var r2 = ListStore.Prepare(dir, new[] { "vn" }, id => newer, now);
                Check(r1.Stale.Contains("vs") && File.ReadAllBytes(P("vs")).SequenceEqual(old) &&
                      r2.Failed.Contains("vn") && !File.Exists(P("vn")), "ListStore: версия SRS новее 5 отвергается");
            }
            {
                var r = ListStore.Prepare(dir, new[] { "a", "b", "c" }, id => good, now);
                Check(r.Files.Count == 3 && r.Failed.Count == 0 && File.Exists(P("a")) && File.Exists(P("b")) && File.Exists(P("c")), "ListStore: три списка сразу");
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // одиночная ссылка, как её строит Session
    static VlessPrep Single(JsonObject proxy, IPAddress ip) => new([ip], [proxy], null, new Dictionary<string, string>(), new Dictionary<string, string>());

    static void PingerTests()
    {
        var api = new FakeApi { Delays = { ["s0"] = 120, ["s1"] = null, ["s2"] = 40 } };
        var got = new List<string>();
        var pings = Pinger.Ping(api, ["s0", "s1", "s2"], 3000, null, (t, _) => got.Add(t), CancellationToken.None);
        Check(got.SequenceEqual(["s0", "s1", "s2"]) && api.Calls.SequenceEqual(["s0", "s1", "s2"]) && api.MaxParallel == 1, "Pinger: по очереди, по одному");
        Check(pings["s1"] == null && pings["s2"] == 40, "Pinger: результаты");
        Check(Pinger.Fastest(pings) == "s2" && Pinger.Fastest(pings, except: "s2") == "s0", "Fastest и except");
        Check(Pinger.Fastest(new Dictionary<string, int?> { ["a"] = null }) == null, "Fastest: все null -> null");
        var slow = new FakeApi { Wait = TimeSpan.FromMilliseconds(80) };
        Check(Pinger.Ping(slow, ["a", "b", "c", "d", "e"], 3000, TimeSpan.FromMilliseconds(150), null, CancellationToken.None).Count < 5, "Pinger: cap обрывает очередь");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Check(Pinger.Ping(new FakeApi(), ["a", "b"], 3000, null, null, cts.Token).Count == 0, "Pinger: отмена -> ничего не проверяет");
    }

    static void SettingsV6Tests()
    {
        var p = Path.GetTempFileName();
        try
        {
            File.WriteAllText(p, "{\"VlessLink\":\"https://sub\",\"Server\":\"NL|h:443\",\"AutoServer\":true}");
            var s = Settings.Load(p);
            Check(s.Server == "NL|h:443" && s.AutoServer && s.Clone().Server == "NL|h:443" && s.Clone().AutoServer, "Settings v6: Server, AutoServer");
            Check(!s.SetVlessLink("https://sub") && s.Server == "NL|h:443", "та же ссылка - выбор сохраняется");
            Check(s.SetVlessLink("vless://u@h:1") && s.Server == null && s.VlessLink == "vless://u@h:1", "смена поля сбрасывает Server");
            new Settings().Save(p);
            Check(!File.ReadAllText(p).Contains("\"Server\""), "Server не пишется, пока не выбран");
            Check(Settings.IsSubscription(" HTTPS://x/y ") && Settings.IsSubscription("http://x") && !Settings.IsSubscription("vless://x") && !Settings.IsSubscription(""), "IsSubscription");
        }
        finally { File.Delete(p); }
    }

    static void SubSessionTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "vivitune-subtests-" + Guid.NewGuid().ToString("N"));
        int n = 0;
        string NewCfg() { var d = Path.Combine(root, (++n).ToString()); Directory.CreateDirectory(d); return Path.Combine(d, "config.json"); }
        const string Url = "https://sub.example.com/t0ken";
        const string K0 = "A|203.0.113.1:443", K1 = "B|203.0.113.2:443", K2 = "C|203.0.113.3:443";
        SubServer Sv(string name, string host) => new(name + "|" + host + ":443", name, FakeVless.FakeServer("vless://u@" + host + ":443"), null);
        SubCache Cache(DateTime? fetched = null) => new(Url, fetched ?? DateTime.UtcNow,
            new SubInfo(null, 24, 0, 0, null), [Sv("A", "203.0.113.1"), Sv("B", "203.0.113.2"), Sv("C", "203.0.113.3")]);
        Settings Sub(string? server = null, bool auto = false) => new Settings { Mode = ProxyMode.Vless, VlessLink = Url, Server = server, AutoServer = auto, Apps = A(@"C:\a\chrome.exe") };
        JsonObject Selector(string cfg) => Obj(Arr(JsonNode.Parse(File.ReadAllText(cfg))!["outbounds"])[0]);
        string? Default(string cfg) => Str(Selector(cfg)["default"]);
        try
        {
            {
                var fb = new FakeSingBox(); var fv = new FakeVless(root, cache: Cache()); var cfg = NewCfg();
                using var ss = new Session(Sub(K1), fb, cfg, Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Connected && fv.Fetches == 0 && Default(cfg) == "s1" && ss.CurrentServer == K1 && fv.Reaches == 1,
                    "подписка: свежий кэш - без скачивания, выбранный из Settings.Server, TCP-проверка выбранного");
                Check(Arr(Selector(cfg)["outbounds"]).Count == 3, "подписка: все серверы в selector");
            }
            {
                var fb = new FakeSingBox(); var fv = new FakeVless(root, cache: Cache(DateTime.UtcNow.AddDays(-3)), fetch: _ => (null, "Подписка не скачалась: нет сети"));
                var logs = new List<string>();
                using var ss = new Session(Sub(), fb, NewCfg(), Up, Delay, fv.Deps);
                ss.Log += l => { lock (logs) logs.Add(l); };
                ss.ConnectAsync().Wait(2000);
                lock (logs)
                    Check(logs.Contains("Подписка не обновилась, работаю по копии: Подписка не скачалась: нет сети") && ss.State == ConnState.Connected && fv.Fetches == 1 && fv.Saves == 0,
                        "подписка не скачалась - работаем по копии");
            }
            {
                var fb = new FakeSingBox(); var fv = new FakeVless(root, fetch: _ => (null, "Подписка не скачалась: нет сети"));
                using var ss = new Session(Sub(), fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                Check(ss.Error == "Подписка не скачалась: нет сети" && fb.Starts == 0 && ss.State == ConnState.Disconnected, "нет копии и не скачалась - ошибка");
            }
            {
                var fb = new FakeSingBox();
                var c = Cache() with { Servers = [new SubServer("X|-", "X", null, "не поддерживается")] };
                var fv = new FakeVless(root, cache: c);
                using var ss = new Session(Sub(), fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                Check(ss.Error == "В подписке нет серверов, которые ViviTune умеет" && fb.Starts == 0, "одни неподдерживаемые");
            }
            {
                var fb = new FakeSingBox(); var fv = new FakeVless(root, fetch: _ => (Cache(), null));
                using var ss = new Session(Sub(), fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Connected && fv.Fetches == 1 && fv.Saves == 1, "подписка: нет копии - скачали и подключились");
            }
            {
                // сервер не разрешился: не в конфиге, выбранным становится первый из оставшихся
                var fb = new FakeSingBox();
                var c = Cache() with { Servers = [Sv("H", "host.example.com"), Sv("A", "203.0.113.1")] };
                var fv = new FakeVless(root, cache: c, resolve: _ => null); var cfg = NewCfg();
                using var ss = new Session(Sub("H|host.example.com:443"), fb, cfg, Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Connected && ss.CurrentServer == K0 && Arr(Selector(cfg)["outbounds"]).Count == 1, "подписка: не разрешившийся сервер не входит в конфиг");
            }
            // автовыбор
            {
                var fb = new FakeSingBox(); var api = new FakeApi { Delays = { ["s0"] = 120, ["s1"] = null, ["s2"] = 40 } };
                var fv = new FakeVless(root, cache: Cache(), api: api); var cfg = NewCfg();
                var changed = new List<string>();
                using var ss = new Session(Sub(K1, true), fb, cfg, Up, Delay, fv.Deps);
                ss.ServerChanged += k => { lock (changed) changed.Add(k); };
                ss.ConnectAsync().Wait(2000);
                lock (changed)
                    Check(api.Selected == ("proxy", "s2") && ss.CurrentServer == K2 && changed.Count == 1 && changed.Last() == K2 && fv.Reaches == 0 && ss.State == ConnState.Connected,
                        "автовыбор: самый быстрый, без TCP-проверки");
                Check(api.MaxParallel == 1 && ss.Pings[K0] == 120 && ss.Pings[K1] == null && ss.Pings[K2] == 40 && fv.AfterStarts == 1, "автовыбор: пинги по очереди, Pings заполнены");
            }
            {
                var fb = new FakeSingBox(); var api = new FakeApi();
                var fv = new FakeVless(root, cache: Cache(), api: api); var cfg = NewCfg();
                using var ss = new Session(Sub(K1, true), fb, cfg, Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                Check(api.Selected == null && ss.CurrentServer == K1 && ss.State == ConnState.Connected && Default(cfg) == "s1", "автовыбор: никто не ответил - прежний");
            }
            {
                var fb = new FakeSingBox(); var api = new FakeApi { Wait = TimeSpan.FromMilliseconds(200) };
                var fv = new FakeVless(root, cache: Cache(), api: api, pingCap: TimeSpan.FromMilliseconds(250));
                using var ss = new Session(Sub(K1, true), fb, NewCfg(), Up, Delay, fv.Deps);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                ss.ConnectAsync().Wait(5000);
                lock (api.Calls)
                    Check(api.Calls.Count == 2 && ss.State == ConnState.Connected && sw.Elapsed < TimeSpan.FromSeconds(3), "автовыбор: cap обрывает очередь");
            }
            {
                // Disconnect во время автовыбора
                var fb = new FakeSingBox(); var api = new FakeApi { Wait = TimeSpan.FromMilliseconds(300), Delays = { ["s0"] = 10, ["s1"] = 5, ["s2"] = 1 } };
                var fv = new FakeVless(root, cache: Cache(), api: api);
                var changed = new List<string>();
                using var ss = new Session(Sub(K1, true), fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ServerChanged += k => { lock (changed) changed.Add(k); };
                var t = ss.ConnectAsync();
                Thread.Sleep(150);
                ss.Disconnect();
                Thread.Sleep(1200);
                lock (changed)
                    Check(ss.State == ConnState.Disconnected && ss.CurrentServer == null && changed.Count == 0 && ss.Pings.Count == 0 && !fb.Running && api.Selected == null,
                        "автовыбор: Disconnect посреди - отключено, ничего не выбрано и не сохранено");
            }
            // ручной выбор
            {
                var fb = new FakeSingBox(); var api = new FakeApi(); var fv = new FakeVless(root, cache: Cache(), api: api);
                var changed = new List<string>();
                using var ss = new Session(Sub(K1), fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ServerChanged += k => { lock (changed) changed.Add(k); };
                Check(!ss.SelectServer(K0), "SelectServer: не подключено - false");
                ss.ConnectAsync().Wait(2000);
                bool bad = ss.SelectServer("нет|-");
                bool ok = ss.SelectServer(K0);
                lock (changed)
                    Check(!bad && ok && api.Selected == ("proxy", "s0") && ss.CurrentServer == K0 && changed.Last() == K0, "SelectServer при подключении - Clash API, без перезапуска");
                Check(fb.Starts == 1, "переключение не перезапускает sing-box");
                int pc = 0; ss.PingsChanged += () => Interlocked.Increment(ref pc);
                api.Delays["s0"] = 50; api.Delays["s1"] = 70;
                Check(ss.PingConnected(CancellationToken.None) && pc == 3 && ss.Pings[K0] == 50 && ss.Pings[K1] == 70 && ss.Pings[K2] == null && api.MaxParallel == 1, "PingConnected: все по очереди, Pings по одному");
                ss.Disconnect();
                Check(!ss.SelectServer(K1) && !ss.PingConnected(CancellationToken.None) && ss.Pings.Count == 0, "после отключения SelectServer и PingConnected - false, Pings пусты");
            }
            // проверка текущего сервера
            {
                // три неудачи с галкой: переключение на самого быстрого из остальных, CurrentServer и события
                var fb = new FakeSingBox(); var api = new FakeApi { Delays = { ["s0"] = 120, ["s1"] = null, ["s2"] = 40, ["proxy"] = 30 } };
                var fv = new FakeVless(root, cache: Cache(), api: api, watchEvery: TimeSpan.FromMilliseconds(20));
                var changed = new List<string>(); var sw = new List<(string, string, int)>();
                using var ss = new Session(Sub(K1, true), fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ServerChanged += k => { lock (changed) changed.Add(k); };
                ss.ServerSwitched += (f, t, m) => { lock (sw) sw.Add((f, t, m)); };
                ss.ConnectAsync().Wait(2000); // автовыбор: s2
                api.Delays["s2"] = 200;       // s2 станет медленнее s0
                lock (api.Calls) for (int i = 0; i < 3; i++) api.Proxy.Enqueue(null);
                bool got = Until(() => { lock (sw) return sw.Count > 0; });
                lock (sw) lock (changed)
                    Check(got && sw[0] == (K2, K0, 120) && ss.CurrentServer == K0 && changed.Last() == K0 && api.Selected == ("proxy", "s0"),
                        "watch в Session: переключение обновляет CurrentServer, ServerChanged и ServerSwitched");
            }
            {
                // без галки: ServerDown с ключом; ручной SelectServer сбрасывает счётчик и «уже сообщали»
                var fb = new FakeSingBox(); var api = new FakeApi();
                for (int i = 0; i < 100; i++) api.Proxy.Enqueue(null);
                var fv = new FakeVless(root, cache: Cache(), api: api, watchEvery: TimeSpan.FromMilliseconds(20));
                var downs = new List<(string, string?, int?)>();
                using var ss = new Session(Sub(K1), fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ServerDown += (c, b, m) => { lock (downs) downs.Add((c, b, m)); };
                ss.ConnectAsync().Wait(2000);
                bool first = Until(() => { lock (downs) return downs.Count > 0; });
                Thread.Sleep(150);
                lock (downs) Check(first && downs.Count == 1 && downs[0] == (K1, null, null), "watch в Session: ServerDown с ключом, один раз");
                Check(ss.SelectServer(K0), "SelectServer после Down");
                Check(Until(() => { lock (downs) return downs.Count == 2; }) && downs[1].Item1 == K0, "SelectServer сбрасывает watch: снова сообщает");
            }
            {
                // PingConnected, пока watch пингует: общий затвор, по одному
                var fb = new FakeSingBox(); var api = new FakeApi { Wait = TimeSpan.FromMilliseconds(15), Delays = { ["s0"] = 120, ["s1"] = 90, ["s2"] = 40 } };
                var fv = new FakeVless(root, cache: Cache(), api: api, watchEvery: TimeSpan.FromMilliseconds(5));
                using var ss = new Session(Sub(K1, true), fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(5000);
                for (int i = 0; i < 6; i++) { ss.PingConnected(CancellationToken.None); Thread.Sleep(10); }
                int checks; lock (api.Calls) checks = api.ProxyChecks;
                Check(api.MaxParallel == 1 && checks >= 3, "PingConnected и watch не пингуют одновременно");
            }
            {
                // одиночная ссылка: SelectServer/PingConnected не работают, ServerChanged нет
                var fb = new FakeSingBox(); var fv = new FakeVless(root); int ev = 0;
                using var ss = new Session(Vless1(), fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ServerChanged += _ => Interlocked.Increment(ref ev);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Connected && !ss.SelectServer(K0) && !ss.PingConnected(CancellationToken.None) && ss.CurrentServer == null && ev == 0, "одиночная ссылка: без выбора сервера");
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static bool Until(Func<bool> f, int ms = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!f()) { if (sw.ElapsedMilliseconds > ms) return false; Thread.Sleep(5); }
        return true;
    }

    static void ServerWatchTests()
    {
        string[] tags = ["s0", "s1", "s2"];
        var iv = TimeSpan.FromMilliseconds(20);
        {
            var api = new FakeApi { Delays = { ["proxy"] = 50 } }; api.Proxy.Enqueue(null); api.Proxy.Enqueue(null);
            var downs = new List<(string, string?, int?)>();
            using var w = new ServerWatch(api, tags, () => "s1", () => false, iv, 100, TimeSpan.FromSeconds(1));
            w.Down += (c, b, m) => { lock (downs) downs.Add((c, b, m)); };
            w.Start();
            bool got = Until(() => { lock (api.Calls) return api.ProxyChecks >= 6; });
            lock (downs) Check(got && downs.Count == 0, "две неудачи — ещё не «не отвечает»");
        }
        {
            var api = new FakeApi { Delays = { ["s0"] = 120 } };
            var downs = new List<(string, string?, int?)>();
            using var w = new ServerWatch(api, tags, () => "s1", () => false, iv, 100, TimeSpan.FromSeconds(1), null,
                () => new Dictionary<string, int?> { ["s0"] = 120, ["s1"] = 30, ["s2"] = null });
            w.Down += (c, b, m) => { lock (downs) downs.Add((c, b, m)); };
            w.Start();
            bool got = Until(() => { lock (downs) return downs.Count > 0; });
            lock (downs) Check(got && downs.Single() == ("s1", "s0", 120), "три неудачи без галки — Down с лучшим из пингов");
            Thread.Sleep(200);
            lock (downs) Check(downs.Count == 1, "после Down повторно не сообщает, пока нет успеха");
            lock (api.Calls) Check(api.Selected == null && api.Calls.All(c => c == "proxy"), "без галки серверы не пингуются и не выбираются");
        }
        {
            var api = new FakeApi { Wait = TimeSpan.FromMilliseconds(5), Delays = { ["s0"] = 120, ["s2"] = 40 } };
            var sw = new List<(string, string, int)>();
            using var w = new ServerWatch(api, tags, () => "s1", () => true, iv, 100, TimeSpan.FromSeconds(1));
            w.Switched += (f, t, m) => { lock (sw) sw.Add((f, t, m)); };
            w.Start();
            bool got = Until(() => { lock (sw) return sw.Count > 0; });
            lock (sw) Check(got && sw[0] == ("s1", "s2", 40) && api.Selected == ("proxy", "s2"), "три неудачи с галкой — переключение на самого быстрого");
            Check(api.MaxParallel == 1, "проверки и пинги — по одному");
        }
        {
            var api = new FakeApi();
            var downs = new List<(string, string?, int?)>();
            using var w = new ServerWatch(api, tags, () => "s1", () => true, iv, 100, TimeSpan.FromSeconds(1));
            w.Down += (c, b, m) => { lock (downs) downs.Add((c, b, m)); };
            w.Start();
            bool got = Until(() => { lock (downs) return downs.Count > 0; });
            Thread.Sleep(200);
            lock (downs) Check(got && downs.Count == 1 && downs[0] == ("s1", null, null) && api.Selected == null, "с галкой никто не ответил — Down без кнопки");
        }
        {
            var api = new FakeApi { Delays = { ["proxy"] = 50 } };
            for (int i = 0; i < 4; i++) api.Proxy.Enqueue(null);
            var downs = new List<(string, string?, int?)>();
            using var w = new ServerWatch(api, tags, () => "s1", () => false, TimeSpan.FromMilliseconds(150), 100, TimeSpan.FromSeconds(1));
            w.Down += (c, b, m) => { lock (downs) downs.Add((c, b, m)); };
            w.Start();
            Until(() => { lock (api.Calls) return api.ProxyChecks >= 2; });
            w.Reset();
            bool got = Until(() => { lock (api.Calls) return api.ProxyChecks >= 6; });
            lock (downs) Check(got && downs.Count == 0, "ручное переключение сбрасывает счётчик");
        }
        {
            var api = new FakeApi(); int n = 0;
            var w = new ServerWatch(api, tags, () => "s1", () => false, iv, 100, TimeSpan.FromSeconds(1));
            w.Down += (_, _, _) => Interlocked.Increment(ref n);
            w.Start(); w.Dispose();
            Thread.Sleep(200);
            Check(n == 0 && api.ProxyChecks <= 1, "Dispose останавливает проверки");
        }
    }

    static void SubscriptionConfigTests()
    {
        var ip1 = IPAddress.Parse("203.0.113.1"); var ip2 = IPAddress.Parse("203.0.113.2"); var ip3 = IPAddress.Parse("2001:db8::3");
        JsonObject O(string t, IPAddress ip) => JsonNode.Parse($"{{\"type\":\"vless\",\"tag\":\"{t}\",\"server\":\"{ip}\",\"server_port\":443,\"uuid\":\"3f1c0a52-0000-4000-8000-000000000001\"}}")!.AsObject();
        var st = new Settings { Mode = ProxyMode.Vless, VlessLink = "vless://x", Apps = A(@"C:\chrome.exe") };
        var files = new Dictionary<string, string>();
        var prep = new VlessPrep([ip1, ip2, ip3], [O("s0", ip1), O("s1", ip2), O("s2", ip3)], "s1", files, new Dictionary<string, string> { ["s0"] = "A|-", ["s1"] = "B|-", ["s2"] = "C|-" });
        var c = JsonNode.Parse(ConfigBuilder.BuildVless(st, 9090, "t", prep, RD))!.AsObject();
        var sel = Obj(Arr(c["outbounds"])[0]);
        Check(Str(sel["type"]) == "selector" && Str(sel["tag"]) == "proxy" && Arr(sel["outbounds"]).Select(Str).SequenceEqual(["s0", "s1", "s2"]) && Str(sel["default"]) == "s1" && sel["interrupt_exist_connections"]!.GetValue<bool>(), "подписка: selector proxy с выбранным по умолчанию");
        var cidrs = Arr(Obj(Arr(Obj(c["route"])["rules"])[2])["ip_cidr"]).Select(Str).ToList();
        Check(Arr(c["outbounds"]).Count == 5 && cidrs.SequenceEqual(["203.0.113.1/32", "203.0.113.2/32", "2001:db8::3/128"]), "подписка: 3 сервера + direct, все IP мимо туннеля");
        Check(Str(Obj(Arr(c["outbounds"])[4])["tag"]) == "direct", "подписка: direct последним");
        Check(Str(prep.FetchProxy["tag"]) == "proxy" && Str(prep.FetchProxy["server"]) == ip2.ToString(), "FetchProxy — выбранный с тегом proxy");
        Check(Str(prep.Outbounds[1]["tag"]) == "s1", "FetchProxy не портит исходный outbound");
        var one = Single(O("proxy", ip1), ip1);
        Check(one.FetchProxy == one.Outbounds[0], "FetchProxy одиночной — сам outbound");

        var pr = JsonNode.Parse(ConfigBuilder.BuildProbe(prep.Outbounds, 9091, "sec"))!.AsObject();
        Check(!pr.ContainsKey("inbounds") && Str(Obj(Obj(pr["experimental"])["clash_api"])["external_controller"]) == "127.0.0.1:9091"
            && Str(Obj(Obj(pr["experimental"])["clash_api"])["secret"]) == "sec" && Str(Obj(pr["log"])["level"]) == "warn"
            && Arr(pr["outbounds"]).Select(o => Str(Obj(o)["tag"])).SequenceEqual(["s0", "s1", "s2", "direct"])
            && Obj(pr["route"])["auto_detect_interface"]!.GetValue<bool>(), "BuildProbe: без inbounds, clash_api и все outbound'ы");
    }

    static void VlessConfigTests()
    {
        // Эталон SOCKS5 (2.3): правила в rule-set'ах, в конфиге только ссылки на них
        const string Golden = @"{""log"":{""level"":""warn"",""timestamp"":true},""experimental"":{""clash_api"":{""external_controller"":""127.0.0.1:9090"",""secret"":""s""}},""dns"":{""servers"":[{""type"":""local"",""tag"":""local""},{""type"":""tcp"",""tag"":""proxy-dns"",""server"":""1.1.1.1"",""detour"":""proxy""}],""rules"":[{""rule_set"":[""apps""],""server"":""proxy-dns""}]},""inbounds"":[{""type"":""tun"",""tag"":""tun-in"",""address"":[""172.19.0.1/30"",""fdfe:dcba:9876::1/126""],""auto_route"":true,""strict_route"":false}],""outbounds"":[{""type"":""socks"",""tag"":""proxy"",""server"":""192.168.1.1"",""server_port"":1818,""version"":""5""},{""type"":""direct"",""tag"":""direct""},{""type"":""socks"",""tag"":""bypass"",""server"":""192.168.1.1"",""server_port"":1819,""version"":""5""}],""route"":{""rules"":[{""action"":""sniff""},{""protocol"":""dns"",""action"":""hijack-dns""},{""ip_cidr"":[""192.168.1.1/32""],""outbound"":""direct""},{""rule_set"":[""apps""],""outbound"":""proxy""},{""rule_set"":[""bypass""],""outbound"":""bypass""}],""rule_set"":[{""type"":""local"",""tag"":""apps"",""format"":""source"",""path"":""C:\\vt\\rules\\apps.json""},{""type"":""local"",""tag"":""bypass"",""format"":""source"",""path"":""C:\\vt\\rules\\bypass.json""}],""final"":""direct"",""auto_detect_interface"":true,""find_process"":true,""default_domain_resolver"":""local""}}";
        var gs = new Settings { Ip = "192.168.1.1", Port = 1818, DirectPort = 1819, Apps = A(@"C:\a b\chrome.exe", @"C:\Игра\Игра.exe"), DirectApps = A(@"C:\q\qbittorrent.exe") };
        var gj = ConfigBuilder.Build(gs, 9090, "s", @"C:\vt\rules");
        Check(gj == Golden, "SOCKS5: конфиг байт в байт (rule-set'ы, 2.3)");
        var gc = JsonNode.Parse(gj)!.AsObject();
        Check(Obj(Arr(gc["inbounds"])[0])["strict_route"]!.GetValue<bool>() == false && !gj.Contains("vless") && !gj.Contains("process_name"), "SOCKS5: strict_route false, нет vless и process_name");

        const string U = "3f1c0a52-0000-4000-8000-000000000001";
        // outbound'ы как их отдаёт движок (decode-link): без tag, server — имя из ссылки
        Server Srv(string json) => new("n", JsonNode.Parse(json)!.AsObject(), []);
        var reality = Srv($"{{\"type\":\"vless\",\"server\":\"nl.example.com\",\"server_port\":443,\"uuid\":\"{U}\",\"flow\":\"xtls-rprx-vision\",\"tls\":{{\"enabled\":true,\"server_name\":\"www.microsoft.com\",\"utls\":{{\"enabled\":true,\"fingerprint\":\"chrome\"}},\"reality\":{{\"enabled\":true,\"public_key\":\"PUBKEY\",\"short_id\":\"ab12\"}}}}}}");
        // ws — настоящий вывод decode-link для ...type=ws&path=/ws?ed=2048&host=cdn.example.com&fp=firefox&alpn=h2,http/1.1 (alpn на ws движок отбрасывает, ed= превращает в max_early_data)
        var wsLink = Srv($"{{\"type\":\"vless\",\"server\":\"1.2.3.4\",\"server_port\":8443,\"uuid\":\"{U}\",\"tls\":{{\"enabled\":true,\"utls\":{{\"enabled\":true,\"fingerprint\":\"firefox\"}}}},\"transport\":{{\"type\":\"ws\",\"path\":\"/ws\",\"headers\":{{\"Host\":\"cdn.example.com\"}},\"max_early_data\":2048,\"early_data_header_name\":\"Sec-WebSocket-Protocol\"}}}}");
        var grpcLink = Srv($"{{\"type\":\"vless\",\"server\":\"nl.example.com\",\"server_port\":8920,\"uuid\":\"{U}\",\"tls\":{{\"enabled\":true,\"server_name\":\"www.microsoft.com\",\"utls\":{{\"enabled\":true,\"fingerprint\":\"randomized\"}},\"reality\":{{\"enabled\":true,\"public_key\":\"PUBKEY\",\"short_id\":\"ab12\"}}}},\"transport\":{{\"type\":\"grpc\",\"service_name\":\"svc\"}}}}");
        var plainLink = Srv($"{{\"type\":\"vless\",\"server\":\"h.example.com\",\"server_port\":80,\"uuid\":\"{U}\"}}");
        var srv = IPAddress.Parse("203.0.113.5");
        var allTags = new[] { "apps", "sites" }.Concat(Catalogs.Lists.Select(l => "list-" + l.Id)).ToArray();
        var files = new[] { "youtube", "russia_inside" };
        JsonObject V(Server link, string[] lists, params string[] apps)
        {
            var st = new Settings { Mode = ProxyMode.Vless, VlessLink = "vless://x", Apps = A(apps), Lists = lists.ToList() };
            return JsonNode.Parse(ConfigBuilder.BuildVless(st, 9090, "t", Single(ConfigBuilder.ProxyOutbound(link, srv), srv), RD))!.AsObject();
        }

        var c = V(reality, files, @"C:\a\chrome.exe");
        var ob = Obj(Arr(c["outbounds"])[0]);
        Check(Str(ob["type"]) == "vless" && Str(ob["tag"]) == "proxy" && Str(ob["server"]) == "203.0.113.5" && (int)ob["server_port"]! == 443
            && Str(ob["uuid"]) == U && Str(ob["flow"]) == "xtls-rprx-vision" && Str(ob["packet_encoding"]) == "xudp", "VLESS: outbound vless, server, uuid, flow, xudp");
        var tls = Obj(ob["tls"]);
        Check(Str(tls["server_name"]) == "www.microsoft.com" && Str(Obj(tls["utls"])["fingerprint"]) == "chrome"
            && Str(Obj(tls["reality"])["public_key"]) == "PUBKEY" && Str(Obj(tls["reality"])["short_id"]) == "ab12" && !ob.ContainsKey("transport"), "VLESS: reality tls, utls, без transport");
        Check(Str(Obj(Arr(c["outbounds"])[1])["type"]) == "direct", "VLESS: outbounds[1] direct");
        var rs = Arr(Obj(c["route"])["rule_set"]);
        Check(rs.Select(x => Str(Obj(x)["tag"])).SequenceEqual(allTags) && rs.All(x => Str(Obj(x)["type"]) == "local")
            && rs.All(x => Str(Obj(x)["path"]) == RuleFiles.PathOf(RD, Str(Obj(x)["tag"])!))
            && rs.All(x => Str(Obj(x)["format"]) == (Str(Obj(x)["tag"])!.StartsWith("list-") ? "binary" : "source")),
            "VLESS: route.rule_set — apps, sites и по списку каталога (binary), пути из PathOf");
        var rules = Arr(Obj(c["route"])["rules"]);
        int iSniff = IndexOfRule(rules, "action"), iDns = IndexOfRule(rules, "protocol"), iIp = IndexOfRule(rules, "ip_cidr"), iRs = IndexOfRule(rules, "rule_set");
        Check(iSniff == 0 && iDns == 1 && iIp == 2 && iRs == 3 && rules.Count == 4, "VLESS: порядок правил sniff, hijack-dns, ip_cidr, rule_set");
        Check(Str(Arr(Obj(rules[iIp])["ip_cidr"])[0]) == "203.0.113.5/32" && Str(Obj(rules[iIp])["outbound"]) == "direct"
            && rules.Count(r => Str(Obj(r)["outbound"]) == "proxy") == 1
            && Arr(Obj(rules[iRs])["rule_set"]).Select(x => Str(x)).SequenceEqual(allTags) && Str(Obj(rules[iRs])["outbound"]) == "proxy",
            "VLESS: одно правило к proxy по всем rule_set");
        Check(!c.ToJsonString().Contains("process_name") && Obj(c["route"])["find_process"]!.GetValue<bool>(), "VLESS: find_process true, process_name в конфиге нет");
        var rt = Obj(c["route"]);
        Check(Str(rt["final"]) == "direct" && rt["auto_detect_interface"]!.GetValue<bool>() && Str(rt["default_domain_resolver"]) == "dns", "VLESS: final direct, default_domain_resolver dns");
        Check(Obj(Arr(c["inbounds"])[0])["strict_route"]!.GetValue<bool>(), "VLESS: strict_route true");
        Check(Obj(Arr(c["inbounds"])[0])["mtu"]?.GetValue<int>() == 9000, "VLESS: TUN mtu 9000, как у Hiddify");

        var w = V(wsLink, files, @"C:\a\chrome.exe");
        var wo = Obj(Arr(w["outbounds"])[0]);
        var tr = Obj(wo["transport"]);
        Check(Str(tr["type"]) == "ws" && Str(tr["path"]) == "/ws" && (int)tr["max_early_data"]! == 2048 && Str(tr["early_data_header_name"]) == "Sec-WebSocket-Protocol" && Str(Obj(tr["headers"])["Host"]) == "cdn.example.com", "VLESS: ws transport, early data");
        var wt = Obj(wo["tls"]);
        Check(Str(Obj(wt["utls"])["fingerprint"]) == "firefox" && Str(wt["server_name"]) == "cdn.example.com" && !wt.ContainsKey("reality") && !wt.ContainsKey("insecure") && !wo.ContainsKey("flow"), "VLESS: tls+ws, utls, SNI из Host, нет reality");

        var plain = V(plainLink, files, @"C:\a\chrome.exe");
        Check(!Obj(Arr(plain["outbounds"])[0]).ContainsKey("tls"), "VLESS: без security нет tls");

        // gRPC — как у Hiddify (ray2sing): ping при простое 15 с, ALPN h2, если в ссылке его нет
        var go = Obj(Arr(V(grpcLink, files, @"C:\a\chrome.exe")["outbounds"])[0]);
        var gtr = Obj(go["transport"]);
        Check(Str(gtr["type"]) == "grpc" && Str(gtr["service_name"]) == "svc" && Str(gtr["idle_timeout"]) == "15s" && Str(gtr["ping_timeout"]) == "15s"
            && Arr(Obj(go["tls"])["alpn"]).Select(x => Str(x)).SequenceEqual(new[] { "h2" }), "VLESS: grpc idle/ping 15s, alpn h2");
        Check(!Obj(wo).ContainsKey("transport") || !Obj(wo["transport"]).ContainsKey("idle_timeout"), "VLESS: ws без idle_timeout");

        var dq = Arr(Obj(c["dns"])["servers"]);
        var d0 = Obj(dq[0]);
        Check(dq.Count == 1 && Str(d0["type"]) == "tcp" && Str(d0["tag"]) == "dns" && Str(d0["server"]) == "1.1.1.1" && Str(d0["detour"]) == "proxy"
            && Str(Obj(c["dns"])["final"]) == "dns" && Str(Obj(c["route"])["default_domain_resolver"]) == "dns"
            && !Obj(c["dns"]).ToJsonString().Contains("\"type\":\"local\""), "VLESS DNS: один сервер 1.1.1.1 по tcp через proxy, нет local");
        Check(Str(Obj(c["dns"])["strategy"]) == "ipv4_only", "VLESS DNS: только IPv4 (у сервера может не быть IPv6)");

        // снимки, отличающиеся программами, сайтами и списками, дают тот же конфиг: за них отвечают файлы правил
        string Cj(Settings st) => ConfigBuilder.BuildVless(st, 9090, "t", Single(ConfigBuilder.ProxyOutbound(reality, srv), srv), RD);
        var s1 = new Settings { Mode = ProxyMode.Vless, VlessLink = "vless://x", Apps = A(@"C:\a\chrome.exe"), Lists = new List<string> { "youtube" }, Sites = new List<string> { "example.com" } };
        var s2 = new Settings { Mode = ProxyMode.Vless, VlessLink = "vless://x", Apps = A(@"C:\a\other.exe", @"C:\b\x.exe"), Lists = new List<string> { "telegram" }, Sites = new List<string> { "1.2.3.4" } };
        Check(Cj(s1) == Cj(s2), "BuildVless: программы, сайты и списки не влияют на конфиг");

        bool threw = false;
        try { V(reality, Array.Empty<string>()); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "VLESS: нет программ, сайтов и списков -> исключение (Validate)");

        var fe = JsonNode.Parse(ConfigBuilder.BuildFetch(ConfigBuilder.ProxyOutbound(reality, srv), 18080))!.AsObject();
        var fin = Obj(Arr(fe["inbounds"])[0]);
        Check(Str(fin["type"]) == "mixed" && Str(fin["listen"]) == "127.0.0.1" && (int)fin["listen_port"]! == 18080
            && Arr(fe["outbounds"]).Count == 1 && Arr(fe["outbounds"])[0]!.ToJsonString() == ob.ToJsonString()
            && !fe.ContainsKey("dns") && !fe.ContainsKey("route"), "VLESS fetch: только mixed на 127.0.0.1 и тот же outbound, без TUN");

        JsonObject P(string json) => ConfigBuilder.ProxyOutbound(new Server("n", JsonNode.Parse(json)!.AsObject(), []), IPAddress.Parse("203.0.113.5"));
        var tlsNoSni = P("{\"type\":\"vless\",\"server\":\"nl.example.com\",\"server_port\":443,\"uuid\":\"u\",\"tls\":{\"enabled\":true}}");
        Check(Str(Obj(tlsNoSni["tls"])["server_name"]) == "nl.example.com" && Str(tlsNoSni["server"]) == "203.0.113.5", "ProxyOutbound: TLS без server_name получает домен");
        var ipNoSni = P("{\"type\":\"vless\",\"server\":\"1.2.3.4\",\"server_port\":443,\"uuid\":\"u\",\"tls\":{\"enabled\":true}}");
        Check(!Obj(ipNoSni["tls"]).ContainsKey("server_name"), "ProxyOutbound: адрес-IP — server_name не выдумывается");
        var grpcAlpn = P("{\"type\":\"vless\",\"server\":\"nl.example.com\",\"server_port\":443,\"uuid\":\"u\",\"tls\":{\"enabled\":true,\"alpn\":[\"http/1.1\"]},\"transport\":{\"type\":\"grpc\"}}");
        Check(Arr(Obj(grpcAlpn["tls"])["alpn"]).Select(Str).SequenceEqual(["http/1.1"]) && Str(Obj(grpcAlpn["transport"])["idle_timeout"]) == "15s", "ProxyOutbound: gRPC со своим ALPN — ALPN не трогаем, idle 15s");
        var hy2 = P("{\"type\":\"hysteria2\",\"server\":\"hy.example.com\",\"server_port\":443,\"password\":\"p\",\"tls\":{\"enabled\":true,\"server_name\":\"hy.example.com\"}}");
        Check(!hy2.ContainsKey("packet_encoding") && !hy2.ContainsKey("transport") && Str(hy2["tag"]) == "proxy", "ProxyOutbound: Hysteria2 без xudp и транспорта");
        var own = P("{\"type\":\"vless\",\"server\":\"nl.example.com\",\"server_port\":443,\"uuid\":\"u\",\"packet_encoding\":\"packetaddr\"}");
        Check(Str(own["packet_encoding"]) == "packetaddr", "ProxyOutbound: свой packet_encoding не перетираем");

        // F7: Host ws/httpupgrade после подмены server на IP не должен стать IP
        var wsNoHost = P("{\"type\":\"vless\",\"server\":\"nl.example.com\",\"server_port\":443,\"uuid\":\"u\",\"tls\":{\"enabled\":true},\"transport\":{\"type\":\"ws\",\"path\":\"/w\"}}");
        Check(Str(Obj(Obj(wsNoHost["transport"])["headers"])["Host"]) == "nl.example.com", "ProxyOutbound: ws без host — Host = домен сервера");
        var wsSni = P("{\"type\":\"vless\",\"server\":\"nl.example.com\",\"server_port\":443,\"uuid\":\"u\",\"tls\":{\"enabled\":true,\"server_name\":\"s.example.com\"},\"transport\":{\"type\":\"ws\"}}");
        Check(Str(Obj(Obj(wsSni["transport"])["headers"])["Host"]) == "s.example.com", "ProxyOutbound: ws без host — Host = server_name");
        var wsHost = P("{\"type\":\"vless\",\"server\":\"nl.example.com\",\"server_port\":443,\"uuid\":\"u\",\"tls\":{\"enabled\":true},\"transport\":{\"type\":\"ws\",\"headers\":{\"Host\":\"cdn.example.com\"}}}");
        Check(Str(Obj(Obj(wsHost["transport"])["headers"])["Host"]) == "cdn.example.com" && Str(Obj(wsHost["tls"])["server_name"]) == "cdn.example.com", "ProxyOutbound: host из ссылки не трогаем, он же SNI");
        var wsIp = P("{\"type\":\"vless\",\"server\":\"1.2.3.4\",\"server_port\":443,\"uuid\":\"u\",\"transport\":{\"type\":\"ws\"}}");
        Check(!Obj(wsIp["transport"]).ContainsKey("headers"), "ProxyOutbound: ws, адрес-IP и нет host — Host не выдумывается");
        var huNoHost = P("{\"type\":\"vless\",\"server\":\"nl.example.com\",\"server_port\":80,\"uuid\":\"u\",\"transport\":{\"type\":\"httpupgrade\"}}");
        Check(Str(Obj(huNoHost["transport"])["host"]) == "nl.example.com", "ProxyOutbound: httpupgrade без host — host = домен сервера");
        var xhNoHost = P("{\"type\":\"vless\",\"server\":\"nl.example.com\",\"server_port\":443,\"uuid\":\"u\",\"tls\":{\"enabled\":true},\"transport\":{\"type\":\"xhttp\"}}");
        Check(!Obj(xhNoHost["transport"]).ContainsKey("host") && !Obj(xhNoHost["transport"]).ContainsKey("headers"), "ProxyOutbound: xhttp без host не трогаем");
        // F1: SNI из транспорта, потом из домена
        var httpArr = P("{\"type\":\"vless\",\"server\":\"1.2.3.4\",\"server_port\":443,\"uuid\":\"u\",\"tls\":{\"enabled\":true},\"transport\":{\"type\":\"http\",\"host\":[\"a.example.com\",\"b.example.com\"]}}");
        var httpStr = P("{\"type\":\"vless\",\"server\":\"1.2.3.4\",\"server_port\":443,\"uuid\":\"u\",\"tls\":{\"enabled\":true},\"transport\":{\"type\":\"http\",\"host\":\"c.example.com\"}}");
        Check(Str(Obj(httpArr["tls"])["server_name"]) == "a.example.com" && Str(Obj(httpStr["tls"])["server_name"]) == "c.example.com", "ProxyOutbound: SNI из http host (массив — первый, строка)");
        var xhHost = P("{\"type\":\"vless\",\"server\":\"1.2.3.4\",\"server_port\":443,\"uuid\":\"u\",\"tls\":{\"enabled\":true},\"transport\":{\"type\":\"xhttp\",\"host\":\"x.example.com\"}}");
        Check(Str(Obj(xhHost["tls"])["server_name"]) == "x.example.com", "ProxyOutbound: SNI из xhttp host");

        SingBoxCheck(srv);
        EngineHostSni(srv);
        SummaryTests();
    }

    // F1 на настоящем выводе движка: SNI из host=, а sni= из ссылки главнее
    static void EngineHostSni(IPAddress srv)
    {
        var exe = SingBoxExe();
        if (exe == null) { Check(false, "sing-box.exe рядом с тестами (SNI из host)"); return; }
        const string U = "3f1c0a52-0000-4000-8000-000000000001";
        var run = Links.Engine(exe);
        string? Sni(string q, out JsonObject? o)
        {
            var d = Links.Decode($"vless://{U}@1.2.3.4:8443?security=tls&{q}", run, out _);
            o = d == null ? null : ConfigBuilder.ProxyOutbound(d, srv);
            return o?["tls"]?["server_name"]?.GetValue<string>();
        }
        Check(Sni("type=ws&host=cdn.example.com&path=%2Fws", out var ws) == "cdn.example.com" && Str(ws!["server"]) == "203.0.113.5", "движок+ProxyOutbound: ws host= даёт SNI");
        Check(Sni("type=httpupgrade&host=cdn.example.com", out _) == "cdn.example.com", "движок+ProxyOutbound: httpupgrade host= даёт SNI");
        Check(Sni("type=xhttp&host=cdn.example.com", out _) == "cdn.example.com", "движок+ProxyOutbound: xhttp host= даёт SNI");
        Check(Sni("type=ws&host=cdn.example.com&sni=a.example.com", out _) == "a.example.com", "движок+ProxyOutbound: sni= главнее host=");
    }

    static void SummaryTests()
    {
        Server S(string json) => new("n", JsonNode.Parse(json)!.AsObject(), []);
        Check(S("{\"type\":\"vmess\",\"server\":\"h.example.com\",\"server_port\":443,\"uuid\":\"u\",\"tls\":{\"enabled\":true},\"transport\":{\"type\":\"ws\"}}").Summary == "h.example.com:443 · VMess · TLS · WebSocket", "Summary: VMess");
        Check(S("{\"type\":\"shadowsocks\",\"server\":\"h.example.com\",\"server_port\":8388,\"method\":\"aes-128-gcm\",\"password\":\"p\"}").Summary == "h.example.com:8388 · Shadowsocks · без TLS · TCP", "Summary: Shadowsocks");
        Check(S("{\"type\":\"socks\",\"server\":\"h.example.com\",\"server_port\":1080}").Summary == "h.example.com:1080 · SOCKS · без TLS · TCP", "Summary: SOCKS");
        Check(S("{\"type\":\"tuic\",\"server\":\"h.example.com\",\"server_port\":443,\"tls\":{\"enabled\":true}}").Summary == "h.example.com:443 · TUIC", "Summary: TUIC");
    }

    // sing-box.exe рядом с тестами, иначе third_party в исходниках; null — не найден
    static string? SingBoxExe()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "sing-box.exe");
        if (File.Exists(exe)) return exe;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var cand = Path.Combine(dir.FullName, "src", "ViviTune", "third_party", "sing-box.exe");
            if (File.Exists(cand)) return cand;
        }
        return null;
    }

    // Образец ответа сервера: «HTTP код», заголовки, пустая строка, тело
    static (Dictionary<string, string> Headers, string Body) Fixture(string name)
    {
        var lines = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".http")).Split('\n');
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 1;
        for (; i < lines.Length && lines[i].TrimEnd('\r') != ""; i++)
        {
            var l = lines[i].TrimEnd('\r');
            int c = l.IndexOf(':');
            if (c > 0) h[l[..c].Trim()] = l[(c + 1)..].Trim();
        }
        return (h, string.Join("\n", lines.Skip(i + 1)));
    }

    static void SubStoreTests()
    {
        var NoHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var (xuiHeaders, xuiBody) = Fixture("3x-ui");
        var (lichnyHtmlHeaders, lichnyHtmlBody) = Fixture("lichny-html");
        var (lichnyHeaders, lichnyBody) = Fixture("lichny-xray");
        // UA: первый — ViviTune/x.y.z, после HTML — Happ/3.5.0
        var uas = new List<string>();
        var p = SubStore.Fetch(ua => { uas.Add(ua); return ua.StartsWith("Happ/") ? (200, lichnyHeaders, lichnyBody) : (200, lichnyHtmlHeaders, lichnyHtmlBody); }, out var e);
        Check(e == null && p!.Entries.Count == 4 && uas.Count == 2 && uas[0].StartsWith("ViviTune/") && uas[1] == "Happ/3.5.0", "Fetch: HTML -> повтор как Happ");
        Check(SubStore.Fetch(_ => (200, xuiHeaders, xuiBody), out _) != null, "Fetch: 3x-ui с первого раза");
        Check(SubStore.Fetch(_ => (404, NoHeaders, ""), out e) == null && e == "Подписка не скачалась: HTTP 404", "Fetch: HTTP 404");
        Check(SubStore.Fetch(_ => throw new HttpRequestException("нет сети"), out e) == null && e == "Подписка не скачалась: нет сети", "Fetch: сеть");
        Check(SubStore.Fetch(_ => null, out e) == null && e != null && e.StartsWith("Подписка не скачалась: "), "Fetch: get вернул null");
        Check(SubStore.Fetch(_ => (200, lichnyHtmlHeaders, lichnyHtmlBody), out e) == null && e == "Это не подписка", "Fetch: HTML и у Happ — ошибка разбора");
        // ключи
        var info = new SubInfo("T", 12, 5, 100, DateTimeOffset.FromUnixTimeSeconds(1793106853));
        var parse = new SubParse(info, [new("АВТО", "vless://a"), new("АВТО", "vless://b"), new("АВТО", "vless://c"), new("NL", "hysteria2://d")], [new("vm", "не поддерживается: vmess")], null);
        var list = SubStore.Decode(parse, l => (new Server("x", Obj(JsonNode.Parse("{\"type\":\"vless\",\"server\":\"h.example.com\",\"server_port\":443}")), []), null));
        Check(list.Select(s => s.Key).SequenceEqual(["АВТО|h.example.com:443", "АВТО|h.example.com:443 #2", "АВТО|h.example.com:443 #3", "NL|h.example.com:443", "vm|-"]), "ключи: повторы получают #2, #3");
        Check(list[^1].Server == null && list[^1].Error == "не поддерживается: vmess", "неподдерживаемый — с причиной");
        var bad = SubStore.Decode(new SubParse(info, [new("B", "vless://bad"), new("B", "vless://bad2")], [], null), _ => (null, "Ссылка не подходит: x"));
        Check(bad[0].Server == null && bad[0].Error == "Ссылка не подходит: x" && bad[0].Key == "B|-" && bad[1].Key == "B|- #2", "движок отказал — серый с причиной");
        // кэш: сохранить/загрузить, другой URL, битый файл
        var dir = Path.Combine(Path.GetTempPath(), "vt-substore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "subscription.json");
            var path2 = Path.Combine(dir, "broken.json");
            var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
            var cache = new SubCache("https://sub.example/x", now, info, list);
            SubStore.Save(path, cache);
            var back = SubStore.Load(path, cache.Url);
            Check(back != null && back.Servers.Count == cache.Servers.Count && back.Servers[0].Server!.Outbound.ToJsonString() == cache.Servers[0].Server!.Outbound.ToJsonString() && back.Info == cache.Info, "кэш: туда и обратно");
            Check(back != null && back.Servers.Select(s => s.Key).SequenceEqual(list.Select(s => s.Key)) && back.Servers[^1].Error == "не поддерживается: vmess" && back.FetchedUtc == now, "кэш: ключи, причины, время");
            Check(!File.Exists(path + ".tmp"), "кэш: tmp убран");
            Check(SubStore.Load(path, "https://other") == null, "кэш другой подписки не берётся");
            Check(SubStore.Load(Path.Combine(dir, "none.json"), cache.Url) == null, "нет файла — null");
            File.WriteAllText(path2, "{битый"); Check(SubStore.Load(path2, cache.Url) == null, "битый кэш — null");
            Check(!SubStore.Stale(cache with { FetchedUtc = now.AddHours(-11) }, now) && SubStore.Stale(cache with { FetchedUtc = now.AddHours(-12) }, now), "Stale по интервалу (12 ч)");
        }
        finally { Directory.Delete(dir, true); }
    }

    static void SubscriptionTests()
    {
        var NoHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var (xuiHeaders, xuiBody) = Fixture("3x-ui");
        var (remnaHeaders, remnaBody) = Fixture("remnawave");
        var (lichnyHtmlHeaders, lichnyHtmlBody) = Fixture("lichny-html");
        var (lichnyHeaders, lichnyBody) = Fixture("lichny-xray");
        var x = Subscription.Parse(xuiHeaders, xuiBody);
        Check(x.Error == null && x.Entries.Count == 1 && x.Entries[0].Link.StartsWith("vless://") && x.Entries[0].Name == "selfsteal-Me", "Подписка 3x-ui: одна ссылка, имя из #");
        Check(x.Info.Title == null && x.Info.IntervalHours == 12 && x.Info.Total == 0 && x.Info.Expire == null && x.Info.Used == 11357853496 + 109480578724, "Подписка 3x-ui: интервал 12, без лимита и срока, расход");
        var r = Subscription.Parse(remnaHeaders, remnaBody);
        Check(r.Info.Title == "StressKVN" && r.Info.IntervalHours == 2 && r.Info.Total == 10737418240 && r.Info.Expire == DateTimeOffset.FromUnixTimeSeconds(1793106853), "Подписка Remnawave: base64:-название, интервал, лимит, срок");
        Check(r.Entries.Count == 15 && r.Entries.Count(e => e.Link.StartsWith("hysteria2://")) == 1 && r.Entries[0].Name == "🇪🇺 ⚡️ Быстрый | АВТО", "Подписка Remnawave: 15 ссылок с именами");
        var h = Subscription.Parse(lichnyHtmlHeaders, lichnyHtmlBody);
        Check(h.Error == "Это не подписка" && h.Entries.Count == 0, "HTML вместо подписки");
        var j = Subscription.Parse(lichnyHeaders, lichnyBody);
        Check(j.Error == null && j.Info.Title == "🛡 Личный VPN" && j.Info.IntervalHours == 1 && j.Entries.Count == 4, "Подписка JSON Xray: группа «Auto» пропущена, 4 сервера");
        Check(j.Entries[2].Name == "🛡 Личный VPN | 81625687 🇩🇪 | 1" && j.Entries.All(e => e.Link.StartsWith("vless://") && e.Link.Contains("type=grpc") && e.Link.Contains("mode=multi") && e.Link.Contains("serviceName=kv") && e.Link.Contains("security=reality") && e.Link.Contains("pbk=")), "JSON Xray -> ссылки VLESS gRPC Reality");
        Check(Subscription.Parse(NoHeaders, Convert.ToBase64String(Encoding.UTF8.GetBytes("vless://u@h:1#a\nvless://u@h:2#b")).TrimEnd('=')).Entries.Count == 2, "base64 без =");
        Check(Subscription.Parse(NoHeaders, Convert.ToBase64String(Encoding.UTF8.GetBytes("vless://u@h:1?a=b>c#a")).Replace('+', '-').Replace('/', '_')).Entries.Count == 1, "URL-safe base64");
        Check(Subscription.Parse(NoHeaders, "vless://u@h:1#a\r\ntrojan://p@h:2#b\r\n").Entries.Count == 2, "ссылки как есть");
        Check(Subscription.Parse(NoHeaders, "").Error == "Это не подписка", "пустой ответ");
        Check(Subscription.Parse(NoHeaders, "proxies:\n  - name: a\n").Error == "Формат подписки не поддерживается", "Clash YAML");
        Check(Subscription.Parse(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["profile-title"] = "Просто имя" }, "vless://u@h:1#a").Info.Title == "Просто имя" && Subscription.Parse(NoHeaders, "vless://u@h:1#a").Info.IntervalHours == 12, "название без base64:, интервал по умолчанию 12");

        var (en, sk) = Subscription.FromXray(JsonNode.Parse("""
            [{"remarks":"v","outbounds":[{"protocol":"vmess","settings":{}},{"protocol":"freedom"}]},
             {"remarks":"t","outbounds":[{"protocol":"trojan","settings":{"servers":[{"address":"t.example.com","port":443,"password":"pw"}]},
               "streamSettings":{"network":"ws","security":"tls","wsSettings":{"path":"/w","headers":{"Host":"h.example.com"}},"tlsSettings":{"serverName":"s.example.com"}}}]},
             {"remarks":"hy","outbounds":[{"protocol":"hysteria","settings":{"version":2,"address":"a","port":443},
               "streamSettings":{"network":"hysteria","security":"tls","tlsSettings":{"serverName":"s.example.com"},"hysteriaSettings":{"version":2,"auth":"auth"}}}]},
             {"remarks":"b","routing":{"balancers":[{"tag":"x"}]},"outbounds":[{"protocol":"vless","settings":{}}]}]
            """)!);
        Check(sk.Count == 1 && sk[0].Name == "v" && sk[0].Reason == "не поддерживается: vmess", "FromXray: vmess -> Skipped с причиной");
        Check(en.Count == 2 && en[0].Name == "t" && en[0].Link == "trojan://pw@t.example.com:443?security=tls&type=ws&path=%2Fw&host=h.example.com&sni=s.example.com#t", "FromXray: trojan ws tls -> ссылка");
        Check(en.Count == 2 && en[1].Link == "hysteria2://auth@a:443?sni=s.example.com#hy", "FromXray: hysteria v2 -> hysteria2://");
        Check(Subscription.Parse(NoHeaders, "{\"a\":1}").Error == "Формат подписки не поддерживается", "JSON без outbounds");
        Check(Subscription.Parse(NoHeaders, "[1]").Error != null && Subscription.Parse(NoHeaders, "[\"x\"]").Error != null, "JSON: массив не объектов -> ошибка, без исключения");
        var odd = Subscription.Parse(NoHeaders, "[{\"routing\":\"x\",\"outbounds\":[{\"protocol\":\"trojan\",\"settings\":{\"address\":\"a\",\"port\":1,\"password\":\"p\"},\"streamSettings\":{\"network\":\"ws\",\"wsSettings\":{\"headers\":\"h\"}}}]}]");
        Check(odd.Error == null && odd.Entries.Count == 1, "FromXray: routing/headers не объекты -> без исключения");
        Check(Subscription.Parse(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["subscription-userinfo"] = "expire=99999999999999" }, "vless://u@h:1#a").Info.Expire == null, "expire огромный -> null, без исключения");
        Check(Subscription.Parse(NoHeaders, "[{\"routing\":{\"balancers\":[{}]},\"outbounds\":[{\"protocol\":\"vless\"}]}]").Error == "Это не подписка"
            && Subscription.Parse(NoHeaders, "{\"outbounds\":[{\"protocol\":\"freedom\"}]}").Error == "Это не подписка", "JSON без серверов -> Это не подписка");
    }

    static void LinksTests()
    {
        const string VlessJson = "{\"type\":\"vless\",\"tag\":\"🇳🇱 NL\",\"server\":\"nl.example.com\",\"server_port\":443,\"uuid\":\"u\",\"flow\":\"xtls-rprx-vision\",\"tls\":{\"enabled\":true,\"server_name\":\"nl.example.com\",\"reality\":{\"enabled\":true,\"public_key\":\"k\"}},\"transport\":{\"type\":\"grpc\"}}";
        var sent = new List<string>();
        var s = Links.Decode("﻿  vless://x \r\n", l => { sent.Add(l); return (0, VlessJson, "warning: the gRPC mode multi is not supported, gun is used\n"); }, out var e);
        Check(e == null && sent.SequenceEqual(["vless://x"]), "Links: в движок уходит ссылка без пробелов и BOM");
        Check(s!.Name == "🇳🇱 NL" && s.Host == "nl.example.com" && s.Port == 443 && !s.Outbound.ContainsKey("tag") && !s.Udp, "Links: имя из tag, адрес, порт; tag убран");
        Check(s.Summary == "nl.example.com:443 · Reality · gRPC · Vision", "Links: сводка VLESS");
        Check(s.Warnings.SequenceEqual(["the gRPC mode multi is not supported, gun is used"]), "Links: предупреждения движка");
        var hy = Links.Decode("hysteria2://p@1.2.3.4:443", _ => (0, "{\"type\":\"hysteria2\",\"server\":\"1.2.3.4\",\"server_port\":443,\"password\":\"p\",\"tls\":{\"enabled\":true}}", ""), out _);
        Check(hy!.Udp && hy.Name == "1.2.3.4" && hy.Summary == "1.2.3.4:443 · Hysteria2", "Links: Hysteria2 — UDP, без tag имя = адрес");
        var tj = Links.Decode("trojan://p@h.example.com:443", _ => (0, "{\"type\":\"trojan\",\"server\":\"h.example.com\",\"server_port\":443,\"password\":\"p\",\"tls\":{\"enabled\":true},\"transport\":{\"type\":\"ws\"}}", ""), out _);
        Check(tj!.Summary == "h.example.com:443 · Trojan · TLS · WebSocket", "Links: сводка Trojan");
        var v6 = Links.Decode("vless://x", _ => (0, "{\"type\":\"vless\",\"server\":\"2001:db8::1\",\"server_port\":80,\"uuid\":\"u\"}", ""), out _);
        Check(v6!.Summary == "[2001:db8::1]:80 · без TLS · TCP", "Links: IPv6 в скобках, без TLS, TCP");
        var xh = Links.Decode("vless://x", _ => (0, "{\"type\":\"vless\",\"server\":\"h.example.com\",\"server_port\":443,\"uuid\":\"u\",\"tls\":{\"enabled\":true},\"transport\":{\"type\":\"xhttp\",\"mode\":\"auto\"}}", ""), out _);
        Check(xh!.Summary == "h.example.com:443 · TLS · xHTTP", "Links: сводка xHTTP");
        Check(Links.Decode("vless://bad", _ => (2, "", "error: invalid server address\n"), out e) == null && e == "Ссылка не подходит: invalid server address", "Links: отказ движка (код 2)");
        Check(Links.Decode("vless://x", _ => (1, "", "unknown command\n"), out e) == null && e == "sing-box не разобрал ссылку (код 1)", "Links: прочий код движка");
        Check(Links.Decode("vless://x", _ => throw new FileNotFoundException("Не найден sing-box.exe"), out e) == null && e == "sing-box не разобрал ссылку: Не найден sing-box.exe", "Links: движок не запустился");
        int calls = 0;
        Check(Links.Decode("  ", _ => { calls++; return (0, "", ""); }, out e) == null && e == "Вставьте ссылку в настройках" && calls == 0, "Links: пусто — без движка");
        foreach (var sub in new[] { "https://sub.example.com/abc", "HTTP://sub.example.com/abc" })
            Check(Links.Decode(sub, _ => { calls++; return (0, "", ""); }, out e) == null && e == "Это адрес подписки. Подписки появятся в следующей версии — пока вставьте ссылку на один сервер (vless://, hysteria2://, trojan://…)" && calls == 0, "Links: адрес подписки — русская ошибка, без движка");
        Check(Links.Decode("просто текст", _ => { calls++; return (0, "", ""); }, out e) == null && e == "Это не ссылка" && calls == 0, "Links: не ссылка — без движка");

        var self = Environment.ProcessPath;
        if (self != null && !Path.GetFileName(self).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Check(Links.Decode("vless://x", Links.Engine(self, 500), out e) == null && e == "sing-box не разобрал ссылку: sing-box не ответил" && sw.Elapsed < TimeSpan.FromSeconds(8), "Links: зависший движок — таймаут, ошибка");
        }
        else Check(false, "Links: тест таймаута требует apphost тестов");

        var exe = SingBoxExe();
        if (exe == null) { Check(false, "sing-box.exe рядом с тестами"); return; }
        const string U = "3f1c0a52-0000-4000-8000-000000000001", Pbk = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0";
        var run = Links.Engine(exe);
        var g = Links.Decode($"vless://{U}@g.example.com:8920?encryption=none&fp=randomized&mode=multi&pbk={Pbk}&security=reality&sid=ab12&sni=g.example.com&spx=%2Fx&type=grpc#gRPC", run, out e);
        Check(e == null && g!.Type == "vless" && g.Outbound["transport"]!["type"]!.GetValue<string>() == "grpc" && g.Warnings.Count > 0, "движок: VLESS gRPC Reality, предупреждение про multi");
        var x = Links.Decode($"vless://{U}@x.example.com:443?security=reality&sni=x.example.com&fp=chrome&pbk={Pbk}&sid=ab12&type=xhttp&path=%2Fxh&mode=auto#xh", run, out e);
        Check(e == null && x!.Outbound["transport"]!["type"]!.GetValue<string>() == "xhttp", "движок: VLESS xHTTP");
        var h = Links.Decode("hysteria2://pass@hy.example.com:443?sni=hy.example.com&obfs=salamander&obfs-password=o#Нидерланды 🇳🇱", run, out e);
        Check(e == null && h!.Udp && h.Name == "Нидерланды 🇳🇱" && h.Outbound["obfs"]!["type"]!.GetValue<string>() == "salamander", "движок: сырой Юникод в имени, Hysteria2 с salamander");
        var t = Links.Decode("trojan://pass@t.example.com:443?security=tls&sni=t.example.com&type=ws&path=%2Fw#t", run, out e);
        Check(e == null && t!.Type == "trojan", "движок: Trojan");
        Check(Links.Decode("vless://@h:443", run, out e) == null && e!.StartsWith("Ссылка не подходит: "), "движок: битая ссылка — код 2");
    }

    // Реальный sing-box check: конфиги из BuildVless принимаются движком из third_party
    static void SingBoxCheck(IPAddress srv)
    {
        var exe = SingBoxExe();
        if (exe == null) { Check(false, "sing-box.exe рядом с тестами"); return; }

        var tmp = Directory.CreateTempSubdirectory("vivitune-sb").FullName;
        try
        {
            int Run(params string[] args)
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe) { WorkingDirectory = tmp, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var pr = System.Diagnostics.Process.Start(psi)!;
                var o = pr.StandardOutput.ReadToEndAsync(); var e = pr.StandardError.ReadToEndAsync();
                pr.WaitForExit();
                if (pr.ExitCode != 0) Console.WriteLine(o.Result + e.Result);
                return pr.ExitCode;
            }
            var src = Path.Combine(tmp, "rs.json");
            var srs = Path.Combine(tmp, "rs.srs");
            File.WriteAllText(src, "{\"version\":3,\"rules\":[{\"domain_suffix\":[\"example.com\"]}]}");
            Check(Run("rule-set", "compile", "--output", srs, src) == 0 && File.Exists(srs), "sing-box rule-set compile");
            var f = new Dictionary<string, string> { ["russia_inside"] = srs };
            var rd = Path.Combine(tmp, "rules");
            const string U = "3f1c0a52-0000-4000-8000-000000000001", Pbk = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0";
            var run = Links.Engine(exe);
            foreach (var (name, link) in new[]
            {
                ("reality", $"vless://{U}@nl.example.com:443?security=reality&sni=www.microsoft.com&pbk={Pbk}&sid=ab12&type=tcp&flow=xtls-rprx-vision"),
                ("ws", $"vless://{U}@1.2.3.4:8443?security=tls&type=ws&host=cdn.example.com&path=%2Fws%3Fed%3D2048&fp=firefox&alpn=h2,http/1.1"),
                ("grpc", $"vless://{U}@nl.example.com:8920?security=reality&sni=www.microsoft.com&pbk={Pbk}&sid=ab12&type=grpc&serviceName=svc&fp=randomized"),
                ("xhttp", $"vless://{U}@x.example.com:443?security=reality&sni=x.example.com&fp=chrome&pbk={Pbk}&sid=ab12&type=xhttp&path=%2Fxh&mode=auto"),
                ("hysteria2", "hysteria2://pass@hy.example.com:443?sni=hy.example.com&obfs=salamander&obfs-password=o"),
            })
            {
                var server = Links.Decode(link, run, out var derr);
                if (server == null) { Check(false, "sing-box check: " + name + " — разбор: " + derr); continue; }
                var proxy = ConfigBuilder.ProxyOutbound(server, srv);
                var st = new Settings { Mode = ProxyMode.Vless, VlessLink = "vless://x", Apps = A(@"C:\a\chrome.exe"), Lists = new List<string> { "russia_inside" }, Sites = new List<string> { "example.com", "10.0.0.0/8" } };
                var cfg = Path.Combine(tmp, name + ".json");
                RuleFiles.Write(rd, st, f);
                File.WriteAllText(cfg, ConfigBuilder.BuildVless(st, 9090, "t", Single(proxy, srv), rd));
                Check(Run("check", "-c", cfg) == 0, "sing-box check: " + name);
                File.WriteAllText(cfg, ConfigBuilder.BuildFetch(proxy, 18080));
                Check(Run("check", "-c", cfg) == 0, "sing-box check: fetch " + name);
            }
            // подписка: три настоящих outbound'а под selector и конфиг пинга
            var subs = new List<JsonObject>();
            foreach (var (n, link) in new[]
            {
                ("s0", $"vless://{U}@nl.example.com:443?security=reality&sni=www.microsoft.com&pbk={Pbk}&sid=ab12&type=tcp&flow=xtls-rprx-vision"),
                ("s1", $"vless://{U}@nl.example.com:8920?security=reality&sni=www.microsoft.com&pbk={Pbk}&sid=ab12&type=grpc&serviceName=svc&fp=randomized"),
                ("s2", "hysteria2://pass@hy.example.com:443?sni=hy.example.com&obfs=salamander&obfs-password=o"),
            })
            {
                var server = Links.Decode(link, run, out var derr);
                if (server == null) { Check(false, "sing-box check: подписка " + n + " — разбор: " + derr); continue; }
                var o = ConfigBuilder.ProxyOutbound(server, srv);
                o["tag"] = n;
                subs.Add(o);
            }
            if (subs.Count == 3)
            {
                var sst = new Settings { Mode = ProxyMode.Vless, VlessLink = "vless://x", Apps = A(@"C:\a\chrome.exe"), Lists = new List<string> { "russia_inside" } };
                var sprep = new VlessPrep([srv, srv, srv], subs, "s1", f, new Dictionary<string, string>());
                var scfg = Path.Combine(tmp, "sub.json");
                RuleFiles.Write(rd, sst, f);
                File.WriteAllText(scfg, ConfigBuilder.BuildVless(sst, 9090, "t", sprep, rd));
                Check(Run("check", "-c", scfg) == 0, "sing-box check: подписка с selector");
                File.WriteAllText(scfg, ConfigBuilder.BuildProbe(subs, 9091, "t"));
                Check(Run("check", "-c", scfg) == 0, "sing-box check: конфиг пинга");
            }
            var both = Both();
            var sc = Path.Combine(tmp, "socks.json");
            RuleFiles.Write(rd, both, f);
            File.WriteAllText(sc, ConfigBuilder.Build(both, 9090, "t", rd));
            Check(Run("check", "-c", sc) == 0, "sing-box check: SOCKS5 с apps и bypass");
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    static void SettingsV4Tests()
    {
        const string Link = "vless://3f1c0a52-0000-4000-8000-000000000001@h.example.com:443?security=tls&sni=a.com";
        var p = Path.GetTempFileName();
        File.WriteAllText(p, """{"Ip":"10.0.0.1","Port":1080,"Apps":[{"Path":"C:\\a\\x.exe","Enabled":true}],"DirectPort":1819,"DirectApps":[{"Path":"C:\\b\\y.exe","Enabled":true}]}""");
        var v3 = Settings.Load(p);
        Check(v3.Mode == ProxyMode.Socks5 && v3.VlessLink == "" && v3.Lists.Count == 0 && v3.Apps.Count == 1 && v3.DirectApps.Count == 1 && v3.DirectPort == 1819,
            "Settings v4: файл v3 читается, новые поля по умолчанию");

        var s = new Settings { Mode = ProxyMode.Vless, VlessLink = Link, Lists = new List<string> { "youtube", "telegram" } };
        s.Save(p);
        Check(File.ReadAllText(p).Contains("\"Mode\": \"Vless\""), "Settings v4: Mode пишется строкой");
        var b = Settings.Load(p);
        Check(b.Mode == ProxyMode.Vless && b.VlessLink == Link && b.Lists.SequenceEqual(new[] { "youtube", "telegram" }),
            "Settings v4: туда и обратно");

        File.WriteAllText(p, """{"Mode":"vless","Doh":"quad9","Lists":["youtube","nope","YOUTUBE",5]}""");
        var d = Settings.Load(p);
        Check(d.Mode == ProxyMode.Vless && d.Lists.SequenceEqual(new[] { "youtube" }), "Settings v4: мусор в Lists отфильтрован, старое поле Doh не мешает");
        File.Delete(p);

        Check(new Settings { Mode = ProxyMode.Vless }.Validate() == "Вставьте ссылку в настройках", "Validate Vless: пустая ссылка");
        Check(new Settings { Mode = ProxyMode.Vless, VlessLink = "abc" }.Validate() == "Это не ссылка", "Validate Vless: не ссылка");
        Check(new Settings { Mode = ProxyMode.Vless, VlessLink = Link }.Validate() == "Добавьте приложение или сайт либо включите список", "Validate Vless: нет программ и списков");
        Check(new Settings { Mode = ProxyMode.Vless, VlessLink = Link, Lists = new List<string> { "youtube" } }.Validate() == null, "Validate Vless: только список");
        Check(new Settings { VlessLink = "мусор", Apps = A(@"C:\a\x.exe") }.Validate() == null, "Validate Socks5: ссылку не смотрит");

        var c = s.Clone();
        c.Lists.Add("discord");
        Check(c.Mode == ProxyMode.Vless && c.VlessLink == Link && s.Lists.Count == 2, "Clone: новые поля, список копии независим");
    }

    static void SiteEntryTests()
    {
        var ok = new[]
        {
            ("example.com", "example.com"), ("  Example.COM ", "example.com"),
            ("https://www.Example.com/path?q=1", "www.example.com"), ("example.com/page", "example.com"),
            ("example.com:8443", "example.com"), ("*.example.com", "example.com"), (".example.com", "example.com"),
            ("сайт.рф", "xn--80aswg.xn--p1ai"), ("1.2.3.4", "1.2.3.4"), ("https://1.2.3.4/x", "1.2.3.4"),
            ("10.1.2.3/8", "10.0.0.0/8"), ("2001:db8::1", "2001:db8::1"), ("2001:db8::/32", "2001:db8::/32"),
        };
        foreach (var (input, want) in ok)
            Check(SiteEntry.Parse(input, out var e) == want && e == null, "SiteEntry: " + input);

        const string Whole = "Введите адрес целиком, например example.com", Bad = "Это не домен и не IP-адрес";
        foreach (var (input, want) in new[] { ("localhost", Whole), ("привет мир", Bad), ("1.2.3", Bad), ("10.0.0.0/33", Bad), ("a_b.com", Bad) })
            Check(SiteEntry.Parse(input, out var e) == null && e == want, "SiteEntry ошибка: " + input);

        Check(SiteEntry.Display("xn--80aswg.xn--p1ai") == "сайт.рф" && SiteEntry.Display("1.2.3.4") == "1.2.3.4", "SiteEntry.Display");
        Check(SiteEntry.Cidr("1.2.3.4") == "1.2.3.4/32" && SiteEntry.Cidr("2001:db8::1") == "2001:db8::1/128" && SiteEntry.Cidr("10.0.0.0/8") == "10.0.0.0/8", "SiteEntry.Cidr");
        Check(SiteEntry.IsAddress("10.0.0.0/8") && !SiteEntry.IsAddress("example.com"), "SiteEntry.IsAddress");
    }

    static void SettingsV5Tests()
    {
        const string Link = "vless://3f1c0a52-0000-4000-8000-000000000001@h.example.com:443?security=tls&sni=a.com";
        var p = Path.GetTempFileName();
        var s = new Settings { Sites = new List<string> { "example.com", "1.2.3.4" } };
        s.Save(p);
        Check(Settings.Load(p).Sites.SequenceEqual(new[] { "example.com", "1.2.3.4" }), "Settings v5: Sites туда и обратно");
        File.WriteAllText(p, """{"Sites":["Example.com","https://example.com/x","мусор мусор",5,"1.2.3.4"]}""");
        Check(Settings.Load(p).Sites.SequenceEqual(new[] { "example.com", "1.2.3.4" }), "Settings v5: мусор и дубли в Sites отфильтрованы");
        File.WriteAllText(p, """{"Port":1080}""");
        Check(Settings.Load(p).Sites.Count == 0, "Settings v5: без Sites пустой список");
        File.Delete(p);

        var c = s.Clone();
        c.Sites.Add("a.com");
        Check(!ReferenceEquals(c.Sites, s.Sites) && s.Sites.Count == 2 && c.Sites.Count == 3, "Settings v5: Clone копирует Sites");

        Check(new Settings { Mode = ProxyMode.Vless, VlessLink = Link, Sites = new List<string> { "example.com" } }.Validate() == null, "Validate Vless: только сайт");
        Check(new Settings { Mode = ProxyMode.Vless, VlessLink = Link }.Validate() == "Добавьте приложение или сайт либо включите список", "Validate Vless: ничего нет");
    }

    static void CatalogsTests()
    {
        Check(Catalogs.Lists.Count == 25 && Catalogs.Lists.Select(l => l.Id).Distinct().Count() == 25, "Catalogs: 25 уникальных списков");
        Check(Catalogs.Lists.All(l => Catalogs.Groups.Contains(l.Group)), "Catalogs: группа каждого списка известна");
        var inRu = Catalogs.Lists.Where(l => l.InRussiaInside).Select(l => l.Id).OrderBy(x => x).ToArray();
        Check(inRu.SequenceEqual(new[] { "anime", "block", "geoblock", "hdrezka", "news", "porn", "tiktok", "youtube" }), "Catalogs: InRussiaInside ровно у восьми");
        Check(Catalogs.ListById("YouTube")?.Id == "youtube" && Catalogs.ListById("nope") == null, "Catalogs: ListById без учёта регистра");
        Check(Catalogs.ListUrl("youtube") == "https://github.com/itdoginfo/allow-domains/releases/latest/download/youtube.srs", "Catalogs: ListUrl");
    }

    static void AppListTests()
    {
        var apps = new List<AppEntry> { new() { Path = @"C:\Games\Game.exe", Enabled = false } };
        Check(!AppListOps.Add(apps, @"c:\games\GAME.EXE") && apps.Count == 1 && !apps[0].Enabled,
            "AppList: добавление без дублей (тот же путь в другом регистре -> false)");
        Check(!AppListOps.Add(apps, "  ") && apps.Count == 1, "AppList: пустой путь не добавляется");
        Check(AppListOps.Add(apps, @"C:\Apps\Discord.exe") && apps.Count == 2 && apps[1].Path == @"C:\Apps\Discord.exe" && apps[1].Enabled,
            "AppList: новое добавляется в конец и сразу включено");
        Check(AppListOps.SetEnabled(apps, @"C:\GAMES\game.exe", true) && apps[0].Enabled
            && !AppListOps.SetEnabled(apps, @"C:\games\game.exe", true) && !AppListOps.SetEnabled(apps, @"C:\нет.exe", false),
            "AppList: SetEnabled по пути без учёта регистра (без изменений -> false)");
        Check(AppListOps.Remove(apps, @"c:\apps\DISCORD.exe") && apps.Count == 1 && apps[0].Path == @"C:\Games\Game.exe"
            && !AppListOps.Remove(apps, @"C:\нет.exe"), "AppList: Remove по пути без учёта регистра");
        var dup = new List<AppEntry> { new() { Path = @"C:\a.exe" }, new() { Path = @"C:\A.EXE" } }; // дубль из старого settings.json
        Check(AppListOps.Remove(dup, @"c:\a.exe") && dup.Count == 0, "AppList: Remove убирает и дубли в другом регистре");
        var orig = new List<AppEntry> { new() { Path = @"C:\a.exe" } };
        var copy = AppListOps.Copy(orig);
        AppListOps.SetEnabled(copy, @"C:\a.exe", false);
        AppListOps.Add(copy, @"C:\b.exe");
        Check(orig.Count == 1 && orig[0].Enabled && copy.Count == 2 && !copy[0].Enabled && !ReferenceEquals(orig[0], copy[0]),
            "AppList: Copy — новый список с новыми элементами (Settings.Apps на месте не меняется)");
        var upd = new List<AppEntry> { new() { Path = @"C:\Discord\app-1.0.1\Discord.exe", Enabled = false }, new() { Path = @"C:\a\chrome.exe" } };
        Check(AppListOps.Add(upd, @"C:\Discord\app-1.0.2\DISCORD.EXE") && upd.Count == 2 && upd[0].Path == @"C:\Discord\app-1.0.2\DISCORD.EXE"
            && !upd[0].Enabled && upd[1].Path == @"C:\a\chrome.exe",
            "AppList: тот же файл в другой папке (обновление) -> путь заменён на месте, Enabled прежний, второй строки нет");
        Check(!AppListOps.Add(upd, @"c:\discord\app-1.0.2\discord.exe") && upd.Count == 2 && upd[0].Path == @"C:\Discord\app-1.0.2\DISCORD.EXE",
            "AppList: тот же файл, путь не изменился -> false");
        Check(AppListOps.SameFile(@"C:\Discord\app-1.0.1\Discord.exe", @"D:\x\DISCORD.EXE") && !AppListOps.SameFile(@"C:\Discord\Discord.exe", @"C:\Discord\Update.exe"),
            "AppList.SameFile: по имени файла без учёта регистра, папка не важна");

        var to = new List<AppEntry>();
        var from = new List<AppEntry> { new() { Path = @"C:\Old\qbittorrent.exe", Enabled = false }, new() { Path = @"C:\a.exe" } };
        Check(AppListOps.Move(to, from, @"C:\New\QBITTORRENT.EXE") && from.Count == 1 && from[0].Path == @"C:\a.exe"
            && to.Count == 1 && to[0].Path == @"C:\New\QBITTORRENT.EXE" && to[0].Enabled,
            "AppList.Move: тот же файл убран из другого списка, добавлен сюда включённым");
        Check(!AppListOps.Move(to, from, @"c:\new\qbittorrent.exe") && to.Count == 1 && from.Count == 1, "AppList.Move: уже здесь, в другом нет -> false");
        Check(AppListOps.Move(to, from, @"C:\b.exe") && to.Count == 2, "AppList.Move: нет нигде -> добавлено");

        var g = new ClickGuard();
        Check(g.TryAccept(0) && !g.TryAccept(1) && !g.TryAccept(499) && g.TryAccept(500) && !g.TryAccept(999) && g.TryAccept(1000) && g.TryAccept(5000),
            "ClickGuard: второе нажатие в течение 500 мс после принятого не принимается");

        var v = new ViewState();
        var first = v.SyncApps(new List<AppEntry> { new() { Path = @"C:\Apps\Discord.exe" }, new() { Path = @"C:\Apps\chrome.exe", Enabled = false } }, new());
        var discord = v.Apps[0];
        Check(first.Count == 2 && v.Apps.Count == 2 && discord.Name == "Discord" && discord.FileName == "Discord.exe" && discord.Enabled
            && !v.Apps[1].Enabled, "ViewState.SyncApps: строки по списку (имя из имени файла)");
        var added = v.SyncApps(new List<AppEntry> { new() { Path = @"c:\apps\DISCORD.exe", Enabled = false }, new() { Path = @"C:\Apps\Steam.exe" } }, new());
        Check(added.Count == 1 && added[0].Path == @"C:\Apps\Steam.exe" && v.Apps.Count == 2 && ReferenceEquals(v.Apps[0], discord)
            && !discord.Enabled && v.Apps[1] == added[0], "ViewState.SyncApps: лишние убраны, новые вставлены, старые строки сохранены");
        v.SyncApps(new List<AppEntry> { new() { Path = @"C:\a.exe" }, new() { Path = @"C:\A.EXE" } }, new()); // дубль из старого settings.json
        v.SyncApps(new List<AppEntry> { new() { Path = @"C:\a.exe", Enabled = false }, new() { Path = @"C:\A.EXE", Enabled = false } }, new());
        Check(v.Apps.Count == 2 && v.Apps.All(r => !r.Enabled), "ViewState.SyncApps: дубли в другом регистре обновляются оба");
    }

    static void CatalogTests()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var list = ProcessCatalog.Running([]);
        Check(list.All(a => !a.Path.StartsWith(win, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(System.IO.Path.GetFileName(a.Path), "sing-box.exe", StringComparison.OrdinalIgnoreCase)),
            "Running: нет файлов из WINDIR и sing-box");
        Check(!list.Any(a => string.Equals(a.Path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)),
            "Running: текущий процесс исключён");
        if (list.Count == 0) Console.WriteLine("skip: Running: excludePaths (список пуст)");
        else
        {
            var first = list[0].Path;
            var after = ProcessCatalog.Running([first.ToUpperInvariant()]);
            Check(!after.Any(a => string.Equals(a.Path, first, StringComparison.OrdinalIgnoreCase)),
                "Running: excludePaths работает без учёта регистра");
            var moved = ProcessCatalog.Running([@"C:\другая\папка\" + System.IO.Path.GetFileName(first)]);
            Check(!moved.Any(a => string.Equals(a.Path, first, StringComparison.OrdinalIgnoreCase)),
                "Running: уже добавленный по имени файла (другая папка) тоже исключён");
        }
        var grouped = ProcessCatalog.Group([(@"C:\a\Discord.exe", false), (@"c:\A\DISCORD.EXE", true), (@"C:\b\python.exe", false), (@"C:\b\python.exe", false)]).ToList();
        Check(grouped.Count == 2 && grouped.Single(g => g.Path == @"C:\a\Discord.exe").Background == false
            && grouped.Single(g => g.Path == @"C:\b\python.exe").Background,
            "Group: одна строка на путь (без учёта регистра); фоновая — если ни у одного процесса нет окна");
        var png = ProcessCatalog.IconPng(System.IO.Path.Combine(win, "explorer.exe"));
        Check(png != null && png.Length > 4 && png[0] == 0x89 && png[1] == (byte)'P' && png[2] == (byte)'N' && png[3] == (byte)'G',
            "IconPng: explorer.exe -> PNG");
        Check(ProcessCatalog.IconPng(@"C:\нет\такого.exe") == null, "IconPng: нет файла -> null");
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "x.exe");
        File.WriteAllBytes(tmp, []);
        Check(ProcessCatalog.DisplayName(tmp) == "x", "DisplayName: без описания");
        File.Delete(tmp);
    }

    static List<AppEntry> A(params string[] paths) => paths.Select(x => new AppEntry { Path = x }).ToList();

    static void SettingsTests()
    {
        var s = new Settings
        {
            Ip = "192.168.1.1", Port = 1818,
            Apps = A(@"C:\Program Files\Chrome\chrome.exe", @"D:\x\CHROME.EXE", @"C:\Игра\Game.exe", @"C:\bin\sing-box.exe")
        };
        var names = s.ProcessNames();
        Check(names.Count == 2 && names[0] == "chrome.exe" && names[1] == "Game.exe",
            "ProcessNames: имя из пути, дубли без учёта регистра убраны, sing-box.exe исключён");
        Check(s.Validate() == null, "Validate: корректные настройки");
        Check(new Settings { Ip = "abc", Apps = s.Apps }.Validate() != null, "Validate: плохой IP");
        Check(new Settings { Port = 0, Apps = s.Apps }.Validate() != null, "Validate: порт 0");
        Check(new Settings { Port = 70000, Apps = s.Apps }.Validate() != null, "Validate: порт 70000");
        Check(new Settings().Validate() != null, "Validate: пустой список приложений");

        var p = Path.GetTempFileName();
        File.WriteAllText(p, "{ not json");
        Check(Settings.Load(p).Port == 1818, "Load: битый файл даёт значения по умолчанию");
        s.Save(p);
        var back = Settings.Load(p);
        Check(back.Ip == s.Ip && back.Port == 1818 && back.Apps.Count == 4, "Save/Load: туда и обратно");
        File.Delete(p);
        Check(Settings.Load(p).Ip == "192.168.1.1", "Load: нет файла, значения по умолчанию");

        File.WriteAllText(p, """{"Ip":"10.0.0.1","Port":1080,"Apps":["C:\\a\\Discord.exe","D:\\b\\chrome.exe"]}""");
        var v1 = Settings.Load(p);
        Check(v1.Ip == "10.0.0.1" && v1.Port == 1080 && v1.Apps.Count == 2 && v1.Apps[0].Path == @"C:\a\Discord.exe" && v1.Apps.All(a => a.Enabled) && v1.Mode == ProxyMode.Socks5,
            "Load: формат v1");

        var v2 = new Settings { Apps = A(@"C:\Игра\a.exe", @"C:\b.exe") };
        v2.Apps[1].Enabled = false;
        v2.Save(p);
        var v2b = Settings.Load(p);
        Check(v2b.Apps.Count == 2 && v2b.Apps[0].Path == @"C:\Игра\a.exe" && v2b.Apps[0].Enabled && !v2b.Apps[1].Enabled && v2b.Mode == ProxyMode.Socks5,
            "Save/Load v2: туда и обратно");

        File.WriteAllText(p, """{"Ip":"10.0.0.2","Port":99,"Apps":[42,"C:\\x.exe"]}""");
        var odd = Settings.Load(p);
        Check(odd.Ip == "10.0.0.2" && odd.Port == 99 && odd.Apps.Count == 1, "Load: элемент Apps неизвестного вида пропущен");
        File.WriteAllText(p, """{"Apps":null}""");
        Check(Settings.Load(p).Apps.Count == 0, "Load: Apps = null -> пустой список");
        File.Delete(p);

        var mix = new Settings { Apps = A(@"C:\chrome.exe", @"C:\Game.exe") };
        mix.Apps[1].Enabled = false;
        var mn = mix.ProcessNames();
        Check(mn.Count == 1 && mn[0] == "chrome.exe", "ProcessNames: только включённые");
        mix.Apps[0].Enabled = false;
        Check(mix.Validate() == "Добавьте хотя бы одно приложение", "Validate: все выключены");
        Check(Settings.ValidateProxy("192.168.1.1", 1818) == null && Settings.ValidateProxy("abc", 1818) == "Некорректный IP"
            && Settings.ValidateProxy("10.0.0.1", 0) == "Порт должен быть от 1 до 65535", "ValidateProxy: только IP и порт");

        File.WriteAllText(p, """{"Ip":"10.0.0.1","Port":1080,"Apps":[{"Path":"C:\\a\\chrome.exe","Enabled":true}]}""");
        var old = Settings.Load(p);
        Check(old.DirectPort == null && old.DirectApps.Count == 0 && old.Apps.Count == 1, "Load: v2 без новых полей -> DirectPort null, DirectApps пуст");

        var v3 = new Settings { Port = 1818, DirectPort = 1819, Apps = A(@"C:\a\chrome.exe"), DirectApps = A(@"C:\q\qbittorrent.exe") };
        v3.DirectApps[0].Enabled = false;
        v3.Save(p);
        var v3b = Settings.Load(p);
        Check(v3b.DirectPort == 1819 && v3b.DirectApps.Count == 1 && v3b.DirectApps[0].Path == @"C:\q\qbittorrent.exe" && !v3b.DirectApps[0].Enabled,
            "Save/Load v3: DirectPort и DirectApps туда и обратно");
        File.WriteAllText(p, """{"DirectPort":"abc","DirectApps":[42,"C:\\t.exe"]}""");
        var odd3 = Settings.Load(p);
        Check(odd3.DirectPort == null && odd3.DirectApps.Count == 1, "Load: DirectPort не число -> null, элемент DirectApps неизвестного вида пропущен");
        File.Delete(p);

        var both = new Settings { Apps = A(@"C:\a\chrome.exe"), DirectApps = A(@"D:\CHROME.EXE", @"C:\q\qbittorrent.exe", @"C:\q\QBITTORRENT.exe", @"C:\x\sing-box.exe") };
        Check(both.BypassNames().SequenceEqual(new[] { "qbittorrent.exe" }),
            "BypassNames: без дублей и sing-box.exe; имя из «Через прокси» сюда не попадает");

        Check(new Settings { Port = 1818, DirectPort = 1818, Apps = A(@"C:\a.exe") }.Validate() == "Порт «Мимо Podkop» совпадает с портом прокси", "Validate: DirectPort == Port");
        Check(new Settings { DirectPort = 0, Apps = A(@"C:\a.exe") }.Validate() == "Порт «Мимо Podkop» должен быть от 1 до 65535", "Validate: DirectPort 0");
        Check(new Settings { DirectPort = 70000, Apps = A(@"C:\a.exe") }.Validate() == "Порт «Мимо Podkop» должен быть от 1 до 65535", "Validate: DirectPort 70000");
        Check(new Settings { DirectApps = A(@"C:\q\qbittorrent.exe") }.Validate() == null, "Validate: программы только в «Мимо Podkop» -> корректно");

        Check(Settings.ParseOptionalPort("") == null && Settings.ParseOptionalPort("  ") == null && Settings.ParseOptionalPort(null) == null
            && Settings.ParseOptionalPort(" 1819 ") == 1819 && Settings.ParseOptionalPort("abc") == 0,
            "ParseOptionalPort: пусто -> null, число -> число, мусор -> 0 (даст ошибку диапазона)");

        var orig = new Settings { DirectPort = 5, Apps = A(@"C:\a.exe"), DirectApps = A(@"C:\b.exe") };
        var cl = orig.Clone();
        cl.DirectApps[0].Enabled = false;
        cl.Apps.Clear();
        cl.DirectPort = 6;
        Check(orig.DirectApps[0].Enabled && orig.Apps.Count == 1 && orig.DirectPort == 5, "Clone: глубокая копия");
    }

    static void AutostartTests()
    {
        // Попытки подключения: attempt -> удалось ли; retrying(n) — перед попыткой n
        (bool Result, int Calls, List<int> Retries) Run(Func<int, bool> ok, int attempts, TimeSpan pause, CancellationToken ct = default)
        {
            int calls = 0;
            var retries = new List<int>();
            bool r = Autostart.Connect(() => Task.FromResult(ok(++calls)), attempts, pause, retries.Add, ct).Result;
            return (r, calls, retries);
        }
        var third = Run(n => n == 3, 3, TimeSpan.Zero);
        Check(third.Result && third.Calls == 3 && third.Retries.SequenceEqual([2, 3]), "Автозапуск: третья попытка удалась -> без окна, лог перед 2-й и 3-й");
        var never = Run(_ => false, 3, TimeSpan.Zero);
        Check(!never.Result && never.Calls == 3, "Автозапуск: три неудачи -> окно, четвёртой попытки нет");
        var once = Run(_ => false, 1, TimeSpan.Zero);
        Check(!once.Result && once.Calls == 1 && once.Retries.Count == 0, "Автозапуск: одна попытка (настройки неверны) -> сразу окно");
        using (var cts = new CancellationTokenSource(100))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var cancelled = Run(_ => false, 3, TimeSpan.FromSeconds(30), cts.Token);
            Check(cancelled.Result && cancelled.Calls == 1 && sw.Elapsed < TimeSpan.FromSeconds(5),
                "Автозапуск: пользователь сам нажал кнопку в паузе -> попытки прекращаются, окно не нужно");
        }

        var exe = @"C:\Игры & VPN\ViviTune\ViviTune.exe";
        var x = System.Xml.Linq.XDocument.Parse(Autostart.TaskXml(exe, @"PC\Петя"));
        string? El(string name) => x.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
        Check(El("Command") == exe && El("Arguments") == "--autostart", "Задача автозапуска: путь с & и кириллицей, флаг --autostart");
        Check(x.Descendants().Any(e => e.Name.LocalName == "LogonTrigger") && El("UserId") == @"PC\Петя" && El("RunLevel") == "HighestAvailable",
            "Задача автозапуска: при входе этого пользователя, с наивысшими правами");
        Check(El("DisallowStartIfOnBatteries") == "false" && El("StopIfGoingOnBatteries") == "false" && El("ExecutionTimeLimit") == "PT0S",
            "Задача автозапуска: работает от батареи и без ограничения 72 ч");
        Check(El("Priority") == "4", "Задача автозапуска: обычный приоритет (иначе sing-box наследует пониженный)");

        var v = new ViewState();
        int ev = 0; bool? got = null;
        v.SetAutostartRequested += on => { ev++; got = on; };
        v.RequestSetAutostart(false);
        Check(ev == 0, "ViewState: автозапуск без изменений — события нет");
        v.RequestSetAutostart(true);
        Check(ev == 1 && got == true, "ViewState: автозапуск включают — событие");
        var names = new List<string?>();
        v.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        v.Autostart = false;
        Check(names.Contains(nameof(ViewState.Autostart)), "ViewState: Autostart уведомляет и без изменения (переключатель вернётся, если не вышло)");
    }

    static void ViewStateVlessTests()
    {
        var v = new ViewState();
        v.SyncApps(A(@"C:\chrome.exe"), new List<AppEntry>());
        Check(v.ProxyTabText == "Через прокси · 1" && v.DirectTabText == "Мимо Podkop · 0" && v.UpLabel == "↑ Отдача через прокси" && v.DownLabel == "↓ Загрузка через прокси",
            "ViewState: SOCKS5 — вкладки и подписи как раньше");
        var changed = new List<string>();
        v.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        v.Mode = ProxyMode.Vless;
        Check(v.IsVless && v.ProxyTabText == "Приложения · 1" && v.DirectTabText == "Сайты · 0" && v.UpLabel == "↑ Отдача через VPN" && v.DownLabel == "↓ Загрузка через VPN"
            && v.ProxyLabel == "VLESS" && v.EmptyText == "Добавьте приложение, трафик которого пойдёт через VPN",
            "ViewState: Vless — вкладки, подписи, ProxyLabel, EmptyText");
        foreach (var n in new[] { "IsVless", "ProxyTabText", "DirectTabText", "ProxyLabel", "EmptyText", "UpLabel", "DownLabel", "StatusText", "SitesTab", "DirectHintVisible", "ExitDownHint" })
            Check(changed.Contains(n), "ViewState: смена режима уведомляет " + n);
        v.State = ConnState.Checking;
        Check(v.StatusText == "Проверяю сервер…", "ViewState: Vless Checking -> «Проверяю сервер…»");
        v.State = ConnState.Disconnected;
        v.DirectTab = true;
        v.ProxyDown = true; v.BypassDown = true;
        Check(v.SitesTab && !v.DirectHintVisible && v.ExitDownHint == null, "ViewState: Vless, вкладка «Сайты» — без подсказок");
        changed.Clear();
        v.DirectTab = false;
        Check(changed.Contains("SitesTab") && !v.SitesTab, "ViewState: DirectTab уведомляет SitesTab");
        v.Mode = ProxyMode.Socks5;
        Check(v.ProxyLabel == $"SOCKS5 · {v.Ip}:{v.Port}" && v.StatusText == "Отключено", "ViewState: возврат в SOCKS5");

        v.Mode = ProxyMode.Vless;
        v.SetVless("vless://a");
        Check(v.VlessSummary == "Проверяю ссылку…" && !v.VlessLinkError && v.VlessHost == "", "ViewState: до разбора — «Проверяю ссылку…»");
        var srvA = new Server("A", JsonNode.Parse("{\"type\":\"vless\",\"server\":\"a.example.com\",\"server_port\":443,\"uuid\":\"u\"}")!.AsObject(), []);
        v.SetVlessDecoded("vless://a", srvA, null);
        Check(v.VlessSummary == "Сервер: a.example.com:443 · без TLS · TCP" && v.VlessHost == "a.example.com" && v.ProxyLabel == "VLESS · a.example.com", "ViewState: сводка после разбора");
        v.SetVless("vless://b");
        v.SetVlessDecoded("vless://a", srvA, null);
        Check(v.VlessSummary == "Проверяю ссылку…" && v.VlessHost == "", "ViewState: устаревший разбор не показывается");
        v.SetVlessDecoded("vless://b", null, "Ссылка не подходит: invalid server address");
        Check(v.VlessLinkError && v.VlessSummary == "Ссылка не подходит: invalid server address", "ViewState: ошибка разбора под полем");
        v.SetVless("");
        Check(v.VlessSummary == "" && !v.VlessLinkError, "ViewState: пустая ссылка — без сводки и ошибки");

        Check(ViewState.DetailVless(new TimeSpan(0, 12, 3), 2, 3, 1) == "00:12:03 · 2 приложения · 3 списка · 1 сайт", "DetailVless: всё");
        Check(ViewState.DetailVless(new TimeSpan(0, 12, 3), 1, 0, 0) == "00:12:03 · 1 приложение", "DetailVless: без списков");
        Check(ViewState.DetailVless(new TimeSpan(0, 12, 3), 0, 5, 0) == "00:12:03 · 5 списков", "DetailVless: без приложений");

        var forms = new (int, string)[] { (1, "1 приложение"), (2, "2 приложения"), (5, "5 приложений"), (11, "11 приложений"), (12, "12 приложений"), (21, "21 приложение"), (22, "22 приложения"), (25, "25 приложений"), (111, "111 приложений"), (112, "112 приложений") };
        Check(forms.All(f => Format.Plural(f.Item1, "приложение", "приложения", "приложений") == f.Item2), "Format.Plural: русские формы");

        var ad = new ViewState();
        bool? toDirect = null;
        ad.AddAppRequested += (_, d) => toDirect = d;
        ad.DirectTab = true;
        ad.RequestAddApp(@"C:x.exe");
        bool socksDirect = toDirect == true;
        ad.Mode = ProxyMode.Vless;
        ad.RequestAddApp(@"C:x.exe");
        Check(socksDirect && toDirect == false, "ViewState.RequestAddApp: в VLESS приложение всегда в Apps");

        var l = new ViewState();
        Check(l.ListGroups.Count == Catalogs.Groups.Count && l.ListGroups.Sum(g => g.Rows.Count) == Catalogs.Lists.Count, "ViewState: ListGroups из каталога");
        var rows = l.ListGroups.SelectMany(g => g.Rows).ToDictionary(r => r.Id);
        var rowChanged = new List<string>();
        rows["youtube"].PropertyChanged += (_, e) => rowChanged.Add(e.PropertyName ?? "");
        l.SyncLists(new[] { "russia_inside" });
        var yt = rows["youtube"];
        Check(yt.Covered && yt.Shown && yt.Note == "уже входит в Russia inside" && !rows["discord"].Covered && rows["russia_inside"].Enabled && !rows["discord"].Shown,
            "ViewState.SyncLists: Covered строка");
        Check(rowChanged.Contains("Covered") && rowChanged.Contains("Shown") && rowChanged.Contains("Note"), "ViewState.SyncLists: строка уведомляет");
        l.Mode = ProxyMode.Vless;
        Check(l.DirectTabText == "Сайты · 1", "ViewState.SyncLists: счётчик на вкладке");
        var sv = new ViewState { Mode = ProxyMode.Vless };
        var added = new List<string>();
        sv.AddSiteRequested += a => added.Add(a);
        bool okAdd = sv.RequestAddSite("https://Example.com/x");
        Check(okAdd && added.SequenceEqual(new[] { "example.com" }) && sv.SiteError == null, "ViewState.RequestAddSite: нормализует и шлёт событие");
        bool badAdd = sv.RequestAddSite("localhost");
        Check(!badAdd && added.Count == 1 && sv.SiteError == "Введите адрес целиком, например example.com", "ViewState.RequestAddSite: ошибка разбора");
        sv.RequestAddSite("example.org");
        Check(sv.SiteError == null && added.Count == 2, "ViewState.RequestAddSite: удачный сбрасывает ошибку");
        bool blank = sv.RequestAddSite("   ");
        Check(!blank && sv.SiteError == null && added.Count == 2, "ViewState.RequestAddSite: пустой ввод");
        string? removed = null;
        sv.RemoveSiteRequested += x => removed = x;
        sv.RequestRemoveSite("example.com");
        Check(removed == "example.com", "ViewState.RequestRemoveSite: событие");
        sv.SyncSites(new[] { "xn--80aswg.xn--p1ai" });
        Check(sv.Sites.Count == 1 && sv.Sites[0].Display == "сайт.рф" && sv.Sites[0].Value == "xn--80aswg.xn--p1ai", "ViewState.SyncSites: Display в Unicode");
        sv.SyncLists(new[] { "youtube" });
        Check(sv.DirectTabText == "Сайты · 2", "ViewState: счётчик вкладки = списки + сайты");
        int lev = 0;
        l.SetListEnabledRequested += (_, _) => lev++;
        l.RequestSetListEnabled("youtube", false);
        Check(lev == 0, "ViewState: Covered строка — события нет");
        l.RequestSetListEnabled("discord", true);
        Check(lev == 1, "ViewState: обычная строка — событие");

        var r = new ViewState();
        r.SetVless("vless://11111111-1111-1111-1111-111111111111@example.com:443?security=tls&type=tcp#n");
        int ve = 0; string? gl = null;
        r.SetVlessRequested += a => { ve++; gl = a; };
        r.RequestSetVless(r.VlessLink);
        Check(ve == 0, "ViewState: RequestSetVless без изменений — события нет");
        r.RequestSetVless("vless://x");
        Check(ve == 1 && gl == "vless://x", "ViewState: RequestSetVless с изменением");
        int me = 0;
        r.SetModeRequested += _ => me++;
        r.RequestSetMode(ProxyMode.Vless);
        Check(me == 1, "ViewState: RequestSetMode");
    }

    static void ViewStateTests()
    {
        var v = new ViewState();
        for (int i = 0; i < 70; i++) v.PushSpeed(new Speed(i, i * 2));
        Check(v.UpHistory.Count == 60 && v.DownHistory.Count == 60 && v.UpHistory[0] == 10 && v.UpHistory[59] == 69
            && v.UpBps == 69 && v.DownBps == 138, "ViewState: лимиты (60 значений скорости)");
        for (int i = 0; i < 600; i++) v.AddLog("line " + i);
        Check(v.LogLines.Count == 500 && v.LogLines[0] == "line 100" && v.LogLines[499] == "line 599",
            "ViewState: лимиты (500 последних строк лога)");
        v.Ip = "10.0.0.1";
        v.Port = 1080;
        v.State = ConnState.Connected;
        Check(v.ProxyLabel == "SOCKS5 · 10.0.0.1:1080" && !v.SettingsEditable && v.StatusText == "Подключено",
            "ViewState: ProxyLabel, SettingsEditable, StatusText");
        int proxyEvents = 0;
        v.SetProxyRequested += (_, _, _) => proxyEvents++;
        v.RequestSetProxy("10.0.0.1", 1080, null); // поля страницы заполнены теми же значениями (старт)
        v.RequestSetProxy("10.0.0.2", 1080, null);
        Check(proxyEvents == 1, "ViewState: RequestSetProxy без изменений не поднимает событие (settings.json не пишется при старте)");

        var hs = new ViewState();
        const string proxyHint = "Прокси не отвечает: программы из этой вкладки сейчас идут обычным путём.";
        const string bypassHint = "Порт «Мимо Podkop» не отвечает: программы из этой вкладки сейчас идут обычным путём.";
        hs.ProxyDown = true;
        Check(hs.ExitDownHint == proxyHint, "ViewState.ExitDownHint: вкладка прокси, прокси не отвечает");
        hs.ProxyDown = false;
        Check(hs.ExitDownHint == null, "ViewState.ExitDownHint: вкладка прокси, прокси жив");
        hs.DirectTab = true;
        hs.BypassDown = true;
        Check(hs.ExitDownHint == bypassHint, "ViewState.ExitDownHint: вкладка «Мимо Podkop», порт не отвечает");
        hs.ProxyDown = true; // чужая вкладка не мешает
        Check(hs.ExitDownHint == bypassHint, "ViewState.ExitDownHint: на вкладке «Мимо Podkop» прокси не важен");
        hs.BypassDown = false;
        Check(hs.ExitDownHint == null, "ViewState.ExitDownHint: вкладка «Мимо Podkop», порт жив");

        Check(ViewState.Detail(new TimeSpan(0, 42, 17), true, 2, 2) == "00:42:17 · TCP + UDP · 2 через прокси · 2 мимо Podkop", "ViewState.Detail: оба списка");
        Check(ViewState.Detail(TimeSpan.FromSeconds(5), false, 1, 0) == "00:00:05 · только TCP · 1 через прокси", "ViewState.Detail: без UDP, нулевая часть не пишется");
        Check(ViewState.Detail(TimeSpan.FromSeconds(5), null, 0, 3) == "00:00:05 · 3 мимо Podkop", "ViewState.Detail: прокси не жив -> без протоколов");
        Check(ViewState.Detail(TimeSpan.FromSeconds(-3), true, 1, 0) == "00:00:00 · TCP + UDP · 1 через прокси", "ViewState.Detail: отрицательное время -> 00:00:00");

        var w = new ViewState();
        w.SyncApps(A(@"C:\a\chrome.exe", @"C:\b\Game.exe"), new List<AppEntry> { new() { Path = @"C:\q\qbittorrent.exe", Enabled = false } });
        Check(w.ProxyTabText == "Через прокси · 2" && w.DirectTabText == "Мимо Podkop · 0" && w.DirectApps.Count == 1 && w.ShownApps == w.Apps,
            "ViewState: две вкладки, числа — включённые");
        string? addedTo = null;
        w.AddAppRequested += (p, d) => addedTo = d ? "direct" : "proxy";
        w.DirectTab = true;
        w.RequestAddApp(@"C:\x.exe");
        Check(w.ShownApps == w.DirectApps && addedTo == "direct" && w.DirectHintVisible
            && w.EmptyText == "Добавьте приложение, трафик которого пойдёт мимо Podkop",
            "ViewState: вкладка «Мимо Podkop» — добавление туда, подсказка про порт");
        w.DirectPort = 1819;
        Check(!w.DirectHintVisible, "ViewState: порт задан -> подсказки нет");
        int ev = 0;
        w.SetProxyRequested += (_, _, _) => ev++;
        w.Ip = "10.0.0.1"; w.Port = 1080;
        w.RequestSetProxy("10.0.0.1", 1080, 1819);
        w.RequestSetProxy("10.0.0.1", 1080, null);
        Check(ev == 1, "ViewState: RequestSetProxy учитывает порт «Мимо Podkop»");

        var b = new ViewState();
        bool idle = !b.IsBusy && !b.IsConnected;
        b.State = ConnState.Checking;
        bool checking = b.IsBusy && !b.IsConnected;
        b.State = ConnState.Restarting;
        bool restarting = b.IsBusy && !b.IsConnected;
        b.State = ConnState.Connected;
        Check(idle && checking && restarting && b.IsConnected && !b.IsBusy, "ViewState: IsBusy (проверка/перезапуск), IsConnected");
        var changed = new List<string>();
        b.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        b.PushSpeed(new Speed(1536, 8808038));
        Check(b.UpText == "1.5 КБ/с" && b.DownText == "8.4 МБ/с" && changed.Contains(nameof(ViewState.UpText)) && changed.Contains(nameof(ViewState.DownText)),
            "ViewState: UpText/DownText через Format.Speed с уведомлением");
        b.State = ConnState.Restarting;
        bool kept = b.UpHistory.Count == 1;
        changed.Clear();
        b.State = ConnState.Disconnected;
        Check(kept && b.UpHistory.Count == 0 && b.DownHistory.Count == 0 && changed.Contains(nameof(ViewState.IsBusy)) && changed.Contains(nameof(ViewState.IsConnected)),
            "ViewState: при отключении история скорости очищается (без подключения — нули)");

        var t = new ViewState();
        t.State = ConnState.Checking;
        t.State = ConnState.Connected;
        t.PushSpeed(new Speed(0, 0, 1024, 2048));
        t.State = ConnState.Restarting; // перезапуск из-за смены приложений: счёт продолжается
        t.State = ConnState.Connected;
        t.PushSpeed(new Speed(0, 0, 512, 1024 * 1024));
        t.State = ConnState.Disconnected;
        bool totals = t.UpTotalText == "всего 1.5 КБ" && t.DownTotalText == "всего 1.0 МБ"; // после отключения итог виден
        t.State = ConnState.Checking; // новое подключение — с нуля
        Check(totals && t.UpTotalText == "всего 0 Б" && t.DownTotalText == "всего 0 Б",
            "ViewState: итог трафика копится через перезапуски, виден после отключения, сбрасывается при подключении");

        SparklineTests();
    }

    static double[] Levels(params double[] values)
    {
        var l = new double[6];
        Sparkline.Levels(values, l);
        return l;
    }

    static void SparklineTests()
    {
        Check(Levels().All(x => x == 0), "Sparkline.Levels: пусто -> ровная линия у низа");
        Check(Levels(0, 0, 0).All(x => x == 0), "Sparkline.Levels: одни нули -> ровная линия у низа");
        var one = Levels(5);
        Check(one[5] == 1 && one.Take(5).All(x => x == 0), "Sparkline.Levels: одно значение справа, слева нули");
        var burst = Levels(1, 1, 100, 1);
        Check(burst[4] == 1 && Math.Abs(burst[5] - 0.01) < 1e-9 && burst[0] == 0 && burst[1] == 0, "Sparkline.Levels: всплеск -> масштаб по максимуму");
        var many = Levels(9, 9, 9, 1, 2, 3, 4, 5, 6);
        Check(many[0] == 1.0 / 6 && many[5] == 1, "Sparkline.Levels: больше значений, чем точек -> последние");
        Check(Levels(-5, double.NaN, double.PositiveInfinity, 2).SequenceEqual(new double[] { 0, 0, 0, 0, 0, 1 }),
            "Sparkline.Levels: отрицательные, NaN и бесконечность -> 0");
    }

    const string RD = @"C:\vt\rules"; // в тестах конфига файлы правил не нужны, нужны только пути

    static JsonObject Cfg(Settings s) => JsonNode.Parse(ConfigBuilder.Build(s, 9090, "t", RD))!.AsObject();

    // Папка rules рядом с конфигом сессии
    static string RulesOf(string cfg) => Path.Combine(Path.GetDirectoryName(cfg)!, "rules");

    // Имена программ из apps.json / bypass.json
    static List<string> RuleNames(string rulesDir, string tag)
    {
        var rules = Arr(JsonNode.Parse(File.ReadAllText(RuleFiles.PathOf(rulesDir, tag)))!["rules"]);
        return rules.Count == 0 ? new List<string>() : Arr(Obj(rules[0])["process_name"]).Select(x => Str(x)!).ToList();
    }

    static JsonObject Obj(JsonNode? o) => o!.AsObject();

    static JsonArray Arr(JsonNode? o) => o!.AsArray();

    static string? Str(JsonNode? o) => (string?)o;

    static int IndexOfRule(JsonArray rules, string key)
    {
        for (int i = 0; i < rules.Count; i++)
            if (Obj(rules[i]).ContainsKey(key)) return i;
        return -1;
    }

    // Правило к выходу outbound по rule_set (apps -> proxy, bypass -> bypass)
    static JsonObject? AppRule(JsonArray rules, string outbound) =>
        rules.Select(Obj).FirstOrDefault(r => r.ContainsKey("rule_set") && Str(r["outbound"]) == outbound);

    static void ConfigTests()
    {
        var s = new Settings { Ip = "192.168.1.1", Port = 1818, Apps = A(@"C:\a b\chrome.exe", @"C:\Игра\Игра.exe") };
        var c = Cfg(s);
        var route = Obj(c["route"]);
        var rules = Arr(route["rules"]);

        Check(Str(route["final"]) == "direct", "route.final = direct");
        Check(route["find_process"]!.GetValue<bool>(), "SOCKS5: route.find_process = true");

        var pr = AppRule(rules, "proxy")!;
        int pi = rules.IndexOf(pr);
        Check(Arr(pr["rule_set"]).Select(x => Str(x)).SequenceEqual(new[] { "apps" }) && !c.ToJsonString().Contains("process_name"),
            "правило rule_set apps -> proxy, process_name в конфиге нигде нет");
        var rsd = Arr(route["rule_set"]);
        Check(rsd.Count == 1 && Str(Obj(rsd[0])["tag"]) == "apps" && Str(Obj(rsd[0])["type"]) == "local" && Str(Obj(rsd[0])["format"]) == "source"
            && Str(Obj(rsd[0])["path"]) == RuleFiles.PathOf(RD, "apps"), "rule_set apps: local, source, путь из PathOf");

        var rdir = Path.Combine(Path.GetTempPath(), "vivitune-rules-" + Guid.NewGuid().ToString("N"));
        try
        {
            RuleFiles.Write(rdir, s, new Dictionary<string, string>());
            Check(RuleNames(rdir, "apps").SequenceEqual(new[] { "chrome.exe", "Игра.exe" }), "apps.json: кириллица и пробелы в пути");
        }
        finally { try { Directory.Delete(rdir, true); } catch { } }

        int xi = IndexOfRule(rules, "ip_cidr");
        var xr = Obj(rules[xi]);
        Check(Str(Arr(xr["ip_cidr"])[0]) == "192.168.1.1/32" && Str(xr["outbound"]) == "direct" && xi < pi,
            "адрес прокси идёт напрямую и раньше правила приложений");

        Check(IndexOfRule(rules, "protocol") >= 0, "есть hijack-dns");

        var dr = Obj(Arr(Obj(c["dns"])["rules"])[0]);
        Check(Str(dr["server"]) == "proxy-dns" && Arr(dr["rule_set"]).Select(x => Str(x)).SequenceEqual(new[] { "apps" }), "DNS выбранных приложений идёт через proxy-dns");

        bool socksOk = false;
        foreach (var o in Arr(c["outbounds"]))
        {
            var ob = Obj(o);
            if (Str(ob["type"]) == "socks") socksOk = Str(ob["server"]) == "192.168.1.1" && (int)ob["server_port"]! == 1818 && Str(ob["tag"]) == "proxy";
        }
        Check(socksOk, "socks outbound с адресом из настроек");

        var v6 = Cfg(new Settings { Ip = "::1", Port = 1, Apps = s.Apps });
        var v6rules = Arr(Obj(v6["route"])["rules"]);
        Check(Str(Arr(Obj(v6rules[IndexOfRule(v6rules, "ip_cidr")])["ip_cidr"])[0]) == "::1/128", "IPv6-прокси исключается как /128");

        var ca = Obj(Obj(JsonNode.Parse(ConfigBuilder.Build(s, 51234, "abc", RD))!["experimental"])["clash_api"]);
        Check(Str(ca["external_controller"]) == "127.0.0.1:51234" && Str(ca["secret"]) == "abc", "clash_api с переданными портом и секретом");

        bool threw = false;
        try { ConfigBuilder.Build(new Settings(), 9090, "t", RD); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "Build: пустой список приложений -> исключение (иначе правило без условий матчит всё)");
        var two = new Settings { Ip = "192.168.1.1", Port = 1818, DirectPort = 1819,
            Apps = A(@"C:\chrome.exe"), DirectApps = A(@"C:\q\qbittorrent.exe", @"D:\CHROME.EXE") };
        JsonObject B(Exits? live) => JsonNode.Parse(ConfigBuilder.Build(two, 9090, "t", RD, live))!.AsObject();
        JsonArray R(JsonObject c2) => Arr(Obj(c2["route"])["rules"]);
        bool HasOut(JsonObject c2, string tag) => Arr(c2["outbounds"]).Any(o => Str(Obj(o)["tag"]) == tag);

        var all = B(null);
        var bo = Arr(all["outbounds"]).Select(Obj).First(o => Str(o["tag"]) == "bypass");
        Check(Str(bo["type"]) == "socks" && Str(bo["server"]) == "192.168.1.1" && (int)bo["server_port"]! == 1819 && Str(bo["version"]) == "5",
            "bypass: socks outbound на IP роутера и порт «Мимо Podkop»");
        Check(Arr(AppRule(R(all), "bypass")!["rule_set"]).Select(x => Str(x)).SequenceEqual(new[] { "bypass" })
            && Arr(AppRule(R(all), "proxy")!["rule_set"]).Select(x => Str(x)).SequenceEqual(new[] { "apps" })
            && Arr(Obj(all["route"])["rule_set"]).Select(x => Str(Obj(x)["tag"])).SequenceEqual(new[] { "apps", "bypass" }),
            "bypass: правила по rule_set apps и bypass");
        var bdir = Path.Combine(Path.GetTempPath(), "vivitune-rules-" + Guid.NewGuid().ToString("N"));
        try
        {
            RuleFiles.Write(bdir, two, new Dictionary<string, string>());
            Check(RuleNames(bdir, "bypass").SequenceEqual(new[] { "qbittorrent.exe" }) && RuleNames(bdir, "apps").SequenceEqual(new[] { "chrome.exe" }),
                "bypass: имя из обоих списков только в apps");
        }
        finally { try { Directory.Delete(bdir, true); } catch { } }
        Check(IndexOfRule(R(all), "ip_cidr") < R(all).IndexOf(AppRule(R(all), "bypass")), "bypass: адрес роутера напрямую раньше правил приложений");

        var px = B(new Exits(true, false));
        Check(!HasOut(px, "bypass") && AppRule(R(px), "bypass") == null && AppRule(R(px), "proxy") != null,
            "жив только прокси: нет outbound bypass и его правила");

        var bp = B(new Exits(false, true));
        Check(AppRule(R(bp), "proxy") == null && AppRule(R(bp), "bypass") != null && Arr(Obj(bp["dns"])["rules"]).Count == 0,
            "жив только bypass: нет правила apps -> proxy и DNS-правила proxy-dns");
        Check(HasOut(bp, "proxy"), "жив только bypass: outbound proxy остаётся (на него ссылается detour proxy-dns)");

        var onlyDirect = new Settings { Port = 1818, DirectPort = 1819, DirectApps = A(@"C:\q\qbittorrent.exe") };
        var od = JsonNode.Parse(ConfigBuilder.Build(onlyDirect, 9090, "t", RD))!.AsObject();
        Check(AppRule(R(od), "proxy") == null && Arr(Obj(od["dns"])["rules"]).Count == 0 && AppRule(R(od), "bypass") != null,
            "нет программ «Через прокси»: нет правила к proxy");

        var noPort = new Settings { Port = 1818, Apps = A(@"C:\chrome.exe"), DirectApps = A(@"C:\q\qbittorrent.exe") };
        Check(!HasOut(JsonNode.Parse(ConfigBuilder.Build(noPort, 9090, "t", RD))!.AsObject(), "bypass"), "DirectPort не задан: bypass нет");

        bool none = false;
        try { ConfigBuilder.Build(two, 9090, "t", RD, new Exits(false, false)); } catch (InvalidOperationException) { none = true; }
        Check(none, "Build: ни одного живого выхода с программами -> исключение");

        // снимки, отличающиеся только программами, дают тот же конфиг: за программы отвечают файлы правил
        var two2 = new Settings { Ip = "192.168.1.1", Port = 1818, DirectPort = 1819,
            Apps = A(@"C:\chrome.exe", @"C:\x\Discord.exe"), DirectApps = A(@"C:\q\other.exe") };
        Check(ConfigBuilder.Build(two, 9090, "t", RD) == ConfigBuilder.Build(two2, 9090, "t", RD), "Build: программы в непустых списках не влияют на конфиг");
    }

    // Фейковый SOCKS5-сервер: на каждое соединение отвечает заданными байтами.
    static int FakeServer(byte[] methodReply, byte[]? udpReply, IPAddress? bind = null)
    {
        var l = new TcpListener(bind ?? IPAddress.Loopback, 0);
        l.Start();
        Task.Run(() =>
        {
            using (var c = l.AcceptTcpClient())
            {
                var st = c.GetStream();
                var buf = new byte[16];
                st.ReadExactly(buf, 0, 3);
                st.Write(methodReply, 0, methodReply.Length);
                if (udpReply == null) return;
                st.ReadExactly(buf, 0, 10);
                st.Write(udpReply, 0, udpReply.Length);
            }
            l.Stop();
        });
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    static void ProbeTests()
    {
        var ok = new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        var r = ProxyProbe.Probe("127.0.0.1", FakeServer(new byte[] { 5, 0 }, ok));
        Check(r.Reachable && r.UdpSupported, "Probe: SOCKS5 с UDP");

        var noUdp = new byte[] { 5, 7, 0, 1, 0, 0, 0, 0, 0, 0 };
        r = ProxyProbe.Probe("127.0.0.1", FakeServer(new byte[] { 5, 0 }, noUdp));
        Check(r.Reachable && !r.UdpSupported, "Probe: SOCKS5 без UDP");

        r = ProxyProbe.Probe("127.0.0.1", FakeServer(new byte[] { 0x48, 0x54 }, null));
        Check(!r.Reachable && r.Error != null, "Probe: не SOCKS5 -> Reachable=false с текстом ошибки");

        if (Socket.OSSupportsIPv6)
        {
            r = ProxyProbe.Probe("::1", FakeServer(new byte[] { 5, 0 }, ok, IPAddress.IPv6Loopback));
            Check(r.Reachable && r.UdpSupported, "Probe: IPv6-прокси (::1)");
        }
        else Console.WriteLine("skip: IPv6 loopback недоступен");

        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int closed = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        r = ProxyProbe.Probe("127.0.0.1", closed);
        Check(!r.Reachable && r.Error != null, "Probe: закрытый порт -> Reachable=false с текстом ошибки");

        var l2 = new TcpListener(IPAddress.Loopback, 0);
        l2.Start();
        Check(ProxyProbe.Reach(IPAddress.Loopback, ((IPEndPoint)l2.LocalEndpoint).Port) == null, "Reach: слушающий порт -> null");
        l2.Stop();
        Check(ProxyProbe.Reach(IPAddress.Loopback, closed) != null, "Reach: закрытый порт -> причина");
    }

    static void VlessProbeTests()
    {
        const string good = "{\"Status\":0,\"Answer\":[{\"data\":\"93.184.215.14\"}]}";
        Func<string, CancellationToken, (int, string)?> Fake(Func<string, (int, string)?> f) => (path, ct) => f(path);
        (int, string)? Ok(string path) =>
            path.StartsWith("/version") ? (200, "{}") :
            path.StartsWith("/proxies/proxy/delay") ? (200, "{\"delay\":50}") :
            path.StartsWith("/dns/query") ? (200, good) : null;
        var ready = TimeSpan.FromSeconds(1);
        var pause = TimeSpan.FromMilliseconds(20);

        Check(VlessProbe.AfterStart(Fake(Ok), CancellationToken.None, ready) == null, "VlessProbe: всё хорошо -> null");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var e = VlessProbe.AfterStart(Fake(_ => null), CancellationToken.None, TimeSpan.FromMilliseconds(300));
        Check(e == "sing-box не запустился, подробности в логе" && sw.ElapsedMilliseconds < 1000, "VlessProbe: Clash API молчит -> не запустился, быстро");
        Check(VlessProbe.AfterStart(Fake(p => p.StartsWith("/proxies") ? (504, "") : Ok(p)), CancellationToken.None, ready, pause) == "Сервер не отвечает", "VlessProbe: delay 504");
        const string dohErr = "DNS через сервер не отвечает";
        Check(VlessProbe.AfterStart(Fake(p => p.StartsWith("/dns") ? (500, "{}") : Ok(p)), CancellationToken.None, ready, pause) == dohErr, "VlessProbe: dns 500");
        Check(VlessProbe.AfterStart(Fake(p => p.StartsWith("/dns") ? (200, "{\"Status\":2}") : Ok(p)), CancellationToken.None, ready, pause) == dohErr, "VlessProbe: dns без ответа");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        sw.Restart();
        Check(VlessProbe.AfterStart((p, c) => c.IsCancellationRequested ? null : Ok(p), cts.Token, ready) != null && sw.ElapsedMilliseconds < 500, "VlessProbe: отмена -> не null сразу");

        // повтор: первая попытка может попасть в сброс сети сразу после старта TUN
        int delays = 0, dnss = 0;
        Check(VlessProbe.AfterStart(Fake(p => p.StartsWith("/proxies") && ++delays == 1 ? (504, "") : Ok(p)), CancellationToken.None, ready, pause) == null && delays == 2,
            "VlessProbe: delay упал и ответил со 2-й попытки -> null");
        delays = 0;
        Check(VlessProbe.AfterStart(Fake(p => p.StartsWith("/proxies") ? (delays++ >= 0 ? (504, "") : null) : Ok(p)), CancellationToken.None, ready, pause) == "Сервер не отвечает" && delays == 2,
            "VlessProbe: delay упал дважды -> ошибка, ровно 2 попытки");
        Check(VlessProbe.AfterStart(Fake(p => p.StartsWith("/dns") && ++dnss == 1 ? (500, "{}") : Ok(p)), CancellationToken.None, ready, pause) == null && dnss == 2,
            "VlessProbe: dns упал и ответил со 2-й попытки -> null");
        dnss = 0;
        Check(VlessProbe.AfterStart(Fake(p => p.StartsWith("/dns") ? (dnss++ >= 0 ? (500, "{}") : null) : Ok(p)), CancellationToken.None, ready, pause) == dohErr && dnss == 2,
            "VlessProbe: dns упал дважды -> ошибка, ровно 2 попытки");
        using var cts2 = new CancellationTokenSource();
        sw.Restart();
        var r2 = VlessProbe.AfterStart((p, c) => { if (p.StartsWith("/proxies")) { cts2.CancelAfter(50); return (504, ""); } return Ok(p); }, cts2.Token, ready, TimeSpan.FromSeconds(5));
        Check(r2 != null && sw.ElapsedMilliseconds < 1000, "VlessProbe: отмена в паузе между попытками -> сразу");

        Check(VlessProbe.DnsAnswered(good), "DnsAnswered: Status 0 и Answer");
        Check(!VlessProbe.DnsAnswered("{\"Status\":0}") && !VlessProbe.DnsAnswered("{\"Status\":3,\"Answer\":[{}]}")
            && !VlessProbe.DnsAnswered("мусор") && !VlessProbe.DnsAnswered("{\"Status\":0,\"Answer\":[]}"), "DnsAnswered: без Answer, Status 3, мусор");
    }

    static IReadOnlyDictionary<string, ConnSample> Prev(params ConnSample[] c) => c.ToDictionary(x => x.Id);

    const string CRLF = "\r\n";

    static void TrafficTests()
    {
        var two = "{\"downloadTotal\":1,\"connections\":[{\"id\":\"a\",\"upload\":10,\"download\":20,\"chains\":[\"proxy\"]},{\"id\":\"b\",\"upload\":30,\"download\":40,\"chains\":[\"direct\"]}]}";
        var p = TrafficMath.Parse(two);
        Check(p.Count == 2 && p[0].ViaProxy && !p[1].ViaProxy && p[0].Upload == 10 && p[0].Download == 20 && p[1].Upload == 30 && p[1].Download == 40,
            "Parse: proxy и direct");
        bool junk = true;
        foreach (var j in new[] { "not json", "", "{}", "{\"connections\":null}", "[1]" })
            try { junk &= TrafficMath.Parse(j).Count == 0; } catch { junk = false; }
        Check(junk, "Parse: мусор");

        var s1 = TrafficMath.Delta(Prev(new ConnSample("a", 100, 1000, true)),
            new[] { new ConnSample("a", 300, 5000, true), new ConnSample("b", 50, 50, false) }, 1);
        Check(s1 == new Speed(200, 4000, 200, 4000), "Delta: прирост только proxy (скорость и байты)");
        var s2 = TrafficMath.Delta(Prev(), new[] { new ConnSample("c", 10, 20, true) }, 2);
        Check(s2 == new Speed(5, 10, 10, 20), "Delta: новое соединение от нуля, байты не делятся на секунды");
        var s3 = TrafficMath.Delta(Prev(new ConnSample("a", 500, 500, true)), new[] { new ConnSample("a", 100, 100, true) }, 1);
        Check(s3 == new Speed(0, 0), "Delta: счётчик уменьшился");
        Check(TrafficMath.Delta(Prev(), new[] { new ConnSample("c", 10, 20, true) }, 0) == new Speed(0, 0, 10, 20), "Delta: seconds<=0 -> скорость 0, байты не теряются");

        Check(Format.Speed(0) == "0 Б/с" && Format.Speed(1536) == "1.5 КБ/с" && Format.Speed(8808038) == "8.4 МБ/с", "Format.Speed");
        var old = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
        Check(Format.Speed(1536) == "1.5 КБ/с", "Format.Speed: не зависит от культуры");
        System.Globalization.CultureInfo.CurrentCulture = old;
        Check(Format.Uptime(new TimeSpan(0, 42, 17)) == "00:42:17" && Format.Uptime(TimeSpan.FromHours(100)) == "100:00:00", "Format.Uptime");
        Check(Format.Bytes(0) == "0 Б" && Format.Bytes(512) == "512 Б" && Format.Bytes(1536) == "1.5 КБ" && Format.Bytes(8808038) == "8.4 МБ"
            && Format.Bytes(1288490189) == "1.20 ГБ", "Format.Bytes");

        // закрытый порт
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int closed = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        Speed? got = null;
        using (var m = new TrafficMeter(closed, "s"))
        using (var ev = new ManualResetEventSlim())
        {
            m.Sample += s => { got = s; ev.Set(); };
            m.Start();
            bool arrived = ev.Wait(3000);
            Check(arrived && got == new Speed(0, 0), "TrafficMeter: закрытый порт");
        }

        // фейковый Clash API
        var l2 = new TcpListener(IPAddress.Loopback, 0);
        l2.Start();
        int port = ((IPEndPoint)l2.LocalEndpoint).Port;
        var headers = new System.Collections.Concurrent.ConcurrentBag<string>();
        int n = 0;
        var cts = new CancellationTokenSource();
        Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    using var c = l2.AcceptTcpClient();
                    c.ReceiveTimeout = 2000;
                    var st = c.GetStream();
                    var sb = new System.Text.StringBuilder();
                    var b = new byte[1];
                    while (!sb.ToString().EndsWith(CRLF + CRLF) && st.Read(b, 0, 1) == 1) sb.Append((char)b[0]);
                    headers.Add(sb.ToString());
                    int up = 100 + (Interlocked.Increment(ref n) >= 2 ? 1000 : 0);
                    var body = "{\"connections\":[{\"id\":\"a\",\"upload\":" + up + ",\"download\":5,\"chains\":[\"proxy\"]}]}";
                    var resp = string.Join(CRLF, "HTTP/1.1 200 OK", "Content-Type: application/json", "Content-Length: " + body.Length, "Connection: close", "", body);
                    st.Write(System.Text.Encoding.ASCII.GetBytes(resp));
                }
                catch { }
            }
        });
        double maxUp = 0;
        using (var m = new TrafficMeter(port, "s"))
        using (var ev = new ManualResetEventSlim())
        {
            m.Sample += s => { if (s.UpBps > maxUp) { maxUp = s.UpBps; ev.Set(); } };
            m.Start();
            ev.Wait(5000);
        }
        // системный прокси, который не пропускает 127.0.0.1 мимо себя: счётчик должен ходить напрямую
        double viaProxyUp = 0;
        var oldProxy = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new DeadProxy();
        Interlocked.Exchange(ref n, 0); // снова рост между первым и вторым ответом
        try
        {
            using var m = new TrafficMeter(port, "s");
            using var ev = new ManualResetEventSlim();
            m.Sample += s => { if (s.UpBps > viaProxyUp) { viaProxyUp = s.UpBps; ev.Set(); } };
            m.Start();
            ev.Wait(5000);
        }
        finally { HttpClient.DefaultProxy = oldProxy; }
        cts.Cancel();
        l2.Stop();
        Check(maxUp > 0, "TrafficMeter: фейковый Clash API даёт UpBps > 0");
        Check(viaProxyUp > 0, "TrafficMeter: системный прокси не используется (127.0.0.1 напрямую)");
        Check(headers.Count > 0 && headers.All(h => h.Contains("Authorization: Bearer s") && h.StartsWith("GET /connections")), "TrafficMeter: заголовок Authorization");
    }

    static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(50);
    const string Crashed = "sing-box завершился, прокси не работает";

    static Settings Two() => new Settings { Ip = "127.0.0.1", Port = 1080, Apps = A(@"C:\a\chrome.exe", @"C:\b\Game.exe") };

    static ProbeResult Up(string ip, int port) => new ProbeResult { Reachable = true, UdpSupported = true };

    static ProbeResult DownProbe(string ip, int port) => new ProbeResult { Error = "таймаут подключения" };

    static Settings Both() => new Settings { Ip = "127.0.0.1", Port = 1080, DirectPort = 1090,
        Apps = A(@"C:\a\chrome.exe"), DirectApps = A(@"C:\q\qbittorrent.exe") };

    const string Reality = "vless://3f1c0a52-0000-4000-8000-000000000001@203.0.113.5:443?security=reality&sni=www.microsoft.com&pbk=PUBKEY&sid=ab12&type=tcp&flow=xtls-rprx-vision";
    const string DnsDown = "DNS через сервер не отвечает";

    // VLESS: reality на 203.0.113.5:443, одна программа и список YouTube
    static Settings Vless1() => new Settings { Mode = ProxyMode.Vless, VlessLink = Reality, Apps = A(@"C:\a\chrome.exe"), Lists = new List<string> { "youtube" } };

    static ListsResult NoFiles(params string[] failed) => new(new Dictionary<string, string>(), failed, Array.Empty<string>());

    // Id списков каталога, у которых в rules\ рядом с конфигом не пустой SRS.
    static List<string> CfgLists(string path) => Catalogs.Lists.Select(l => l.Id)
        .Where(id => !File.ReadAllBytes(RuleFiles.PathOf(RulesOf(path), RuleFiles.ListTag(id))).SequenceEqual(RuleFiles.EmptySrs)).ToList();

    static void EvaluateTests()
    {
        var ok = new ProbeResult { Reachable = true, UdpSupported = true };
        var noUdp = new ProbeResult { Reachable = true, UdpSupported = false };
        var down = new ProbeResult { Error = "таймаут подключения" };
        var s2 = new Settings { Port = 1080, DirectPort = 1090, Apps = A(@"C:\a\chrome.exe"), DirectApps = A(@"C:\q\qbittorrent.exe") };

        var v = Session.Evaluate(s2, ok, ok);
        Check(v.Error == null && v.Live == new Exits(true, true) && v.Warnings.Count == 0 && v.UdpOk == true, "Evaluate: оба живы");
        v = Session.Evaluate(s2, down, ok);
        Check(v.Error == null && v.Live == new Exits(false, true) && v.UdpOk == null
            && v.Warnings.SequenceEqual(new[] { "Прокси недоступен (таймаут подключения): программы «Через прокси» идут обычным путём." }),
            "Evaluate: прокси недоступен, bypass жив -> предупреждение");
        v = Session.Evaluate(s2, noUdp, down);
        Check(v.Live == new Exits(true, false) && v.Warnings.SequenceEqual(new[] {
                "Порт «Мимо Podkop» недоступен (таймаут подключения): программы из этого списка идут обычным путём.",
                "UDP через этот прокси не работает: игры и голосовая связь могут не заработать." }),
            "Evaluate: bypass недоступен + нет UDP у прокси -> два предупреждения по порядку");
        v = Session.Evaluate(s2, down, down);
        Check(v.Error == "Прокси недоступен: таймаут подключения" && v.Warnings.Count == 0, "Evaluate: оба недоступны -> ошибка прокси");
        var onlyD = new Settings { DirectApps = A(@"C:\q\qbittorrent.exe") };
        Check(Session.Evaluate(onlyD, null, null).Error == "Порт «Мимо Podkop» не указан", "Evaluate: только «Мимо Podkop», порт не указан -> ошибка");
        onlyD.DirectPort = 1090;
        Check(Session.Evaluate(onlyD, null, down).Error == "Порт «Мимо Podkop» недоступен: таймаут подключения", "Evaluate: только «Мимо Podkop», порт недоступен -> ошибка");
        Check(Session.Evaluate(onlyD, null, noUdp).Warnings.SequenceEqual(new[] {
                "UDP через порт «Мимо Podkop» не работает: игры и голосовая связь в этих программах могут не заработать." }),
            "Evaluate: нет UDP у bypass -> предупреждение");
        var noPort = new Settings { Apps = A(@"C:\a\chrome.exe"), DirectApps = A(@"C:\q\qbittorrent.exe") };
        Check(Session.Evaluate(noPort, ok, null).Warnings.SequenceEqual(new[] { "Порт «Мимо Podkop» не указан: программы из этого списка идут обычным путём." }),
            "Evaluate: порт не указан при программах в списке -> предупреждение");
        noPort.DirectApps[0].Enabled = false;
        Check(Session.Evaluate(noPort, ok, null).Warnings.Count == 0, "Evaluate: программы «Мимо Podkop» выключены -> про порт молчим");

        var srv = IPAddress.Parse("203.0.113.5");
        var vs = new Settings { Mode = ProxyMode.Vless, VlessLink = Reality, Apps = A(@"C:\a\chrome.exe"), Lists = new List<string> { "youtube", "telegram" } };
        v = Session.EvaluateVless(vs, Single(new JsonObject(), srv), NoFiles("telegram", "youtube"));
        Check(v.Error == null && v.Live == new Exits(true, false) && v.UdpOk == null && v.Vless != null && v.Vless.Ips[0].Equals(srv) && v.Vless.ListFiles.Count == 0
            && v.Warnings.SequenceEqual(new[] { "Список Telegram не загрузился", "Список YouTube не загрузился" }),
            "EvaluateVless: два Failed -> два предупреждения в порядке Failed");
        vs.Apps.Clear();
        v = Session.EvaluateVless(vs, Single(new JsonObject(), srv), NoFiles("telegram", "youtube"));
        Check(v.Error == "Списки не загрузились" && v.Vless == null && v.Warnings.Count == 0, "EvaluateVless: нет программ и ни одного файла -> ошибка");
        var one = new Dictionary<string, string> { ["youtube"] = @"C:\l\youtube.srs" };
        v = Session.EvaluateVless(vs, Single(new JsonObject(), srv), new ListsResult(one, new[] { "telegram" }, Array.Empty<string>()));
        Check(v.Error == null && v.Vless!.ListFiles.ContainsKey("youtube") && v.Warnings.SequenceEqual(new[] { "Список Telegram не загрузился" }),
            "EvaluateVless: без программ хватает одного загруженного списка");
    }

    static List<string> CfgApps(string path) => RuleNames(RulesOf(path), RuleFiles.Apps);

    static void SessionTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "vivitune-tests-" + Guid.NewGuid().ToString("N"));
        int n = 0;
        string NewCfg() { var d = Path.Combine(root, (++n).ToString()); Directory.CreateDirectory(d); return Path.Combine(d, "config.json"); }
        try
        {
            {
                var fb = new FakeSingBox();
                var cfg = NewCfg();
                var speeds = new List<Speed>();
                using var ss = new Session(Two(), fb, cfg, Up, Delay);
                ss.Speed += sp => { lock (speeds) speeds.Add(sp); };
                bool done = ss.ConnectAsync().Wait(2000);
                Check(done && ss.State == ConnState.Connected && fb.Starts == 1 && ss.UdpOk == true && File.Exists(cfg) &&
                      ss.Error == null && ss.ConnectedSince != null && ss.Warnings.Count == 0 && ss.Live == new Exits(true, false),
                      "Session: успешное подключение");
                var ca = Obj(Obj(JsonNode.Parse(File.ReadAllText(cfg))!["experimental"])["clash_api"]);
                var secret = Str(ca["secret"]) ?? "";
                Check(secret.Length == 32 && secret.All(Uri.IsHexDigit) && Str(ca["external_controller"])!.StartsWith("127.0.0.1:"),
                    "Session: Clash API на 127.0.0.1, секрет 16 байт hex");
                ss.Disconnect();
                Speed last;
                lock (speeds) last = speeds.Count > 0 ? speeds[^1] : new Speed(-1, -1);
                Check(ss.State == ConnState.Disconnected && ss.ConnectedSince == null && last == new Speed(0, 0),
                    "Session: после Disconnect скорость 0");
            }
            {
                var fb = new FakeSingBox();
                using var ss = new Session(Two(), fb, NewCfg(), (_, _) => new ProbeResult { Error = "таймаут подключения" }, Delay);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Disconnected && ss.Error != null && ss.Error.StartsWith("Прокси недоступен") && fb.Starts == 0,
                    "Session: прокси недоступен");
            }
            {
                var fb = new FakeSingBox();
                var s = Two();
                foreach (var a in s.Apps) a.Enabled = false;
                using var ss = new Session(s, fb, NewCfg(), Up, Delay);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Disconnected && ss.Error == "Добавьте хотя бы одно приложение" && fb.Starts == 0,
                    "Session: нет включённых приложений");
            }
            {
                var fb = new FakeSingBox { StartError = new FileNotFoundException("Не найден sing-box.exe рядом с программой") };
                using var ss = new Session(Two(), fb, NewCfg(), Up, Delay);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Disconnected && ss.Error == "Не удалось запустить: Не найден sing-box.exe рядом с программой",
                    "Session: ошибка запуска sing-box");
            }
            {
                var fb = new FakeSingBox();
                var s = Both(); // перезапуск вызываем сменой Needs: единственная программа «Мимо Podkop»
                var cfg = NewCfg();
                using var ss = new Session(s, fb, cfg, Up, Delay);
                ss.ConnectAsync().Wait(2000);
                s.DirectApps[0].Enabled = false;
                ss.AppsChanged();
                bool restarting = ss.State == ConnState.Restarting;
                s.DirectApps[0].Enabled = true;
                ss.AppsChanged();
                s.Apps.Add(new AppEntry { Path = @"C:\c\Discord.exe" });
                s.DirectApps[0].Enabled = false;
                ss.AppsChanged();
                Thread.Sleep(300);
                var apps = CfgApps(cfg);
                Check(restarting && fb.Starts == 2 && fb.Stops == 1 && ss.State == ConnState.Connected,
                    "Session: три быстрых изменения -> один перезапуск");
                Check(apps.SequenceEqual(new[] { "chrome.exe", "Discord.exe" }), "Session: конфиг перезапуска по последнему состоянию");
            }
            {
                var fb = new FakeSingBox();
                var s = new Settings { Ip = "127.0.0.1", Port = 1080, Apps = A(@"C:\a\chrome.exe") };
                using var ss = new Session(s, fb, NewCfg(), Up, Delay);
                var logs = new List<string>();
                ss.Log += l => { lock (logs) logs.Add(l); };
                ss.ConnectAsync().Wait(2000);
                s.Apps[0].Enabled = false;
                ss.AppsChanged();
                Thread.Sleep(150);
                Check(ss.State == ConnState.Disconnected && fb.Starts == 1 && ss.Error == null, "Session: выключено последнее приложение");
                lock (logs) Check(logs.Contains("Отключено: нет включённых приложений"), "Session: выключено последнее приложение -> строка в логе");
            }
            {
                var fb = new FakeSingBox();
                var s = Two();
                s.DirectPort = 1090;
                s.DirectApps = A(@"C:\q\qbittorrent.exe"); // перезапуск вызываем сменой Needs: её единственная программа
                s.Apps.Add(new AppEntry { Path = @"C:\c\Discord.exe", Enabled = false });
                s.Apps.Add(new AppEntry { Path = @"D:\old\CHROME.EXE", Enabled = false });
                var cfg = NewCfg();
                using var ss = new Session(s, fb, cfg, Up, Delay);
                ss.ConnectAsync().Wait(2000);
                s.Apps[3].Enabled = true; // включён тот же chrome.exe по другому пути: правила те же
                ss.AppsChanged();
                bool a = ss.State == ConnState.Connected;
                s.Apps.RemoveAt(2); // убран выключенный Discord
                ss.AppsChanged();
                bool b = ss.State == ConnState.Connected;
                Thread.Sleep(150);
                Check(a && b && ss.State == ConnState.Connected && fb.Starts == 1 && fb.Stops == 0,
                    "Session: правила те же (включён дубль, убран выключенный) -> без перезапуска");
                s.Apps[1].Enabled = false;
                s.DirectApps[0].Enabled = false;
                ss.AppsChanged();
                Thread.Sleep(300);
                Check(fb.Starts == 2 && ss.State == ConnState.Connected && CfgApps(cfg).SequenceEqual(new[] { "chrome.exe" }),
                    "Session: после изменений без перезапуска следующий перезапуск по последнему состоянию");
            }
            {
                var fb = new FakeSingBox();
                var s = Both();
                using var ss = new Session(s, fb, NewCfg(), Up, Delay);
                ss.ConnectAsync().Wait(2000);
                s.DirectApps[0].Enabled = false;
                ss.AppsChanged();
                bool restarting = ss.State == ConnState.Restarting;
                s.DirectApps[0].Enabled = true; // вернули как было, пока ждали перезапуска
                ss.AppsChanged();
                Thread.Sleep(300);
                Check(restarting && ss.State == ConnState.Connected && fb.Starts == 1 && fb.Stops == 0 && ss.Error == null,
                    "Session: выключили и сразу включили обратно -> без перезапуска");
            }
            {
                var fb = new FakeSingBox();
                var s = Both();
                using var ss = new Session(s, fb, NewCfg(), Up, Delay);
                var logs = new List<string>();
                ss.Log += l => { lock (logs) logs.Add(l); };
                ss.ConnectAsync().Wait(2000);
                s.DirectApps[0].Enabled = false;
                ss.AppsChanged();
                bool restarting = ss.State == ConnState.Restarting;
                s.DirectApps[0].Enabled = true; // вернули и добавили программу, пока ждали перезапуска
                s.Apps.AddRange(A(@"C:\a\other.exe"));
                ss.AppsChanged();
                Thread.Sleep(300);
                lock (logs)
                {
                    int i = logs.IndexOf("Правила обновлены без перезапуска");
                    Check(restarting && ss.State == ConnState.Connected && fb.Starts == 1 && fb.Stops == 0
                          && i >= 0 && i + 1 < logs.Count && logs[i + 1] == "Перезапустите выбранные приложения: уже открытые соединения остаются прямыми.",
                        "Session: правила обновлены в ожидании перезапуска, новое приложение -> подсказка про перезапуск программ");
                }
            }
            {
                var fb = new FakeSingBox();
                var s = Both();
                using var ss = new Session(s, fb, NewCfg(), Up, Delay);
                ss.ConnectAsync().Wait(2000);
                s.DirectApps[0].Enabled = false;
                ss.AppsChanged();
                bool stillUp = ss.State == ConnState.Restarting;
                s.Apps[0].Enabled = false;
                ss.AppsChanged();
                bool off = ss.State == ConnState.Disconnected;
                Thread.Sleep(150);
                Check(stillUp && off && ss.State == ConnState.Disconnected && fb.Starts == 1 && ss.Error == null,
                    "Session: выключено последнее приложение во время ожидания перезапуска");
            }
            {
                var fb = new FakeSingBox { ExitOnStop = true };
                var s = Both();
                using var ss = new Session(s, fb, NewCfg(), Up, Delay);
                ss.ConnectAsync().Wait(2000);
                s.DirectApps[0].Enabled = false;
                ss.AppsChanged();
                Thread.Sleep(300);
                bool afterRestart = ss.State == ConnState.Connected && ss.Error == null && fb.Starts == 2;
                ss.Disconnect();
                Check(afterRestart && ss.State == ConnState.Disconnected && ss.Error == null,
                    "Session: Exited при перезапуске и Disconnect не ошибка");
            }
            {
                var fb = new FakeSingBox();
                var s = Both();
                using var ss = new Session(s, fb, NewCfg(), Up, Delay);
                ss.ConnectAsync().Wait(2000);
                fb.LateExited(); // событие от прежнего процесса: текущий жив
                bool a = ss.State == ConnState.Connected && ss.Error == null;
                s.DirectApps[0].Enabled = false;
                ss.AppsChanged();
                Thread.Sleep(300);
                fb.LateExited();
                bool b = ss.State == ConnState.Connected && ss.Error == null;
                ss.Disconnect();
                fb.LateExited();
                Check(a && b && ss.State == ConnState.Disconnected && ss.Error == null, "Session: запоздалый Exited от прежнего процесса не ошибка");
            }
            {
                var fb = new FakeSingBox();
                using var ss = new Session(Two(), fb, NewCfg(), Up, Delay);
                ss.ConnectAsync().Wait(2000);
                bool up = ss.State == ConnState.Connected;
                fb.Crash();
                Check(up && ss.State == ConnState.Disconnected && ss.Error == Crashed && ss.ConnectedSince == null,
                    "Session: неожиданный выход");
            }
            {
                var fb = new FakeSingBox();
                using var release = new ManualResetEventSlim();
                using var ss = new Session(Two(), fb, NewCfg(), (ip, port) => { release.Wait(2000); return Up(ip, port); }, Delay);
                var t = ss.ConnectAsync();
                bool checking = ss.State == ConnState.Checking;
                ss.Disconnect();
                release.Set();
                t.Wait(2000);
                Check(checking && ss.State == ConnState.Disconnected && fb.Starts == 0 && ss.Error == null,
                    "Session: Disconnect во время проверки отменяет запуск");
            }
            {
                var fb = new FakeSingBox();
                var s = Two();
                var cfg = NewCfg();
                using var release = new ManualResetEventSlim();
                using var ss = new Session(s, fb, cfg, (ip, port) => { release.Wait(2000); return Up(ip, port); }, Delay);
                var t = ss.ConnectAsync();
                s.Apps.Add(new AppEntry { Path = @"C:\c\Discord.exe" });
                ss.AppsChanged();
                release.Set();
                t.Wait(2000);
                Thread.Sleep(150);
                Check(ss.State == ConnState.Connected && fb.Starts == 1 && CfgApps(cfg).Contains("Discord.exe"),
                    "Session: изменение списка во время проверки попадает в конфиг");
            }
            {
                var fb = new FakeSingBox();
                var cfg = NewCfg();
                using var ss = new Session(Both(), fb, cfg, (ip, port) => port == 1090 ? Up(ip, port) : DownProbe(ip, port), Delay);
                ss.ConnectAsync().Wait(2000);
                var rules = Arr(Obj(JsonNode.Parse(File.ReadAllText(cfg))!["route"])["rules"]);
                Check(ss.State == ConnState.Connected && ss.Live == new Exits(false, true) && ss.Warnings.Count == 1 && ss.UdpOk == null
                    && AppRule(rules, "proxy") == null && AppRule(rules, "bypass") != null,
                    "Session: прокси недоступен, bypass жив -> подключено с предупреждением, в конфиге только bypass");
            }
            {
                var fb = new FakeSingBox();
                using var ss = new Session(Both(), fb, NewCfg(), DownProbe, Delay);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Disconnected && ss.Error == "Прокси недоступен: таймаут подключения" && fb.Starts == 0 && ss.Warnings.Count == 0,
                    "Session: оба выхода недоступны -> ошибка, sing-box не запускается");
            }
            {
                var fb = new FakeSingBox();
                var s = Both();
                s.DirectApps.Clear();
                var cfg = NewCfg();
                var bypassUp = false;
                var probed = new List<int>();
                using var ss = new Session(s, fb, cfg, (ip, port) =>
                {
                    lock (probed) probed.Add(port);
                    return port == 1090 && !Volatile.Read(ref bypassUp) ? DownProbe(ip, port) : Up(ip, port);
                }, Delay);
                ss.ConnectAsync().Wait(2000);
                bool firstNoBypass = !probed.Contains(1090); // список «Мимо Podkop» пуст: его порт не проверяется
                s.DirectApps = A(@"C:\q\qbittorrent.exe");   // как App: новый список целиком
                ss.AppsChanged();
                Thread.Sleep(300);
                bool warned = ss.Warnings.Any(w => w.StartsWith("Порт «Мимо Podkop» недоступен"));
                Volatile.Write(ref bypassUp, true);
                s.Apps = new List<AppEntry>(); // Needs снова меняется (остался только «Мимо Podkop»): перезапуск
                ss.AppsChanged();
                Thread.Sleep(300);
                var rules = Arr(Obj(JsonNode.Parse(File.ReadAllText(cfg))!["route"])["rules"]);
                Check(firstNoBypass && warned && ss.State == ConnState.Connected && ss.Warnings.Count == 0 && AppRule(rules, "bypass") != null
                    && fb.Starts == 3, "Session: перезапуск проверяет выходы заново, предупреждения заменяются");
            }
            {
                var fb = new FakeSingBox();
                var s = Both();
                var cfg = NewCfg();
                using var ss = new Session(s, fb, cfg, Up, Delay);
                ss.ConnectAsync().Wait(2000);
                var apps = AppListOps.Copy(s.Apps);
                var direct = AppListOps.Copy(s.DirectApps);
                AppListOps.Move(direct, apps, @"C:\a\chrome.exe"); // перенос во вкладку «Мимо Podkop» во время работы
                s.Apps = apps;
                s.DirectApps = direct;
                ss.AppsChanged();
                Thread.Sleep(300);
                var rules = Arr(Obj(JsonNode.Parse(File.ReadAllText(cfg))!["route"])["rules"]);
                Check(fb.Starts == 2 && AppRule(rules, "proxy") == null
                    && RuleNames(RulesOf(cfg), RuleFiles.Bypass).OrderBy(x => x).SequenceEqual(new[] { "chrome.exe", "qbittorrent.exe" }),
                    "Session: перенос программы в другой список во время работы -> перезапуск, она только в новом правиле");
            }
            {
                var fb = new FakeSingBox();
                var s = Both();
                int calls = 0;
                using var started = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                using var ss = new Session(s, fb, NewCfg(), (ip, port) =>
                {
                    int c = Interlocked.Increment(ref calls);
                    if (c != 3) return Up(ip, port); // 1–2 — первое подключение (прокси и «Мимо Podkop»), 4+ — новое подключение
                    started.Set(); // 3 — проверка перезапуска: ждёт и сообщает «не отвечает»
                    release.Wait(2000);
                    return DownProbe(ip, port);
                }, Delay);
                ss.ConnectAsync().Wait(2000);
                s.DirectApps[0].Enabled = false;
                ss.AppsChanged(); // перезапуск повис в проверке
                started.Wait(2000);
                ss.Disconnect();
                ss.ConnectAsync().Wait(2000); // новое подключение, пока старая проверка ещё висит
                release.Set();
                Thread.Sleep(300);
                Check(ss.State == ConnState.Connected && fb.Running && ss.Error == null,
                    "Session: запоздавший перезапуск после Disconnect + Connect не трогает новое подключение");
            }
            {
                var fb = new FakeSingBox();
                var s = Both();
                var up = true;
                using var ss = new Session(s, fb, NewCfg(), (ip, port) => Volatile.Read(ref up) ? Up(ip, port) : DownProbe(ip, port), Delay);
                ss.ConnectAsync().Wait(2000);
                Volatile.Write(ref up, false);
                s.DirectApps[0].Enabled = false;
                ss.AppsChanged();
                Thread.Sleep(300);
                Check(ss.State == ConnState.Disconnected && ss.Error == "Прокси недоступен: таймаут подключения" && !fb.Running && fb.Starts == 1
                    && ss.Warnings.Count == 0, "Session: при перезапуске ни один выход не отвечает -> sing-box остановлен, ошибка");
            }
            {
                var fb = new FakeSingBox();
                var s = Both();
                s.DirectApps.Clear();
                var cfg = NewCfg();
                var probed = new List<int>();
                using var started = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                using var ss = new Session(s, fb, cfg, (ip, port) =>
                {
                    lock (probed) probed.Add(port);
                    started.Set();
                    release.Wait(2000);
                    return Up(ip, port);
                }, Delay);
                var t = ss.ConnectAsync();
                started.Wait(2000); // проверка уже идёт по снимку без «Мимо Podkop»
                s.DirectApps = A(@"C:\q\qbittorrent.exe"); // список «Мимо Podkop» появился во время проверки
                ss.AppsChanged();
                release.Set();
                t.Wait(2000);
                var rules = Arr(Obj(JsonNode.Parse(File.ReadAllText(cfg))!["route"])["rules"]);
                bool bypassProbed;
                lock (probed) bypassProbed = probed.Contains(1090);
                Check(ss.State == ConnState.Connected && fb.Starts == 1 && bypassProbed && ss.Live == new Exits(true, true) && AppRule(rules, "bypass") != null,
                    "Session: во время проверки появился список «Мимо Podkop» -> его порт проверяется до запуска");
            }

            // --- горячее применение (SOCKS5) ---
            {
                var fb = new FakeSingBox();
                var s = Two();
                var cfg = NewCfg();
                using var ss = new Session(s, fb, cfg, Up, Delay);
                var logs = new List<string>();
                ss.Log += l => { lock (logs) logs.Add(l); };
                ss.ConnectAsync().Wait(2000);
                s.Apps[1].Enabled = false;
                ss.AppsChanged();
                lock (logs)
                    Check(fb.Starts == 1 && fb.Stops == 0 && ss.State == ConnState.Connected && CfgApps(cfg).SequenceEqual(new[] { "chrome.exe" })
                          && logs.Contains("Правила обновлены без перезапуска"),
                        "Session: программу выключили -> apps.json обновлён без перезапуска");
            }
            {
                var fb = new FakeSingBox();
                var s = Two();
                using var ss = new Session(s, fb, NewCfg(), Up, Delay);
                var logs = new List<string>();
                ss.Log += l => { lock (logs) logs.Add(l); };
                ss.ConnectAsync().Wait(2000);
                s.Apps.Add(new AppEntry { Path = @"C:\c\Discord.exe" });
                ss.AppsChanged();
                lock (logs)
                {
                    int i = logs.LastIndexOf("Правила обновлены без перезапуска");
                    Check(i >= 0 && i + 1 < logs.Count && logs[i + 1] == "Перезапустите выбранные приложения: уже открытые соединения остаются прямыми.",
                        "Session: добавлена программа -> после строки обновления напоминание перезапустить приложения");
                }
            }
            {
                var fb = new FakeSingBox();
                var s = Two();
                s.DirectPort = 1090;
                var cfg = NewCfg();
                using var ss = new Session(s, fb, cfg, Up, Delay);
                ss.ConnectAsync().Wait(2000);
                s.DirectApps = A(@"C:\q\qbittorrent.exe"); // список «Мимо Podkop» стал непустым: другой выход, перезапуск
                ss.AppsChanged();
                Thread.Sleep(300);
                Check(fb.Starts == 2 && ss.State == ConnState.Connected && RuleNames(RulesOf(cfg), RuleFiles.Bypass).SequenceEqual(new[] { "qbittorrent.exe" }),
                    "Session: первая программа «Мимо Podkop» -> перезапуск, bypass.json с ней");
            }

            // --- VLESS ---
            {
                var fb = new FakeSingBox();
                var fv = new FakeVless(root);
                var cfg = NewCfg();
                using var ss = new Session(Vless1(), fb, cfg, Up, Delay, fv.Deps);
                bool done = ss.ConnectAsync().Wait(2000);
                var c = JsonNode.Parse(File.ReadAllText(cfg))!;
                var ob = Obj(Arr(c["outbounds"])[0]);
                var ca = Obj(Obj(c["experimental"])["clash_api"]);
                Check(done && ss.State == ConnState.Connected && fb.Starts == 1 && ss.Live == new Exits(true, false) && ss.UdpOk == null
                      && ss.Warnings.Count == 0 && ss.Error == null && ss.ConnectedSince != null, "Session VLESS: успешное подключение");
                Check(Str(ob["type"]) == "vless" && Str(ob["server"]) == "203.0.113.5" && CfgLists(cfg).SequenceEqual(new[] { "youtube" }) && fv.Resolves == 0,
                    "Session VLESS: в конфиге outbound vless на IP из ссылки и rule_set youtube");
                lock (fv.Api)
                    Check(fv.AfterStarts == 1 && Str(ca["external_controller"]) == "127.0.0.1:" + fv.ApiPort
                          && Str(ca["secret"]) == fv.ApiSecret, "Session VLESS: проверка после запуска один раз, Clash API из конфига");
            }
            {
                var fb = new FakeSingBox();
                var s = Vless1();
                s.VlessLink = Reality.Replace("203.0.113.5", "nl.example.com");
                var fv = new FakeVless(root, resolve: _ => null);
                using var ss = new Session(s, fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                var fv2 = new FakeVless(root, resolve: _ => throw new System.Net.Sockets.SocketException(11001));
                using var ss2 = new Session(s, fb, NewCfg(), Up, Delay, fv2.Deps);
                ss2.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Disconnected && ss.Error == "Сервер не найден: nl.example.com" && fv.Resolves == 1 && fv.Reaches == 0
                      && ss2.State == ConnState.Disconnected && ss2.Error == "Сервер не найден: nl.example.com" && fb.Starts == 0,
                    "Session VLESS: имя сервера не разрешилось (null или исключение) -> ошибка, sing-box не запускается");
            }
            {
                var fb = new FakeSingBox();
                var s = Vless1();
                s.VlessLink = Reality.Replace("203.0.113.5", "nl.example.com");
                IPAddress? reached = null;
                var fv = new FakeVless(root, resolve: h => h == "nl.example.com" ? IPAddress.Parse("198.51.100.7") : null, reach: (ip, port) => { reached = ip; return port == 443 ? null : "порт"; });
                var cfg = NewCfg();
                using var ss = new Session(s, fb, cfg, Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Connected && "198.51.100.7".Equals(reached?.ToString()) && Str(Obj(Arr(JsonNode.Parse(File.ReadAllText(cfg))!["outbounds"])[0])["server"]) == "198.51.100.7",
                    "Session VLESS: имя сервера разрешается до запуска, в конфиг и проверку TCP идёт IP");
            }
            {
                // Hysteria2 (UDP): TCP-проверки нет
                var fb = new FakeSingBox();
                var s = Vless1();
                s.VlessLink = "hysteria2://p@hy.example.com:443";
                var fv = new FakeVless(root, resolve: _ => IPAddress.Parse("198.51.100.7"), reach: (_, _) => "таймаут подключения");
                using var ss = new Session(s, fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Connected && fv.Reaches == 0, "Session: Hysteria2 без TCP-проверки");
            }
            {
                // ошибка разбора — в статусе, sing-box не стартует
                var fb = new FakeSingBox();
                var fv = new FakeVless(root, decode: _ => (null, "Ссылка не подходит: invalid server address"));
                using var ss = new Session(Vless1(), fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Disconnected && ss.Error == "Ссылка не подходит: invalid server address" && fb.Starts == 0, "Session: ошибка разбора ссылки");
            }
            {
                // предупреждения движка — в лог; списки качаются через готовый outbound с IP сервера
                var fb = new FakeSingBox();
                var fv = new FakeVless(root, decode: l => (new Server("n", FakeVless.FakeServer(l).Outbound, ["the gRPC mode multi is not supported, gun is used"]), null));
                var logs = new List<string>();
                using var ss = new Session(Vless1(), fb, NewCfg(), Up, Delay, fv.Deps);
                ss.Log += m => { lock (logs) logs.Add(m); };
                ss.ConnectAsync().Wait(2000);
                lock (logs) Check(logs.Contains("Ссылка: the gRPC mode multi is not supported, gun is used"), "Session: предупреждение движка в логе");
                Check(Str(fv.LastProxy!["server"]) == "203.0.113.5" && Str(fv.LastProxy["tag"]) == "proxy", "Session: PrepareLists получает outbound proxy с IP");
            }
            {
                var fb = new FakeSingBox();
                var fv = new FakeVless(root, decode: _ => (new Server("n", JsonNode.Parse("{\"type\":\"vless\",\"server\":\"203.0.113.5\",\"server_port\":443,\"tls\":{\"enabled\":\"да\"}}")!.AsObject(), []), null));
                using var ss = new Session(Vless1(), fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Disconnected && !string.IsNullOrEmpty(ss.Error) && !ss.Error.Contains("vless://") && fb.Starts == 0,
                    "Session VLESS: ProxyOutbound упал -> ошибка, состояние не зависает");
            }
            {
                var fb = new FakeSingBox();
                var fv = new FakeVless(root, reach: (_, _) => "таймаут подключения");
                using var ss = new Session(Vless1(), fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Disconnected && ss.Error == "Сервер не отвечает: таймаут подключения" && fb.Starts == 0 && fv.Prepares == 0,
                    "Session VLESS: сервер не отвечает -> ошибка, sing-box не запускается");
            }
            {
                var fb = new FakeSingBox();
                var fv = new FakeVless(root, afterStart: (_, _, _) => DnsDown);
                using var ss = new Session(Vless1(), fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Disconnected && ss.Error == DnsDown && fb.Starts == 1 && !fb.Running && ss.Warnings.Count == 0,
                    "Session VLESS: DNS не ответил после запуска -> sing-box остановлен, ошибка как есть");
            }
            {
                var fb = new FakeSingBox();
                var fv = new FakeVless(root, lists: ids => NoFiles("youtube"));
                var cfg = NewCfg();
                using var ss = new Session(Vless1(), fb, cfg, Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Connected && fb.Starts == 1 && ss.Warnings.SequenceEqual(new[] { "Список YouTube не загрузился" }) && CfgLists(cfg).Count == 0,
                    "Session VLESS: список не загрузился при программе -> подключено с предупреждением");
            }
            {
                var fb = new FakeSingBox();
                var s = Vless1();
                var fv = new FakeVless(root, lists: ids => NoFiles("youtube"));
                using var ss = new Session(s, fb, NewCfg(), Up, Delay, fv.Deps);
                var logs = new List<string>();
                ss.Log += l => { lock (logs) logs.Add(l); };
                ss.ConnectAsync().Wait(2000);
                int before = fv.Prepares;
                s.Apps.AddRange(A(@"C:\a\other.exe"));
                ss.AppsChanged();
                lock (logs)
                    Check(before == 1 && fv.Prepares == 1 && logs.Count(l => l == "ВНИМАНИЕ: Список YouTube не загрузился") == 1 && logs.Contains("Правила обновлены без перезапуска"),
                        "Session VLESS: не загрузившийся список не качается заново при других изменениях");
            }
            {
                var fb = new FakeSingBox();
                var s = Vless1();
                s.Apps.Clear();
                var fv = new FakeVless(root, lists: ids => NoFiles("youtube"));
                using var ss = new Session(s, fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Disconnected && ss.Error == "Списки не загрузились" && fb.Starts == 0,
                    "Session VLESS: ни один список не загрузился и программ нет -> ошибка");
            }
            {
                var fb = new FakeSingBox();
                var old = Path.Combine(root, "old-youtube.srs");
                File.WriteAllBytes(old, new byte[] { 0x53, 0x52, 0x53 });
                var fv = new FakeVless(root, lists: ids => new ListsResult(new Dictionary<string, string> { ["youtube"] = old }, Array.Empty<string>(), new[] { "youtube" }));
                var cfg = NewCfg();
                using var ss = new Session(Vless1(), fb, cfg, Up, Delay, fv.Deps);
                var logs = new List<string>();
                ss.Log += l => { lock (logs) logs.Add(l); };
                ss.ConnectAsync().Wait(2000);
                lock (logs)
                    Check(ss.State == ConnState.Connected && ss.Warnings.Count == 0 && CfgLists(cfg).SequenceEqual(new[] { "youtube" })
                          && logs.Contains("Список YouTube не обновился, работаю со старой копией"),
                        "Session VLESS: старая копия -> подключено без предупреждения, строка в логе");
            }
            {
                var fb = new FakeSingBox();
                var s = Vless1();
                var fv = new FakeVless(root);
                var cfg = NewCfg();
                using var ss = new Session(s, fb, cfg, Up, Delay, fv.Deps);
                var logs = new List<string>();
                ss.Log += l => { lock (logs) logs.Add(l); };
                ss.ConnectAsync().Wait(2000);
                s.Lists.Add("telegram");
                ss.AppsChanged(); // горячее применение синхронно: сразу после возврата всё готово
                string[] last;
                lock (fv.Prepared) last = fv.Prepared[^1];
                var tg = File.ReadAllBytes(RuleFiles.PathOf(RulesOf(cfg), RuleFiles.ListTag("telegram")));
                Check(fb.Starts == 1 && fb.Stops == 0 && ss.State == ConnState.Connected && last.SequenceEqual(new[] { "telegram" })
                      && tg.SequenceEqual(new byte[] { 0x53, 0x52, 0x53 }) && CfgLists(cfg).OrderBy(x => x).SequenceEqual(new[] { "telegram", "youtube" })
                      && fv.AfterStarts == 1,
                    "Session VLESS: список включён во время работы -> качается только он, файл на месте, без перезапуска");
                lock (logs) Check(logs.Contains("Правила обновлены без перезапуска"), "Session VLESS: строка горячего применения");
            }
            {
                var fb = new FakeSingBox();
                var s = Vless1();
                var fv = new FakeVless(root);
                var cfg = NewCfg();
                using var ss = new Session(s, fb, cfg, Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                var yt = RuleFiles.PathOf(RulesOf(cfg), RuleFiles.ListTag("youtube"));
                s.Lists.Clear();
                ss.AppsChanged();
                bool emptied = File.ReadAllBytes(yt).SequenceEqual(RuleFiles.EmptySrs);
                s.Lists.Add("youtube");
                ss.AppsChanged();
                Check(emptied && !File.ReadAllBytes(yt).SequenceEqual(RuleFiles.EmptySrs) && fv.Prepares == 1 && fb.Starts == 1 && fb.Stops == 0 && ss.State == ConnState.Connected,
                    "Session VLESS: список выключили (пустой SRS) и включили снова -> копия на месте, заново не качается");
            }
            {
                var fb = new FakeSingBox();
                var s = Vless1();
                var fv = new FakeVless(root);
                var cfg = NewCfg();
                using var ss = new Session(s, fb, cfg, Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                s.Sites.Add("example.com");
                ss.AppsChanged();
                var sites = File.ReadAllText(RuleFiles.PathOf(RulesOf(cfg), RuleFiles.Sites));
                Check(fb.Starts == 1 && fb.Stops == 0 && ss.State == ConnState.Connected && sites.Contains("example.com") && fv.Prepares == 1,
                    "Session VLESS: добавлен свой сайт -> sites.json, без перезапуска и без скачивания");
            }
            {
                // «Отключить» во время скачивания нового списка: ничего не пишем, ошибки нет
                var fb = new FakeSingBox();
                var s = Vless1();
                var cfg = NewCfg();
                using var inPrepare = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                int calls = 0;
                var fv = new FakeVless(root, lists: ids =>
                {
                    if (Interlocked.Increment(ref calls) == 1) return new ListsResult(new Dictionary<string, string> { ["youtube"] = Path.Combine(root, "yt.srs") }, [], []);
                    inPrepare.Set();
                    release.Wait(5000);
                    return new ListsResult(new Dictionary<string, string> { ["telegram"] = Path.Combine(root, "tg.srs") }, [], []);
                });
                File.WriteAllBytes(Path.Combine(root, "yt.srs"), new byte[] { 1 });
                File.WriteAllBytes(Path.Combine(root, "tg.srs"), new byte[] { 2 });
                using var ss = new Session(s, fb, cfg, Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                s.Lists.Add("telegram");
                var change = Task.Run(ss.AppsChanged);
                bool blocked = inPrepare.Wait(2000);
                ss.Disconnect();
                release.Set();
                change.Wait(2000);
                Check(blocked && ss.State == ConnState.Disconnected && ss.Error == null
                      && File.ReadAllBytes(RuleFiles.PathOf(RulesOf(cfg), RuleFiles.ListTag("telegram"))).SequenceEqual(RuleFiles.EmptySrs),
                    "Session VLESS: Disconnect во время скачивания нового списка -> файл не записан, ошибки нет");
            }
            {
                // вернули к applied, пока горячее применение ждёт скачивания: записанное устаревшее правило не остаётся
                var fb = new FakeSingBox();
                var s = Vless1();
                var cfg = NewCfg();
                using var inPrepare = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                int calls = 0;
                File.WriteAllBytes(Path.Combine(root, "yt2.srs"), new byte[] { 1 });
                File.WriteAllBytes(Path.Combine(root, "tg2.srs"), new byte[] { 2 });
                var fv = new FakeVless(root, lists: ids =>
                {
                    if (Interlocked.Increment(ref calls) == 1) return new ListsResult(new Dictionary<string, string> { ["youtube"] = Path.Combine(root, "yt2.srs") }, [], []);
                    inPrepare.Set();
                    release.Wait(5000);
                    return new ListsResult(new Dictionary<string, string> { ["telegram"] = Path.Combine(root, "tg2.srs") }, [], []);
                });
                using var ss = new Session(s, fb, cfg, Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                s.Lists.Add("telegram");
                var change = Task.Run(ss.AppsChanged);
                bool blocked = inPrepare.Wait(2000);
                s.Lists.Remove("telegram");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                ss.AppsChanged(); // правила как у applied: возвращается сразу
                var took = sw.Elapsed;
                release.Set();
                bool done = change.Wait(3000);
                Check(blocked && done && took < TimeSpan.FromSeconds(1) && ss.State == ConnState.Connected && fb.Starts == 1
                      && File.ReadAllBytes(RuleFiles.PathOf(RulesOf(cfg), RuleFiles.ListTag("telegram"))).SequenceEqual(RuleFiles.EmptySrs),
                    "Session VLESS: список включили и вернули, пока он качался -> в файлах итоговое состояние");
            }
            {
                var fb = new FakeSingBox();
                var s = Vless1();
                int calls = 0;
                var cfg = NewCfg();
                var fv = new FakeVless(root, lists: ids => Interlocked.Increment(ref calls) == 1
                    ? new ListsResult(new Dictionary<string, string> { ["youtube"] = Path.Combine(root, "yt.srs") }, [], []) : NoFiles(ids.ToArray()));
                File.WriteAllBytes(Path.Combine(root, "yt.srs"), new byte[] { 1 });
                using var ss = new Session(s, fb, cfg, Up, Delay, fv.Deps);
                var logs = new List<string>();
                ss.Log += l => { lock (logs) logs.Add(l); };
                ss.ConnectAsync().Wait(2000);
                s.Lists.Add("telegram");
                ss.AppsChanged();
                lock (logs)
                    Check(ss.State == ConnState.Connected && fb.Starts == 1 && logs.Contains("ВНИМАНИЕ: Список Telegram не загрузился")
                          && File.ReadAllBytes(RuleFiles.PathOf(RulesOf(cfg), RuleFiles.ListTag("telegram"))).SequenceEqual(RuleFiles.EmptySrs),
                        "Session VLESS: новый список не загрузился -> предупреждение в логе, подключено, файл пустой");
            }
            {
                // Review Focus 3: запись правил не удалась
                var fb = new FakeSingBox();
                var s = Vless1();
                var cfg = NewCfg();
                using var ss = new Session(s, fb, cfg, Up, Delay, new FakeVless(root).Deps);
                var logs = new List<string>();
                ss.Log += l => { lock (logs) logs.Add(l); };
                ss.ConnectAsync().Wait(2000);
                Directory.Delete(RulesOf(cfg), true);
                File.WriteAllText(RulesOf(cfg), "не папка");
                s.Apps[0].Enabled = false;
                ss.AppsChanged();
                lock (logs)
                    Check(ss.State == ConnState.Connected && fb.Starts == 1 && logs.Any(l => l.StartsWith("Не удалось обновить правила: ")),
                        "Session VLESS: правила не записались -> Connected, строка в логе");
            }
            {
                // без программ и списков, только свой сайт
                var fb = new FakeSingBox();
                var s = Vless1();
                s.Apps.Clear();
                s.Lists.Clear();
                s.Sites.Add("example.com");
                var fv = new FakeVless(root);
                var cfg = NewCfg();
                using var ss = new Session(s, fb, cfg, Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Connected && ss.Error == null && fv.Prepares == 0
                      && File.ReadAllText(RuleFiles.PathOf(RulesOf(cfg), RuleFiles.Sites)).Contains("example.com"),
                    "Session VLESS: только свой сайт -> подключено, списки не готовятся");
            }
            {
                // убрали программу и список, остался сайт; убрали и сайт
                var fb = new FakeSingBox();
                var s = Vless1();
                s.Sites.Add("example.com");
                using var ss = new Session(s, fb, NewCfg(), Up, Delay, new FakeVless(root).Deps);
                var logs = new List<string>();
                ss.Log += l => { lock (logs) logs.Add(l); };
                ss.ConnectAsync().Wait(2000);
                s.Apps[0].Enabled = false;
                s.Lists.Clear();
                ss.AppsChanged();
                bool stillUp = ss.State == ConnState.Connected && fb.Starts == 1;
                s.Sites.Clear();
                ss.AppsChanged();
                lock (logs)
                    Check(stillUp && ss.State == ConnState.Disconnected && ss.Error == null && !fb.Running
                          && logs.Contains("Отключено: нет включённых приложений, сайтов и списков"),
                        "Session VLESS: остался только сайт -> работает; убран и он -> отключено, строка в логе");
            }
            {
                var fb = new FakeSingBox();
                var s = Vless1();
                var fv = new FakeVless(root);
                using var ss = new Session(s, fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                s.Lists.Add("telegram");
                ss.AppsChanged();
                s.Lists.Remove("telegram");
                ss.AppsChanged();
                Thread.Sleep(300);
                Check(ss.State == ConnState.Connected && fb.Starts == 1 && fb.Stops == 0 && fv.Prepares == 2,
                    "Session VLESS: список включили и сразу выключили -> без перезапуска");
            }
            {
                var fb = new FakeSingBox();
                var s = Vless1();
                s.Lists.Add("telegram");
                using var ss = new Session(s, fb, NewCfg(), Up, Delay, new FakeVless(root).Deps);
                ss.ConnectAsync().Wait(2000);
                s.Lists = new List<string> { "telegram", "youtube" };
                ss.AppsChanged();
                bool same = ss.State == ConnState.Connected;
                Thread.Sleep(150);
                Check(same && ss.State == ConnState.Connected && fb.Starts == 1, "Session VLESS: те же списки в другом порядке -> без перезапуска");
            }
            {
                var fb = new FakeSingBox();
                var s = Vless1();
                using var ss = new Session(s, fb, NewCfg(), Up, Delay, new FakeVless(root).Deps);
                var logs = new List<string>();
                ss.Log += l => { lock (logs) logs.Add(l); };
                ss.ConnectAsync().Wait(2000);
                s.Apps[0].Enabled = false;
                ss.AppsChanged();
                bool still = ss.State == ConnState.Connected; // остался список: правила обновлены, не отключение
                s.Lists.Clear();
                ss.AppsChanged();
                Thread.Sleep(150);
                lock (logs)
                    Check(still && ss.State == ConnState.Disconnected && ss.Error == null && fb.Starts == 1 && !fb.Running
                          && logs.Contains("Отключено: нет включённых приложений, сайтов и списков"),
                        "Session VLESS: убраны программа и все списки -> отключено, строка в логе");
            }
            {
                var fb = new FakeSingBox();
                var s = Vless1();
                s.VlessLink = "";
                var fv = new FakeVless(root);
                using var ss = new Session(s, fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Disconnected && ss.Error == "Вставьте ссылку в настройках" && fb.Starts == 0 && fv.Reaches == 0,
                    "Session VLESS: пустая ссылка -> ошибка, sing-box не запускается");
            }
            {
                var fb = new FakeSingBox();
                using var ss = new Session(Vless1(), fb, NewCfg(), Up, Delay);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Disconnected && ss.Error == "Режим VLESS недоступен" && fb.Starts == 0, "Session VLESS: без VlessDeps -> ошибка");
            }
            {
                // Review Focus 4: «Отключить» во время проверки после запуска
                var fb = new FakeSingBox();
                using var started = new ManualResetEventSlim();
                var fv = new FakeVless(root, afterStart: (_, _, ct) => { started.Set(); ct.WaitHandle.WaitOne(5000); return "Сервер не отвечает"; });
                using var ss = new Session(Vless1(), fb, NewCfg(), Up, Delay, fv.Deps);
                var t = ss.ConnectAsync();
                bool inCheck = started.Wait(2000);
                Thread.Sleep(200);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                ss.Disconnect();
                var took = sw.Elapsed;
                bool done = t.Wait(2000);
                Check(inCheck && done && took < TimeSpan.FromSeconds(1) && ss.State == ConnState.Disconnected && ss.Error == null && !fb.Running && fb.Starts == 1,
                    "Session VLESS: Disconnect во время проверки после запуска -> быстро, sing-box остановлен, без ошибки");
            }
            {
                var fb = new FakeSingBox();
                var s = Vless1();
                var cfg = NewCfg();
                using var started = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                var fv = new FakeVless(root, reach: (_, _) => { started.Set(); release.Wait(2000); return null; });
                using var ss = new Session(s, fb, cfg, Up, Delay, fv.Deps);
                var t = ss.ConnectAsync();
                started.Wait(2000); // проверка идёт по снимку с одним YouTube
                s.Lists.Add("telegram");
                ss.AppsChanged();
                release.Set();
                t.Wait(2000);
                string[] last;
                lock (fv.Prepared) last = fv.Prepared[^1];
                Check(ss.State == ConnState.Connected && fb.Starts == 1 && fv.Prepares == 2 && last.SequenceEqual(new[] { "youtube", "telegram" })
                      && CfgLists(cfg).SequenceEqual(new[] { "youtube", "telegram" }),
                    "Session VLESS: список включён во время проверки -> списки готовятся заново до запуска");
            }
            {
                var fb = new FakeSingBox();
                var s = Vless1();
                using var started = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                var fv = new FakeVless(root, reach: (_, _) => { started.Set(); release.Wait(2000); return null; }, lists: ids => NoFiles(ids.ToArray()));
                using var ss = new Session(s, fb, NewCfg(), Up, Delay, fv.Deps);
                var t = ss.ConnectAsync();
                started.Wait(2000);
                s.Apps[0].Enabled = false; // единственную программу выключили во время проверки, а списки не загрузятся
                ss.AppsChanged();
                release.Set();
                t.Wait(2000);
                Check(ss.State == ConnState.Disconnected && ss.Error == "Списки не загрузились" && fb.Starts == 0,
                    "Session VLESS: программы пропали во время проверки -> итог проверяется заново");
            }
            using (var rn = new ViviTune.Windows.Runner(Path.Combine(root, "нет", "sing-box.exe")))
            {
                ISingBox sb = rn;
                string? msg = null;
                try { sb.Start(NewCfg()); } catch (FileNotFoundException e) { msg = e.Message; }
                Check(msg == "Не найден sing-box.exe рядом с программой" && !sb.Running, "Runner: нет sing-box.exe -> FileNotFoundException");
            }
            {
                // прошлый запуск был, новый не стартовал (здесь: нет рабочей папки) -> Running false, Stop/Dispose без исключений
                var rn = new ViviTune.Windows.Runner(Path.Combine(Environment.SystemDirectory, "whoami.exe")); // ругается на аргументы и выходит
                bool ok;
                try
                {
                    rn.Start(NewCfg());
                    for (int i = 0; i < 60 && rn.Running; i++) Thread.Sleep(50);
                    bool threw = false;
                    try { rn.Start(Path.Combine(root, "нет-папки", "config.json")); }
                    catch (System.ComponentModel.Win32Exception) { threw = true; }
                    ok = threw && !rn.Running;
                    rn.Stop();
                    rn.Dispose();
                }
                catch (Exception) { ok = false; }
                Check(ok, "Runner: Start не удался после прошлого запуска -> Running == false, Stop/Dispose без исключений");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}

// Системный прокси, который никого не пропускает мимо себя (и 127.0.0.1 тоже) и сам не отвечает.
sealed class DeadProxy : IWebProxy
{
    public ICredentials? Credentials { get; set; }
    public Uri GetProxy(Uri destination) => new("http://127.0.0.1:9");
    public bool IsBypassed(Uri host) => false;
}

sealed class FakeSingBox : ISingBox
{
    volatile bool running;
    public int Starts, Stops;
    public bool ExitOnStop;
    public Exception? StartError;

    public event Action<string>? Line;
    public event Action? Exited;

    public bool Running => running;

    public void Start(string configPath)
    {
        if (StartError != null) throw StartError;
        Interlocked.Increment(ref Starts);
        running = true;
        Line?.Invoke("fake start " + configPath);
    }

    public void Stop()
    {
        Interlocked.Increment(ref Stops);
        bool was = running;
        running = false;
        if (ExitOnStop && was) Exited?.Invoke(); // как настоящий процесс: Exited прямо внутри Stop
    }

    public void Crash()
    {
        running = false;
        Exited?.Invoke();
    }

    public void LateExited() => Exited?.Invoke(); // Exited прежнего процесса пришёл, когда новый уже работает
}

sealed class FakeApi : IClashApi
{
    public Dictionary<string, int?> Delays = new();
    public List<string> Calls = new();
    public int MaxParallel;
    public TimeSpan Wait;
    public (string Group, string Tag)? Selected;
    public Queue<int?> Proxy = new();  // очередь ответов для "proxy"; пустая — как обычно по Delays
    public int ProxyChecks;
    int cur;
    public int? Delay(string tag, int timeoutMs, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref cur);
        int? queued = null; bool fromQueue = false;
        lock (Calls)
        {
            Calls.Add(tag); MaxParallel = Math.Max(MaxParallel, n);
            if (tag == "proxy") { ProxyChecks++; if (Proxy.Count > 0) { queued = Proxy.Dequeue(); fromQueue = true; } }
        }
        try { if (Wait > TimeSpan.Zero) Thread.Sleep(Wait); if (fromQueue) return queued; return Delays.TryGetValue(tag, out var d) ? d : null; }
        finally { Interlocked.Decrement(ref cur); }
    }
    public bool Select(string group, string tag) { Selected = (group, tag); return true; }
}

// Подмена VlessDeps: считает вызовы, запоминает id списков и адрес Clash API. По умолчанию имя не разрешается,
// сервер отвечает, каждый список — существующий файл в dir, проверка после запуска проходит.
sealed class FakeVless
{
    public int Resolves, Reaches, Prepares, AfterStarts;
    public readonly List<string[]> Prepared = new();  // под lock (Prepared)
    public readonly object Api = new();               // под lock (Api) — ApiPort/ApiSecret
    public int ApiPort;
    public string? ApiSecret;
    public JsonObject? LastProxy;
    public int Fetches, Saves;
    public SubCache? Cache;
    public readonly VlessDeps Deps;

    // «Разбор» ссылки без движка: адрес и порт из Uri, тип по схеме
    public static Server FakeServer(string link)
    {
        var u = new Uri(link);
        var o = new JsonObject { ["type"] = u.Scheme == "hysteria2" ? "hysteria2" : "vless", ["server"] = u.Host, ["server_port"] = u.Port };
        return new Server(u.Host, o, []);
    }

    public FakeVless(string dir, Func<string, IPAddress?>? resolve = null, Func<IPAddress, int, string?>? reach = null,
        Func<IReadOnlyList<string>, ListsResult>? lists = null, Func<int, string, CancellationToken, string?>? afterStart = null,
        Func<string, (Server?, string?)>? decode = null, SubCache? cache = null, Func<SubCache?, (SubCache?, string?)>? fetch = null,
        FakeApi? api = null, TimeSpan? pingCap = null, TimeSpan? watchEvery = null)
    {
        Cache = cache;
        Deps = new VlessDeps
        {
            LoadSub = url => Cache != null && Cache.Url == url ? Cache : null,
            FetchSub = (url, old) =>
            {
                Interlocked.Increment(ref Fetches);
                var r = fetch != null ? fetch(old) : (old, null);
                if (r.Item1 != null) { Interlocked.Increment(ref Saves); Cache = r.Item1; }
                return r;
            },
            Api = (port, secret) => { lock (Api) { ApiPort = port; ApiSecret = secret; } return api ?? new FakeApi(); },
            PingMs = 100,
            WatchEvery = watchEvery ?? TimeSpan.FromSeconds(30),
            PingCap = pingCap ?? TimeSpan.FromSeconds(5),
            Decode = decode ?? (l => (FakeServer(l), null)),
            Resolve = host => { Interlocked.Increment(ref Resolves); return resolve?.Invoke(host); },
            Reach = (ip, port) => { Interlocked.Increment(ref Reaches); return reach?.Invoke(ip, port); },
            PrepareLists = (ids, proxy) =>
            {
                LastProxy = proxy;
                Interlocked.Increment(ref Prepares);
                lock (Prepared)
                {
                    Prepared.Add(ids.ToArray());
                    if (lists != null) return lists(ids);
                    var files = new Dictionary<string, string>();
                    foreach (var id in ids)
                    {
                        files[id] = Path.Combine(dir, id + ".srs");
                        File.WriteAllBytes(files[id], new byte[] { 0x53, 0x52, 0x53 });
                    }
                    return new ListsResult(files, Array.Empty<string>(), Array.Empty<string>());
                }
            },
            AfterStart = (port, secret, ct) =>
            {
                Interlocked.Increment(ref AfterStarts);
                lock (Api) { ApiPort = port; ApiSecret = secret; }
                return afterStart?.Invoke(port, secret, ct);
            }
        };
    }
}
