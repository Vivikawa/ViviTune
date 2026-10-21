using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ViviTune
{
    public sealed class AppEntry
    {
        public string Path { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }

    public class Settings
    {
        static readonly JsonCtx Json = new(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        public string Ip { get; set; } = "192.168.1.1";
        public int Port { get; set; } = 1818;
        public List<AppEntry> Apps { get; set; } = new List<AppEntry>();
        public int? DirectPort { get; set; } // порт SOCKS5 «Мимо Podkop»; null — не задан
        public List<AppEntry> DirectApps { get; set; } = new List<AppEntry>();
        [JsonConverter(typeof(JsonStringEnumConverter<ProxyMode>))]
        public ProxyMode Mode { get; set; } = ProxyMode.Socks5;
        public string VlessLink { get; set; } = "";
        public List<string> Lists { get; set; } = new List<string>(); // id включённых списков ITDog

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
                    else if (kv.Key.Equals("DirectPort", StringComparison.OrdinalIgnoreCase) && kv.Value is JsonValue dv && dv.TryGetValue<int>(out var dport)) s.DirectPort = dport;
                    else if (kv.Key.Equals("Apps", StringComparison.OrdinalIgnoreCase) && kv.Value is JsonArray arr) ReadApps(arr, s.Apps);
                    else if (kv.Key.Equals("DirectApps", StringComparison.OrdinalIgnoreCase) && kv.Value is JsonArray darr) ReadApps(darr, s.DirectApps);
                    else if (kv.Key.Equals("Mode", StringComparison.OrdinalIgnoreCase) && kv.Value is JsonValue mv && mv.TryGetValue<string>(out var mode))
                        s.Mode = mode.Equals("vless", StringComparison.OrdinalIgnoreCase) ? ProxyMode.Vless : ProxyMode.Socks5;
                    else if (kv.Key.Equals("VlessLink", StringComparison.OrdinalIgnoreCase) && kv.Value is JsonValue lv && lv.TryGetValue<string>(out var link)) s.VlessLink = link;
                    else if (kv.Key.Equals("Lists", StringComparison.OrdinalIgnoreCase) && kv.Value is JsonArray larr)
                    {
                        foreach (var item in larr)
                            if (item is JsonValue iv2 && iv2.TryGetValue<string>(out var lid) && Catalogs.ListById(lid) is { } known && !s.Lists.Contains(known.Id)) s.Lists.Add(known.Id);
                    }
                }
                return s;
            }
            catch { return new Settings(); } // ponytail: нет файла или он битый: молча берём значения по умолчанию
        }

        static void ReadApps(JsonArray arr, List<AppEntry> into)
        {
            foreach (var item in arr)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var str)) into.Add(new AppEntry { Path = str });
                else if (item is JsonObject o)
                {
                    try { var e = o.Deserialize(Json.AppEntry); if (e != null) into.Add(e); }
                    catch (JsonException) { } // элемент неверного вида: пропускаем
                }
            }
        }

        public void Save(string path)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json.Settings));
        }

        // null, если всё корректно; иначе текст ошибки для пользователя
        public string? Validate()
        {
            if (Mode == ProxyMode.Vless)
            {
                VlessServer.Parse(VlessLink, out var linkError);
                return linkError ?? (ProcessNames().Count + Lists.Count == 0 ? "Добавьте приложение или включите список сайтов" : null);
            }
            return ValidateProxy(Ip, Port) ?? ValidateDirectPort(DirectPort, Port)
                ?? (ProcessNames().Count + BypassNames().Count == 0 ? "Добавьте хотя бы одно приложение" : null);
        }

        // Только адрес прокси (для страницы настроек): null, если IP и порт корректны.
        public static string? ValidateProxy(string ip, int port)
        {
            if (!IPAddress.TryParse(ip, out _)) return "Некорректный IP";
            if (port < 1 || port > 65535) return "Порт должен быть от 1 до 65535";
            return null;
        }

        // Порт «Мимо Podkop» (необязателен): null, если не задан или корректен и не совпадает с портом прокси.
        public static string? ValidateDirectPort(int? directPort, int port)
        {
            if (directPort is not int d) return null;
            if (d < 1 || d > 65535) return "Порт «Мимо Podkop» должен быть от 1 до 65535";
            if (d == port) return "Порт «Мимо Podkop» совпадает с портом прокси";
            return null;
        }

        // Пусто -> null (не задан); число -> число; мусор -> 0 (ValidateDirectPort выдаст ошибку диапазона).
        public static int? ParseOptionalPort(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        }

        // Глубокая копия: новые списки и новые AppEntry.
        public Settings Clone() => new Settings
        {
            Ip = Ip, Port = Port, DirectPort = DirectPort,
            Mode = Mode, VlessLink = VlessLink, Lists = Lists.ToList(),
            Apps = Apps.Select(a => new AppEntry { Path = a.Path, Enabled = a.Enabled }).ToList(),
            DirectApps = DirectApps.Select(a => new AppEntry { Path = a.Path, Enabled = a.Enabled }).ToList()
        };

        // Имена .exe для правил sing-box: без путей, без дублей, без самого sing-box.
        public List<string> ProcessNames() => Names(Apps);

        // То же для «Мимо Podkop», кроме имён из «Через прокси» (она побеждает).
        public List<string> BypassNames()
        {
            var proxy = ProcessNames();
            return Names(DirectApps).Where(n => !proxy.Any(p => p.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        static List<string> Names(List<AppEntry> apps)
        {
            var result = new List<string>();
            foreach (var app in apps)
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

    // JSON без рефлексии: сборка обрезается (PublishTrimmed), и рефлексия в ней не работает.
    // Значения конфига sing-box (ConfigBuilder) — object, поэтому их типы перечислены здесь.
    [JsonSerializable(typeof(Settings))]
    [JsonSerializable(typeof(Dictionary<string, object>))]
    [JsonSerializable(typeof(object[]))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(bool))]
    internal partial class JsonCtx : JsonSerializerContext { }
}
