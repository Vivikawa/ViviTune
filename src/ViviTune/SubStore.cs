using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace ViviTune
{
    // Сервер подписки: Server null — не поддерживается, Error — причина. Key стабилен между обновлениями
    public sealed record SubServer(string Key, string Name, Server? Server, string? Error);
    public sealed record SubCache(string Url, DateTime FetchedUtc, SubInfo Info, IReadOnlyList<SubServer> Servers);

    // Запрос подписки, ключи серверов и кэш subscription.json. Ссылки, ключи и URL в ошибки не попадают
    public static class SubStore
    {
        const string NotSub = "Это не подписка";

        // get(userAgent): сначала ViviTune/<версия>, при «Это не подписка» ещё раз как Happ
        public static SubParse? Fetch(Func<string, (int Status, IReadOnlyDictionary<string, string> Headers, string Body)?> get, out string? error)
        {
            var v = typeof(SubStore).Assembly.GetName().Version;
            var own = "ViviTune/" + (v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}");
            error = null;
            SubParse? last = null;
            foreach (var ua in new[] { own, "Happ/3.5.0" })
            {
                (int Status, IReadOnlyDictionary<string, string> Headers, string Body)? r;
                try { r = get(ua); }
                catch (Exception ex) { error = "Подписка не скачалась: " + ex.Message; return null; }
                if (r == null) { error = "Подписка не скачалась: нет ответа"; return null; }
                if (r.Value.Status != 200) { error = "Подписка не скачалась: HTTP " + r.Value.Status; return null; }
                last = Subscription.Parse(r.Value.Headers, r.Value.Body);
                if (last.Error != NotSub) break;
            }
            if (last!.Error != null) { error = last.Error; return null; }
            return last;
        }

        public const string NoUsableServers = "В подписке нет серверов, которые ViviTune умеет";

        // Скачать, разобрать и сохранить. Ответ без единого годного сервера не затирает рабочую копию (old): ошибка, на диск не пишем.
        // save может бросить (папка недоступна для записи) — работаем без кэша на диске.
        public static (SubCache? Cache, string? Error) Refresh(string url, Func<string, (int Status, IReadOnlyDictionary<string, string> Headers, string Body)?> get,
            Func<string, (Server? Server, string? Error)> decode, SubCache? old, Action<SubCache> save, DateTime nowUtc)
        {
            var p = Fetch(get, out var error);
            if (p == null) return (null, error);
            var cache = new SubCache(url, nowUtc, p.Info, Decode(p, decode));
            if (cache.Servers.All(s => s.Server == null) && old != null && old.Servers.Any(s => s.Server != null)) return (null, NoUsableServers);
            try { save(cache); } catch (Exception) { }
            return (cache, null);
        }

        // Ключ = «имя|host:port»; повторы получают « #2», « #3»; неподдерживаемые — «имя|-». Порядок как в подписке
        public static IReadOnlyList<SubServer> Decode(SubParse p, Func<string, (Server? Server, string? Error)> decode)
        {
            var seen = new Dictionary<string, int>();
            string Unique(string key)
            {
                seen[key] = seen.GetValueOrDefault(key) + 1;
                return seen[key] == 1 ? key : key + " #" + seen[key];
            }
            var list = new List<SubServer>();
            foreach (var en in p.Entries)
            {
                var (srv, err) = decode(en.Link);
                list.Add(srv == null
                    ? new SubServer(Unique(en.Name + "|-"), en.Name, null, err ?? "Ссылка не подходит")
                    : new SubServer(Unique(en.Name + "|" + srv.Host + ":" + srv.Port), en.Name, srv, null));
            }
            foreach (var s in p.Skipped) list.Add(new SubServer(Unique(s.Name + "|-"), s.Name, null, s.Reason));
            return list;
        }

        public static SubCache? Load(string path, string url)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))?.AsObject();
                if (root == null || root["url"]?.GetValue<string>() != url) return null;
                var i = root["info"]!.AsObject();
                var expire = i["expire"]?.GetValue<long>();
                var info = new SubInfo(i["title"]?.GetValue<string>(), i["interval"]!.GetValue<int>(), i["used"]!.GetValue<long>(), i["total"]!.GetValue<long>(),
                    expire == null ? null : DateTimeOffset.FromUnixTimeSeconds(expire.Value));
                var servers = new List<SubServer>();
                foreach (var n in root["servers"]!.AsArray())
                {
                    var o = n!.AsObject();
                    Server? srv = null;
                    if (o["outbound"] is JsonObject ob)
                        srv = new Server(o["serverName"]?.GetValue<string>() ?? "", (JsonObject)ob.DeepClone(),
                            (o["warnings"]?.AsArray() ?? new JsonArray()).Select(w => w!.GetValue<string>()).ToList());
                    servers.Add(new SubServer(o["key"]!.GetValue<string>(), o["name"]!.GetValue<string>(), srv, o["error"]?.GetValue<string>()));
                }
                var fetched = DateTime.Parse(root["fetched"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
                return new SubCache(url, DateTime.SpecifyKind(fetched, DateTimeKind.Utc), info, servers);
            }
            catch (Exception) { return null; }
        }

        // Атомарно: во временный файл и поверх старого
        public static void Save(string path, SubCache c)
        {
            var servers = new JsonArray();
            foreach (var s in c.Servers)
            {
                var o = new JsonObject { ["key"] = s.Key, ["name"] = s.Name };
                if (s.Server != null)
                {
                    o["serverName"] = s.Server.Name;
                    o["outbound"] = s.Server.Outbound.DeepClone();
                    o["warnings"] = new JsonArray(s.Server.Warnings.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray());
                }
                if (s.Error != null) o["error"] = s.Error;
                servers.Add(o);
            }
            var i = c.Info;
            var root = new JsonObject
            {
                ["url"] = c.Url,
                ["fetched"] = c.FetchedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["info"] = new JsonObject
                {
                    ["title"] = i.Title,
                    ["interval"] = i.IntervalHours,
                    ["used"] = i.Used,
                    ["total"] = i.Total,
                    ["expire"] = i.Expire?.ToUnixTimeSeconds(),
                },
                ["servers"] = servers,
            };
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }

        public static bool Stale(SubCache c, DateTime nowUtc) => nowUtc - c.FetchedUtc >= TimeSpan.FromHours(c.Info.IntervalHours);
    }
}
