using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Web.Script.Serialization;

namespace ViviTune
{
    public class Settings
    {
        public string Ip = "192.168.1.1";
        public int Port = 1818;
        public List<string> Apps = new List<string>();

        public static Settings Load(string path)
        {
            try { return new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings(); }
            catch { return new Settings(); } // ponytail: нет файла или он битый: молча берём значения по умолчанию
        }

        public void Save(string path)
        {
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(this));
        }

        // null, если всё корректно; иначе текст ошибки для пользователя
        public string Validate()
        {
            IPAddress ip;
            if (!IPAddress.TryParse(Ip, out ip)) return "Некорректный IP";
            if (Port < 1 || Port > 65535) return "Порт должен быть от 1 до 65535";
            if (ProcessNames().Count == 0) return "Добавьте хотя бы одно приложение";
            return null;
        }

        // Имена .exe для правил sing-box: без путей, без дублей, без самого sing-box.
        public List<string> ProcessNames()
        {
            var result = new List<string>();
            foreach (var path in Apps ?? new List<string>())
            {
                string name;
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
