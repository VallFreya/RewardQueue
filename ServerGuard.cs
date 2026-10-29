using System.Security.Cryptography;
using System.Text;

namespace RewardQueue
{
    /// <summary>
    /// Правила доступа к локальному серверу, без сети и OBS: порт, ключ в адресе, проверка заголовков Host и Origin.
    /// Ключ защищает очередь от других программ на компьютере, Host и Origin от чужих страниц в браузере
    /// (в том числе от подмены DNS-имени на 127.0.0.1).
    /// </summary>
    internal static class ServerGuard
    {
        public const int DefaultPort = 47615;
        public const int MinPort = 1024;
        public const int MaxPort = 65535;

        public const string LoopbackHost = "127.0.0.1";

        public static int ClampPort(int port) => port < MinPort || port > MaxPort ? DefaultPort : port;

        /// <summary>Новый случайный ключ: 24 байта из системного генератора, в виде безопасной для адреса строки</summary>
        public static string NewKey()
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(24);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>Ключ годится для использования: достаточно длинный и без символов, которые ломают адрес</summary>
        public static bool IsValidKey(string? key)
        {
            if (string.IsNullOrEmpty(key) || key.Length < 16 || key.Length > 128) return false;
            foreach (char c in key)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')) return false;
            }
            return true;
        }

        /// <summary>Сравнение ключей за одно и то же время независимо от того, где они различаются</summary>
        public static bool KeyMatches(string? expected, string? given)
        {
            if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(given)) return false;
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given));
        }

        /// <summary>Host запроса должен быть адресом нашего сервера: так отсекаются запросы чужих страниц через DNS-имя</summary>
        public static bool HostAllowed(string? host, int port)
        {
            if (string.IsNullOrEmpty(host)) return false;
            return string.Equals(host, $"{LoopbackHost}:{port}", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, $"localhost:{port}", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Origin нужен у запросов, меняющих очередь: наши страницы шлют его всегда, и он должен быть нашим же адресом</summary>
        public static bool OriginAllowed(string? origin, int port)
        {
            if (string.IsNullOrEmpty(origin)) return false;
            return string.Equals(origin, $"http://{LoopbackHost}:{port}", StringComparison.OrdinalIgnoreCase)
                || string.Equals(origin, $"http://localhost:{port}", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Достаёт значение параметра key из строки запроса вида «?key=abc&amp;x=1»</summary>
        public static string? KeyFromQuery(string? query)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (part.AsSpan(0, eq).Equals("key", StringComparison.Ordinal)) return Uri.UnescapeDataString(part[(eq + 1)..]);
            }
            return null;
        }

        /// <summary>Адрес страницы для вставки в OBS</summary>
        public static string BuildUrl(int port, string key, string page) => $"http://{LoopbackHost}:{port}/{page}?key={key}";
    }
}
