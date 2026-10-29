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
        // Очереди по порядку: название (может быть пустым) и сколько записей каждой показывать на трансляции
        public List<StateQueue> queues { get; set; } = new List<StateQueue>();
        // Сколько записей всего показывать на трансляции; 0 без ограничения
        public int show_total { get; set; }
    }

    public class StateQueue
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public int show_limit { get; set; }
        // награды очереди нужны доку для выбора при ручном добавлении
        public List<string> reward_ids { get; set; } = new List<string>();
    }

    public class AddRequest
    {
        public string title { get; set; } = "";
        public string user { get; set; } = "";
        public string input { get; set; } = "";
        // Награда из списка отмеченных: по ней определяется очередь
        public string reward_id { get; set; } = "";
        // Очередь, если награда не из списка (своё название)
        public string queue_id { get; set; } = "";
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

        /// <summary>«Делаю»: запись поднимается на первое место. false, если такой записи нет</summary>
        bool Start(string id);

        /// <summary>«Вернуть в очередь»: пометка снимается. false, если такой записи нет</summary>
        bool Release(string id);

        /// <summary>
        /// Ручное добавление. Награда из списка (rewardId) определяет очередь сама, иначе берётся queueId, иначе первая очередь.
        /// Возвращает id записи или null, если название пустое
        /// </summary>
        string? AddManual(string title, string? user, string? input, string? rewardId, string? queueId);
    }

    /// <summary>Настоящая реализация: очередь плагина и отмеченные награды</summary>
    internal sealed class QueueApi : IQueueApi
    {
        public string BuildState() => BuildStateJson(QueueService.Current?.Snapshot() ?? Array.Empty<QueueEntry>(), Plugin.Rewards);

        public bool Remove(string id) => QueueService.Current?.Remove(id) ?? false;

        public bool Start(string id) => QueueService.Current?.StartWorking(id) ?? false;

        public bool Release(string id) => QueueService.Current?.ReleaseWorking(id) ?? false;

        public string? AddManual(string title, string? user, string? input, string? rewardId, string? queueId)
        {
            var rewards = Plugin.Rewards;
            string reward = (rewardId ?? "").Trim();
            string? byReward = reward.Length > 0 ? rewards.QueueFor(reward) : null;
            string queue = byReward ?? (rewards.Queue((queueId ?? "").Trim()) != null ? queueId!.Trim() : rewards.FirstQueueId);
            return QueueService.Current?.AddManual(title, user, input, queue, byReward != null ? reward : "");
        }

        /// <summary>Состояние для страниц: очередь и отмеченные награды</summary>
        public static string BuildStateJson(IReadOnlyList<QueueEntry> entries, RewardSettings rewards)
        {
            var message = new StateMessage
            {
                entries = entries.ToList(),
                rewards = rewards.Known.Where(r => rewards.IsTracked(r.id)).ToList(),
                queues = rewards.Queues.Select(q => new StateQueue { id = q.id, name = q.name, show_limit = q.show_limit, reward_ids = q.reward_ids.ToList() }).ToList(),
                show_total = rewards.ShowTotal
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
