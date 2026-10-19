using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace ViviTune.Ui
{
    // Список приложений и панель «＋ Добавить». Изменения уходят в ViewState.Request*: App меняет Settings и строки.
    public partial class AppsPage : UserControl
    {
        readonly ObservableCollection<AppRow> shown = new(), shownBg = new(); // «Запущены сейчас» и «Фоновые» после поиска
        List<AppRow> running = new(), background = new(); // с окном и без
        bool loading, bgOpen;
        int opening; // номер открытия панели: результаты прошлой загрузки не применяются
        readonly ClickGuard pickGuard = new(), removeGuard = new(); // список сдвигается: второй клик попал бы по соседней строке

        public AppsPage()
        {
            InitializeComponent();
            RunningList.ItemsSource = shown;
            BgList.ItemsSource = shownBg;
            BgButton.Click += (_, _) => { bgOpen = !bgOpen; Filter(); };
            AddPopup.PlacementTarget = ListArea;
            AddPopup.Opened += (_, _) => SearchBox.Focus();
            AddButton.Click += (_, _) => OpenAddPanel();
            PickFileButton.Click += (_, _) => PickFile();
            SearchBox.TextChanged += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(SearchBox.Text)) bgOpen = true; // ищем и среди фоновых; свернуть можно и при поиске
                Filter();
            };
        }

        ViewState? State => DataContext as ViewState;

        // Ушли на другую страницу — панель закрывается (она в слое окна и иначе осталась бы поверх)
        protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty && !IsVisible) AddPopup.IsOpen = false;
        }

        // Панель открывается сразу; список запущенных и иконки приходят позже, не из UI-потока.
        // Task — для Shots: дождаться списка и иконок перед снимком.
        public Task OpenAddPanel()
        {
            if (State is not { } s) return Task.CompletedTask;
            var n = ++opening;
            running = new();
            background = new();
            loading = true;
            bgOpen = false;
            SearchBox.Text = "";
            Filter();
            AddPopup.IsOpen = true;
            var added = s.Apps.Concat(s.DirectApps).Select(a => a.Path).ToList();
            return Task.Run(() =>
            {
                List<(AppRow Row, bool Background)> rows;
                try
                {
                    rows = s.ListRunning(added)
                        .Select(a => (new AppRow { Path = a.Path, FileName = Path.GetFileName(a.Path), Name = a.Name }, a.Background))
                        .OrderBy(r => r.Item2) // иконки сначала тем, что видно сразу; порядок по имени сохраняется
                        .ToList();
                }
                catch (Exception) { rows = new(); }
                Dispatcher.UIThread.Post(() =>
                {
                    if (n != opening) return;
                    running = rows.Where(r => !r.Background).Select(r => r.Row).ToList();
                    background = rows.Where(r => r.Background).Select(r => r.Row).ToList();
                    loading = false;
                    Filter();
                });
                foreach (var (r, _) in rows) // иконки по одной, по мере извлечения
                {
                    if (n != Volatile.Read(ref opening)) return; // панель открыли заново: дальше грузит новая загрузка
                    var icon = s.LoadIcon(r.Path);
                    if (icon != null) Dispatcher.UIThread.Post(() => r.Icon = icon);
                }
            });
        }

        // Поиск по имени без учёта регистра; уже добавленные (то же имя .exe, хоть и в другой папке) не показываются.
        void Filter()
        {
            var q = (SearchBox.Text ?? "").Trim();
            var apps = State?.Apps.Concat(State.DirectApps).ToList();
            void Fill(ObservableCollection<AppRow> to, List<AppRow> from)
            {
                to.Clear();
                foreach (var r in from)
                    if (r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) && apps?.Any(a => AppListOps.SameFile(a.Path, r.Path)) != true)
                        to.Add(r);
            }
            Fill(shown, running);
            Fill(shownBg, background);
            BgButton.IsVisible = shownBg.Count > 0;
            BgText.Text = $"Фоновые · {shownBg.Count}";
            BgChevron.RenderTransform = bgOpen ? new RotateTransform(90) : null;
            BgList.IsVisible = bgOpen;
            RunningHint.Text = loading ? "Загрузка…"
                : shown.Count > 0 ? ""
                : running.Count + background.Count == 0 ? "Нет запущенных программ"
                : q.Length == 0 ? "Нет программ с открытым окном"
                : shownBg.Count > 0 ? ""
                : "Ничего не найдено";
            RunningHint.IsVisible = RunningHint.Text.Length > 0;
        }

        void OnProxyTab(object? sender, RoutedEventArgs e) => SetTab(false);
        void OnDirectTab(object? sender, RoutedEventArgs e) => SetTab(true);

        // Смена вкладки закрывает панель: добавление идёт в открытый список
        void SetTab(bool direct)
        {
            if (State is not { } s || s.DirectTab == direct) return;
            AddPopup.IsOpen = false;
            s.DirectTab = direct;
        }

        void OnPickRunning(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control { DataContext: AppRow r } || State is not { } s || !pickGuard.TryAccept(Environment.TickCount64)) return;
            s.RequestAddApp(r.Path);
            Filter(); // добавленное уходит из списка; панель остаётся открытой для следующих
        }

        void OnToggle(object? sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton { DataContext: AppRow r } t) State?.RequestSetAppEnabled(r.Path, t.IsChecked == true);
        }

        void OnRemove(object? sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: AppRow r } && removeGuard.TryAccept(Environment.TickCount64)) State?.RequestRemoveApp(r.Path);
        }

        async void PickFile()
        {
            AddPopup.IsOpen = false;
            if (State is not { } s || TopLevel.GetTopLevel(this) is not { } top) return;
            try
            {
                var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Выбрать .exe",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType("Программы (*.exe)") { Patterns = new[] { "*.exe" } } }
                });
                var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
                if (path != null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s.RequestAddApp(path);
            }
            catch (Exception ex) { s.AddLog("Ошибка: " + ex.Message); }
        }
    }
}
