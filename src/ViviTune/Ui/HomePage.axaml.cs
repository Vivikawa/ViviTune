using Avalonia.Controls;

namespace ViviTune.Ui
{
    // Главная: кнопка подключения, статус, скорость. Нажатие уходит в ViewState.RequestToggle (App решает, что делать).
    public partial class HomePage : UserControl
    {
        readonly ClickGuard powerGuard = new(); // двойной клик не подключает и сразу не отключает

        public HomePage()
        {
            InitializeComponent();
            Power.Click += (_, _) =>
            {
                if (powerGuard.TryAccept(Environment.TickCount64)) (DataContext as ViewState)?.RequestToggle();
            };
        }

        void OnSocksMode(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => (DataContext as ViewState)?.RequestSetMode(ProxyMode.Socks5);
        void OnVlessMode(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => (DataContext as ViewState)?.RequestSetMode(ProxyMode.Vless);
    }
}
