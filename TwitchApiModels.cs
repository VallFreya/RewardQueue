using System.Text.Json.Serialization;

namespace RewardQueue
{
    // Ответы и запросы Twitch Helix. Имена полей такие же, как в JSON Twitch
    public class HelixReward
    {
        public string id { get; set; } = "";
        public string title { get; set; } = "";
        public int cost { get; set; }
        public bool is_enabled { get; set; }
    }

    public class HelixRewardsResponse
    {
        public List<HelixReward>? data { get; set; }
    }

    public class HelixError
    {
        public int status { get; set; }
        public string error { get; set; } = "";
        public string message { get; set; } = "";
    }

    public class EventSubCondition
    {
        public string broadcaster_user_id { get; set; } = "";
    }

    public class EventSubTransport
    {
        public string method { get; set; } = "websocket";
        public string session_id { get; set; } = "";
    }

    public class EventSubSubscribeRequest
    {
        public string type { get; set; } = EventSubMessages.RedemptionType;
        public string version { get; set; } = "1";
        public EventSubCondition condition { get; set; } = new EventSubCondition();
        public EventSubTransport transport { get; set; } = new EventSubTransport();
    }

    // Source Generator для NativeAOT (убирает требование рефлексии)
    [JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(HelixRewardsResponse))]
    [JsonSerializable(typeof(HelixError))]
    [JsonSerializable(typeof(EventSubSubscribeRequest))]
    internal partial class HelixJsonContext : JsonSerializerContext;
}
