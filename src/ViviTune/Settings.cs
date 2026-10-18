using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ViviTune
{
    public sealed class AppEntry
    {
        public string Path { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }

    public class Settings
    {
        static readonly JsonSerializerOptions Json = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public string Ip { get; set; } = "192.168.1.1";
        public int Port { get; set; } = 1818;
        public List<AppEntry> Apps { get; set; } = new List<AppEntry>();

        // Читает и v1 (Apps: ["путь"]), и v2 (Apps: [{Path, Enabled}]); неизвестные элементы пропускает.
        public static Settings Load(string path)
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                var s = new Settings();
                if (root == null) return s;
                foreach (var kv in root)
                {
                    if (kv.Key.Equals("Ip", StringComparison.OrdinalIgnoreCase) && kv.Value is JsonValue iv && iv.TryGetValue<string>(out var ip)) s.Ip = ip;
                    else if (kv.Key.Equals("Port", StringComparison.OrdinalIgnoreCase) && kv.Value is JsonValue pv && pv.TryGetValue<int>(out var port)) s.Port = port;
                    else if (kv.Key.Equals("Apps", StringComparison.OrdinalIgnoreCase) && kv.Value is JsonArray arr)
                    {
                        foreach (var item in arr)
                        {
                            if (item is JsonValue v && v.TryGetValue<string>(out var str)) s.Apps.Add(new AppEntry { Path = str });
                            else if (item is JsonObject o)
                            {
                                try { var e = o.Deserialize<AppEntry>(Json); if (e != null) s.Apps.Add(e); }
                                catch (JsonException) { } // элемент неверного вида: пропускаем
                            }
                        }
                    }
                }
                return s;
            }
            catch { return new Settings(); } // ponytail: нет файла или он битый: молча берём значения по умолчанию
        }

        public void Save(string path)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }

        // null, если всё корректно; иначе текст ошибки для пользователя
        public string? Validate()
        {
            return ValidateProxy(Ip, Port) ?? (ProcessNames().Count == 0 ? "Добавьте хотя бы одно приложение" : null);
        }

        // Только адрес прокси (для страницы настроек): null, если IP и порт корректны.
        public static string? ValidateProxy(string ip, int port)
        {
            if (!IPAddress.TryParse(ip, out _)) return "Некорректный IP";
            if (port < 1 || port > 65535) return "Порт должен быть от 1 до 65535";
            return null;
        }

        // Имена .exe для правил sing-box: без путей, без дублей, без самого sing-box.
        public List<string> ProcessNames()
        {
            var result = new List<string>();
            foreach (var app in Apps)
            {
                if (!app.Enabled) continue;
                var path = app.Path;
                string? name;
                try { name = Path.GetFileName(path); }
                catch (ArgumentException) { continue; } // недопустимые символы в пути
                if (string.IsNullOrEmpty(name)) continue;
                if (name.Equals("sing-box.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (result.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(name);
            }
            return result;
        }
    }
}
