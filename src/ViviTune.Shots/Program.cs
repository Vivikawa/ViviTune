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
int enabled = s.Apps.Count(a => a.Enabled), direct = s.DirectApps.Count(a => a.Enabled);

// Главная в разных состояниях (DetailText и WarningText в приложении ставит App по Session)
Shot(NavPage.Home, "home-disconnected.png");
s.State = ConnState.Checking;
Shot(NavPage.Home, "home-checking.png");
s.State = ConnState.Connected;
s.DetailText = ViewState.Detail(new TimeSpan(0, 42, 17), true, enabled, direct);
const double MiB = 1024 * 1024;
for (int i = 0; i < 60; i++) // демо-история: волны и всплеск отдачи; последнее значение как в макете (1.2 и 8.4 МБ/с)
{
    double up = 1.2 * MiB * (1 + 0.45 * Math.Sin((i - 59) / 4.5)) + (i == 40 ? 1.6 * MiB : 0);
    double down = 8.4 * MiB * (1 + 0.5 * Math.Sin((i - 59) / 6.0));
    s.PushSpeed(new Speed(up, down, (long)up, (long)down));
}
Shot(NavPage.Home, "home-connected.png");
// Две строки предупреждений, как их склеивает App; текст порта — копия константы Session (она не public)
s.DetailText = ViewState.Detail(new TimeSpan(0, 42, 17), true, enabled, 0); // порт не отвечает: «мимо Podkop» программ нет, как в приложении
s.WarningText = "Порт «Мимо Podkop» недоступен (таймаут подключения): программы из этого списка идут обычным путём.\n"
    + "UDP через этот прокси не работает: игры и голосовая связь могут не заработать.";
Shot(NavPage.Home, "home-connected-warning.png");
s.WarningText = null;
s.DetailText = ViewState.Detail(new TimeSpan(0, 3, 5), false, enabled, direct);
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
s.DirectTab = true;
Shot(NavPage.Apps, "apps-direct.png");
s.DirectPort = null;
Shot(NavPage.Apps, "apps-direct-noport.png"); // подсказка про порт
s.DirectPort = 1819;
s.BypassDown = true;
Shot(NavPage.Apps, "apps-direct-down.png"); // подсказка, что порт не отвечает
s.BypassDown = false;
s.DirectTab = false;
var appsPage = window.GetVisualDescendants().OfType<AppsPage>().Single();
appsPage.OpenAddPanel().Wait(); // список и иконки готовит фоновый поток; Shot применит его Post
Shot(NavPage.Apps, "apps-add.png");
appsPage.FindControl<Button>("BgButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
Shot(NavPage.Apps, "apps-add-background.png"); // «Фоновые» раскрыты
appsPage.OpenAddPanel().Wait(); // открытие заново — «Фоновые» снова свёрнуты
appsPage.FindControl<TextBox>("SearchBox")!.Text = "st"; // поиск по имени без учёта регистра
Shot(NavPage.Apps, "apps-add-search.png");
appsPage.FindControl<TextBox>("SearchBox")!.Text = "pyth"; // нашлось только среди фоновых: они раскрываются
Shot(NavPage.Apps, "apps-add-search-background.png");
Shot(NavPage.Log, "log.png"); // уход со страницы закрывает панель
Shot(NavPage.Settings, "settings.png");
// VLESS: главная при подключении, вкладка «Сайты», настройки
s.State = ConnState.Disconnected;
s.Mode = ProxyMode.Vless;
s.SetVless("vless://3f1c2a9e-7b4d-4c1e-9a55-0d6f8e2b7c41@nl-amsterdam.example.com:443?security=reality&sni=www.microsoft.com&fp=chrome&pbk=Qk2yq3xN8fWv0m6T1sZp4aLr9uHcDe7oJbXgYiAn5Rw&sid=6a1b&type=tcp&flow=xtls-rprx-vision#Amsterdam");
s.SyncLists(["russia_inside", "telegram", "google_ai"]);
s.State = ConnState.Connected;
s.DetailText = ViewState.DetailVless(new TimeSpan(0, 12, 3), 2, 3);
for (int i = 0; i < 60; i++) // демо-история скорости, как в SOCKS5
{
    double up = 0.9 * MiB * (1 + 0.45 * Math.Sin((i - 59) / 4.5)) + (i == 40 ? 1.2 * MiB : 0);
    double down = 6.1 * MiB * (1 + 0.5 * Math.Sin((i - 59) / 6.0));
    s.PushSpeed(new Speed(up, down, (long)up, (long)down));
}
Shot(NavPage.Home, "home-vless-connected.png");
s.State = ConnState.Disconnected;
s.DetailText = "";
s.DirectTab = true;
Shot(NavPage.Apps, "apps-sites.png");
Shot(NavPage.Settings, "settings-vless.png");
s.Mode = ProxyMode.Socks5; // назад к SOCKS5 для снимка пустого списка
s.DirectTab = false;
s.State = ConnState.Disconnected;
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
    // у Проводника настоящая иконка, у остальных (файлов нет) — плашки с буквой, как в макете. Без окна — в «Фоновые».
    var running = new (string Path, string Name, bool Background)[]
    {
        (@"C:\USERS\ME\APPDATA\LOCAL\DISCORD\APP-1.0.9200\DISCORD.EXE", "Discord", false),
        (@"C:\Program Files\obs-studio\bin\64bit\obs64.exe", "OBS Studio", false),
        (@"C:\Users\me\AppData\Roaming\Spotify\Spotify.exe", "Spotify", false),
        (@"C:\Program Files (x86)\Steam\steam.exe", "Steam", false),
        (@"C:\Users\me\AppData\Roaming\Telegram Desktop\Telegram.exe", "Telegram", false),
        (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "Проводник", false),
        (@"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\msedgewebview2.exe", "Microsoft Edge WebView2", true),
        (@"C:\Users\me\AppData\Local\Programs\Python\Python313\python.exe", "Python", true),
        (@"C:\Program Files\PowerShell\7\pwsh.exe", "PowerShell 7", true)
    };
    var s = new ViewState
    {
        Ip = "192.168.1.1",
        Port = 1818,
        DirectPort = 1819,
        ListRunning = exclude => running.Where(r => !exclude.Contains(r.Path, StringComparer.OrdinalIgnoreCase)),
        IconPng = ProcessCatalog.IconPng
    };
    s.Apps.Add(new AppRow { Path = @"C:\Users\me\AppData\Local\Discord\app-1.0.9200\Discord.exe", FileName = "Discord.exe", Name = "Discord", Enabled = true });
    s.Apps.Add(new AppRow { Path = @"C:\Program Files\Google\Chrome\Application\chrome.exe", FileName = "chrome.exe", Name = "Google Chrome", Enabled = false });
    s.Apps.Add(new AppRow { Path = @"C:\Program Files\Mozilla Firefox\firefox.exe", FileName = "firefox.exe", Name = "Firefox", Enabled = true });
    // Два приложения «Мимо Podkop»; Steam есть и среди запущенных — в панели «＋ Добавить» его не будет
    foreach (var (p, name) in new[] { (@"C:\Program Files\qBittorrent\qbittorrent.exe", "qBittorrent"), (@"C:\Program Files (x86)\Steam\steam.exe", "Steam") })
        s.DirectApps.Add(new AppRow { Path = p, FileName = System.IO.Path.GetFileName(p), Name = name, Enabled = true });
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
