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

        private static bool Ru
        {
            get
            {
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
    }
}
