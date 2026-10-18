using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ViviTune.Windows;

namespace ViviTune.Ui
{
    public class App : Application
    {
        const string NoUdpWarning = "UDP через этот прокси не работает: игры и голосовая связь могут не заработать.";

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
                ListRunning = exclude => ProcessCatalog.Running(exclude).Select(a => (a.Path, a.Name)),
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
