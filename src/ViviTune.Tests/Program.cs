using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
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
        return failures == 0 ? 0 : 1;
    }

    static void SettingsTests()
    {
        var s = new Settings
        {
            Ip = "192.168.1.1", Port = 1818,
            Apps = new List<string> { @"C:\Program Files\Chrome\chrome.exe", @"D:\x\CHROME.EXE", @"C:\Игра\Game.exe", @"C:\bin\sing-box.exe" }
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
    }

    static IDictionary<string, object> Cfg(Settings s)
    {
        return (IDictionary<string, object>)new JavaScriptSerializer().DeserializeObject(ConfigBuilder.Build(s));
    }

    static IDictionary<string, object> Obj(object o) { return (IDictionary<string, object>)o; }

    static int IndexOfRule(IList rules, string key)
    {
        for (int i = 0; i < rules.Count; i++)
            if (Obj(rules[i]).ContainsKey(key)) return i;
        return -1;
    }

    static void ConfigTests()
    {
        var s = new Settings { Ip = "192.168.1.1", Port = 1818, Apps = new List<string> { @"C:\a b\chrome.exe", @"C:\Игра\Игра.exe" } };
        var c = Cfg(s);
        var route = Obj(c["route"]);
        var rules = (IList)route["rules"];

        Check((string)route["final"] == "direct", "route.final = direct");

        int pi = IndexOfRule(rules, "process_name");
        var pr = Obj(rules[pi]);
        var pn = (IList)pr["process_name"];
        Check(pn.Count == 2 && (string)pn[0] == "chrome.exe" && (string)pn[1] == "Игра.exe" && (string)pr["outbound"] == "proxy",
            "правило process_name -> proxy (кириллица и пробелы в пути)");

        int xi = IndexOfRule(rules, "ip_cidr");
        var xr = Obj(rules[xi]);
        Check((string)((IList)xr["ip_cidr"])[0] == "192.168.1.1/32" && (string)xr["outbound"] == "direct" && xi < pi,
            "адрес прокси идёт напрямую и раньше правила приложений");

        Check(IndexOfRule(rules, "protocol") >= 0, "есть hijack-dns");

        var dr = Obj(((IList)Obj(c["dns"])["rules"])[0]);
        Check((string)dr["server"] == "proxy-dns" && ((IList)dr["process_name"]).Count == 2, "DNS выбранных приложений идёт через proxy-dns");

        bool socksOk = false;
        foreach (var o in (IList)c["outbounds"])
        {
            var ob = Obj(o);
            if ((string)ob["type"] == "socks") socksOk = (string)ob["server"] == "192.168.1.1" && (int)ob["server_port"] == 1818 && (string)ob["tag"] == "proxy";
        }
        Check(socksOk, "socks outbound с адресом из настроек");

        var v6 = Cfg(new Settings { Ip = "::1", Port = 1, Apps = s.Apps });
        var v6rules = (IList)Obj(v6["route"])["rules"];
        Check((string)((IList)Obj(v6rules[IndexOfRule(v6rules, "ip_cidr")])["ip_cidr"])[0] == "::1/128", "IPv6-прокси исключается как /128");

        bool threw = false;
        try { ConfigBuilder.Build(new Settings()); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "Build: пустой список приложений -> исключение (иначе правило без условий матчит всё)");
    }
}
