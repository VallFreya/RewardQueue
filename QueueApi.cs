using System.Text.Json;
using System.Text.Json.Serialization;

namespace RewardQueue
{
    // Что страницы получают и присылают. Имена полей такие же, как в JSON страниц
    public class StateMessage
    {
        public string type { get; set; } = "state";
        // Вся очередь по порядку заказа: ник и сообщение зрителя видны и в доке, и на трансляции
        public List<QueueEntry> entries { get; set; } = new List<QueueEntry>();
        // Отслеживаемые награды: из них док предлагает выбрать название при ручном добавлении
        public List<RewardInfo> rewards { get; set; } = new List<RewardInfo>();
    }

    public class AddRequest
    {
        public string title { get; set; } = "";
        public string user { get; set; } = "";
        public string input { get; set; } = "";
    }

    public class AddResponse
    {
        public string id { get; set; } = "";
    }

    // Source Generator для NativeAOT (убирает требование рефлексии)
    [JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(StateMessage))]
    [JsonSerializable(typeof(AddRequest))]
    [JsonSerializable(typeof(AddResponse))]
    internal partial class WebJsonContext : JsonSerializerContext;

    /// <summary>То, что сервер умеет делать с очередью по просьбе страниц. Подменяется в проверках</summary>
    internal interface IQueueApi
    {
        /// <summary>Текущее состояние одной строкой JSON (без переводов строки: идёт в поток событий)</summary>
        string BuildState();

        /// <summary>«Выполнено» и «Убрать»: убирает запись. false, если такой записи нет</summary>
        bool Remove(string id);

        /// <summary>Ручное добавление. Возвращает id записи или null, если название пустое</summary>
        string? AddManual(string title, string? user, string? input);
    }

    /// <summary>Настоящая реализация: очередь плагина и отмеченные награды</summary>
    internal sealed class QueueApi : IQueueApi
    {
        public string BuildState() => BuildStateJson(QueueService.Current?.Snapshot() ?? Array.Empty<QueueEntry>(), Plugin.Rewards);

        public bool Remove(string id) => QueueService.Current?.Remove(id) ?? false;

        public string? AddManual(string title, string? user, string? input) => QueueService.Current?.AddManual(title, user, input);

        /// <summary>Состояние для страниц: очередь и отмеченные награды</summary>
        public static string BuildStateJson(IReadOnlyList<QueueEntry> entries, RewardSettings rewards)
        {
            var message = new StateMessage
            {
                entries = entries.ToList(),
                rewards = rewards.Known.Where(r => rewards.IsTracked(r.id)).ToList()
            };
            return JsonSerializer.Serialize(message, WebJsonContext.Default.StateMessage);
        }

        /// <summary>Разбор тела запроса «добавить вручную»: null, если это не JSON или названия нет</summary>
        public static AddRequest? ParseAddRequest(string body)
        {
            try
            {
                var request = JsonSerializer.Deserialize(body, WebJsonContext.Default.AddRequest);
                return request == null || string.IsNullOrWhiteSpace(request.title) ? null : request;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
