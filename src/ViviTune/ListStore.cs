using System;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ViviTune
{
    // Files: id -> путь к копии, которую можно отдать sing-box; Failed: копии нет и скачать не удалось;
    // Stale: скачать не удалось, работаем со старой копией (она есть в Files).
    public sealed record ListsResult(IReadOnlyDictionary<string, string> Files, IReadOnlyList<string> Failed, IReadOnlyList<string> Stale);

    public static class ListStore
    {
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

        public static ListsResult Prepare(string dir, IReadOnlyList<string> ids, Func<string, byte[]> download, DateTime nowUtc)
        {
            Directory.CreateDirectory(dir);
            var files = new ConcurrentDictionary<string, string>();
            var failed = new ConcurrentBag<string>();
            var stale = new ConcurrentBag<string>();
            Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = 6 }, id =>
            {
                var path = Path.Combine(dir, id + ".srs");
                bool exists = File.Exists(path);
                if (exists && nowUtc - File.GetLastWriteTimeUtc(path) < MaxAge) { files[id] = path; return; }
                try
                {
                    var data = download(id);
                    // магия SRS и версия формата: sing-box 1.14 читает до 5
                    if (data.Length < 4 || !data.AsSpan(0, 3).SequenceEqual("SRS"u8) || data[3] > 5) throw new InvalidDataException("не SRS 1–5");
                    var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllBytes(tmp, data);
                        File.Move(tmp, path, overwrite: true);
                    }
                    catch { try { File.Delete(tmp); } catch { } throw; }
                    files[id] = path;
                }
                catch
                {
                    if (exists) { files[id] = path; stale.Add(id); }
                    else failed.Add(id);
                }
            });
            // в порядке ids (качали параллельно): предупреждения и лог идут в порядке настроек
            return new ListsResult(files, ids.Where(failed.Contains).ToList(), ids.Where(stale.Contains).ToList());
        }

        // Как Prepare, но качает через VLESS-сервер (провайдер может резать или тормозить GitHub), не вышло — напрямую.
        // Временный sing-box (sb, без TUN) запускается, только если какой-то список надо скачать, и останавливается здесь же.
        public static ListsResult PrepareVia(string dir, IReadOnlyList<string> ids, ISingBox sb, JsonObject proxy, DateTime nowUtc)
        {
            Directory.CreateDirectory(dir);
            int port = Session.FreePort();
            var cfg = Path.Combine(dir, $"fetch-{port}.json"); // со своим портом: два вызова подряд не делят файл
            var tunnel = new Lazy<IWebProxy>(() => // исключение кэшируется: не поднялся — все списки сразу напрямую
            {
                File.WriteAllText(cfg, ConfigBuilder.BuildFetch(proxy, port));
                sb.Start(cfg);
                for (var end = DateTime.UtcNow.AddSeconds(3); ; Thread.Sleep(50))
                    try { using var c = new TcpClient(); c.Connect(IPAddress.Loopback, port); break; }
                    catch (SocketException) when (DateTime.UtcNow < end) { }
                return new WebProxy("http://127.0.0.1:" + port);
            });
            try
            {
                return Prepare(dir, ids, id =>
                {
                    try { return Download(id, tunnel.Value); }
                    catch { return Download(id); }
                }, nowUtc);
            }
            finally
            {
                sb.Stop();
                try { File.Delete(cfg); } catch { } // в нём ключ VLESS
            }
        }

        // proxy null — системный, как у браузера.
        public static byte[] Download(string id, IWebProxy? proxy = null)
        {
            using var http = new HttpClient(new HttpClientHandler { Proxy = proxy }) { Timeout = TimeSpan.FromSeconds(15) };
            return http.GetByteArrayAsync(Catalogs.ListUrl(id)).GetAwaiter().GetResult();
        }
    }
}
