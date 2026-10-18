using Avalonia;

namespace ViviTune
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            using var m = new Mutex(true, "ViviTune.SingleInstance", out bool created);
            if (!created)
            {
                Windows.Native.Message("ViviTune уже запущен");
                return 0;
            }
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<Ui.App>().UsePlatformDetect().WithInterFont();
    }
}
