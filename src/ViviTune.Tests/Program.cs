using System.Net;
using System.Net.Sockets;
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

    static int Main()
    {
        SettingsTests();
        ConfigTests();
        ProbeTests();
        TrafficTests();
        EvaluateTests();
        SessionTests();
        CatalogTests();
        ViewStateTests();
        AppListTests();
        VlessLinkTests();
        SettingsV4Tests();
        CatalogsTests();
        VlessConfigTests();
        ListStoreTests();
        VlessProbeTests();
        ViewStateVlessTests();
        return failures == 0 ? 0 : 1;
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
                var rv = ListStore.PrepareVia(dir, new[] { "fresh" }, sb, "", IPAddress.Loopback, now);
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
                var newer = new byte[] { 0x53, 0x52, 0x53, 4, 1 };
                Seed("vs", true);
                var r1 = ListStore.Prepare(dir, new[] { "vs" }, id => newer, now);
                var r2 = ListStore.Prepare(dir, new[] { "vn" }, id => newer, now);
                Check(r1.Stale.Contains("vs") && File.ReadAllBytes(P("vs")).SequenceEqual(old) &&
                      r2.Failed.Contains("vn") && !File.Exists(P("vn")), "ListStore: версия SRS новее 3 отвергается");
            }
            {
                var r = ListStore.Prepare(dir, new[] { "a", "b", "c" }, id => good, now);
                Check(r.Files.Count == 3 && r.Failed.Count == 0 && File.Exists(P("a")) && File.Exists(P("b")) && File.Exists(P("c")), "ListStore: три списка сразу");
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static void VlessConfigTests()
    {
        // Эталон SOCKS5 из 2.1: режим SOCKS5 не меняется ни на байт
        const string Golden = @"{""log"":{""level"":""warn"",""timestamp"":true},""experimental"":{""clash_api"":{""external_controller"":""127.0.0.1:9090"",""secret"":""s""}},""dns"":{""servers"":[{""type"":""local"",""tag"":""local""},{""type"":""tcp"",""tag"":""proxy-dns"",""server"":""1.1.1.1"",""detour"":""proxy""}],""rules"":[{""process_name"":[""chrome.exe"",""Игра.exe""],""server"":""proxy-dns""}]},""inbounds"":[{""type"":""tun"",""tag"":""tun-in"",""address"":[""172.19.0.1/30"",""fdfe:dcba:9876::1/126""],""auto_route"":true,""strict_route"":false}],""outbounds"":[{""type"":""socks"",""tag"":""proxy"",""server"":""192.168.1.1"",""server_port"":1818,""version"":""5""},{""type"":""direct"",""tag"":""direct""},{""type"":""socks"",""tag"":""bypass"",""server"":""192.168.1.1"",""server_port"":1819,""version"":""5""}],""route"":{""rules"":[{""action"":""sniff""},{""protocol"":""dns"",""action"":""hijack-dns""},{""ip_cidr"":[""192.168.1.1/32""],""outbound"":""direct""},{""process_name"":[""chrome.exe"",""Игра.exe""],""outbound"":""proxy""},{""process_name"":[""qbittorrent.exe""],""outbound"":""bypass""}],""final"":""direct"",""auto_detect_interface"":true,""default_domain_resolver"":""local""}}";
        var gs = new Settings { Ip = "192.168.1.1", Port = 1818, DirectPort = 1819, Apps = A(@"C:\a b\chrome.exe", @"C:\Игра\Игра.exe"), DirectApps = A(@"C:\q\qbittorrent.exe") };
        var gj = ConfigBuilder.Build(gs, 9090, "s");
        Check(gj == Golden, "SOCKS5: конфиг байт в байт как в 2.1");
        var gc = JsonNode.Parse(gj)!.AsObject();
        Check(Obj(Arr(gc["inbounds"])[0])["strict_route"]!.GetValue<bool>() == false && !gj.Contains("vless") && !gj.Contains("rule_set"), "SOCKS5: strict_route false, нет vless и rule_set");

        const string U = "3f1c0a52-0000-4000-8000-000000000001";
        var reality = $"vless://{U}@nl.example.com:443?security=reality&sni=www.microsoft.com&pbk=PUBKEY&sid=ab12&type=tcp&flow=xtls-rprx-vision";
        var wsLink = $"vless://{U}@1.2.3.4:8443?security=tls&type=ws&host=cdn.example.com&path=%2Fws%3Fed%3D2048&fp=firefox&alpn=h2,http/1.1";
        var grpcLink = $"vless://{U}@nl.example.com:8920?security=reality&sni=www.microsoft.com&pbk=PUBKEY&sid=ab12&type=grpc&serviceName=svc&fp=randomized";
        var srv = IPAddress.Parse("203.0.113.5");
        var files = new Dictionary<string, string> { ["youtube"] = @"C:\l\youtube.srs", ["russia_inside"] = @"C:\l\russia_inside.srs" };
        JsonObject V(string link, Dictionary<string, string> f, params string[] apps)
        {
            var st = new Settings { Mode = ProxyMode.Vless, VlessLink = link, Apps = A(apps), Lists = f.Keys.ToList() };
            return JsonNode.Parse(ConfigBuilder.BuildVless(st, 9090, "t", srv, f))!.AsObject();
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
        Check(rs.Count == 2 && Str(Obj(rs[0])["tag"]) == "russia_inside" && Str(Obj(rs[1])["tag"]) == "youtube"
            && Str(Obj(rs[0])["type"]) == "local" && Str(Obj(rs[0])["format"]) == "binary" && Str(Obj(rs[1])["path"]) == @"C:\l\youtube.srs", "VLESS: route.rule_set в порядке каталога");
        var rules = Arr(Obj(c["route"])["rules"]);
        int iSniff = IndexOfRule(rules, "action"), iDns = IndexOfRule(rules, "protocol"), iIp = IndexOfRule(rules, "ip_cidr"), iPn = IndexOfRule(rules, "process_name"), iRs = IndexOfRule(rules, "rule_set");
        Check(iSniff == 0 && iDns == 1 && iIp == 2 && iPn == 3 && iRs == 4, "VLESS: порядок правил sniff, hijack-dns, ip_cidr, process_name, rule_set");
        Check(Str(Arr(Obj(rules[iIp])["ip_cidr"])[0]) == "203.0.113.5/32" && Str(Obj(rules[iIp])["outbound"]) == "direct"
            && Str(Obj(rules[iPn])["outbound"]) == "proxy" && Str(Arr(Obj(rules[iRs])["rule_set"])[0]) == "russia_inside" && Str(Obj(rules[iRs])["outbound"]) == "proxy", "VLESS: содержимое правил");
        var rt = Obj(c["route"]);
        Check(Str(rt["final"]) == "direct" && rt["auto_detect_interface"]!.GetValue<bool>() && Str(rt["default_domain_resolver"]) == "dns", "VLESS: final direct, default_domain_resolver dns");
        Check(Obj(Arr(c["inbounds"])[0])["strict_route"]!.GetValue<bool>(), "VLESS: strict_route true");
        Check(Obj(Arr(c["inbounds"])[0])["mtu"]?.GetValue<int>() == 9000, "VLESS: TUN mtu 9000, как у Hiddify");

        var w = V(wsLink, files, @"C:\a\chrome.exe");
        var wo = Obj(Arr(w["outbounds"])[0]);
        var tr = Obj(wo["transport"]);
        Check(Str(tr["type"]) == "ws" && Str(tr["path"]) == "/ws?ed=2048" && Str(Obj(tr["headers"])["Host"]) == "cdn.example.com", "VLESS: ws transport");
        var wt = Obj(wo["tls"]);
        Check(Arr(wt["alpn"]).Select(x => Str(x)).SequenceEqual(new[] { "h2", "http/1.1" }) && !wt.ContainsKey("reality") && !wt.ContainsKey("insecure") && !wo.ContainsKey("flow"), "VLESS: tls+ws, alpn, нет reality");

        var plain = V($"vless://{U}@h.example.com:80", files, @"C:\a\chrome.exe");
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

        var onlyLists = Arr(Obj(V(reality, files)["route"])["rules"]);
        Check(IndexOfRule(onlyLists, "process_name") < 0 && IndexOfRule(onlyLists, "rule_set") >= 0, "VLESS: только списки, нет process_name");
        var onlyApp = V(reality, new Dictionary<string, string>(), @"C:\a\chrome.exe");
        Check(IndexOfRule(Arr(Obj(onlyApp["route"])["rules"]), "rule_set") < 0 && !Obj(onlyApp["route"]).ContainsKey("rule_set"), "VLESS: только программа, нет rule_set");

        bool threw = false;
        try { V(reality, new Dictionary<string, string>()); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "VLESS: нет программ и списков -> исключение");

        var fe = JsonNode.Parse(ConfigBuilder.BuildFetch(reality, srv, 18080))!.AsObject();
        var fin = Obj(Arr(fe["inbounds"])[0]);
        Check(Str(fin["type"]) == "mixed" && Str(fin["listen"]) == "127.0.0.1" && (int)fin["listen_port"]! == 18080
            && Arr(fe["outbounds"]).Count == 1 && Arr(fe["outbounds"])[0]!.ToJsonString() == ob.ToJsonString()
            && !fe.ContainsKey("dns") && !fe.ContainsKey("route"), "VLESS fetch: только mixed на 127.0.0.1 и тот же outbound, без TUN");

        SingBoxCheck(reality, wsLink, grpcLink, srv);
    }

    // Реальный sing-box check: конфиги из BuildVless принимаются 1.12.12
    static void SingBoxCheck(string reality, string wsLink, string grpcLink, IPAddress srv)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "sing-box.exe");
        if (!File.Exists(exe))
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var cand = Path.Combine(dir.FullName, "src", "ViviTune", "third_party", "sing-box.exe");
                if (File.Exists(cand)) { exe = cand; break; }
            }
        if (!File.Exists(exe)) { Check(false, "sing-box.exe рядом с тестами"); return; }

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
            foreach (var (name, link) in new[] { ("reality", reality.Replace("PUBKEY", "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0")), ("ws", wsLink), ("grpc", grpcLink.Replace("PUBKEY", "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0")) })
            {
                var st = new Settings { Mode = ProxyMode.Vless, VlessLink = link, Apps = A(@"C:\a\chrome.exe") };
                var cfg = Path.Combine(tmp, name + ".json");
                File.WriteAllText(cfg, ConfigBuilder.BuildVless(st, 9090, "t", srv, f));
                Check(Run("check", "-c", cfg) == 0, "sing-box check: " + name);
                File.WriteAllText(cfg, ConfigBuilder.BuildFetch(link, srv, 18080));
                Check(Run("check", "-c", cfg) == 0, "sing-box check: fetch " + name);
            }
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

        Check(new Settings { Mode = ProxyMode.Vless }.Validate() == "Вставьте VLESS-ссылку в настройках", "Validate Vless: пустая ссылка");
        Check(new Settings { Mode = ProxyMode.Vless, VlessLink = Link }.Validate() == "Добавьте приложение или включите список сайтов", "Validate Vless: нет программ и списков");
        Check(new Settings { Mode = ProxyMode.Vless, VlessLink = Link, Lists = new List<string> { "youtube" } }.Validate() == null, "Validate Vless: только список");
        Check(new Settings { VlessLink = "мусор", Apps = A(@"C:\a\x.exe") }.Validate() == null, "Validate Socks5: ссылку не смотрит");

        var c = s.Clone();
        c.Lists.Add("discord");
        Check(c.Mode == ProxyMode.Vless && c.VlessLink == Link && s.Lists.Count == 2, "Clone: новые поля, список копии независим");
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

    static void VlessLinkTests()
    {
        const string U = "3f1c0a52-0000-4000-8000-000000000001";
        var r = VlessServer.Parse($"  vless://{U}@nl.example.com:443?security=reality&sni=www.microsoft.com&pbk=PUBKEY&sid=ab12&type=tcp&flow=xtls-rprx-vision#%F0%9F%87%B3%F0%9F%87%B1%20Amsterdam\r\n", out var e);
        Check(r != null && e == null, "Vless: Reality-ссылка разбирается без ошибки");
        Check(r?.Uuid == U && r.Host == "nl.example.com" && r.Port == 443, "Vless: uuid, хост, порт");
        Check(r?.Security == "reality" && r.Transport == "tcp" && r.Flow == "xtls-rprx-vision", "Vless: security, transport, flow");
        Check(r?.Sni == "www.microsoft.com" && r.Fingerprint == "chrome" && r.PublicKey == "PUBKEY" && r.ShortId == "ab12", "Vless: sni, fp по умолчанию chrome, pbk, sid");
        Check(r?.Name == "🇳🇱 Amsterdam", "Vless: имя из фрагмента");
        Check(r?.Summary == "nl.example.com:443 · Reality · TCP · Vision", "Vless: Summary Reality");

        var w = VlessServer.Parse($"vless://{U}@1.2.3.4:8443?security=tls&type=ws&host=cdn.example.com&path=%2Fws%3Fed%3D2048&fp=firefox&alpn=h2,http/1.1", out _);
        Check(w?.Sni == "cdn.example.com" && w.HostHeader == "cdn.example.com", "Vless: sni берётся из host");
        Check(w?.Path == "/ws?ed=2048" && w.Fingerprint == "firefox", "Vless: path раскодирован, fp");
        Check(w != null && w.Alpn.SequenceEqual(new[] { "h2", "http/1.1" }), "Vless: alpn списком");
        Check(w?.Summary == "1.2.3.4:8443 · TLS · WebSocket", "Vless: Summary TLS WebSocket");

        var g = VlessServer.Parse($"vless://{U}@h.example.com:443?security=tls&type=grpc&serviceName=gun&sni=s.example.com", out _);
        Check(g?.Transport == "grpc" && g.ServiceName == "gun" && g.Sni == "s.example.com", "Vless: grpc и serviceName");
        Check(VlessServer.Parse($"vless://{U}@h.example.com:443?type=httpupgrade&host=a.com&path=/u", out _)?.Transport == "httpupgrade", "Vless: httpupgrade");
        Check(VlessServer.Parse($"vless://{U}@h.example.com:443?type=http&host=a.com&path=/p", out _)?.Transport == "http", "Vless: http");
        Check(VlessServer.Parse($"vless://{U}@h.example.com:443?type=h2", out _)?.Transport == "http", "Vless: h2 -> http");
        Check(VlessServer.Parse($"vless://{U}@h.example.com:443?type=raw", out _)?.Transport == "tcp", "Vless: raw -> tcp");

        var n = VlessServer.Parse($"vless://{U}@h.example.com:80", out _);
        Check(n?.Security == "none" && n.Sni == null && n.Fingerprint == null && n.Name == "h.example.com", "Vless: без TLS, sni/fp null, имя = хост");
        Check(n?.Summary == "h.example.com:80 · без TLS · TCP", "Vless: Summary без TLS");

        var v6 = VlessServer.Parse($"vless://{U}@[2001:db8::1]:443?security=tls&sni=a.com", out _);
        Check(v6?.Host == "2001:db8::1" && v6.Summary == "[2001:db8::1]:443 · TLS · TCP", "Vless: IPv6 без скобок в Host, со скобками в Summary");

        void Err(string? link, string want, string name)
        {
            var x = VlessServer.Parse(link, out var er);
            Check(x == null && er == want, "Vless: ошибка - " + name);
        }
        Err("", "Вставьте VLESS-ссылку в настройках", "пусто");
        Err(null, "Вставьте VLESS-ссылку в настройках", "null");
        Err("https://x", "Ссылка должна начинаться с vless://", "схема");
        Err("vless://@h:443", "В ссылке нет UUID", "нет UUID");
        Err($"vless://{U}@h.example.com?security=tls", "В ссылке нет порта сервера", "нет порта");
        Err($"vless://{U}@h.example.com:443?type=xhttp", "XHTTP не поддерживается: sing-box 1.12 его не умеет", "xhttp");
        Err($"vless://{U}@h.example.com:443?type=kcp", "Неизвестный тип транспорта: kcp", "kcp");
        Err($"vless://{U}@h.example.com:443?security=reality", "Для Reality в ссылке нужен pbk", "reality без pbk");
        Err($"vless://{U}@h.example.com:443?flow=xtls-rprx-direct", "Неизвестный flow: xtls-rprx-direct", "flow");
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
        v.SetVless("vless://11111111-1111-1111-1111-111111111111@example.com:443?security=tls&type=tcp#n");
        Check(v.VlessHost == "example.com" && v.VlessSummary.StartsWith("Сервер: ") && !v.VlessLinkError && v.ProxyLabel == "VLESS · example.com", "ViewState.SetVless: верная ссылка");
        v.SetVless("");
        Check(v.VlessHost == "" && v.VlessSummary == "" && !v.VlessLinkError, "ViewState.SetVless: пустая ссылка");
        v.SetVless("http://x");
        Check(v.VlessHost == "" && v.VlessSummary == "Ссылка должна начинаться с vless://" && v.VlessLinkError, "ViewState.SetVless: битая ссылка");

        Check(ViewState.DetailVless(new TimeSpan(0, 12, 3), 2, 3) == "00:12:03 · 2 приложения · 3 списка", "DetailVless: всё");
        Check(ViewState.DetailVless(new TimeSpan(0, 12, 3), 1, 0) == "00:12:03 · 1 приложение", "DetailVless: без списков");
        Check(ViewState.DetailVless(new TimeSpan(0, 12, 3), 0, 5) == "00:12:03 · 5 списков", "DetailVless: без приложений");

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

    static JsonObject? AppRule(JsonArray rules, string outbound) =>
        rules.Select(Obj).FirstOrDefault(r => r.ContainsKey("process_name") && Str(r["outbound"]) == outbound);

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
        var two = new Settings { Ip = "192.168.1.1", Port = 1818, DirectPort = 1819,
            Apps = A(@"C:\chrome.exe"), DirectApps = A(@"C:\q\qbittorrent.exe", @"D:\CHROME.EXE") };
        JsonObject B(Exits? live) => JsonNode.Parse(ConfigBuilder.Build(two, 9090, "t", live))!.AsObject();
        JsonArray R(JsonObject c2) => Arr(Obj(c2["route"])["rules"]);
        bool HasOut(JsonObject c2, string tag) => Arr(c2["outbounds"]).Any(o => Str(Obj(o)["tag"]) == tag);

        var all = B(null);
        var bo = Arr(all["outbounds"]).Select(Obj).First(o => Str(o["tag"]) == "bypass");
        Check(Str(bo["type"]) == "socks" && Str(bo["server"]) == "192.168.1.1" && (int)bo["server_port"]! == 1819 && Str(bo["version"]) == "5",
            "bypass: socks outbound на IP роутера и порт «Мимо Podkop»");
        Check(Arr(AppRule(R(all), "bypass")!["process_name"]).Select(x => Str(x)).SequenceEqual(new[] { "qbittorrent.exe" })
            && Arr(AppRule(R(all), "proxy")!["process_name"]).Select(x => Str(x)).SequenceEqual(new[] { "chrome.exe" }),
            "bypass: правила по спискам, имя из обоих списков только в proxy");
        Check(IndexOfRule(R(all), "ip_cidr") < R(all).IndexOf(AppRule(R(all), "bypass")), "bypass: адрес роутера напрямую раньше правил приложений");

        var px = B(new Exits(true, false));
        Check(!HasOut(px, "bypass") && AppRule(R(px), "bypass") == null && AppRule(R(px), "proxy") != null,
            "жив только прокси: нет outbound bypass и его правила");

        var bp = B(new Exits(false, true));
        Check(AppRule(R(bp), "proxy") == null && AppRule(R(bp), "bypass") != null && Arr(Obj(bp["dns"])["rules"]).Count == 0,
            "жив только bypass: нет правила proxy и DNS-правила proxy-dns");
        Check(HasOut(bp, "proxy"), "жив только bypass: outbound proxy остаётся (на него ссылается detour proxy-dns)");

        var onlyDirect = new Settings { Port = 1818, DirectPort = 1819, DirectApps = A(@"C:\q\qbittorrent.exe") };
        var od = JsonNode.Parse(ConfigBuilder.Build(onlyDirect, 9090, "t"))!.AsObject();
        Check(AppRule(R(od), "proxy") == null && Arr(Obj(od["dns"])["rules"]).Count == 0 && AppRule(R(od), "bypass") != null,
            "нет программ «Через прокси»: нет правил с пустым process_name");

        var noPort = new Settings { Port = 1818, Apps = A(@"C:\chrome.exe"), DirectApps = A(@"C:\q\qbittorrent.exe") };
        Check(!HasOut(JsonNode.Parse(ConfigBuilder.Build(noPort, 9090, "t"))!.AsObject(), "bypass"), "DirectPort не задан: bypass нет");

        bool none = false;
        try { ConfigBuilder.Build(two, 9090, "t", new Exits(false, false)); } catch (InvalidOperationException) { none = true; }
        Check(none, "Build: ни одного живого выхода с программами -> исключение");
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

        Check(VlessProbe.AfterStart(Fake(Ok), CancellationToken.None, ready) == null, "VlessProbe: всё хорошо -> null");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var e = VlessProbe.AfterStart(Fake(_ => null), CancellationToken.None, TimeSpan.FromMilliseconds(300));
        Check(e == "sing-box не запустился, подробности в логе" && sw.ElapsedMilliseconds < 1000, "VlessProbe: Clash API молчит -> не запустился, быстро");
        Check(VlessProbe.AfterStart(Fake(p => p.StartsWith("/proxies") ? (504, "") : Ok(p)), CancellationToken.None, ready) == "VLESS-сервер не отвечает", "VlessProbe: delay 504");
        const string dohErr = "DNS через VLESS-сервер не отвечает";
        Check(VlessProbe.AfterStart(Fake(p => p.StartsWith("/dns") ? (500, "{}") : Ok(p)), CancellationToken.None, ready) == dohErr, "VlessProbe: dns 500");
        Check(VlessProbe.AfterStart(Fake(p => p.StartsWith("/dns") ? (200, "{\"Status\":2}") : Ok(p)), CancellationToken.None, ready) == dohErr, "VlessProbe: dns без ответа");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        sw.Restart();
        Check(VlessProbe.AfterStart((p, c) => c.IsCancellationRequested ? null : Ok(p), cts.Token, ready) != null && sw.ElapsedMilliseconds < 500, "VlessProbe: отмена -> не null сразу");

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
    const string DnsDown = "DNS через VLESS-сервер не отвечает";

    // VLESS: reality на 203.0.113.5:443, одна программа и список YouTube
    static Settings Vless1() => new Settings { Mode = ProxyMode.Vless, VlessLink = Reality, Apps = A(@"C:\a\chrome.exe"), Lists = new List<string> { "youtube" } };

    static ListsResult NoFiles(params string[] failed) => new(new Dictionary<string, string>(), failed, Array.Empty<string>());

    // Теги route.rule_set конфига (пусто, если их нет).
    static List<string> CfgLists(string path)
    {
        var rs = Obj(JsonNode.Parse(File.ReadAllText(path))!["route"])["rule_set"];
        return rs == null ? new List<string>() : Arr(rs).Select(x => Str(Obj(x)["tag"])!).ToList();
    }

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
        v = Session.EvaluateVless(vs, srv, NoFiles("telegram", "youtube"));
        Check(v.Error == null && v.Live == new Exits(true, false) && v.UdpOk == null && v.Vless != null && v.Vless.Server.Equals(srv) && v.Vless.ListFiles.Count == 0
            && v.Warnings.SequenceEqual(new[] { "Список Telegram не загрузился", "Список YouTube не загрузился" }),
            "EvaluateVless: два Failed -> два предупреждения в порядке Failed");
        vs.Apps.Clear();
        v = Session.EvaluateVless(vs, srv, NoFiles("telegram", "youtube"));
        Check(v.Error == "Списки не загрузились" && v.Vless == null && v.Warnings.Count == 0, "EvaluateVless: нет программ и ни одного файла -> ошибка");
        var one = new Dictionary<string, string> { ["youtube"] = @"C:\l\youtube.srs" };
        v = Session.EvaluateVless(vs, srv, new ListsResult(one, new[] { "telegram" }, Array.Empty<string>()));
        Check(v.Error == null && v.Vless!.ListFiles.ContainsKey("youtube") && v.Warnings.SequenceEqual(new[] { "Список Telegram не загрузился" }),
            "EvaluateVless: без программ хватает одного загруженного списка");
    }

    static List<string> CfgApps(string path)
    {
        var rules = Arr(Obj(JsonNode.Parse(File.ReadAllText(path))!["route"])["rules"]);
        return Arr(Obj(rules[IndexOfRule(rules, "process_name")])["process_name"]).Select(x => Str(x)!).ToList();
    }

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
                var s = Two();
                var cfg = NewCfg();
                using var ss = new Session(s, fb, cfg, Up, Delay);
                ss.ConnectAsync().Wait(2000);
                s.Apps[1].Enabled = false;
                ss.AppsChanged();
                bool restarting = ss.State == ConnState.Restarting;
                s.Apps[1].Enabled = true;
                ss.AppsChanged();
                s.Apps.Add(new AppEntry { Path = @"C:\c\Discord.exe" });
                ss.AppsChanged();
                Thread.Sleep(300);
                var apps = CfgApps(cfg);
                Check(restarting && fb.Starts == 2 && fb.Stops == 1 && ss.State == ConnState.Connected,
                    "Session: три быстрых изменения -> один перезапуск");
                Check(apps.SequenceEqual(new[] { "chrome.exe", "Game.exe", "Discord.exe" }), "Session: конфиг перезапуска по последнему состоянию");
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
                ss.AppsChanged();
                Thread.Sleep(300);
                Check(fb.Starts == 2 && ss.State == ConnState.Connected && CfgApps(cfg).SequenceEqual(new[] { "chrome.exe" }),
                    "Session: после изменений без перезапуска следующий перезапуск по последнему состоянию");
            }
            {
                var fb = new FakeSingBox();
                var s = Two();
                using var ss = new Session(s, fb, NewCfg(), Up, Delay);
                ss.ConnectAsync().Wait(2000);
                s.Apps[1].Enabled = false;
                ss.AppsChanged();
                bool restarting = ss.State == ConnState.Restarting;
                s.Apps[1].Enabled = true; // вернули как было, пока ждали перезапуска
                ss.AppsChanged();
                Thread.Sleep(300);
                Check(restarting && ss.State == ConnState.Connected && fb.Starts == 1 && fb.Stops == 0 && ss.Error == null,
                    "Session: выключили и сразу включили обратно -> без перезапуска");
            }
            {
                var fb = new FakeSingBox();
                var s = Two();
                using var ss = new Session(s, fb, NewCfg(), Up, Delay);
                ss.ConnectAsync().Wait(2000);
                s.Apps[0].Enabled = false;
                ss.AppsChanged();
                bool stillUp = ss.State == ConnState.Restarting;
                s.Apps[1].Enabled = false;
                ss.AppsChanged();
                bool off = ss.State == ConnState.Disconnected;
                Thread.Sleep(150);
                Check(stillUp && off && ss.State == ConnState.Disconnected && fb.Starts == 1 && ss.Error == null,
                    "Session: выключено последнее приложение во время ожидания перезапуска");
            }
            {
                var fb = new FakeSingBox { ExitOnStop = true };
                var s = Two();
                using var ss = new Session(s, fb, NewCfg(), Up, Delay);
                ss.ConnectAsync().Wait(2000);
                s.Apps[0].Enabled = false;
                ss.AppsChanged();
                Thread.Sleep(300);
                bool afterRestart = ss.State == ConnState.Connected && ss.Error == null && fb.Starts == 2;
                ss.Disconnect();
                Check(afterRestart && ss.State == ConnState.Disconnected && ss.Error == null,
                    "Session: Exited при перезапуске и Disconnect не ошибка");
            }
            {
                var fb = new FakeSingBox();
                var s = Two();
                using var ss = new Session(s, fb, NewCfg(), Up, Delay);
                ss.ConnectAsync().Wait(2000);
                fb.LateExited(); // событие от прежнего процесса: текущий жив
                bool a = ss.State == ConnState.Connected && ss.Error == null;
                s.Apps[0].Enabled = false;
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
                s.Apps = A(@"C:\a\chrome.exe", @"C:\b\Game.exe");
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
                    && Arr(AppRule(rules, "bypass")!["process_name"]).Select(x => Str(x)).OrderBy(x => x).SequenceEqual(new[] { "chrome.exe", "qbittorrent.exe" }),
                    "Session: перенос программы в другой список во время работы -> перезапуск, она только в новом правиле");
            }
            {
                var fb = new FakeSingBox();
                var s = Two();
                int calls = 0;
                using var started = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                using var ss = new Session(s, fb, NewCfg(), (ip, port) =>
                {
                    int c = Interlocked.Increment(ref calls);
                    if (c != 2) return Up(ip, port); // 1 — первое подключение, 3+ — новое подключение
                    started.Set(); // 2 — проверка перезапуска: ждёт и сообщает «не отвечает»
                    release.Wait(2000);
                    return DownProbe(ip, port);
                }, Delay);
                ss.ConnectAsync().Wait(2000);
                s.Apps[1].Enabled = false;
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
                var s = Two();
                var up = true;
                using var ss = new Session(s, fb, NewCfg(), (ip, port) => Volatile.Read(ref up) ? Up(ip, port) : DownProbe(ip, port), Delay);
                ss.ConnectAsync().Wait(2000);
                Volatile.Write(ref up, false);
                s.Apps[1].Enabled = false;
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
                Check(ss.State == ConnState.Disconnected && ss.Error == "VLESS-сервер не найден: nl.example.com" && fv.Resolves == 1 && fv.Reaches == 0
                      && ss2.State == ConnState.Disconnected && ss2.Error == "VLESS-сервер не найден: nl.example.com" && fb.Starts == 0,
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
                var fb = new FakeSingBox();
                var fv = new FakeVless(root, reach: (_, _) => "таймаут подключения");
                using var ss = new Session(Vless1(), fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Disconnected && ss.Error == "VLESS-сервер не отвечает: таймаут подключения" && fb.Starts == 0 && fv.Prepares == 0,
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
                ss.AppsChanged();
                bool restarting = ss.State == ConnState.Restarting;
                Thread.Sleep(300);
                string[] last;
                lock (fv.Prepared) last = fv.Prepared[^1];
                Check(restarting && fb.Starts == 2 && ss.State == ConnState.Connected && CfgLists(cfg).SequenceEqual(new[] { "youtube", "telegram" })
                      && last.SequenceEqual(new[] { "youtube", "telegram" }) && fv.AfterStarts == 2,
                    "Session VLESS: список включён во время работы -> перезапуск, списки готовятся заново");
                lock (logs) Check(logs.Contains("Перезапуск sing-box: приложения или списки изменены"), "Session VLESS: строка перезапуска");
            }
            {
                var fb = new FakeSingBox();
                var s = Vless1();
                var fv = new FakeVless(root);
                using var ss = new Session(s, fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                s.Lists.Add("telegram");
                ss.AppsChanged();
                bool restarting = ss.State == ConnState.Restarting;
                s.Lists.Remove("telegram");
                ss.AppsChanged();
                Thread.Sleep(300);
                Check(restarting && ss.State == ConnState.Connected && fb.Starts == 1 && fb.Stops == 0 && fv.Prepares == 1,
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
                bool still = ss.State == ConnState.Restarting; // остался список: перезапуск, не отключение
                Thread.Sleep(300);
                s.Lists.Clear();
                ss.AppsChanged();
                Thread.Sleep(150);
                lock (logs)
                    Check(still && ss.State == ConnState.Disconnected && ss.Error == null && fb.Starts == 2 && !fb.Running
                          && logs.Contains("Отключено: нет включённых приложений и списков"),
                        "Session VLESS: убраны программа и все списки -> отключено, строка в логе");
            }
            {
                var fb = new FakeSingBox();
                var s = Vless1();
                s.VlessLink = "";
                var fv = new FakeVless(root);
                using var ss = new Session(s, fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                Check(ss.State == ConnState.Disconnected && ss.Error == "Вставьте VLESS-ссылку в настройках" && fb.Starts == 0 && fv.Reaches == 0,
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
                var fv = new FakeVless(root, afterStart: (_, _, ct) => { started.Set(); ct.WaitHandle.WaitOne(5000); return "VLESS-сервер не отвечает"; });
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
                int calls = 0;
                using var started = new ManualResetEventSlim();
                var fv = new FakeVless(root, afterStart: (_, _, ct) =>
                {
                    if (Interlocked.Increment(ref calls) == 1) return null;
                    started.Set(); // проверка после перезапуска висит
                    ct.WaitHandle.WaitOne(5000);
                    return "VLESS-сервер не отвечает";
                });
                using var ss = new Session(s, fb, NewCfg(), Up, Delay, fv.Deps);
                ss.ConnectAsync().Wait(2000);
                s.Lists.Add("telegram");
                ss.AppsChanged();
                bool inCheck = started.Wait(2000);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                ss.Disconnect();
                var took = sw.Elapsed;
                Thread.Sleep(100);
                Check(inCheck && took < TimeSpan.FromSeconds(1) && ss.State == ConnState.Disconnected && ss.Error == null && !fb.Running && fb.Starts == 2,
                    "Session VLESS: Disconnect во время проверки после перезапуска -> быстро, sing-box остановлен, без ошибки");
            }
            {
                var fb = new FakeSingBox();
                var s = Vless1();
                int calls = 0;
                var fv = new FakeVless(root, afterStart: (_, _, _) => Interlocked.Increment(ref calls) == 1 ? null : DnsDown);
                using var ss = new Session(s, fb, NewCfg(), Up, Delay, fv.Deps);
                var logs = new List<string>();
                ss.Log += l => { lock (logs) logs.Add(l); };
                ss.ConnectAsync().Wait(2000);
                s.Lists.Add("telegram");
                ss.AppsChanged();
                Thread.Sleep(300);
                lock (logs)
                    Check(ss.State == ConnState.Disconnected && ss.Error == DnsDown && !fb.Running && fb.Starts == 2 && logs.Contains(DnsDown),
                        "Session VLESS: DNS не ответил при перезапуске -> sing-box остановлен, ошибка");
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

// Подмена VlessDeps: считает вызовы, запоминает id списков и адрес Clash API. По умолчанию имя не разрешается,
// сервер отвечает, каждый список — существующий файл в dir, проверка после запуска проходит.
sealed class FakeVless
{
    public int Resolves, Reaches, Prepares, AfterStarts;
    public readonly List<string[]> Prepared = new();  // под lock (Prepared)
    public readonly object Api = new();               // под lock (Api) — ApiPort/ApiSecret
    public int ApiPort;
    public string? ApiSecret;
    public readonly VlessDeps Deps;

    public FakeVless(string dir, Func<string, IPAddress?>? resolve = null, Func<IPAddress, int, string?>? reach = null,
        Func<IReadOnlyList<string>, ListsResult>? lists = null, Func<int, string, CancellationToken, string?>? afterStart = null)
    {
        Deps = new VlessDeps
        {
            Resolve = host => { Interlocked.Increment(ref Resolves); return resolve?.Invoke(host); },
            Reach = (ip, port) => { Interlocked.Increment(ref Reaches); return reach?.Invoke(ip, port); },
            PrepareLists = (ids, _, _) =>
            {
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
