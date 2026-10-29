using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RewardQueue
{
    /// <summary>Файл queue.json: чтение при запуске и атомарная запись при каждом изменении очереди</summary>
    internal static class QueueStore
    {
        public const string FileName = "queue.json";

        // Контекст с кириллицей без экранирования: файл остаётся читаемым
        private static readonly QueueJsonContext JsonContext = new QueueJsonContext(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        /// <summary>Записи из файла. Нет файла: пустая очередь. Файл не разобрался: копия рядом с .broken и пустая очередь</summary>
        public static List<QueueEntry> Load(string path)
        {
            if (!File.Exists(path)) return new List<QueueEntry>();

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var file = JsonSerializer.Deserialize(json, QueueJsonContext.Default.QueueFile);
                if (file == null) throw new JsonException("the file is empty or contains null");
                return file.entries?.Where(e => e != null).ToList() ?? new List<QueueEntry>();
            }
            catch (Exception ex)
            {
                // Очередь потеряна, но файл не должен пропасть: следующая запись его перезапишет
                Plugin.LogError($"Queue file '{path}' is not valid and was not loaded: {ex.Message}");
                try
                {
                    File.Copy(path, path + ".broken", overwrite: true);
                    Plugin.LogWarn($"A copy of the unreadable queue file was saved to '{path}.broken'.");
                }
                catch (Exception copyEx)
                {
                    Plugin.LogError($"Could not back up the unreadable queue file: {copyEx.Message}");
                }
                return new List<QueueEntry>();
            }
        }

        public static void Save(string path, IReadOnlyList<QueueEntry> entries)
        {
            var file = new QueueFile { entries = entries.ToList() };
            string json = JsonSerializer.Serialize(file, JsonContext.QueueFile);
            AtomicFile.WriteAllText(path, json, Encoding.UTF8);
        }
    }
}
