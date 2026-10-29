using System.Runtime.InteropServices;

namespace RewardQueue
{
    /// <summary>
    /// Тексты интерфейса. Язык берётся из OBS (obs_get_locale): русский для ru-*, иначе английский.
    /// Пункт меню Tools нельзя переименовать после регистрации, поэтому язык определяется один раз
    /// и меняется вместе с языком OBS (после перезапуска OBS).
    /// </summary>
    internal static class Loc
    {
        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr obs_get_locale();

        private static bool _initialized;
        private static bool _ru;

        /// <summary>Язык вместо выбранного в OBS (для проверок вне OBS): true русский, false английский</summary>
        internal static bool? LanguageOverride { get; set; }

        private static bool Ru
        {
            get
            {
                if (LanguageOverride.HasValue) return LanguageOverride.Value;
                if (!_initialized)
                {
                    try
                    {
                        string? locale = Marshal.PtrToStringUTF8(obs_get_locale());
                        _ru = locale != null && locale.StartsWith("ru", StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        _ru = false;
                    }
                    _initialized = true;
                }
                return _ru;
            }
        }

        // Название плагина: пункт меню Tools и заголовок окна
        public static string PluginName => Ru ? "Очередь наград" : "Reward Queue";

        public static string WindowTitle => Ru ? $"{PluginName} — настройки" : $"{PluginName} Settings";
        public static string Ok => Ru ? "ОК" : "OK";
        public static string Cancel => Ru ? "Отмена" : "Cancel";

        public static string Theme => Ru ? "Тема:" : "Theme:";
        public static string ThemeDarkName => Ru ? "Тёмная" : "Dark";
        public static string ThemeLightName => Ru ? "Светлая" : "Light";

        // Блок «Адреса для OBS»
        public static string AddressesTitle => Ru ? "Адреса для OBS" : "Addresses for OBS";
        public static string AddressesIntro => Ru
            ? "Плагин работает как небольшой веб-сервер на этом компьютере. Вставьте адреса в OBS один раз: при перезапуске OBS они не меняются."
            : "The plugin runs a small web server on this computer. Paste the addresses into OBS once: they do not change when OBS restarts.";
        public static string DockLabel => Ru ? "Док с очередью (панель управления)" : "Queue dock (control panel)";
        public static string DockHow => Ru
            ? "Как добавить: в меню «Док-панели» выберите «Пользовательские док-панели браузера», введите любое название, вставьте адрес в колонку URL и нажмите «Применить»."
            : "How to add: open the Docks menu, choose Custom Browser Docks, enter any name, paste the address into the URL column and click Apply.";
        public static string OverlayLabel => Ru ? "Источник для трансляции (то, что видят зрители)" : "Stream source (what viewers see)";
        public static string OverlayHow => Ru
            ? "Как добавить: в нужной сцене нажмите «+» в списке источников, выберите «Браузер», вставьте адрес в поле URL и задайте ширину и высоту блока."
            : "How to add: in the scene click + in the Sources list, choose Browser, paste the address into the URL field and set the width and height of the block.";
        public static string SecretWarning => Ru
            ? "В адресах есть секретный ключ. Не показывайте их на трансляции и не отправляйте другим."
            : "The addresses contain a secret key. Do not show them on stream and do not send them to anyone.";
        public static string Copy => Ru ? "Копировать" : "Copy";
        public static string Copied => Ru ? "Скопировано" : "Copied";

        // Блок «Состояние»
        public static string StatusTitle => Ru ? "Состояние" : "Status";
        public static string ServerRunning(int port) => Ru ? $"Локальный сервер работает: 127.0.0.1, порт {port}" : $"Local server is running: 127.0.0.1, port {port}";
        public static string ServerFailed(string reason) => Ru ? $"Локальный сервер не запущен: {reason}" : $"Local server is not running: {reason}";
        public static string PortBusy => Ru ? "порт занят другой программой" : "the port is used by another program";
        public static string OpenPages(int count) => Ru ? $"Открытых страниц очереди: {count}" : $"Open queue pages: {count}";
        public static string ProbeButton => Ru ? "Проверить связь с Twitch" : "Check the Twitch connection";
        public static string ProbeNotRun => Ru ? "Проверка ещё не выполнялась" : "Not checked yet";
        public static string ProbeRunning => Ru ? "Проверяю…" : "Checking…";
        public static string ProbeOk => Ru ? "Связь с Twitch есть" : "Connected to Twitch";
        public static string ProbeFailed(string detail) => Ru ? $"Не удалось подключиться: {detail}" : $"Could not connect: {detail}";
    }
}
