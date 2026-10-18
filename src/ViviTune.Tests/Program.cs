using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ViviTune;

static class Program
{
    static int failures;

    static void Check(bool ok, string name)
    {
        if (!ok) failures++;
        Console.WriteLine((ok ? "ok:   " : "FAIL: ") + name);
    }

    static int Main()
    {
        SettingsTests();
        ConfigTests();
        ProbeTests();
        TrafficTests();
        return failures == 0 ? 0 : 1;
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
        Check(v1.Ip == "10.0.0.1" && v1.Port == 1080 && v1.Apps.Count == 2 && v1.Apps[0].Path == @"C:\a\Discord.exe" && v1.Apps.All(a => a.Enabled),
            "Load: формат v1");

        var v2 = new Settings { Apps = A(@"C:\Игра\a.exe", @"C:\b.exe") };
        v2.Apps[1].Enabled = false;
        v2.Save(p);
        var v2b = Settings.Load(p);
        Check(v2b.Apps.Count == 2 && v2b.Apps[0].Path == @"C:\Игра\a.exe" && v2b.Apps[0].Enabled && !v2b.Apps[1].Enabled,
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
    }

    static JsonObject Cfg(Settings s) => JsonNode.Parse(ConfigBuilder.Build(s, 9090, "t"))!.AsObject();

    static JsonObject Obj(JsonNode? o) => o!.AsObject();

    static JsonArray Arr(JsonNode? o) => o!.AsArray();

    static string? Str(JsonNode? o) => (string?)o;

    static int IndexOfRule(JsonArray rules, string key)
    {
        for (int i = 0; i < rules.Count; i++)
            if (Obj(rules[i]).ContainsKey(key)) return i;
        return -1;
    }

    static void ConfigTests()
    {
        var s = new Settings { Ip = "192.168.1.1", Port = 1818, Apps = A(@"C:\a b\chrome.exe", @"C:\Игра\Игра.exe") };
        var c = Cfg(s);
        var route = Obj(c["route"]);
        var rules = Arr(route["rules"]);

        Check(Str(route["final"]) == "direct", "route.final = direct");

        int pi = IndexOfRule(rules, "process_name");
        var pr = Obj(rules[pi]);
        var pn = Arr(pr["process_name"]);
        Check(pn.Count == 2 && Str(pn[0]) == "chrome.exe" && Str(pn[1]) == "Игра.exe" && Str(pr["outbound"]) == "proxy",
            "правило process_name -> proxy (кириллица и пробелы в пути)");

        int xi = IndexOfRule(rules, "ip_cidr");
        var xr = Obj(rules[xi]);
        Check(Str(Arr(xr["ip_cidr"])[0]) == "192.168.1.1/32" && Str(xr["outbound"]) == "direct" && xi < pi,
            "адрес прокси идёт напрямую и раньше правила приложений");

        Check(IndexOfRule(rules, "protocol") >= 0, "есть hijack-dns");

        var dr = Obj(Arr(Obj(c["dns"])["rules"])[0]);
        Check(Str(dr["server"]) == "proxy-dns" && Arr(dr["process_name"]).Count == 2, "DNS выбранных приложений идёт через proxy-dns");

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

        var ca = Obj(Obj(JsonNode.Parse(ConfigBuilder.Build(s, 51234, "abc"))!["experimental"])["clash_api"]);
        Check(Str(ca["external_controller"]) == "127.0.0.1:51234" && Str(ca["secret"]) == "abc", "clash_api с переданными портом и секретом");

        bool threw = false;
        try { ConfigBuilder.Build(new Settings(), 9090, "t"); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "Build: пустой список приложений -> исключение (иначе правило без условий матчит всё)");
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
        Check(s1 == new Speed(200, 4000), "Delta: прирост только proxy");
        var s2 = TrafficMath.Delta(Prev(), new[] { new ConnSample("c", 10, 20, true) }, 2);
        Check(s2 == new Speed(5, 10), "Delta: новое соединение от нуля");
        var s3 = TrafficMath.Delta(Prev(new ConnSample("a", 500, 500, true)), new[] { new ConnSample("a", 100, 100, true) }, 1);
        Check(s3 == new Speed(0, 0), "Delta: счётчик уменьшился");
        Check(TrafficMath.Delta(Prev(), new[] { new ConnSample("c", 10, 20, true) }, 0) == new Speed(0, 0), "Delta: seconds<=0");

        Check(Format.Speed(0) == "0 Б/с" && Format.Speed(1536) == "1.5 КБ/с" && Format.Speed(8808038) == "8.4 МБ/с", "Format.Speed");
        var old = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
        Check(Format.Speed(1536) == "1.5 КБ/с", "Format.Speed: не зависит от культуры");
        System.Globalization.CultureInfo.CurrentCulture = old;
        Check(Format.Uptime(new TimeSpan(0, 42, 17)) == "00:42:17" && Format.Uptime(TimeSpan.FromHours(100)) == "100:00:00", "Format.Uptime");

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
        cts.Cancel();
        l2.Stop();
        Check(maxUp > 0, "TrafficMeter: фейковый Clash API даёт UpBps > 0");
        Check(headers.Count > 0 && headers.All(h => h.Contains("Authorization: Bearer s") && h.StartsWith("GET /connections")), "TrafficMeter: заголовок Authorization");
    }
}
