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
                SettingsError = Settings.ValidateProxy(settings.Ip, settings.Port)
            };
            foreach (var a in settings.Apps)
                state.Apps.Add(new AppRow
                {
                    Path = a.Path,
                    FileName = Path.GetFileName(a.Path),
                    Name = Path.GetFileNameWithoutExtension(a.Path),
                    Enabled = a.Enabled
                });

            void Sync()
            {
                state.State = session.State;
                state.ErrorText = session.Error;
                state.WarningText = session.UdpOk == false ? NoUdpWarning : null;
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
                session.Dispose(); // останавливает sing-box
                runner.Dispose();  // Session его не освобождает
            };
            return window;
        }
    }
}
