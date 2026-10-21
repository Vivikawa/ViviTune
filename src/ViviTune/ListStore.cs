using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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
                    if (data.Length < 4 || data[0] != 0x53 || data[1] != 0x52 || data[2] != 0x53) throw new InvalidDataException("не SRS");
                    if (data[3] > 3) throw new InvalidDataException("версия SRS новее поддерживаемой sing-box 1.12.12");
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
            return new ListsResult(files, failed.ToList(), stale.ToList());
        }

        public static byte[] Download(string id)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            return http.GetByteArrayAsync(Catalogs.ListUrl(id)).GetAwaiter().GetResult();
        }
    }
}
