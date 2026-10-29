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

        // Подпись внизу окна: обычный текст, затем «сохраняю», «сохранено» и снова обычный текст
        public static string AutoSaveHint => Ru
            ? "Настройки сохраняются автоматически, как только вы что-то меняете."
            : "Settings are saved automatically as soon as you change anything.";
        public static string SavingHint => Ru ? "Сохраняю…" : "Saving…";
        public static string SavedHint => Ru ? "Настройки сохранены" : "Settings saved";
        public static string SaveFailedHint => Ru
            ? "Не удалось сохранить настройки (подробности в журнале). Изменения действуют до перезапуска OBS."
            : "Could not save the settings (see the log). The changes work until OBS restarts.";

        // Блок «Вход в Twitch»
        public static string TwitchTitle => Ru ? "Вход в Twitch" : "Twitch login";
        public static string AuthSignedOut => Ru
            ? "Вход не выполнен. Пока вы не войдёте, плагин не увидит заказы наград."
            : "Not signed in. The plugin cannot see reward redemptions until you sign in.";
        public static string AuthSignedIn(string login) => Ru ? $"Вы вошли как {login}" : $"Signed in as {login}";
        public static string AuthWaiting => Ru
            ? "Откройте страницу входа и подтвердите доступ. Если страница попросит код, введите код ниже. Окно можно оставить открытым: плагин сам узнает о подтверждении."
            : "Open the login page and confirm access. If the page asks for a code, enter the code below. You can leave this window open: the plugin will notice the confirmation.";
        public static string AuthSignIn => Ru ? "Войти через Twitch" : "Sign in with Twitch";
        public static string AuthSignOut => Ru ? "Выйти" : "Sign out";
        public static string AuthOpenPage => Ru ? "Открыть страницу входа" : "Open the login page";
        public static string AuthCopyCode => Ru ? "Копировать код" : "Copy code";
        public static string TwitchPrivacy => Ru
            ? "Плагин получает только право читать заказы наград. Пароль вводится на сайте Twitch и плагину не передаётся; вход хранится на этом компьютере в зашифрованном виде."
            : "The plugin only gets permission to read reward redemptions. Your password is entered on the Twitch site and never reaches the plugin; the login is stored on this computer in encrypted form.";

        public static string AuthProblemText(AuthProblem problem, string detail) => problem switch
        {
            AuthProblem.Offline => Ru ? "Нет связи с Twitch. Вход сохранён, проверка повторится позже." : "No connection to Twitch. The login is kept, the check will be repeated later.",
            AuthProblem.Denied => Ru ? "Доступ не подтверждён. Нажмите «Войти» и попробуйте ещё раз." : "Access was not confirmed. Click Sign in and try again.",
            AuthProblem.CodeExpired => Ru ? "Время ввода кода истекло. Нажмите «Войти» ещё раз." : "The code has expired. Click Sign in again.",
            AuthProblem.MissingScope => Ru ? "Twitch не выдал нужное право (чтение заказов наград). Войдите заново и разрешите доступ." : "Twitch did not grant the required permission (reading reward redemptions). Sign in again and allow access.",
            AuthProblem.SessionExpired => Ru ? "Вход истёк: Twitch отозвал его или давно не видел плагин. Войдите заново." : "The login has expired: Twitch revoked it or has not seen the plugin for a long time. Sign in again.",
            _ => Ru ? $"Не удалось выполнить вход{(detail.Length > 0 ? ": " + detail : "")}" : $"Sign in failed{(detail.Length > 0 ? ": " + detail : "")}"
        };

        // Блок «Награды»
        public static string RewardsTitle => Ru ? "Награды за баллы канала" : "Channel point rewards";
        public static string RewardsIntro => Ru
            ? "Отметьте награды, заказы которых нужно показывать в очереди. Остальные плагин не трогает. Список берётся с вашего канала на Twitch."
            : "Tick the rewards whose redemptions should appear in the queue. The plugin ignores the others. The list comes from your Twitch channel.";
        public static string RewardsRefresh => Ru ? "Обновить список" : "Refresh the list";
        public static string RewardsRefreshing => Ru ? "Загружаю…" : "Loading…";
        public static string RewardsLoaded(int count) => Ru ? $"Загружено наград: {count}" : $"Rewards loaded: {count}";
        public static string RewardsNoneOnChannel => Ru ? "На канале нет наград за баллы." : "The channel has no channel point rewards.";
        public static string RewardsEmpty => Ru
            ? "Список пока пуст. Войдите в Twitch и нажмите «Обновить список»."
            : "The list is empty. Sign in to Twitch and click Refresh the list.";
        public static string RewardsTracked(int tracked, int total) => Ru ? $"Отслеживается наград: {tracked} из {total}" : $"Rewards tracked: {tracked} of {total}";
        public static string RewardsProblem(RewardsResultKind kind, string detail) => kind switch
        {
            RewardsResultKind.NotSignedIn => Ru ? "Сначала войдите в Twitch." : "Sign in to Twitch first.",
            RewardsResultKind.Unauthorized => Ru ? "Twitch не принял вход. Войдите заново." : "Twitch did not accept the login. Sign in again.",
            RewardsResultKind.Forbidden => Ru ? "У канала нет статуса партнёра или аффилиата, поэтому наград за баллы там нет." : "The channel has no partner or affiliate status, so it has no channel point rewards.",
            RewardsResultKind.Offline => Ru ? "Нет связи с Twitch." : "No connection to Twitch.",
            _ => Ru ? $"Не удалось получить список{(detail.Length > 0 ? ": " + detail : "")}" : $"Could not get the list{(detail.Length > 0 ? ": " + detail : "")}"
        };

        // Строки состояния
        public static string OrdersConnected => Ru ? "Заказы наград: подключено, заказы приходят в очередь" : "Reward redemptions: connected, redemptions go to the queue";
        public static string OrdersConnecting => Ru ? "Заказы наград: подключаюсь…" : "Reward redemptions: connecting…";
        public static string OrdersNeedLogin => Ru ? "Заказы наград: не подключено, нужен вход в Twitch" : "Reward redemptions: not connected, sign in to Twitch";
        public static string OrdersRetry(string detail) => Ru
            ? $"Заказы наград: нет соединения, повторная попытка{(detail.Length > 0 ? " (" + detail + ")" : "")}"
            : $"Reward redemptions: no connection, retrying{(detail.Length > 0 ? " (" + detail + ")" : "")}";
        public static string QueueCount(int count) => Ru ? $"Записей в очереди: {count}" : $"Entries in the queue: {count}";

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
    }
}
