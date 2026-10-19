using Avalonia;

namespace ViviTune
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // ShowSignal держит и первый экземпляр: сигнал второго не потеряется, пока первый ещё стартует.
            using var show = new EventWaitHandle(false, EventResetMode.AutoReset, Ui.App.ShowSignal);
            using var m = new Mutex(true, "ViviTune.SingleInstance", out bool created);
            if (!created)
            {
                Windows.Native.AllowAnyForeground();
                show.Set(); // уже запущен (может быть, в трее): пусть покажет окно
                return 0;
            }
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<Ui.App>().UsePlatformDetect().WithInterFont();
    }
}
