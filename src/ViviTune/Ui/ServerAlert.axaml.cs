using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ViviTune.Windows;

namespace ViviTune.Ui
{
    // Окошко над треем: «Сервер не отвечает» (с кнопками) или «Сервер переключён» (само закроется)
    public partial class ServerAlert : Window
    {
        public static (string Title, string Text, string? Best) DownText(string name, string? bestName, int? bestMs) =>
            ("Сервер не отвечает", $"{name} не отвечает уже 1,5 минуты. Выберите другой сервер.",
                bestName == null ? null : $"{bestName} · {bestMs} мс");

        public static (string Title, string Text) SwitchedText(string from, string to, int ms) =>
            ("Сервер переключён", $"{from} не отвечал — теперь {to} · {ms} мс.");

        // «Не отвечает» имеет смысл только при подключении
        public static bool DownStillValid(ConnState s) => s == ConnState.Connected;

        public ServerAlert() => AvaloniaXamlLoader.Load(this); // для загрузчика XAML

        // best — текст первой кнопки (null — без неё); autoClose != null — вариант «переключён»: без кнопок.
        // onBest/onAll вызываются в UI-потоке после закрытия; любая кнопка закрывает окно
        public ServerAlert(string title, string text, string? best, Action? onBest, Action? onAll, TimeSpan? autoClose)
        {
            AvaloniaXamlLoader.Load(this);
            this.FindControl<TextBlock>("TitleText")!.Text = title;
            this.FindControl<TextBlock>("BodyText")!.Text = text;
            var bestBtn = this.FindControl<Button>("BestButton")!;
            bestBtn.IsVisible = best != null;
            bestBtn.Content = best;
            this.FindControl<WrapPanel>("Buttons")!.IsVisible = autoClose == null;
            this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
            bestBtn.Click += (_, _) => { Close(); onBest?.Invoke(); };
            this.FindControl<Button>("AllButton")!.Click += (_, _) => { Close(); onAll?.Invoke(); };
            if (autoClose is { } t)
            {
                var timer = new DispatcherTimer { Interval = t };
                timer.Tick += (_, _) => { timer.Stop(); Close(); };
                Opened += (_, _) => timer.Start();
                Closed += (_, _) => timer.Stop();
            }
            Opened += (_, _) => Place();
        }

        // правый нижний угол рабочей области основного экрана, отступ 12 px
        void Place()
        {
            var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
            if (screen == null) return;
            var wa = screen.WorkingArea;
            int m = (int)(12 * screen.Scaling);
            Position = new PixelPoint(wa.Right - (int)(Bounds.Width * screen.Scaling) - m, wa.Bottom - (int)(Bounds.Height * screen.Scaling) - m);
        }
    }
}
