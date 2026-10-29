using System.Text.Json.Serialization;

namespace RewardQueue
{
    // Одна запись очереди: заказ награды Twitch или запись, добавленная вручную
    public class QueueEntry
    {
        public const string SourceTwitch = "twitch";
        public const string SourceManual = "manual";

        // Заказ Twitch: id заказа из события; ручная запись: свой GUID. По нему же отбрасываются повторы события
        public string id { get; set; } = "";
        public string source { get; set; } = SourceTwitch;
        // К какой очереди относится запись. Пока очередь одна, но формат рассчитан на несколько
        public string queue_id { get; set; } = QueueState.MainQueueId;
        public string reward_id { get; set; } = "";
        public string reward_title { get; set; } = "";
        public string user_name { get; set; } = "";
        public string user_input { get; set; } = "";
        // Время заказа: по нему строится порядок. У ручной записи это время добавления
        public DateTimeOffset redeemed_at { get; set; }

        public QueueEntry Clone() => (QueueEntry)MemberwiseClone();
    }

    // Файл queue.json
    public class QueueFile
    {
        public int version { get; set; } = 1;
        public List<QueueEntry> entries { get; set; } = new List<QueueEntry>();
    }

    // Source Generator для NativeAOT (убирает требование рефлексии)
    [JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(QueueFile))]
    internal partial class QueueJsonContext : JsonSerializerContext;
}
