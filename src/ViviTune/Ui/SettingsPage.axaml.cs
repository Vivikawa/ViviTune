using System.Globalization;
using Avalonia.Controls;

namespace ViviTune.Ui
{
    // Поля IP и порта: каждое изменение уходит в ViewState.RequestSetProxy (App проверяет и сохраняет).
    public partial class SettingsPage : UserControl
    {
        bool filling;

        public SettingsPage()
        {
            InitializeComponent();
            VersionText.Text = $"ViviTune {typeof(SettingsPage).Assembly.GetName().Version?.ToString(3)} · sing-box 1.12.12";
            IpBox.TextChanged += (_, _) => Push();
            PortBox.TextChanged += (_, _) => Push();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is not ViewState s) return;
            filling = true;
            IpBox.Text = s.Ip;
            PortBox.Text = s.Port.ToString(CultureInfo.InvariantCulture);
            filling = false;
        }

        void Push()
        {
            if (filling || DataContext is not ViewState s) return;
            // нечисловой порт — 0: App покажет «Порт должен быть от 1 до 65535»
            int.TryParse(PortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port);
            s.RequestSetProxy((IpBox.Text ?? "").Trim(), port);
        }
    }
}
