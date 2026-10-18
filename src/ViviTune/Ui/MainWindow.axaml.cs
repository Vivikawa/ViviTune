using Avalonia.Controls;

namespace ViviTune.Ui
{
    public enum NavPage { Home, Apps, Log, Settings }

    // Боковое меню и страницы; страницы получают DataContext = state по наследству.
    public partial class MainWindow : Window
    {
        public MainWindow() : this(new ViewState()) { } // для дизайнера XAML

        public MainWindow(ViewState state)
        {
            InitializeComponent();
            DataContext = state;
        }

        public void ShowPage(NavPage page) =>
            (page switch
            {
                NavPage.Apps => NavApps,
                NavPage.Log => NavLog,
                NavPage.Settings => NavSettings,
                _ => NavHome
            }).IsChecked = true;
    }
}
