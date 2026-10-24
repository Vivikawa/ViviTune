using System.Globalization;
using Avalonia.Controls;

namespace ViviTune.Ui
{
    // Поля IP и порта: каждое изменение уходит в ViewState.RequestSetProxy (App проверяет и сохраняет).
    public partial class SettingsPage : UserControl
    {
        public SettingsPage()
        {
            InitializeComponent();
            VersionText.Text = $"ViviTune {typeof(SettingsPage).Assembly.GetName().Version?.ToString(3)} · sing-box 1.14.3 (podkop-engine)";
            IpBox.TextChanged += (_, _) => Push();
            PortBox.TextChanged += (_, _) => Push();
            DirectPortBox.TextChanged += (_, _) => Push();
            VlessBox.TextChanged += (_, _) => PushVless();
        }

        // Страницу показали снова: поля — как в ViewState (ссылку мог поменять App)
        protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty && IsVisible) Fill();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            Fill();
        }

        void Fill()
        {
            if (DataContext is not ViewState s) return;
            // TextChanged от этого заполнения придёт с теми же значениями: RequestSetProxy и RequestSetVless его пропустят
            VlessBox.Text = s.VlessLink;
            IpBox.Text = s.Ip;
            PortBox.Text = s.Port.ToString(CultureInfo.InvariantCulture);
            DirectPortBox.Text = s.DirectPort?.ToString(CultureInfo.InvariantCulture) ?? "";
        }

        void OnAutostart(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is Avalonia.Controls.Primitives.ToggleButton t) (DataContext as ViewState)?.RequestSetAutostart(t.IsChecked == true);
        }

        void PushVless()
        {
            if (DataContext is not ViewState s) return;
            s.RequestSetVless((VlessBox.Text ?? "").Trim());
        }

        void Push()
        {
            if (DataContext is not ViewState s) return;
            // нечисловой порт — 0: App покажет «Порт должен быть от 1 до 65535»
            int.TryParse(PortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port);
            s.RequestSetProxy((IpBox.Text ?? "").Trim(), port, Settings.ParseOptionalPort(DirectPortBox.Text));
        }
    }
}
