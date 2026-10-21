using System;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ViviTune.Ui
{
    // Лог: прокрутка держится внизу, пока пользователь сам не прокрутил вверх.
    public partial class LogPage : UserControl
    {
        bool stick = true;

        public LogPage()
        {
            InitializeComponent();
            Scroll.ScrollChanged += OnScrollChanged;
        }

        void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            // новые строки, первый показ страницы или смена размера окна
            if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
            {
                if (stick) Dispatcher.UIThread.Post(() => Scroll.ScrollToEnd());
            }
            else if (e.OffsetDelta.Y != 0) // прокрутил пользователь
                stick = Scroll.Offset.Y >= Scroll.Extent.Height - Scroll.Viewport.Height - 4;
        }

        // Весь лог одним текстом. Буфер обмена может держать другая программа: тогда строка в лог, не падение.
        async void OnCopy(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewState s || TopLevel.GetTopLevel(this)?.Clipboard is not { } clip) return;
            try { await clip.SetTextAsync(string.Join(Environment.NewLine, s.LogLines)); }
            catch (Exception ex) { s.AddLog("Не удалось скопировать: " + ex.Message); }
        }
    }
}
