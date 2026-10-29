using System.Text.Json;

namespace RewardQueue
{
    internal enum TokenPollKind { Success, Pending, SlowDown, Denied, Expired, Failed }

    internal enum RefreshKind { Success, InvalidGrant, Failed }

    internal readonly record struct TokenPoll(TokenPollKind Kind, TokenResponse? Tokens, string Message);

    internal readonly record struct RefreshResult(RefreshKind Kind, TokenResponse? Tokens, string Message);

    /// <summary>Разбор ответов id.twitch.tv: код статуса и тело ответа превращаются в понятный результат. Без сети и OBS</summary>
    internal static class TwitchAuthParsers
    {
        public const string RequiredScope = "channel:read:redemptions";

        public const int DefaultPollSeconds = 5;
        public const int MaxPollSeconds = 30;

        /// <summary>Ответ на запрос кода входа. false, если в нём нет нужных полей</summary>
        public static bool TryParseDeviceCode(string body, out DeviceCodeResponse device)
        {
            device = new DeviceCodeResponse();
            var parsed = Deserialize(body, TwitchJsonContext.Default.DeviceCodeResponse);
            if (parsed == null
                || string.IsNullOrWhiteSpace(parsed.device_code)
                || string.IsNullOrWhiteSpace(parsed.user_code)
                || string.IsNullOrWhiteSpace(parsed.verification_uri)
                || parsed.expires_in <= 0)
            {
                return false;
            }

            // интервал опроса из ответа, но в разумных пределах
            if (parsed.interval <= 0) parsed.interval = DefaultPollSeconds;
            parsed.interval = Math.Min(parsed.interval, MaxPollSeconds);
            device = parsed;
            return true;
        }

        /// <summary>Ответ на опрос «пользователь уже подтвердил вход?»</summary>
        public static TokenPoll ParseTokenPoll(int status, string body)
        {
            if (status == 200)
            {
                var tokens = Deserialize(body, TwitchJsonContext.Default.TokenResponse);
                return IsComplete(tokens)
                    ? new TokenPoll(TokenPollKind.Success, tokens, "")
                    : new TokenPoll(TokenPollKind.Failed, null, "the token response is incomplete");
            }

            string message = ErrorMessage(body);
            string lower = message.ToLowerInvariant();
            TokenPollKind kind =
                lower.Contains("authorization_pending") ? TokenPollKind.Pending
                : lower.Contains("slow_down") ? TokenPollKind.SlowDown
                : lower.Contains("access_denied") ? TokenPollKind.Denied
                : lower.Contains("expired") || lower.Contains("invalid device code") ? TokenPollKind.Expired
                : TokenPollKind.Failed;
            return new TokenPoll(kind, null, message);
        }

        /// <summary>Ответ на обновление токена. «Отказ» (недействительный refresh-токен) отделён от временных сбоев:
        /// после отказа нужен повторный вход, после сбоя достаточно повторить позже</summary>
        public static RefreshResult ParseRefresh(int status, string body)
        {
            if (status == 200)
            {
                var tokens = Deserialize(body, TwitchJsonContext.Default.TokenResponse);
                return IsComplete(tokens)
                    ? new RefreshResult(RefreshKind.Success, tokens, "")
                    : new RefreshResult(RefreshKind.Failed, null, "the token response is incomplete");
            }

            string message = ErrorMessage(body);
            bool rejected = status is 400 or 401;
            return new RefreshResult(rejected ? RefreshKind.InvalidGrant : RefreshKind.Failed, null, message);
        }

        public static bool TryParseValidate(string body, out ValidateResponse validate)
        {
            validate = new ValidateResponse();
            var parsed = Deserialize(body, TwitchJsonContext.Default.ValidateResponse);
            if (parsed == null || string.IsNullOrWhiteSpace(parsed.user_id) || string.IsNullOrWhiteSpace(parsed.login)) return false;

            validate = parsed;
            return true;
        }

        /// <summary>У токена есть право читать заказы наград</summary>
        public static bool HasRequiredScope(IEnumerable<string>? scopes) =>
            scopes != null && scopes.Contains(RequiredScope, StringComparer.OrdinalIgnoreCase);

        private static bool IsComplete(TokenResponse? tokens) =>
            tokens != null && !string.IsNullOrWhiteSpace(tokens.access_token) && !string.IsNullOrWhiteSpace(tokens.refresh_token);

        private static string ErrorMessage(string body)
        {
            var error = Deserialize(body, TwitchJsonContext.Default.AuthErrorResponse);
            return string.IsNullOrWhiteSpace(error?.message) ? "unknown error" : error.message;
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
    }
}
