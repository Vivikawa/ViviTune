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
        const string NoUdpWarning = "UDP через этот прокси не работает: игры и голосовая связь могут не заработать.";
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
            var session = new Session(settings, runner, Path.Combine(dir, "config.json"), ProxyProbe.Probe, TimeSpan.FromSeconds(1));
            var state = new ViewState
            {
                Ip = settings.Ip,
                Port = settings.Port,
                SettingsError = Settings.ValidateProxy(settings.Ip, settings.Port),
                ListRunning = exclude => ProcessCatalog.Running(exclude).Select(a => (a.Path, a.Name, a.Background)),
                IconPng = ProcessCatalog.IconPng
            };

            // Новые строки (с именем файла и плашкой) получают описание программы и иконку, не в UI-потоке.
            // Файла нет — так и остаётся (имя из имени файла, плашка с буквой).
            void SyncRows(List<AppEntry> apps)
            {
                var rows = state.SyncApps(apps);
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
            SyncRows(settings.Apps);

            // Settings.Apps не меняется на месте: Session снимает копию настроек в своих потоках.
            // Новый список → Save → строки → Session.AppsChanged. Вызовы AppsChanged идут по очереди:
            // каждый читает текущий Settings.Apps, так что последний вызов видит последнее состояние.
            var appsChain = Task.CompletedTask;
            void ChangeApps(Func<List<AppEntry>, bool> change)
            {
                var apps = AppListOps.Copy(settings.Apps);
                if (!change(apps)) return;
                settings.Apps = apps;
                try { settings.Save(settingsPath); }
                catch (Exception) { } // папка недоступна для записи: работаем без сохранения
                SyncRows(apps);
                appsChain = appsChain.ContinueWith(_ =>
                {
                    try { session.AppsChanged(); } // может отключать и ждать перезапуска: не в UI-потоке
                    catch (Exception e) { Dispatcher.UIThread.Post(() => state.AddLog("Ошибка: " + e.Message)); }
                }, TaskScheduler.Default);
            }
            state.AddAppRequested += path => ChangeApps(apps => AppListOps.Add(apps, path));
            state.RemoveAppRequested += path => ChangeApps(apps => AppListOps.Remove(apps, path));
            state.SetAppEnabledRequested += (path, on) => ChangeApps(apps => AppListOps.SetEnabled(apps, path, on));

            // Трей: пункт подключения и подсказка следуют за состоянием (Sync), остальное — после окна.
            var toggleItem = new NativeMenuItem("Подключить");
            toggleItem.Click += (_, _) => state.RequestToggle();
            var tray = new TrayIcon { ToolTipText = "ViviTune: " + state.StatusText };

            // Строка «время · UDP · приложения» под статусом: раз в секунду, только при Connected.
            var detailTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            void UpdateDetail()
            {
                var since = session.ConnectedSince;
                state.DetailText = state.State == ConnState.Connected && since != null
                    ? ViewState.Detail(DateTime.Now - since.Value, session.UdpOk, state.Apps.Count(a => a.Enabled))
                    : "";
            }
            detailTimer.Tick += (_, _) => UpdateDetail();

            void Sync()
            {
                state.State = session.State;
                state.ErrorText = session.Error;
                state.WarningText = session.UdpOk == false ? NoUdpWarning : null;
                UpdateDetail();
                detailTimer.IsEnabled = state.State == ConnState.Connected;
                toggleItem.Header = state.State == ConnState.Disconnected ? "Подключить" : "Отключить";
                toggleItem.IsEnabled = !state.IsBusy; // как кнопка на Главной
                tray.ToolTipText = "ViviTune: " + state.StatusText;
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
            state.SetProxyRequested += (ip, port) =>
            {
                if (session.State != ConnState.Disconnected) return; // во время подключения прокси не меняется
                settings.Ip = ip;
                settings.Port = port;
                state.Ip = ip;
                state.Port = port;
                state.SettingsError = Settings.ValidateProxy(ip, port);
                try { settings.Save(settingsPath); }
                catch (Exception) { } // папка недоступна для записи: работаем без сохранения
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
