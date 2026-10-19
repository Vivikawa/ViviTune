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
        SessionTests();
        CatalogTests();
        ViewStateTests();
        AppListTests();
        return failures == 0 ? 0 : 1;
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
        var first = v.SyncApps(new List<AppEntry> { new() { Path = @"C:\Apps\Discord.exe" }, new() { Path = @"C:\Apps\chrome.exe", Enabled = false } });
        var discord = v.Apps[0];
        Check(first.Count == 2 && v.Apps.Count == 2 && discord.Name == "Discord" && discord.FileName == "Discord.exe" && discord.Enabled
            && !v.Apps[1].Enabled, "ViewState.SyncApps: строки по списку (имя из имени файла)");
        var added = v.SyncApps(new List<AppEntry> { new() { Path = @"c:\apps\DISCORD.exe", Enabled = false }, new() { Path = @"C:\Apps\Steam.exe" } });
        Check(added.Count == 1 && added[0].Path == @"C:\Apps\Steam.exe" && v.Apps.Count == 2 && ReferenceEquals(v.Apps[0], discord)
            && !discord.Enabled && v.Apps[1] == added[0], "ViewState.SyncApps: лишние убраны, новые вставлены, старые строки сохранены");
        v.SyncApps(new List<AppEntry> { new() { Path = @"C:\a.exe" }, new() { Path = @"C:\A.EXE" } }); // дубль из старого settings.json
        v.SyncApps(new List<AppEntry> { new() { Path = @"C:\a.exe", Enabled = false }, new() { Path = @"C:\A.EXE", Enabled = false } });
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
        v.SetProxyRequested += (_, _) => proxyEvents++;
        v.RequestSetProxy("10.0.0.1", 1080); // поля страницы заполнены теми же значениями (старт)
        v.RequestSetProxy("10.0.0.2", 1080);
        Check(proxyEvents == 1, "ViewState: RequestSetProxy без изменений не поднимает событие (settings.json не пишется при старте)");

        Check(ViewState.Detail(new TimeSpan(0, 42, 17), true, 2) == "00:42:17 · TCP + UDP · 2 прилож.", "ViewState.Detail: время, TCP + UDP, приложения");
        Check(ViewState.Detail(TimeSpan.FromSeconds(5), false, 1) == "00:00:05 · только TCP · 1 прилож.", "ViewState.Detail: без UDP -> только TCP");
        Check(ViewState.Detail(TimeSpan.FromSeconds(5), null, 1) == "00:00:05 · 1 прилож.", "ViewState.Detail: UDP неизвестен -> без протоколов");
        Check(ViewState.Detail(TimeSpan.FromSeconds(-3), true, 1) == "00:00:00 · TCP + UDP · 1 прилож.", "ViewState.Detail: отрицательное время -> 00:00:00");

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
                      ss.Error == null && ss.ConnectedSince != null, "Session: успешное подключение");
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
