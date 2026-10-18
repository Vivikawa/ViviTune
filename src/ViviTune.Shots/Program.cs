using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ViviTune;
using ViviTune.Ui;

// Снимки окна без запуска sing-box: dotnet run --project src/ViviTune.Shots -- <outDir>
var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("artifacts", "shots"));
Directory.CreateDirectory(outDir);

// Без desktop lifetime: App не создаёт Session и Runner.
AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

var window = new MainWindow(Demo()) { Width = 900, Height = 600 };
window.Show();
foreach (var (page, file) in new[] { (NavPage.Home, "home.png"), (NavPage.Apps, "apps.png"), (NavPage.Log, "log.png"), (NavPage.Settings, "settings.png") })
{
    window.ShowPage(page);
    for (int i = 0; i < 3; i++) // разметка, затем отложенные действия страниц (прокрутка лога вниз)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
    var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("кадр не отрисован: " + file);
    var path = Path.Combine(outDir, file);
    frame.Save(path, PngBitmapEncoderOptions.Default);
    Console.WriteLine(path);
}
window.Close();
return 0;

static ViewState Demo()
{
    var s = new ViewState { Ip = "192.168.1.1", Port = 1818 };
    s.Apps.Add(new AppRow { Path = @"C:\Users\me\AppData\Local\Discord\app-1.0.9200\Discord.exe", FileName = "Discord.exe", Name = "Discord", Enabled = true });
    s.Apps.Add(new AppRow { Path = @"C:\Program Files\Google\Chrome\Application\chrome.exe", FileName = "chrome.exe", Name = "Google Chrome", Enabled = false });
    for (int i = 0; i < 60; i++) s.PushSpeed(new Speed(1.2e6 * (0.5 + 0.5 * Math.Sin(i / 5.0)), 8.4e6 * (0.5 + 0.5 * Math.Cos(i / 7.0))));
    for (int i = 0; i < 6; i++)
    {
        s.AddLog($"+0300 2026-10-18 21:{14 + i:00}:03 WARN [{3810 + i} 12ms] inbound/tun[tun-in]: process connection from 172.19.0.1:{51200 + i} to 162.159.135.232:443: connection reset by peer");
        s.AddLog($"+0300 2026-10-18 21:{14 + i:00}:09 WARN [{3820 + i} 4ms] dns: exchange failed for discord.gg. IN A: context deadline exceeded");
        s.AddLog("Перезапуск sing-box: список приложений изменён");
        s.AddLog("Перезапустите выбранные приложения: уже открытые соединения остаются прямыми.");
    }
    s.AddLog("ВНИМАНИЕ: UDP через этот прокси не работает: игры и голосовая связь могут не заработать.");
    s.AddLog("Последняя строка лога: прокрутка должна держаться здесь");
    return s;
}
