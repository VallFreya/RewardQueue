namespace RewardQueue
{
    internal enum AuthState { SignedOut, WaitingForUser, SignedIn }

    /// <summary>Что не так со входом. Окно показывает текст по этому значению, а не по английскому сообщению Twitch</summary>
    internal enum AuthProblem { None, Offline, Denied, CodeExpired, MissingScope, SessionExpired, Failed }

    /// <summary>Состояние входа для окна: кто вошёл, или какой код ввести на странице Twitch, или что пошло не так</summary>
    internal sealed record AuthSnapshot(
        AuthState State = AuthState.SignedOut,
        string Login = "",
        string UserId = "",
        string UserCode = "",
        string VerificationUri = "",
        DateTimeOffset CodeExpiresAt = default,
        AuthProblem Problem = AuthProblem.None,
        string Detail = "");

    /// <summary>
    /// Вход в Twitch по Device Code Flow для публичного приложения (без секрета и без адреса возврата): плагин показывает код,
    /// пользователь подтверждает вход в браузере, плагин получает токены. Токены хранятся зашифрованными (TokenVault),
    /// access-токен обновляется заранее, refresh-токен одноразовый (новый записывается на диск до использования).
    /// Сеть, часы и паузы подменяются в проверках.
    /// </summary>
    internal sealed class TwitchAuth : IDisposable
    {
        // Публичный Client ID приложения «RewardQueue» на dev.twitch.tv: он не секрет, секрет приложению не нужен.
        // Можно подменить полем client_id в config.json (если приложение заблокируют или у форка свой)
        public const string DefaultClientId = "g1e25xwrf3oqosokb3r98vpqhhj0pd";

        private const string DeviceUrl = "https://id.twitch.tv/oauth2/device";
        private const string TokenUrl = "https://id.twitch.tv/oauth2/token";
        private const string ValidateUrl = "https://id.twitch.tv/oauth2/validate";
        private const string RevokeUrl = "https://id.twitch.tv/oauth2/revoke";

        // Токен живёт 4 часа: обновляем, когда до конца остаётся меньше десяти минут
        private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(10);
        private const int MaxPollFailures = 5;

        private readonly object _lock = new object();
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
        private readonly HttpClient _http;
        private readonly string _vaultPath;
        private readonly string _clientId;
        private readonly Func<DateTimeOffset> _clock;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private AuthSnapshot _snapshot = new AuthSnapshot();
        private StoredAuth? _stored;
        private CancellationTokenSource? _loginCts;

        /// <summary>Действующий вход (создаётся при загрузке плагина). В проверках подменяется</summary>
        public static TwitchAuth? Current { get; set; }

        /// <summary>Состояние изменилось. Вызывается из потока, который его изменил, вне блокировок</summary>
        public event Action? Changed;

        public TwitchAuth(string vaultPath, string? clientId = null, HttpMessageHandler? handler = null,
            Func<DateTimeOffset>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            _vaultPath = vaultPath;
            _clientId = ValidClientId(clientId) ? clientId! : DefaultClientId;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _delay = delay ?? ((span, token) => Task.Delay(span, token));
            _http = handler != null ? new HttpClient(handler) : new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        public AuthSnapshot Snapshot
        {
            get { lock (_lock) return _snapshot; }
        }

        /// <summary>Client ID пригоден: только строчные буквы и цифры разумной длины (так выглядят ID приложений Twitch)</summary>
        public static bool ValidClientId(string? id) =>
            !string.IsNullOrEmpty(id) && id.Length is >= 20 and <= 64 && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c));

        /// <summary>Подмена состояния для проверки внешнего вида окна без сети</summary>
        internal void SetSnapshotForPreview(AuthSnapshot snapshot) => SetSnapshot(snapshot);

        // ---- запуск плагина ----

        /// <summary>Читает сохранённый вход и проверяет его у Twitch (при необходимости обновляет токен)</summary>
        public async Task InitializeAsync()
        {
            var stored = TokenVault.Load(_vaultPath);
            if (stored == null) return;

            if (stored.client_id != _clientId)
            {
                // токены выданы другому приложению: они не подойдут, нужен новый вход
                Plugin.LogInfo("Saved Twitch login belongs to another application and is ignored");
                TokenVault.Delete(_vaultPath);
                return;
            }

            lock (_lock) _stored = stored;
            SetSnapshot(new AuthSnapshot(AuthState.SignedIn, stored.login, stored.user_id));
            await RevalidateAsync().ConfigureAwait(false);
        }

        /// <summary>Проверка токена у Twitch (раз в час и при запуске). Нет связи: вход остаётся, в состоянии отмечается проблема</summary>
        public async Task RevalidateAsync()
        {
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var stored = _stored;
                    if (stored == null) return;

                    if (stored.expires_at - _clock() < RefreshMargin)
                    {
                        if (!await RefreshAsync(force: false).ConfigureAwait(false)) return;
                        stored = _stored;
                        if (stored == null) return;
                    }

                    var (status, body) = await GetValidateAsync(stored.access_token, CancellationToken.None).ConfigureAwait(false);
                    if (status == 200 && TwitchAuthParsers.TryParseValidate(body, out var validate))
                    {
                        if (!TwitchAuthParsers.HasRequiredScope(validate.scopes))
                        {
                            SignOutLocal(AuthProblem.MissingScope, "");
                            return;
                        }

                        var updated = Copy(stored);
                        updated.expires_at = _clock().AddSeconds(validate.expires_in);
                        updated.login = validate.login;
                        updated.user_id = validate.user_id;
                        lock (_lock) _stored = updated;
                        SetSnapshot(new AuthSnapshot(AuthState.SignedIn, validate.login, validate.user_id));
                        return;
                    }

                    if (status == 401 && attempt == 0)
                    {
                        // Twitch не признал токен: пробуем получить новый
                        if (!await RefreshAsync(force: true).ConfigureAwait(false)) return;
                        continue;
                    }

                    ReportProblem(AuthProblem.Failed, $"validate returned {status}");
                    return;
                }
            }
            catch (Exception ex) when (IsNetworkFailure(ex))
            {
                ReportProblem(AuthProblem.Offline, ex.Message);
            }
            catch (Exception ex)
            {
                Plugin.LogError($"Twitch login check failed: {ex.Message}");
                ReportProblem(AuthProblem.Failed, ex.Message);
            }
        }

        // ---- вход ----

        /// <summary>
        /// Начинает вход: запрашивает код, показывает его (состояние WaitingForUser) и ждёт, пока пользователь подтвердит вход
        /// в браузере. Возвращается, когда вход завершён, отменён или не удался
        /// </summary>
        public async Task LoginAsync()
        {
            CancellationTokenSource cts;
            lock (_lock)
            {
                // вход уже идёт или уже выполнен
                if (_snapshot.State != AuthState.SignedOut) return;
                cts = _loginCts = new CancellationTokenSource();
            }

            var ct = cts.Token;
            try
            {
                var (status, body) = await PostFormAsync(DeviceUrl, new Dictionary<string, string>
                {
                    ["client_id"] = _clientId,
                    ["scopes"] = TwitchAuthParsers.RequiredScope
                }, ct).ConfigureAwait(false);

                if (status != 200 || !TwitchAuthParsers.TryParseDeviceCode(body, out var device))
                {
                    SignOutLocal(AuthProblem.Failed, $"device code request returned {status}", deleteVault: false);
                    return;
                }

                var expiresAt = _clock().AddSeconds(device.expires_in);
                SetSnapshot(new AuthSnapshot(AuthState.WaitingForUser, UserCode: device.user_code,
                    VerificationUri: device.verification_uri, CodeExpiresAt: expiresAt));
                Plugin.LogInfo("Twitch login started, waiting for the user to confirm it in the browser");

                await PollUntilConfirmedAsync(device, expiresAt, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lock (_lock)
                {
                    if (_snapshot.State != AuthState.WaitingForUser) return;
                }
                SetSnapshot(new AuthSnapshot());
                Plugin.LogInfo("Twitch login cancelled");
            }
            catch (Exception ex) when (IsNetworkFailure(ex))
            {
                SignOutLocal(AuthProblem.Offline, ex.Message, deleteVault: false);
            }
            catch (Exception ex)
            {
                Plugin.LogError($"Twitch login failed: {ex.Message}");
                SignOutLocal(AuthProblem.Failed, ex.Message, deleteVault: false);
            }
        }

        private async Task PollUntilConfirmedAsync(DeviceCodeResponse device, DateTimeOffset expiresAt, CancellationToken ct)
        {
            int interval = device.interval;
            int failures = 0;

            while (true)
            {
                await _delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
                if (_clock() >= expiresAt)
                {
                    SignOutLocal(AuthProblem.CodeExpired, "", deleteVault: false);
                    return;
                }

                TokenPoll poll;
                try
                {
                    var (status, body) = await PostFormAsync(TokenUrl, new Dictionary<string, string>
                    {
                        ["client_id"] = _clientId,
                        ["scopes"] = TwitchAuthParsers.RequiredScope,
                        ["device_code"] = device.device_code,
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                    }, ct).ConfigureAwait(false);
                    poll = TwitchAuthParsers.ParseTokenPoll(status, body);
                    failures = 0;
                }
                catch (Exception ex) when (IsNetworkFailure(ex) && !ct.IsCancellationRequested)
                {
                    // кратковременный обрыв связи не должен сбрасывать вход: пробуем в следующий раз
                    if (++failures >= MaxPollFailures)
                    {
                        SignOutLocal(AuthProblem.Offline, ex.Message, deleteVault: false);
                        return;
                    }
                    continue;
                }

                switch (poll.Kind)
                {
                    case TokenPollKind.Pending:
                        break;
                    case TokenPollKind.SlowDown:
                        interval = Math.Min(interval + 5, 60);
                        break;
                    case TokenPollKind.Success:
                        await CompleteLoginAsync(poll.Tokens!, ct).ConfigureAwait(false);
                        return;
                    case TokenPollKind.Denied:
                        SignOutLocal(AuthProblem.Denied, "", deleteVault: false);
                        return;
                    case TokenPollKind.Expired:
                        SignOutLocal(AuthProblem.CodeExpired, "", deleteVault: false);
                        return;
                    default:
                        SignOutLocal(AuthProblem.Failed, poll.Message, deleteVault: false);
                        return;
                }
            }
        }

        // Токены получены: узнаём, чей это вход, проверяем права и сохраняем
        private async Task CompleteLoginAsync(TokenResponse tokens, CancellationToken ct)
        {
            var (status, body) = await GetValidateAsync(tokens.access_token, ct).ConfigureAwait(false);
            if (status != 200 || !TwitchAuthParsers.TryParseValidate(body, out var validate))
            {
                SignOutLocal(AuthProblem.Failed, $"validate returned {status}", deleteVault: false);
                return;
            }

            if (!TwitchAuthParsers.HasRequiredScope(validate.scopes ?? tokens.scope))
            {
                SignOutLocal(AuthProblem.MissingScope, "", deleteVault: false);
                return;
            }

            var stored = new StoredAuth
            {
                client_id = _clientId,
                access_token = tokens.access_token,
                refresh_token = tokens.refresh_token,
                expires_at = _clock().AddSeconds(tokens.expires_in),
                user_id = validate.user_id,
                login = validate.login,
                scopes = validate.scopes ?? tokens.scope ?? Array.Empty<string>()
            };

            lock (_lock) _stored = stored;
            SaveVault(stored);
            SetSnapshot(new AuthSnapshot(AuthState.SignedIn, stored.login, stored.user_id));
            Plugin.LogInfo("Twitch login completed");
        }

        /// <summary>Отмена входа, пока пользователь не подтвердил его в браузере</summary>
        public void CancelLogin()
        {
            lock (_lock) _loginCts?.Cancel();
        }

        /// <summary>Выход: вход отменяется, токены стираются с диска, а у Twitch отзываются (если есть связь)</summary>
        public void SignOut()
        {
            StoredAuth? stored;
            lock (_lock)
            {
                _loginCts?.Cancel();
                stored = _stored;
                _stored = null;
            }

            TokenVault.Delete(_vaultPath);
            SetSnapshot(new AuthSnapshot());
            Plugin.LogInfo("Signed out of Twitch");

            if (stored != null) _ = Task.Run(() => RevokeAsync(stored.access_token));
        }

        // ---- токен для запросов ----

        /// <summary>Действующий access-токен (при необходимости обновляется) или null, если входа нет</summary>
        public async Task<string?> GetAccessTokenAsync()
        {
            var stored = _stored;
            if (stored == null) return null;

            if (stored.expires_at - _clock() < RefreshMargin)
            {
                try
                {
                    if (!await RefreshAsync(force: false).ConfigureAwait(false) && _stored == null) return null;
                }
                catch (Exception ex) when (IsNetworkFailure(ex))
                {
                    ReportProblem(AuthProblem.Offline, ex.Message);
                }
            }

            stored = _stored;
            return stored != null && stored.expires_at > _clock() ? stored.access_token : null;
        }

        // true: токен годен (свежий или обновлён). Одновременные вызовы обновляют токен один раз:
        // refresh-токен одноразовый, второе обновление тем же токеном было бы отказом
        private async Task<bool> RefreshAsync(bool force)
        {
            await _refreshGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var stored = _stored;
                if (stored == null) return false;
                if (!force && stored.expires_at - _clock() >= RefreshMargin) return true;

                RefreshResult result;
                try
                {
                    var (status, body) = await PostFormAsync(TokenUrl, new Dictionary<string, string>
                    {
                        ["client_id"] = _clientId,
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = stored.refresh_token
                    }, CancellationToken.None).ConfigureAwait(false);
                    result = TwitchAuthParsers.ParseRefresh(status, body);
                }
                catch (Exception ex) when (IsNetworkFailure(ex))
                {
                    ReportProblem(AuthProblem.Offline, ex.Message);
                    return false;
                }

                switch (result.Kind)
                {
                    case RefreshKind.Success:
                        var updated = Copy(stored);
                        updated.access_token = result.Tokens!.access_token;
                        updated.refresh_token = result.Tokens.refresh_token;
                        updated.expires_at = _clock().AddSeconds(result.Tokens.expires_in);
                        // новый refresh-токен сначала на диск: прежний уже недействителен
                        SaveVault(updated);
                        lock (_lock) _stored = updated;
                        Plugin.LogInfo("Twitch access token refreshed");
                        return true;

                    case RefreshKind.InvalidGrant:
                        // refresh-токен истёк (30 дней без использования) или отозван: нужен новый вход
                        SignOutLocal(AuthProblem.SessionExpired, "");
                        return false;

                    default:
                        ReportProblem(AuthProblem.Failed, result.Message);
                        return false;
                }
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        // ---- состояние ----

        private void SignOutLocal(AuthProblem problem, string detail, bool deleteVault = true)
        {
            if (deleteVault)
            {
                lock (_lock) _stored = null;
                TokenVault.Delete(_vaultPath);
            }

            Plugin.LogWarn($"Twitch login is not active: {problem}{(detail.Length > 0 ? " (" + detail + ")" : "")}");
            SetSnapshot(new AuthSnapshot(Problem: problem, Detail: detail));
        }

        // Вход остаётся, но в состоянии видно, что проверка не удалась
        private void ReportProblem(AuthProblem problem, string detail)
        {
            AuthSnapshot current;
            lock (_lock) current = _snapshot;
            if (current.State != AuthState.SignedIn) return;
            SetSnapshot(current with { Problem = problem, Detail = detail });
        }

        private void SetSnapshot(AuthSnapshot snapshot)
        {
            lock (_lock)
            {
                if (_snapshot == snapshot) return;
                _snapshot = snapshot;
            }

            try
            {
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                Plugin.LogError($"Twitch login change handler failed: {ex.Message}");
            }
        }

        private void SaveVault(StoredAuth stored)
        {
            try
            {
                TokenVault.Save(_vaultPath, stored);
            }
            catch (Exception ex)
            {
                // вход в этой сессии работает, но после перезапуска OBS придётся войти заново
                Plugin.LogError($"Could not save the Twitch login to disk: {ex.Message}");
            }
        }

        private static StoredAuth Copy(StoredAuth s) => new StoredAuth
        {
            client_id = s.client_id,
            access_token = s.access_token,
            refresh_token = s.refresh_token,
            expires_at = s.expires_at,
            user_id = s.user_id,
            login = s.login,
            scopes = s.scopes
        };

        // ---- сеть ----

        private static bool IsNetworkFailure(Exception ex) => ex is HttpRequestException or OperationCanceledException or IOException;

        private async Task<(int Status, string Body)> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }

        private async Task<(int Status, string Body)> GetValidateAsync(string accessToken, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ValidateUrl);
            request.Headers.TryAddWithoutValidation("Authorization", "OAuth " + accessToken);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }

        // Лучшее из возможного: нет связи или Twitch не ответил, значит токен просто истечёт сам
        private async Task RevokeAsync(string accessToken)
        {
            try
            {
                await PostFormAsync(RevokeUrl, new Dictionary<string, string>
                {
                    ["client_id"] = _clientId,
                    ["token"] = accessToken
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Plugin.LogWarn($"Could not revoke the Twitch token: {ex.Message}");
            }
        }

        public void Dispose()
        {
            lock (_lock) _loginCts?.Cancel();
            _http.Dispose();
        }
    }
}
