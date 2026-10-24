using System;
using System.IO;
using System.Text;

namespace ViviTune
{
    // Подробный лог sing-box для разработчика: строки с временем в файл, при превышении размера — в <имя>.old.log.
    public sealed class LogFile : IDisposable
    {
        readonly string path;
        readonly long maxBytes;
        readonly object gate = new();
        StreamWriter? w;

        public LogFile(string path, long maxBytes = 20 * 1024 * 1024)
        {
            this.path = path;
            this.maxBytes = maxBytes;
        }

        public void Write(string line)
        {
            lock (gate)
            {
                try
                {
                    w ??= Open();
                    if (w.BaseStream.Length > maxBytes)
                    {
                        w.Dispose();
                        w = null;
                        var old = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + ".old" + Path.GetExtension(path));
                        File.Move(path, old, true);
                        w = Open();
                    }
                    w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff ") + line);
                }
                catch { } // ponytail: файл занят или диск недоступен — лог не должен ронять приложение
            }
        }

        StreamWriter Open() => new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };

        public void Dispose()
        {
            lock (gate) { try { w?.Dispose(); } catch { } w = null; }
        }

        // Строка sing-box уровня TRACE/DEBUG/INFO: "+0500 2026-10-24 19:37:55 INFO …" или "INFO …" без префикса времени.
        public static bool IsVerbose(string line)
        {
            var parts = line.Split(' ', 5);
            for (var i = 0; i < parts.Length && i < 4; i++)
            {
                if (parts[i] is "TRACE" or "DEBUG" or "INFO") return true;
                if (parts[i] is "WARN" or "ERROR" or "FATAL" or "PANIC") return false;
            }
            return false;
        }
    }
}
