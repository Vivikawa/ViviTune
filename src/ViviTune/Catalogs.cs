using System;
using System.Collections.Generic;
using System.Linq;

namespace ViviTune
{
    public enum ProxyMode { Socks5, Vless }

    public sealed record ItdogList(string Id, string Name, string Group, string Note, bool InRussiaInside);

    public static class Catalogs
    {
        public static IReadOnlyList<string> Groups { get; } = new[] { "Страны", "Категории", "Сервисы", "Хостинги" };

        const string AlsoInRu = "Домены уже есть в Russia inside; список добавляет IP-подсети (звонки и трафик по IP)";

        public static IReadOnlyList<ItdogList> Lists { get; } = new ItdogList[]
        {
            new("russia_inside", "Russia inside", "Страны", "Сайты, заблокированные или ограниченные в России", false),
            new("russia_outside", "Russia outside", "Страны", "Российские сайты, недоступные из-за рубежа", false),
            new("ukraine_inside", "Ukraine", "Страны", "Сайты, заблокированные на Украине", false),
            new("anime", "Anime", "Категории", "Аниме-сайты", true),
            new("block", "Block", "Категории", "Заблокированные ресурсы", true),
            new("geoblock", "GeoBlock", "Категории", "Сайты, закрывающиеся для российских адресов", true),
            new("news", "News", "Категории", "Новостные сайты", true),
            new("porn", "Porn", "Категории", "Сайты для взрослых", true),
            new("hodca", "H.O.D.C.A", "Категории", "Сайты Hetzner, OVH, DigitalOcean, Cloudflare, AWS, Akamai", false),
            new("youtube", "YouTube", "Сервисы", "Видео и домены Google для YouTube", true),
            new("discord", "Discord", "Сервисы", AlsoInRu, false),
            new("telegram", "Telegram", "Сервисы", "Подсети и домены Telegram", false),
            new("meta", "Meta", "Сервисы", AlsoInRu, false),
            new("twitter", "Twitter", "Сервисы", AlsoInRu, false),
            new("tiktok", "TikTok", "Сервисы", "Домены TikTok", true),
            new("hdrezka", "HDRezka", "Сервисы", "Домены HDRezka", true),
            new("google_ai", "Google AI", "Сервисы", "Gemini и другие сервисы Google AI", false),
            new("google_meet", "Google Meet", "Сервисы", "Видеозвонки Google Meet", false),
            new("google_play", "Google Play", "Сервисы", "Магазин Google Play", false),
            new("roblox", "Roblox", "Сервисы", "Игра Roblox", false),
            new("cloudflare", "Cloudflare", "Хостинги", "Подсети Cloudflare", false),
            new("cloudfront", "CloudFront", "Хостинги", "Подсети Amazon CloudFront", false),
            new("digitalocean", "DigitalOcean", "Хостинги", "Подсети DigitalOcean", false),
            new("hetzner", "Hetzner", "Хостинги", "Подсети Hetzner", false),
            new("ovh", "OVH", "Хостинги", "Подсети OVH", false),
        };

        public static ItdogList? ListById(string? id) =>
            Lists.FirstOrDefault(l => l.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        public static string ListUrl(string id) =>
            $"https://github.com/itdoginfo/allow-domains/releases/latest/download/{id}.srs";
    }
}
