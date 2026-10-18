using Avalonia.Controls;

namespace ViviTune.Ui
{
    // Главная: кнопка подключения, статус, скорость. Нажатие уходит в ViewState.RequestToggle (App решает, что делать).
    public partial class HomePage : UserControl
    {
        public HomePage()
        {
            InitializeComponent();
            Power.Click += (_, _) => (DataContext as ViewState)?.RequestToggle();
        }
    }
}
