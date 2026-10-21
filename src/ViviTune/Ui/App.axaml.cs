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
                desktop.MainWindow = CreateMainWindow();
            base.OnFrameworkInitializationCompleted();
        }

        static MainWindow CreateMainWindow()
        {
            var dir = AppContext.BaseDirectory;
            var settingsPath = Path.Combine(dir, "settings.json");
            var settings = Settings.Load(settingsPath);
            var runner = new Runner(Path.Combine(dir, "sing-box.exe"));
            var session = new Session(settings, runner, Path.Combine(dir, "config.json"), ProxyProbe.Probe, TimeSpan.FromSeconds(1), new VlessDeps
            {
                // сначала IPv4; исключения ловит Session
                Resolve = name => System.Net.Dns.GetHostAddresses(name).OrderBy(a => a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork).FirstOrDefault(),
                Reach = ProxyProbe.Reach,
                PrepareLists = (ids, link, server) =>
                {
                    using var fetch = new Runner(Path.Combine(dir, "sing-box.exe")); // Job Object: умрёт вместе с нами
                    return ListStore.PrepareVia(Path.Combine(dir, "lists"), ids, fetch, link, server, DateTime.UtcNow);
                },
                AfterStart = (p, s, ct) => VlessProbe.AfterStart(p, s, ct) ?? Flush()
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
                IconPng = ProcessCatalog.IconPng
            };

            state.SetVless(settings.VlessLink);
            state.SyncLists(settings.Lists);

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
                    ? ViewState.DetailVless(DateTime.Now - since.Value, state.Apps.Count(a => a.Enabled), settings.Lists.Count)
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
            session.Log += line => Dispatcher.UIThread.Post(() => state.AddLog(line));
            session.Speed += s => Dispatcher.UIThread.Post(() => state.PushSpeed(s));

            state.ToggleRequested += async () =>
            {
                try
                {
                    if (session.State == ConnState.Disconnected) await session.ConnectAsync();
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
                settings.VlessLink = link;
                state.SetVless(link);
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

            var window = new MainWindow(state);

            // Крестик и Alt+F4 прячут окно в трей, подключение остаётся. По-настоящему закрывают «Выход»
            // (Close из кода — IsProgrammatic) и выключение Windows (другая CloseReason).
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
            quitItem.Click += (_, _) => window.Close();
            tray.Icon = window.Icon;
            tray.Menu = new NativeMenu { openItem, toggleItem, new NativeMenuItemSeparator(), quitItem };
            tray.Clicked += (_, _) => ShowWindow();
            TrayIcon.SetIcons(Current!, new TrayIcons { tray });
            var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignal);
            ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => Dispatcher.UIThread.Post(ShowWindow), null, -1, false);

            window.Closed += (_, _) =>
            {
                detailTimer.Stop();
                session.Dispose(); // останавливает sing-box
                runner.Dispose();  // Session его не освобождает
            };
            return window;
        }
    }
}
