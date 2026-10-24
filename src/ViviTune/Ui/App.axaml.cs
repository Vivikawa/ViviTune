using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ViviTune.Windows;

namespace ViviTune.Ui
{
    public class App : Application
    {
        public const string ShowSignal = "ViviTune.Show"; // второй экземпляр просит показать окно (Program)

        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            // Session и sing-box только при настоящем запуске: Shots (headless, без lifetime) рисует окно без них.
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                Start(desktop);
            base.OnFrameworkInitializationCompleted();
        }

        static void Start(IClassicDesktopStyleApplicationLifetime desktop)
        {
            // При автозапуске окно не показывается (только трей), поэтому выход только явный: «Выход» и выключение Windows
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            bool autostart = desktop.Args?.Contains(Autostart.Flag) == true;
            var dir = AppContext.BaseDirectory;
            var settingsPath = Path.Combine(dir, "settings.json");
            var settings = Settings.Load(settingsPath);
            // подробный лог sing-box в файл — только если в settings.json есть SingBoxLog
            LogFile? devLog = settings.SingBoxLog != null ? new LogFile(Path.Combine(dir, "sing-box.log")) : null;
            devLog?.Write($"=== ViviTune {typeof(App).Assembly.GetName().Version?.ToString(3)} ===");
            var engine = Links.Engine(Path.Combine(dir, "sing-box.exe")); // decode-link: для Session и сводки в UI
            var runner = new Runner(Path.Combine(dir, "sing-box.exe"));
            var subPath = Path.Combine(dir, "subscription.json");
            // GET с заданным User-Agent; proxy null — системный. Текст исключения не содержит адрес: токен секретен
            static (int, IReadOnlyDictionary<string, string>, string)? HttpGet(string url, string ua, System.Net.IWebProxy? proxy)
            {
                try
                {
                    using var http = new HttpClient(new HttpClientHandler { Proxy = proxy, AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(15) };
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.UserAgent.ParseAdd(ua);
                    using var resp = http.SendAsync(req).GetAwaiter().GetResult();
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var h in resp.Headers.Concat(resp.Content.Headers)) headers[h.Key] = string.Join(",", h.Value);
                    return ((int)resp.StatusCode, headers, resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                }
                catch (Exception e) when (e is not HttpRequestException and not TaskCanceledException) { throw new InvalidOperationException("ошибка запроса"); }
            }
            // Скачать подписку одним способом (прокси null — системный): кэш уже сохранён или текст ошибки
            (SubCache? Cache, string? Error) FetchVia(string url, System.Net.IWebProxy? proxy)
            {
                var p = SubStore.Fetch(ua => HttpGet(url, ua, proxy), out var error);
                if (p == null) return (null, error);
                var cache = new SubCache(url, DateTime.UtcNow, p.Info, SubStore.Decode(p, l => (Links.Decode(l, engine, out var e), e)));
                try { SubStore.Save(subPath, cache); } catch (Exception) { } // папка недоступна для записи: работаем без кэша на диске
                return (cache, null);
            }
            // Напрямую; не вышло и есть копия — через выбранный сервер из неё временным sing-box (как списки)
            (SubCache? Cache, string? Error) FetchSub(string url, SubCache? old)
            {
                var r = FetchVia(url, null);
                if (r.Cache != null || old == null) return r;
                var srv = (old.Servers.FirstOrDefault(s => s.Server != null && s.Key == settings.Server) ?? old.Servers.FirstOrDefault(s => s.Server != null))?.Server;
                if (srv == null) return r;
                int port = Session.FreePort();
                var cfg = Path.Combine(dir, $"fetch-{port}.json");
                try
                {
                    var ip = System.Net.IPAddress.TryParse(srv.Host, out var lit) ? lit : System.Net.Dns.GetHostAddresses(srv.Host).First();
                    var proxy = ConfigBuilder.ProxyOutbound(srv, ip);
                    using var fetch = new Runner(Path.Combine(dir, "sing-box.exe"));
                    try { return FetchVia(url, ListStore.StartTunnel(cfg, fetch, proxy, port)); }
                    finally { fetch.Stop(); }
                }
                catch (Exception) { return r; } // не вышло и так: ошибка прямой попытки
                finally { try { File.Delete(cfg); } catch { } } // в нём ключ VLESS
            }
            // сначала IPv4; исключения ловит вызывающий
            static System.Net.IPAddress? Resolve(string name) =>
                System.Net.Dns.GetHostAddresses(name).OrderBy(a => a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork).FirstOrDefault();
            var session = new Session(settings, runner, Path.Combine(dir, "config.json"), ProxyProbe.Probe, TimeSpan.FromSeconds(1), new VlessDeps
            {
                Resolve = Resolve, // исключения ловит Session
                Reach = ProxyProbe.Reach,
                Decode = link => (Links.Decode(link, engine, out var e), e),
                PrepareLists = (ids, proxy) =>
                {
                    using var fetch = new Runner(Path.Combine(dir, "sing-box.exe")); // Job Object: умрёт вместе с нами
                    return ListStore.PrepareVia(Path.Combine(dir, "lists"), ids, fetch, proxy, DateTime.UtcNow);
                },
                LoadSub = url => SubStore.Load(subPath, url),
                FetchSub = FetchSub,
                Api = (p, s) => new ClashApi(p, s),
                AfterStart = (p, s, ct) => VlessProbe.AfterStart(p, s, ct, devLog == null ? null : devLog.Write) ?? Flush()
            });
            // Очистка кэша DNS — по возможности: экспорт недокументирован, его сбой не должен ронять запуск.
            static string? Flush() { try { Native.FlushDns(); } catch { } return null; }
            // папка недоступна для записи: работаем без сохранения
            void Save() { try { settings.Save(settingsPath); } catch (Exception) { } }
            var state = new ViewState
            {
                Ip = settings.Ip,
                Port = settings.Port,
                Mode = settings.Mode,
                DirectPort = settings.DirectPort,
                SettingsError = Settings.ValidateProxy(settings.Ip, settings.Port) ?? Settings.ValidateDirectPort(settings.DirectPort, settings.Port),
                ListRunning = exclude => ProcessCatalog.Running(exclude).Select(a => (a.Path, a.Name, a.Background)),
                IconPng = ProcessCatalog.IconPng,
                AutoServer = settings.AutoServer
            };

            // Сводку по ссылке даёт движок (~70 мс): не в UI-потоке. Поле шлёт ссылку на каждый символ —
            // разбираем только последнюю, через 300 мс тишины
            // Подписка: серверы и сводка из subscription.json (без движка); файл читаем не в UI-потоке
            SubCache? subCache = null; // только UI-поток
            void LoadSub(string link)
            {
                subCache = null;
                state.SetSubError(null);
                state.SetServers([], null);
                state.SetSubInfo(null, null, DateTime.UtcNow);
                if (!state.IsSubscription) return;
                Task.Run(() =>
                {
                    var c = SubStore.Load(subPath, link);
                    Dispatcher.UIThread.Post(() => { if (state.VlessLink == link) ShowSub(c); });
                });
            }
            void ShowSub(SubCache? c)
            {
                subCache = c;
                if (c == null) return;
                state.SetServers(c.Servers, settings.Server);
                state.SetSubInfo(c.Info, c.FetchedUtc, DateTime.UtcNow);
            }
            CancellationTokenSource? decodeCts = null;
            void SetVless(string link)
            {
                state.SetVless(link);
                decodeCts?.Cancel();
                LoadSub(link);
                if (string.IsNullOrWhiteSpace(link) || state.IsSubscription) return;
                var cts = decodeCts = new CancellationTokenSource();
                Task.Run(async () =>
                {
                    Server? srv = null; string? e;
                    try
                    {
                        await Task.Delay(300, cts.Token);
                        srv = Links.Decode(link, engine, out e);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception) { e = "Не удалось разобрать ссылку"; } // текст исключения не показываем: ссылка секретна
                    if (!cts.IsCancellationRequested) Dispatcher.UIThread.Post(() => state.SetVlessDecoded(link, srv, e));
                });
            }
            SetVless(settings.VlessLink);
            state.SyncLists(settings.Lists);
            state.SyncSites(settings.Sites);

            // Новые строки (с именем файла и плашкой) получают описание программы и иконку, не в UI-потоке.
            // Файла нет — так и остаётся (имя из имени файла, плашка с буквой).
            void SyncRows(List<AppEntry> apps, List<AppEntry> directApps)
            {
                var rows = state.SyncApps(apps, directApps);
                if (rows.Count == 0) return;
                Task.Run(() =>
                {
                    foreach (var r in rows)
                    {
                        var name = ProcessCatalog.DisplayName(r.Path);
                        var icon = state.LoadIcon(r.Path);
                        Dispatcher.UIThread.Post(() => { r.Name = name; r.Icon = icon; });
                    }
                });
            }
            SyncRows(settings.Apps, settings.DirectApps);

            // Settings.Apps и DirectApps не меняются на месте: Session снимает копию настроек в своих потоках.
            // Новый список → Save → строки → Session.AppsChanged. Вызовы AppsChanged идут по очереди:
            // каждый читает текущий Settings.Apps, так что последний вызов видит последнее состояние.
            var appsChain = Task.CompletedTask;
            void QueueAppsChanged()
            {
                appsChain = appsChain.ContinueWith(_ =>
                {
                    try { session.AppsChanged(); } // может отключать и ждать перезапуска: не в UI-потоке
                    catch (Exception e) { Dispatcher.UIThread.Post(() => state.AddLog("Ошибка: " + e.Message)); }
                }, TaskScheduler.Default);
            }
            void ChangeApps(Func<List<AppEntry>, List<AppEntry>, bool> change)
            {
                var apps = AppListOps.Copy(settings.Apps);
                var direct = AppListOps.Copy(settings.DirectApps);
                if (!change(apps, direct)) return;
                settings.Apps = apps;
                settings.DirectApps = direct;
                Save();
                SyncRows(apps, direct);
                QueueAppsChanged();
            }
            // Settings.Lists тоже заменяется целиком (порядок каталога), не меняется на месте.
            state.SetListEnabledRequested += (id, on) =>
            {
                var ids = settings.Lists.ToHashSet();
                if (!(on ? ids.Add(id) : ids.Remove(id))) return;
                settings.Lists = Catalogs.Lists.Where(l => ids.Contains(l.Id)).Select(l => l.Id).ToList();
                Save();
                state.SyncLists(settings.Lists);
                QueueAppsChanged();
            };
            // Settings.Sites тоже заменяется целиком; повтор молча игнорируется
            void ChangeSites(List<string> sites)
            {
                settings.Sites = sites;
                Save();
                state.SyncSites(sites);
                QueueAppsChanged();
            }
            state.AddSiteRequested += v => { if (!settings.Sites.Contains(v)) ChangeSites([.. settings.Sites, v]); };
            state.RemoveSiteRequested += v => { if (settings.Sites.Contains(v)) ChangeSites(settings.Sites.Where(x => x != v).ToList()); };
            state.AddAppRequested += (path, toDirect) => ChangeApps((p, d) => toDirect ? AppListOps.Move(d, p, path) : AppListOps.Move(p, d, path));
            // Путь может быть в любом из списков: без короткого замыкания (| вместо ||), чтобы затронуть оба
            state.RemoveAppRequested += path => ChangeApps((p, d) => AppListOps.Remove(p, path) | AppListOps.Remove(d, path));
            state.SetAppEnabledRequested += (path, on) => ChangeApps((p, d) => AppListOps.SetEnabled(p, path, on) | AppListOps.SetEnabled(d, path, on));

            // Трей: пункт подключения и подсказка следуют за состоянием (Sync), остальное — после окна.
            var toggleItem = new NativeMenuItem("Подключить");
            toggleItem.Click += (_, _) => state.RequestToggle();
            var tray = new TrayIcon { ToolTipText = "ViviTune: " + state.StatusText };

            // Строка «время · UDP · приложения» под статусом: раз в секунду, только при Connected.
            var detailTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            void UpdateDetail()
            {
                var since = session.ConnectedSince;
                var live = session.Live;
                state.DetailText = state.State != ConnState.Connected || since == null ? ""
                    : state.IsVless
                    ? ViewState.DetailVless(DateTime.Now - since.Value, state.Apps.Count(a => a.Enabled), settings.Lists.Count, settings.Sites.Count)
                    : ViewState.Detail(DateTime.Now - since.Value, session.UdpOk,
                        live.Proxy ? state.Apps.Count(a => a.Enabled) : 0, live.Bypass ? state.DirectApps.Count(a => a.Enabled) : 0);
            }
            detailTimer.Tick += (_, _) => UpdateDetail();

            void Sync()
            {
                state.State = session.State;
                state.ErrorText = session.Error;
                state.WarningText = session.Warnings.Count > 0 ? string.Join("\n", session.Warnings) : null;
                bool live = state.State is ConnState.Connected or ConnState.Restarting;
                state.ProxyDown = !state.IsVless && live && state.Apps.Any(a => a.Enabled) && !session.Live.Proxy;
                state.BypassDown = !state.IsVless && live && state.DirectPort != null && state.DirectApps.Any(a => a.Enabled) && !session.Live.Bypass;
                UpdateDetail();
                detailTimer.IsEnabled = state.State == ConnState.Connected;
                toggleItem.Header = state.State == ConnState.Disconnected ? "Подключить" : "Отключить";
                toggleItem.IsEnabled = !state.IsBusy; // как кнопка на Главной
                tray.ToolTipText = "ViviTune: " + state.StatusText + (session.Warnings.Count > 0 ? " · есть предупреждения" : "");
            }

            // События Session приходят из любых потоков (Speed — под её блокировкой): только Post, никогда Invoke.
            session.Changed += () => Dispatcher.UIThread.Post(Sync);
            session.Log += line =>
            {
                devLog?.Write(line);
                if (devLog == null || !LogFile.IsVerbose(line)) Dispatcher.UIThread.Post(() => state.AddLog(line));
            };
            // выбранный сервер подписки запоминаем (при автовыборе и ручном переключении); AppsChanged не нужен
            // Сохраняем session.CurrentServer, а не аргумент: событие от подключения может прийти после быстрого ручного выбора
            session.ServerChanged += _ => Dispatcher.UIThread.Post(() =>
            {
                if (session.CurrentServer is not { } cur) return;
                settings.Server = cur;
                Save();
                state.SetSelected(cur);
            });
            session.PingsChanged += () => Dispatcher.UIThread.Post(() =>
            {
                foreach (var kv in session.Pings) state.SetPing(kv.Key, kv.Value);
            });
            session.Speed += s => Dispatcher.UIThread.Post(() => state.PushSpeed(s));

            // ---- подписка: получить серверы, пинг, выбор, галка ----
            CancellationTokenSource? pingCts = null;
            Task pingTask = Task.CompletedTask; // идущий пинг: подключение ждёт его остановки // пинг временным sing-box: подключение его отменяет
            // Пинг без подключения: один временный sing-box с outbound'ами всех поддерживаемых серверов; не в UI-потоке
            void PingOffline(IReadOnlyList<SubServer> servers, CancellationToken ct)
            {
                var tags = new List<string>(); var keys = new Dictionary<string, string>(); var outs = new List<System.Text.Json.Nodes.JsonObject>();
                foreach (var sv in servers.Where(x => x.Server != null))
                {
                    System.Net.IPAddress? ip = null;
                    try { ip = System.Net.IPAddress.TryParse(sv.Server!.Host, out var lit) ? lit : Resolve(sv.Server.Host); } catch (Exception) { }
                    if (ip == null) { Dispatcher.UIThread.Post(() => state.SetPing(sv.Key, null)); continue; }
                    var o = ConfigBuilder.ProxyOutbound(sv.Server!, ip);
                    var tag = "s" + outs.Count;
                    o["tag"] = tag;
                    outs.Add(o); tags.Add(tag); keys[tag] = sv.Key;
                }
                if (outs.Count == 0 || ct.IsCancellationRequested) return;
                int apiPort = Session.FreePort();
                var secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                var cfg = Path.Combine(dir, $"probe-{apiPort}.json");
                using var sb = new Runner(Path.Combine(dir, "sing-box.exe")); // Job Object: умрёт вместе с нами
                try
                {
                    File.WriteAllText(cfg, ConfigBuilder.BuildProbe(outs, apiPort, secret));
                    sb.Start(cfg);
                    for (var end = DateTime.UtcNow.AddSeconds(3); ; Thread.Sleep(50))
                    {
                        if (ct.IsCancellationRequested) return; // finally остановит sing-box
                        try { using var c = new System.Net.Sockets.TcpClient(); c.Connect(System.Net.IPAddress.Loopback, apiPort); break; }
                        catch (System.Net.Sockets.SocketException) when (DateTime.UtcNow < end) { }
                    }
                    using var api = new ClashApi(apiPort, secret);
                    Pinger.Ping(api, tags, 3000, null, (t, ms) => { if (!ct.IsCancellationRequested) Dispatcher.UIThread.Post(() => state.SetPing(keys[t], ms)); }, ct);
                }
                finally
                {
                    sb.Stop();
                    try { File.Delete(cfg); } catch { } // в нём ключи
                }
            }
            // Пинг всех серверов: подключено — через сессию, иначе временным sing-box. Одновременно только один (Pinging).
            void StartPing()
            {
                if (state.Pinging || state.Fetching || subCache == null) return;
                var servers = subCache.Servers;
                state.Pinging = true;
                foreach (var r in state.Servers.Where(r => r.Supported)) state.SetPinging(r.Key);
                pingCts?.Cancel();
                var cts = pingCts = new CancellationTokenSource();
                pingTask = Task.Run(() =>
                {
                    try
                    {
                        if (!session.PingConnected(cts.Token) && session.State == ConnState.Disconnected) PingOffline(servers, cts.Token);
                    }
                    catch (Exception e) { Dispatcher.UIThread.Post(() => state.AddLog("Пинг серверов: " + e.Message)); }
                    Dispatcher.UIThread.Post(() => { state.Pinging = false; state.ResetPinging(); });
                });
            }
            state.PingRequested += StartPing;
            state.FetchServersRequested += () =>
            {
                if (!state.CanFetchNow) return;
                var link = state.VlessLink;
                var old = subCache;
                state.Fetching = true;
                state.SetSubError(null);
                Task.Run(() =>
                {
                    var r = FetchSub(link, old);
                    Dispatcher.UIThread.Post(() =>
                    {
                        state.Fetching = false;
                        if (state.VlessLink != link) return; // поле уже другое
                        if (r.Cache == null) { state.SetSubError(r.Error ?? "Подписка не скачалась"); return; }
                        ShowSub(r.Cache);
                        StartPing();
                    });
                });
            };
            var selectChain = Task.CompletedTask; // выбор сервера по одному, не в UI-потоке
            state.SelectServerRequested += key =>
            {
                settings.Server = key;
                Save();
                state.SetSelected(key);
                if (session.State != ConnState.Connected) return;
                selectChain = selectChain.ContinueWith(_ =>
                {
                    bool ok;
                    try { ok = session.SelectServer(key); } catch (Exception) { ok = false; }
                    if (ok) return;
                    // не вышло: возвращаем то, что реально выбрано
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (session.CurrentServer is not { } cur) return;
                        settings.Server = cur; Save(); state.SetSelected(cur);
                    });
                }, TaskScheduler.Default);
            };
            state.SetAutoServerRequested += on =>
            {
                settings.AutoServer = on;
                Save();
                session.AutoServer = on;
                state.AutoServer = on;
            };

            CancellationTokenSource? auto = null; // попытки подключения при автозапуске
            state.ToggleRequested += async () =>
            {
                auto?.Cancel(); // пользователь решил сам: попытки автозапуска больше не нужны
                try
                {
                    if (session.State == ConnState.Disconnected)
                    {
                        // пробный sing-box должен остановиться до подключения: два процесса и пинги рядом не нужны
                        pingCts?.Cancel();
                        await Task.WhenAny(pingTask, Task.Delay(5000));
                        await session.ConnectAsync();
                    }
                    else await Task.Run(session.Disconnect); // может ждать перезапуска до ~3 с: не в UI-потоке
                }
                catch (Exception e) { state.AddLog("Ошибка: " + e.Message); }
            };
            state.SetModeRequested += mode =>
            {
                if (session.State != ConnState.Disconnected) return; // режим меняется только без подключения
                settings.Mode = mode;
                state.Mode = mode;
                Save();
            };
            state.SetVlessRequested += link =>
            {
                if (session.State != ConnState.Disconnected) return;
                if (settings.SetVlessLink(link)) try { File.Delete(subPath); } catch (Exception) { } // другое поле — чужой кэш и выбор
                SetVless(link);
                Save();
            };
            state.SetProxyRequested += (ip, port, direct) =>
            {
                if (session.State != ConnState.Disconnected) return; // во время подключения прокси не меняется
                settings.Ip = ip;
                settings.Port = port;
                state.Ip = ip;
                settings.DirectPort = direct;
                state.Port = port;
                state.DirectPort = direct;
                state.SettingsError = Settings.ValidateProxy(ip, port) ?? Settings.ValidateDirectPort(direct, port);
                Save();
            };

            // Переключатель автозапуска показывает, есть ли задача; schtasks — не в UI-потоке.
            // Запуск ярлыком при включённом автозапуске обновляет в задаче путь (папку могли перенести).
            var exe = Environment.ProcessPath ?? Path.Combine(dir, "ViviTune.exe");
            Task.Run(() =>
            {
                try
                {
                    bool on = Autostart.IsEnabled();
                    if (on && !autostart) Autostart.Enable(exe);
                    Dispatcher.UIThread.Post(() => state.Autostart = on);
                }
                catch (Exception e) { Dispatcher.UIThread.Post(() => state.AddLog("Автозапуск: " + e.Message)); }
            });
            state.SetAutostartRequested += on => Task.Run(() =>
            {
                string? err = null;
                try { if (on) Autostart.Enable(exe); else Autostart.Disable(); }
                catch (Exception e) { err = e.Message; }
                bool now;
                try { now = Autostart.IsEnabled(); }
                catch { now = !on; }
                Dispatcher.UIThread.Post(() =>
                {
                    state.Autostart = now; // не вышло — переключатель возвращается
                    if (err != null) state.AddLog((on ? "Не удалось включить автозапуск: " : "Не удалось выключить автозапуск: ") + err);
                });
            });

            var window = new MainWindow(state);

            // Крестик и Alt+F4 прячут окно в трей, подключение остаётся. По-настоящему закрывают «Выход»
            // (Shutdown) и выключение Windows (другая CloseReason).
            window.Closing += (_, e) =>
            {
                if (e.CloseReason != WindowCloseReason.WindowClosing || e.IsProgrammatic) return;
                e.Cancel = true;
                window.Hide();
            };
            void ShowWindow()
            {
                window.Show();
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            }
            var openItem = new NativeMenuItem("Открыть");
            openItem.Click += (_, _) => ShowWindow();
            var quitItem = new NativeMenuItem("Выход");
            quitItem.Click += (_, _) => desktop.Shutdown();
            tray.Icon = window.Icon;
            tray.Menu = new NativeMenu { openItem, toggleItem, new NativeMenuItemSeparator(), quitItem };
            tray.Clicked += (_, _) => ShowWindow();
            TrayIcon.SetIcons(Current!, new TrayIcons { tray });
            var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignal);
            ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => Dispatcher.UIThread.Post(ShowWindow), null, -1, false);

            desktop.Exit += (_, _) =>
            {
                detailTimer.Stop();
                session.Dispose(); // останавливает sing-box
                runner.Dispose();  // Session его не освобождает
                devLog?.Dispose();
            };

            if (!autostart)
            {
                desktop.MainWindow = window; // lifetime покажет его при старте
                return;
            }
            // Автозапуск: только трей и подключение. Не вышло за 3 попытки (неверные настройки — сразу) — окно с ошибкой.
            auto = new CancellationTokenSource();
            var ct = auto.Token;
            bool valid = settings.Clone().Validate() == null;
            Dispatcher.UIThread.Post(async () =>
            {
                bool ok = await Autostart.Connect(async () =>
                {
                    try { await session.ConnectAsync(); }
                    catch (Exception e) { state.AddLog("Ошибка: " + e.Message); }
                    return session.State != ConnState.Disconnected;
                }, valid ? 3 : 1, TimeSpan.FromSeconds(10), n => state.AddLog($"Автозапуск: попытка {n} из 3"), ct);
                if (!ok) ShowWindow();
            });
        }
    }
}
