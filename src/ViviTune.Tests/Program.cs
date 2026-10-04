using System;
using System.Collections.Generic;
using System.IO;
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
}
