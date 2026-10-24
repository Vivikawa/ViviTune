using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace ViviTune
{
    // Свой сайт пользователя: домен, IP или подсеть. Хранится в нормализованном виде.
    public static class SiteEntry
    {
        const string Whole = "Введите адрес целиком, например example.com";
        const string Bad = "Это не домен и не IP-адрес";

        // Записи через любые пробельные символы (в т.ч. неразрывный пробел), «,» и «;»
        public static IReadOnlyList<string> Split(string text) =>
            (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).SelectMany(w => w.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)).ToList();

        // Нормализованная запись или null + текст ошибки.
        public static string? Parse(string input, out string? error)
        {
            error = null;
            var s = (input ?? "").Trim();
            if (s.Contains("://"))
            {
                if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Host.Length == 0) { error = Bad; return null; }
                s = uri.Host.Trim('[', ']');
            }
            if (s.Length == 0 || s.Any(char.IsWhiteSpace)) { error = Bad; return null; }

            if (TryAddress(s, out var addr, out var addrError)) return addr;
            if (addrError) { error = Bad; return null; }

            var host = s;
            var cut = host.IndexOf('/');
            if (cut >= 0) host = host[..cut];
            cut = host.IndexOf(':');
            if (cut >= 0) host = host[..cut];
            if (host.StartsWith("*.")) host = host[2..];
            host = host.TrimStart('.');
            if (TryAddress(host, out addr, out _)) return addr; // 1.2.3.4:80

            if (!host.Contains('.')) { error = host.Length == 0 ? Bad : Whole; return null; }
            string ascii;
            try { ascii = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(host).ToLowerInvariant(); }
            catch (ArgumentException) { error = Bad; return null; }
            if (ascii.Split('.')[^1].All(char.IsAsciiDigit)) { error = Bad; return null; }
            return ascii;
        }

        // IP или подсеть: true + нормализованная запись; isBad — это адрес, но с неверной маской.
        static bool TryAddress(string s, out string? result, out bool isBad)
        {
            result = null; isBad = false;
            var slash = s.IndexOf('/');
            var ipPart = slash >= 0 ? s[..slash] : s;
            if (ipPart.Contains('%') || !IPAddress.TryParse(ipPart, out var ip)) return false;
            var v4 = ip.AddressFamily == AddressFamily.InterNetwork;
            if (v4 && ipPart.Count(c => c == '.') != 3) return false; // «1.2.3» IPAddress принимает за адрес
            if (!v4 && ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
            if (slash < 0) { result = ip.ToString(); return true; }

            var maskText = s[(slash + 1)..];
            var max = v4 ? 32 : 128;
            if (maskText.Length == 0 || !maskText.All(char.IsAsciiDigit)
                || !int.TryParse(maskText, NumberStyles.None, CultureInfo.InvariantCulture, out var mask) || mask > max)
            { isBad = true; return false; }
            var bytes = ip.GetAddressBytes();
            for (var i = 0; i < bytes.Length; i++)
            {
                var keep = Math.Clamp(mask - i * 8, 0, 8);
                bytes[i] &= (byte)(0xFF << (8 - keep));
            }
            result = new IPAddress(bytes) + "/" + mask;
            return true;
        }

        // IP или подсеть (иначе домен).
        public static bool IsAddress(string entry) => TryAddress(entry, out _, out _);

        // Для ip_cidr: адрес без маски получает /32 или /128.
        public static string Cidr(string entry)
        {
            if (entry.Contains('/')) return entry;
            return entry + (entry.Contains(':') ? "/128" : "/32");
        }

        // Домен из punycode в Unicode, адрес как есть.
        public static string Display(string entry)
        {
            if (IsAddress(entry)) return entry;
            try { return new IdnMapping().GetUnicode(entry); }
            catch (ArgumentException) { return entry; }
        }
    }
}
