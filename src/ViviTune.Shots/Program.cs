using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ViviTune;
using ViviTune.Ui;
using ViviTune.Windows;

// Снимки окна без запуска sing-box: dotnet run --project src/ViviTune.Shots -- <outDir>
var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("artifacts", "shots"));
Directory.CreateDirectory(outDir);

// Без desktop lifetime: App не создаёт Session и Runner.
AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

var s = Demo();
var window = new MainWindow(s) { Width = 900, Height = 600 };
window.Show();
int enabled = s.Apps.Count(a => a.Enabled);

// Главная в разных состояниях (DetailText и WarningText в приложении ставит App по Session)
Shot(NavPage.Home, "home-disconnected.png");
s.State = ConnState.Checking;
Shot(NavPage.Home, "home-checking.png");
s.State = ConnState.Connected;
s.DetailText = ViewState.Detail(new TimeSpan(0, 42, 17), true, enabled);
const double MiB = 1024 * 1024;
for (int i = 0; i < 60; i++) // демо-история: волны и всплеск отдачи; последнее значение как в макете (1.2 и 8.4 МБ/с)
    s.PushSpeed(new Speed(1.2 * MiB * (1 + 0.45 * Math.Sin((i - 59) / 4.5)) + (i == 40 ? 1.6 * MiB : 0),
                          8.4 * MiB * (1 + 0.5 * Math.Sin((i - 59) / 6.0))));
Shot(NavPage.Home, "home-connected.png");
s.DetailText = ViewState.Detail(new TimeSpan(0, 3, 5), false, enabled);
s.WarningText = "UDP через этот прокси не работает: игры и голосовая связь могут не заработать.";
Shot(NavPage.Home, "home-connected-noudp.png");
s.State = ConnState.Disconnected; // как Session: ошибка, затем нулевая скорость
s.DetailText = "";
s.WarningText = null;
s.ErrorText = "Прокси недоступен: таймаут подключения";
s.PushSpeed(new Speed(0, 0));
Shot(NavPage.Home, "home-error.png");
s.ErrorText = null;

Shot(NavPage.Apps, "apps.png");
var appsPage = window.GetVisualDescendants().OfType<AppsPage>().Single();
appsPage.OpenAddPanel().Wait(); // список и иконки готовит фоновый поток; Shot применит его Post
Shot(NavPage.Apps, "apps-add.png");
appsPage.FindControl<TextBox>("SearchBox")!.Text = "st"; // поиск по имени без учёта регистра
Shot(NavPage.Apps, "apps-add-search.png");
Shot(NavPage.Log, "log.png"); // уход со страницы закрывает панель
Shot(NavPage.Settings, "settings.png");
s.Apps.Clear();
Shot(NavPage.Apps, "apps-empty.png");
window.Close();
return 0;

void Shot(NavPage page, string file)
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

static ViewState Demo()
{
    // «Запущены сейчас»: Discord (в другом регистре) уже добавлен и не должен попасть в панель;
    // у Проводника настоящая иконка, у остальных (файлов нет) — плашки с буквой, как в макете.
    var running = new (string Path, string Name)[]
    {
        (@"C:\USERS\ME\APPDATA\LOCAL\DISCORD\APP-1.0.9200\DISCORD.EXE", "Discord"),
        (@"C:\Program Files\obs-studio\bin\64bit\obs64.exe", "OBS Studio"),
        (@"C:\Users\me\AppData\Roaming\Spotify\Spotify.exe", "Spotify"),
        (@"C:\Program Files (x86)\Steam\steam.exe", "Steam"),
        (@"C:\Users\me\AppData\Roaming\Telegram Desktop\Telegram.exe", "Telegram"),
        (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "Проводник")
    };
    var s = new ViewState
    {
        Ip = "192.168.1.1",
        Port = 1818,
        ListRunning = exclude => running.Where(r => !exclude.Contains(r.Path, StringComparer.OrdinalIgnoreCase)),
        IconPng = ProcessCatalog.IconPng
    };
    s.Apps.Add(new AppRow { Path = @"C:\Users\me\AppData\Local\Discord\app-1.0.9200\Discord.exe", FileName = "Discord.exe", Name = "Discord", Enabled = true });
    s.Apps.Add(new AppRow { Path = @"C:\Program Files\Google\Chrome\Application\chrome.exe", FileName = "chrome.exe", Name = "Google Chrome", Enabled = false });
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
