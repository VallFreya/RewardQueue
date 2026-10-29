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
    }

    // Source Generator для NativeAOT (убирает требование рефлексии)
    [JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(PluginConfig))]
    internal partial class PluginConfigJsonContext : JsonSerializerContext;
}
