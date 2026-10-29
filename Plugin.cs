using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace RewardQueue
{
    public static class Plugin
    {
        public static IntPtr ModuleContext { get; private set; }
        private static readonly ObsFrontendCb MenuCallbackDelegate = OnMenuClicked;

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

        /// <summary>Порт локального сервера и ключ в адресах страниц (см. ServerGuard)</summary>
        public static int Port { get; private set; } = ServerGuard.DefaultPort;
        public static string AccessKey { get; private set; } = "";

        /// <summary>Сколько дней хранится невыполненная запись очереди</summary>
        public static int RetentionDays { get; private set; } = QueueState.DefaultRetentionDays;

        /// <summary>Свой Client ID Twitch из config.json (пусто: встроенный). Хранится, чтобы запись конфига его не стирала</summary>
        public static string ClientIdOverride { get; private set; } = "";

        // Проверка токена Twitch раз в час: refresh-токен обновляется при использовании, а отозванный вход надо заметить
        private static System.Threading.Timer? _hourlyTimer;

        /// <summary>Какие награды отслеживаются (неизменяемый снимок, заменяется целиком)</summary>
        internal static RewardSettings Rewards { get; private set; } = RewardSettings.Empty;
        private static readonly object RewardsLock = new object();

        private static IntPtr _moduleName = IntPtr.Zero;
        private static int _settingsOpen;

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

        [DllImport("obs-frontend-api", CallingConvention = CallingConvention.Cdecl)]
        private static extern void obs_frontend_add_tools_menu_item(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            ObsFrontendCb callback,
            IntPtr privateData
        );

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ObsFrontendCb(IntPtr privateData);

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
                obs_frontend_add_tools_menu_item(Loc.PluginName, MenuCallbackDelegate, IntPtr.Zero);
                LogInfo("Tools menu item registered.");
            }
            catch (Exception ex)
            {
                LogError($"Failed to register menu item: {ex.Message}");
            }

            // Раздельно: сбой чтения настроек не должен лишать плагин сервера
            try
            {
                LoadConfig();
                EnsureAccessKey();
            }
            catch (Exception ex)
            {
                LogError($"Failed to load the config: {ex.Message}");
            }

            try
            {
                LocalServer.StartCurrent(Port, AccessKey, new QueueApi());
            }
            catch (Exception ex)
            {
                LogError($"Failed to start the local server: {ex.Message}");
            }

            try
            {
                StartTwitchLogin();
            }
            catch (Exception ex)
            {
                LogError($"Failed to start the Twitch login: {ex.Message}");
            }

            return true;
        }

        // Очередь, вход, API и клиент EventSub. Вход читается и проверяется в фоне; когда он подтверждён, клиент подключается к EventSub
        // и заказы выбранных наград попадают в очередь. Дальше раз в час проверяются вход и срок хранения записей
        private static void StartTwitchLogin()
        {
            QueueService.Current = new QueueService(Path.Combine(GetConfigDirectory(), QueueStore.FileName), () => DateTimeOffset.UtcNow, RetentionDays);
            LogInfo($"Queue restored: {QueueService.Current.Count} entries");

            // Любое изменение очереди или списка отмеченных наград сразу уходит на открытые страницы (док и оверлей)
            QueueService.Current.Changed += () => LocalServer.Current?.PushState();
            RewardsChanged += () => LocalServer.Current?.PushState();

            var auth = new TwitchAuth(Path.Combine(GetConfigDirectory(), TokenVault.FileName), ClientIdOverride);
            TwitchAuth.Current = auth;
            var api = new TwitchApi(auth);
            TwitchApi.Current = api;

            var client = new EventSubClient(
                connect: WebSocketEventSubSocket.ConnectAsync,
                subscribe: api.SubscribeRedemptionsAsync,
                onRedemption: OnRedemption,
                onUnauthorized: auth.RevalidateAsync);
            EventSubClient.Current = client;

            // Клиент работает, пока есть вход: вышли или вход истёк, и он останавливается
            auth.Changed += () => SyncEventSub(auth, client);

            _ = Task.Run(async () =>
            {
                try
                {
                    await auth.InitializeAsync().ConfigureAwait(false);
                    SyncEventSub(auth, client);
                }
                catch (Exception ex)
                {
                    LogError($"Twitch login initialization failed: {ex.Message}");
                }
            });

            _hourlyTimer = new System.Threading.Timer(OnHourlyTimer, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        }

        private static void SyncEventSub(TwitchAuth auth, EventSubClient client)
        {
            try
            {
                if (auth.Snapshot.State == AuthState.SignedIn) client.Start();
                else _ = client.StopAsync();
            }
            catch (Exception ex)
            {
                LogError($"EventSub start/stop failed: {ex.Message}");
            }
        }

        // Заказ награды из EventSub (поток клиента): в очередь попадают только отмеченные награды
        private static void OnRedemption(RedemptionEvent redemption)
        {
            string? queueId = Rewards.QueueFor(redemption.RewardId);
            if (queueId == null) return;

            var queue = QueueService.Current;
            if (queue == null) return;

            bool added = queue.Add(new QueueEntry
            {
                id = redemption.Id,
                source = QueueEntry.SourceTwitch,
                queue_id = queueId,
                reward_id = redemption.RewardId,
                reward_title = redemption.RewardTitle,
                user_name = redemption.UserName.Length > 0 ? redemption.UserName : redemption.UserLogin,
                user_input = redemption.UserInput,
                redeemed_at = redemption.RedeemedAt
            });

            // ники и тексты зрителей в журнал не пишем
            LogInfo(added ? "Reward redemption added to the queue" : "Reward redemption ignored (already known or empty)");
        }

        // Поток пула: исключение отсюда завершило бы процесс OBS
        private static void OnHourlyTimer(object? state)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    QueueService.Current?.PurgeExpired();
                    var auth = TwitchAuth.Current;
                    if (auth != null) await auth.RevalidateAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogError($"Hourly check failed: {ex.Message}");
                }
            });
        }

        [UnmanagedCallersOnly(EntryPoint = "obs_module_unload", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void ObsModuleUnload()
        {
            LogInfo("=== Plugin ObsModuleUnload ===");
            try
            {
                CloseSettingsWindow();
                _hourlyTimer?.Dispose();
                _hourlyTimer = null;
                // клиент EventSub останавливается первым: пока он работает, он держит поток в нашей DLL
                var client = EventSubClient.Current;
                EventSubClient.Current = null;
                if (client != null)
                {
                    client.StopAsync().GetAwaiter().GetResult();
                    client.Dispose();
                }
                TwitchApi.Current?.Dispose();
                TwitchApi.Current = null;
                TwitchAuth.Current?.Dispose();
                TwitchAuth.Current = null;
                QueueService.Current = null;
                LocalServer.StopCurrent();
            }
            catch (Exception ex)
            {
                // исключение из экспортируемого метода уронило бы OBS при выходе
                LogError($"Error during unload: {ex.Message}");
            }
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
                Port = ServerGuard.ClampPort(config.port);
                AccessKey = ServerGuard.IsValidKey(config.access_key) ? config.access_key : "";
                RetentionDays = QueueState.ClampRetentionDays(config.retention_days);
                ClientIdOverride = TwitchAuth.ValidClientId(config.client_id) ? config.client_id : "";
                Rewards = RewardSettings.FromConfig(config.known_rewards, config.queues);
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

        /// <summary>Ключ создаётся один раз и сохраняется: адреса в доке и источниках OBS должны переживать перезапуск</summary>
        internal static void EnsureAccessKey()
        {
            if (ServerGuard.IsValidKey(AccessKey)) return;
            AccessKey = ServerGuard.NewKey();
            WriteConfig();
        }

        private static bool WriteConfig()
        {
            string configPath = "";

            try
            {
                configPath = GetConfigFilePath();
                var config = new PluginConfig { theme = ThemeDark ? "dark" : "light", port = Port, access_key = AccessKey, retention_days = RetentionDays, client_id = ClientIdOverride,
                    known_rewards = Rewards.Known.ToList(), queues = Rewards.Queues.ToList() };

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
        /// <summary>Меняет настройки наград (список или отметки) и записывает конфиг. Вызывается из окна и после обновления списка</summary>
        internal static bool UpdateRewards(Func<RewardSettings, RewardSettings> change)
        {
            bool written;
            lock (RewardsLock)
            {
                Rewards = change(Rewards);
                written = WriteConfig();
            }

            try
            {
                RewardsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                LogError($"Rewards change handler failed: {ex.Message}");
            }
            return written;
        }

        /// <summary>Список отмеченных наград изменился (док предлагает их при ручном добавлении)</summary>
        internal static event Action? RewardsChanged;

        public static bool SetTheme(bool dark)
        {
            ThemeDark = dark;
            return WriteConfig();
        }

        private static void OnMenuClicked(IntPtr privateData)
        {
            // второе окно настроек не открываем
            if (Interlocked.CompareExchange(ref _settingsOpen, 1, 0) != 0) return;

            var thread = new Thread(() =>
            {
                try
                {
                    // Исключение в обработчике события окна иначе открыло бы системное окно ошибки .NET поверх OBS.
                    // Вместо этого пишем в журнал, и окно продолжает работать
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException, threadScope: true);
                    if (Interlocked.Exchange(ref _threadExceptionHooked, 1) == 0)
                    {
                        Application.ThreadException += (s, e) => LogError($"Unhandled exception in the settings window: {e.Exception}");
                    }

                    using var form = new SettingsForm();
                    _settingsForm = form;
                    form.ShowDialog();
                }
                catch (Exception ex)
                {
                    LogError($"Settings window failed: {ex}");
                }
                finally
                {
                    _settingsForm = null;
                    Interlocked.Exchange(ref _settingsOpen, 0);
                }
            });
            thread.IsBackground = true; // окно не должно удерживать процесс OBS при закрытии
            thread.SetApartmentState(ApartmentState.STA);
            _settingsThread = thread;
            thread.Start();
        }

        private static int _threadExceptionHooked;
        private static volatile SettingsForm? _settingsForm;
        private static Thread? _settingsThread;

        /// <summary>
        /// Закрывает окно настроек, если оно открыто. Вызывается при выгрузке плагина: окно живёт в своём потоке
        /// и иначе продолжало бы работать с кодом, который OBS уже выгружает
        /// </summary>
        private static void CloseSettingsWindow()
        {
            var form = _settingsForm;
            var thread = _settingsThread;
            if (form == null || thread == null || !thread.IsAlive) return;

            try
            {
                if (form.IsHandleCreated) form.BeginInvoke(new Action(form.Close));
                thread.Join(3000);
            }
            catch (Exception ex)
            {
                LogError($"Failed to close the settings window: {ex.Message}");
            }
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
