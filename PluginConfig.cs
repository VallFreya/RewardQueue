using System.Text.Json.Serialization;

namespace RewardQueue
{
    // Файл конфига
    public class PluginConfig
    {
        public int version { get; set; } = 1;
        // Тема окна настроек: "dark" (по умолчанию) или "light"
        public string theme { get; set; } = "dark";
        // Порт локального сервера на 127.0.0.1. Значение вне допустимых границ заменяется на значение по умолчанию
        public int port { get; set; } = ServerGuard.DefaultPort;
        // Ключ в адресах страниц. Создаётся при первом запуске и не меняется, чтобы док не приходилось добавлять заново
        public string access_key { get; set; } = "";
        // Сколько дней хранится невыполненная запись очереди; потом она удаляется
        public int retention_days { get; set; } = QueueState.DefaultRetentionDays;
        // Свой Client ID приложения Twitch вместо встроенного (для форка или на случай блокировки приложения). Пусто: встроенный
        public string client_id { get; set; } = "";
        // Награды канала, полученные в последний раз кнопкой «Обновить список» (чтобы окно показывало список без сети)
        public List<RewardInfo> known_rewards { get; set; } = new List<RewardInfo>();
        // Очереди и награды, заказы которых в них попадают. Пока очередь одна, но формат рассчитан на несколько
        public List<QueueConfig> queues { get; set; } = new List<QueueConfig>();
        // Сколько записей всего показывать на трансляции по всем очередям вместе; 0 без ограничения
        public int show_total { get; set; }
    }

    // Награда канала Twitch: id не меняется при переименовании, название только для показа
    public class RewardInfo
    {
        public string id { get; set; } = "";
        public string title { get; set; } = "";
    }

    public class QueueConfig
    {
        public string id { get; set; } = QueueState.MainQueueId;
        public string name { get; set; } = "";
        // Сколько записей этой очереди показывать на трансляции (первые по порядку), остальные скрываются; 0 без ограничения
        public int show_limit { get; set; }
        // id наград, заказы которых попадают в эту очередь
        public List<string> reward_ids { get; set; } = new List<string>();
    }

    // Source Generator для NativeAOT (убирает требование рефлексии)
    [JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(PluginConfig))]
    internal partial class PluginConfigJsonContext : JsonSerializerContext;
}
