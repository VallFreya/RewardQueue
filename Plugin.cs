using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RewardQueue
{
    public static class Plugin
    {
        public static IntPtr ModuleContext { get; private set; }

        private static readonly string LogFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp",
            "RewardQueue_debug.log"
        );

        private static readonly object LogLock = new object();

        // Контекст сериализации с поддержкой NativeAOT и сохранения кириллицы
        private static readonly PluginConfigJsonContext JsonContext = new PluginConfigJsonContext(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        /// <summary>Тема окна настроек: тёмная по умолчанию, выбор хранится в config.json</summary>
        public static bool ThemeDark { get; private set; } = true;
        private static IntPtr _moduleName = IntPtr.Zero;

        public static void LogInfo(string msg) => WriteLog("INFO", msg);
        public static void LogWarn(string msg) => WriteLog("WARN", msg);
        public static void LogError(string msg) => WriteLog("ERROR", msg);

        /// <summary>Выключается в проверках, чтобы они не писали в настоящий журнал плагина</summary>
        internal static bool LogEnabled { get; set; } = true;

        private static void WriteLog(string level, string message)
        {
            if (!LogEnabled) return;

            lock (LogLock)
            {
                try
                {
                    string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
                    File.AppendAllText(LogFilePath, line, Encoding.UTF8);
                }
                catch { }
            }
        }

        // --- OBS Native Imports ---
        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr obs_module_get_config_path(IntPtr module, [MarshalAs(UnmanagedType.LPStr)] string file);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void bfree(IntPtr ptr);

        // --- Module Exports ---
        [UnmanagedCallersOnly(EntryPoint = "obs_module_set_pointer", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void ObsModuleSetPointer(IntPtr module)
        {
            ModuleContext = module;
        }

        [UnmanagedCallersOnly(EntryPoint = "obs_module_ver", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static uint ObsModuleVer()
        {
            return (30u << 24) | (0u << 16) | 0u;
        }

        [UnmanagedCallersOnly(EntryPoint = "obs_module_name", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static unsafe char* ObsModuleName()
        {
            // OBS ждёт UTF-8, строку создаём один раз и держим до выгрузки
            if (_moduleName == IntPtr.Zero)
            {
                _moduleName = Marshal.StringToCoTaskMemUTF8(Loc.PluginName);
            }
            return (char*)_moduleName;
        }

        [UnmanagedCallersOnly(EntryPoint = "obs_module_load", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static bool ObsModuleLoad()
        {
            RotateLogIfLarge();
            LogInfo("===========================================");
            LogInfo("=== Plugin ObsModuleLoad Executing ===");
            LogInfo($"Log File Path: {LogFilePath}");

            try
            {
                LoadConfig();
            }
            catch (Exception ex)
            {
                LogError($"Failed to load the config: {ex.Message}");
            }

            return true;
        }

        [UnmanagedCallersOnly(EntryPoint = "obs_module_unload", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void ObsModuleUnload()
        {
            LogInfo("=== Plugin ObsModuleUnload ===");
        }

        /// <summary>Путь к config.json вместо выданного OBS (для проверок вне OBS)</summary>
        internal static string? ConfigPathOverride { get; set; }

        /// <summary>Папка настроек плагина: рядом с config.json лежит и queue.json</summary>
        internal static string GetConfigDirectory()
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(GetConfigFilePath()));
            return string.IsNullOrEmpty(dir) ? "." : dir;
        }

        private static string GetConfigFilePath()
        {
            if (ConfigPathOverride != null) return ConfigPathOverride;
            if (ModuleContext == IntPtr.Zero) return "reward_queue_config.json";

            // Если OBS не отдал папку настроек или её нельзя создать, плагин не должен падать
            try
            {
                IntPtr pathPtr = obs_module_get_config_path(ModuleContext, "config.json");
                string? path = pathPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(pathPtr) : null;
                if (pathPtr != IntPtr.Zero)
                {
                    // строка выделена в OBS, освобождать её нужно через bfree; сбой здесь не должен менять путь к конфигу
                    try { bfree(pathPtr); } catch { }
                }

                if (!string.IsNullOrEmpty(path))
                {
                    string? dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    return path;
                }
            }
            catch (Exception ex)
            {
                LogError($"Failed to get the OBS config folder: {ex.Message}");
            }

            return FallbackConfigPath();
        }

        // Запасное место для настроек: папка в профиле пользователя
        private static string FallbackConfigPath()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RewardQueue");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "config.json");
            }
            catch
            {
                return "reward_queue_config.json";
            }
        }

        public static void LoadConfig()
        {
            string configPath = GetConfigFilePath();
            if (!File.Exists(configPath))
            {
                LogWarn($"Config file not found at '{configPath}'. Defaults are used.");
                return;
            }

            try
            {
                string json = File.ReadAllText(configPath, Encoding.UTF8);

                if (!TryParseConfig(json, out var config, out string? error))
                {
                    // Файл не разобрался (правили вручную с ошибкой или оборвалась запись). Окно настроек при первом
                    // сохранении перезапишет его, поэтому сначала сохраняем копию рядом
                    LogError($"Config file '{configPath}' is not valid and was not loaded: {error}");
                    BackupBrokenConfig(configPath);
                    return;
                }

                ThemeDark = !string.Equals(config!.theme, "light", StringComparison.OrdinalIgnoreCase);
                LogInfo($"Loaded config from '{configPath}'");
            }
            catch (Exception ex)
            {
                LogError($"Error reading config file: {ex.Message}");
            }
        }

        /// <summary>Разбирает текст config.json. false и описание ошибки, если это не JSON нужной формы</summary>
        internal static bool TryParseConfig(string json, out PluginConfig? config, out string? error)
        {
            config = null;
            error = null;

            try
            {
                config = JsonSerializer.Deserialize(json, PluginConfigJsonContext.Default.PluginConfig);
                if (config == null) error = "the file is empty or contains null";
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            return config != null;
        }

        private static void BackupBrokenConfig(string configPath)
        {
            try
            {
                File.Copy(configPath, configPath + ".broken", overwrite: true);
                LogWarn($"A copy of the unreadable config was saved to '{configPath}.broken'.");
            }
            catch (Exception ex)
            {
                LogError($"Could not back up the unreadable config: {ex.Message}");
            }
        }

        private static bool WriteConfig()
        {
            string configPath = "";

            try
            {
                configPath = GetConfigFilePath();
                var config = new PluginConfig { theme = ThemeDark ? "dark" : "light" };

                string json = JsonSerializer.Serialize(config, JsonContext.PluginConfig);
                AtomicFile.WriteAllText(configPath, json, Encoding.UTF8);

                LogInfo($"Saved config to file '{configPath}'");
                return true;
            }
            catch (Exception ex)
            {
                LogError($"Failed to save config to '{configPath}': {ex.Message}");
                return false;
            }
        }

        /// <summary>Запоминает тему окна</summary>
        public static void SetTheme(bool dark)
        {
            ThemeDark = dark;
            WriteConfig();
        }

        // Журнал растёт, пока плагин жив; при загрузке слишком большой файл откладывается в сторону
        private static void RotateLogIfLarge()
        {
            try
            {
                var info = new FileInfo(LogFilePath);
                if (info.Exists && info.Length > 5 * 1024 * 1024)
                {
                    lock (LogLock) File.Move(LogFilePath, LogFilePath + ".old", overwrite: true);
                }
            }
            catch
            {
                // журнал не критичен
            }
        }
    }
}
