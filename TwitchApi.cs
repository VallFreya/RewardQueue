using System.Text;
using System.Text.Json;

namespace RewardQueue
{
    internal enum RewardsResultKind { Success, NotSignedIn, Unauthorized, Forbidden, Offline, Failed }

    internal sealed record RewardsResult(RewardsResultKind Kind, IReadOnlyList<RewardInfo> Rewards, string Message = "");

    /// <summary>
    /// Запросы к API Twitch (Helix) от имени вошедшего пользователя: список наград канала и подписка на заказы.
    /// Токен берётся у TwitchAuth (при необходимости обновляется); если Twitch ответил 401, вход проверяется заново
    /// и запрос повторяется один раз.
    /// </summary>
    internal sealed class TwitchApi : IDisposable
    {
        private const string RewardsUrl = "https://api.twitch.tv/helix/channel_points/custom_rewards";
        private const string SubscriptionsUrl = "https://api.twitch.tv/helix/eventsub/subscriptions";

        private readonly TwitchAuth _auth;
        private readonly HttpClient _http;

        /// <summary>Действующий клиент API (создаётся при загрузке плагина)</summary>
        public static TwitchApi? Current { get; set; }

        public TwitchApi(TwitchAuth auth, HttpMessageHandler? handler = null)
        {
            _auth = auth;
            _http = handler != null ? new HttpClient(handler) : new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        /// <summary>
        /// Все награды канала, включая созданные в панели автора (Twitch отдаёт их этому приложению для чтения).
        /// Список отсортирован по названию
        /// </summary>
        public async Task<RewardsResult> GetRewardsAsync(CancellationToken ct = default)
        {
            var snapshot = _auth.Snapshot;
            if (snapshot.State != AuthState.SignedIn) return new RewardsResult(RewardsResultKind.NotSignedIn, Array.Empty<RewardInfo>());

            try
            {
                var (status, body) = await SendAsync(HttpMethod.Get, $"{RewardsUrl}?broadcaster_id={Uri.EscapeDataString(snapshot.UserId)}", null, ct).ConfigureAwait(false);
                switch (status)
                {
                    case 200:
                        var parsed = Deserialize(body, HelixJsonContext.Default.HelixRewardsResponse);
                        if (parsed?.data == null) return new RewardsResult(RewardsResultKind.Failed, Array.Empty<RewardInfo>(), "unexpected answer");

                        var rewards = parsed.data
                            .Where(r => !string.IsNullOrWhiteSpace(r.id))
                            .Select(r => new RewardInfo { id = r.id.Trim(), title = r.title.Trim() })
                            .OrderBy(r => r.title, StringComparer.CurrentCultureIgnoreCase)
                            .ToList();
                        Plugin.LogInfo($"Reward list loaded: {rewards.Count} rewards");
                        return new RewardsResult(RewardsResultKind.Success, rewards);

                    case 401:
                        return new RewardsResult(RewardsResultKind.Unauthorized, Array.Empty<RewardInfo>(), ErrorMessage(body));

                    case 403:
                        // у канала нет статуса партнёра или аффилиата: наград за баллы там быть не может
                        return new RewardsResult(RewardsResultKind.Forbidden, Array.Empty<RewardInfo>(), ErrorMessage(body));

                    default:
                        Plugin.LogWarn($"Reward list request returned {status}: {ErrorMessage(body)}");
                        return new RewardsResult(RewardsResultKind.Failed, Array.Empty<RewardInfo>(), $"{status}: {ErrorMessage(body)}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                return new RewardsResult(RewardsResultKind.Offline, Array.Empty<RewardInfo>(), ex.Message);
            }
        }

        /// <summary>Подписка на заказы наград для сессии EventSub. Вызывается клиентом EventSub сразу после приветствия</summary>
        public async Task<SubscribeResult> SubscribeRedemptionsAsync(string sessionId, CancellationToken ct)
        {
            var snapshot = _auth.Snapshot;
            if (snapshot.State != AuthState.SignedIn) return SubscribeResult.Unauthorized;

            var request = new EventSubSubscribeRequest
            {
                condition = new EventSubCondition { broadcaster_user_id = snapshot.UserId },
                transport = new EventSubTransport { session_id = sessionId }
            };
            string json = JsonSerializer.Serialize(request, HelixJsonContext.Default.EventSubSubscribeRequest);

            var (status, body) = await SendAsync(HttpMethod.Post, SubscriptionsUrl, json, ct).ConfigureAwait(false);
            if (status == 202 || status == 200) return SubscribeResult.Success;
            if (status == 401) return SubscribeResult.Unauthorized;

            Plugin.LogWarn($"Subscription request returned {status}: {ErrorMessage(body)}");
            return SubscribeResult.Failed;
        }

        // Запрос с токеном. Получив 401, проверяет вход заново (это обновляет токен) и повторяет запрос один раз
        private async Task<(int Status, string Body)> SendAsync(HttpMethod method, string url, string? jsonBody, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                string? token = await _auth.GetAccessTokenAsync().ConfigureAwait(false);
                if (token == null) return (401, "");

                using var request = new HttpRequestMessage(method, url);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                request.Headers.TryAddWithoutValidation("Client-Id", _auth.ClientId);
                if (jsonBody != null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (status == 401 && attempt == 0)
                {
                    await _auth.RevalidateAsync().ConfigureAwait(false);
                    if (_auth.Snapshot.State != AuthState.SignedIn) return (status, body);
                    continue;
                }

                return (status, body);
            }
        }

        private static string ErrorMessage(string body)
        {
            var error = Deserialize(body, HelixJsonContext.Default.HelixError);
            return string.IsNullOrWhiteSpace(error?.message) ? "" : error.message;
        }

        private static T? Deserialize<T>(string body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
        {
            try
            {
                return JsonSerializer.Deserialize(body, info);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
