using System.Text.Json.Serialization;

namespace RewardQueue
{
    // Ответы id.twitch.tv. Имена полей такие же, как в JSON Twitch
    public class DeviceCodeResponse
    {
        public string device_code { get; set; } = "";
        public string user_code { get; set; } = "";
        public string verification_uri { get; set; } = "";
        public int expires_in { get; set; }
        public int interval { get; set; }
    }

    public class TokenResponse
    {
        public string access_token { get; set; } = "";
        public string refresh_token { get; set; } = "";
        public int expires_in { get; set; }
        public string[]? scope { get; set; }
        public string token_type { get; set; } = "";
    }

    public class ValidateResponse
    {
        public string client_id { get; set; } = "";
        public string login { get; set; } = "";
        public string user_id { get; set; } = "";
        public string[]? scopes { get; set; }
        public int expires_in { get; set; }
    }

    public class AuthErrorResponse
    {
        public int status { get; set; }
        public string message { get; set; } = "";
    }

    // То, что хранится на диске (зашифрованным через DPAPI): токены и сведения о пользователе
    public class StoredAuth
    {
        // Приложение, которому выданы токены: при смене Client ID старые токены не годятся
        public string client_id { get; set; } = "";
        public string access_token { get; set; } = "";
        public string refresh_token { get; set; } = "";
        public DateTimeOffset expires_at { get; set; }
        public string user_id { get; set; } = "";
        public string login { get; set; } = "";
        public string[] scopes { get; set; } = Array.Empty<string>();
    }

    // Source Generator для NativeAOT (убирает требование рефлексии)
    [JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(DeviceCodeResponse))]
    [JsonSerializable(typeof(TokenResponse))]
    [JsonSerializable(typeof(ValidateResponse))]
    [JsonSerializable(typeof(AuthErrorResponse))]
    [JsonSerializable(typeof(StoredAuth))]
    internal partial class TwitchJsonContext : JsonSerializerContext;
}
