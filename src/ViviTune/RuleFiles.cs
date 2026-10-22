using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace ViviTune
{
    // Правила живут в локальных rule-set'ах рядом с конфигом: sing-box перечитывает их при смене файла, без перезапуска.
    public static class RuleFiles
    {
        public const string Apps = "apps", Bypass = "bypass", Sites = "sites";

        public static string ListTag(string id) => "list-" + id;

        public static string PathOf(string rulesDir, string tag) =>
            Path.Combine(rulesDir, tag + (tag.StartsWith("list-") ? ".srs" : ".json"));

        // Пустой бинарный rule-set: ничего не ловит
        public static readonly byte[] EmptySrs = { 0x53, 0x52, 0x53, 0x02, 0x78, 0xda, 0x62, 0x00, 0x04, 0x00, 0x00, 0xff, 0xff, 0x00, 0x01, 0x00, 0x01 };

        static string RuleSet(params object[] rules) =>
            JsonSerializer.Serialize(ConfigBuilder.D("version", 3, "rules", rules), ConfigBuilder.Json.DictionaryStringObject);

        public static string ProcessJson(IEnumerable<string> names)
        {
            var n = names.ToArray();
            return n.Length == 0 ? RuleSet() : RuleSet(ConfigBuilder.D("process_name", n));
        }

        public static string SitesJson(IEnumerable<string> sites)
        {
            var all = sites.ToArray();
            var domains = all.Where(e => !SiteEntry.IsAddress(e)).ToArray();
            var cidrs = all.Where(SiteEntry.IsAddress).Select(SiteEntry.Cidr).ToArray();
            var rules = new List<object>();
            if (domains.Length > 0) rules.Add(ConfigBuilder.D("domain_suffix", domains));
            if (cidrs.Length > 0) rules.Add(ConfigBuilder.D("ip_cidr", cidrs));
            return RuleSet(rules.ToArray());
        }

        // listFiles: id списка -> путь к скачанному .srs. Файл с тем же содержимым не трогаем.
        public static void Write(string rulesDir, Settings s, IReadOnlyDictionary<string, string> listFiles)
        {
            Directory.CreateDirectory(rulesDir);
            void Put(string tag, byte[] bytes) => Replace(PathOf(rulesDir, tag), bytes);
            Put(Apps, Encoding.UTF8.GetBytes(ProcessJson(s.ProcessNames())));
            if (s.Mode == ProxyMode.Vless)
            {
                Put(Sites, Encoding.UTF8.GetBytes(SitesJson(s.Sites)));
                foreach (var l in Catalogs.Lists)
                    Put(ListTag(l.Id), s.Lists.Contains(l.Id) && listFiles.TryGetValue(l.Id, out var f) ? File.ReadAllBytes(f) : EmptySrs);
            }
            else
                Put(Bypass, Encoding.UTF8.GetBytes(ProcessJson(s.BypassNames())));
        }

        static void Replace(string path, byte[] bytes)
        {
            if (Retry(() => File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))) return;
            var tmp = path + ".tmp";
            try
            {
                File.WriteAllBytes(tmp, bytes);
                Retry(() => { File.Move(tmp, path, overwrite: true); return true; });
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        // sing-box может в этот момент читать файл: ждём до ~1 с
        static T Retry<T>(Func<T> f)
        {
            for (int i = 0; ; i++)
            {
                try { return f(); }
                catch (Exception e) when (i < 20 && e is IOException or UnauthorizedAccessException) { Thread.Sleep(50); }
            }
        }
    }
}
