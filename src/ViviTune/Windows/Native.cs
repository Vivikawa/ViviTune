using System.Runtime.InteropServices;

namespace ViviTune.Windows
{
    static class Native
    {
        // Окно сообщения до старта Avalonia (второй экземпляр).
        public static void Message(string text) => MessageBoxW(IntPtr.Zero, text, "ViviTune", 0);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
    }
}
