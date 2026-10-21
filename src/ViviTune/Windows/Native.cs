using System.Runtime.InteropServices;

namespace ViviTune.Windows
{
    static class Native
    {
        // Второй экземпляр разрешает первому вывести окно на передний план (иначе Windows только мигнёт кнопкой).
        public static void AllowAnyForeground() => AllowSetForegroundWindow(-1); // ASFW_ANY

        // После подключения старые ответы DNS из кэша Windows могут вести мимо туннеля.
        public static void FlushDns() => DnsFlushResolverCache();

        [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
        static extern void DnsFlushResolverCache();

        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int processId);
    }
}
