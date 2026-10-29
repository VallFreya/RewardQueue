namespace RewardQueue
{
    /// <summary>
    /// Очередь записей в памяти, без OBS, сети и файлов: порядок по времени заказа, отсев повторов по id,
    /// очистка по сроку. Потокобезопасна: Twitch, страницы и хоткей работают с ней из разных потоков.
    /// </summary>
    internal sealed class QueueState
    {
        public const string MainQueueId = "main";

        // Пределы длины текстов: Twitch ограничивает сообщение зрителя 500 символами, остальное страховка от мусора
        public const int MaxTitleLength = 100;
        public const int MaxNameLength = 50;
        public const int MaxInputLength = 500;

        public const int DefaultRetentionDays = 3;
        public const int MaxRetentionDays = 30;

        private readonly object _lock = new object();
        private readonly List<QueueEntry> _entries = new List<QueueEntry>();

        public QueueState(IEnumerable<QueueEntry>? initial = null)
        {
            if (initial == null) return;
            foreach (var entry in initial)
            {
                if (entry != null) Add(entry);
            }
        }

        public int Count
        {
            get { lock (_lock) return _entries.Count; }
        }

        /// <summary>Копия очереди в порядке показа: кто заказал раньше, тот выше</summary>
        public IReadOnlyList<QueueEntry> Snapshot()
        {
            lock (_lock) return _entries.Select(e => e.Clone()).ToList();
        }

        public bool Contains(string id)
        {
            lock (_lock) return _entries.Any(e => e.id == id);
        }

        /// <summary>Добавляет запись. false, если запись пустая или такой id уже есть (повтор события)</summary>
        public bool Add(QueueEntry entry)
        {
            var clean = Normalize(entry);
            if (clean == null) return false;

            lock (_lock)
            {
                if (_entries.Any(e => e.id == clean.id)) return false;

                // после всех записей не позже этой: при одинаковом времени порядок добавления сохраняется
                int index = _entries.Count;
                while (index > 0 && _entries[index - 1].redeemed_at > clean.redeemed_at) index--;
                _entries.Insert(index, clean);
                return true;
            }
        }

        /// <summary>Убирает запись («Выполнено» и «Убрать» для очереди одно и то же). Возвращает убранную или null</summary>
        public QueueEntry? Remove(string id)
        {
            lock (_lock)
            {
                int index = _entries.FindIndex(e => e.id == id);
                if (index < 0) return null;

                var removed = _entries[index];
                _entries.RemoveAt(index);
                return removed;
            }
        }

        /// <summary>Убирает первую запись очереди (хоткей «Выполнено для первой награды»)</summary>
        public QueueEntry? RemoveFirst(string queueId = MainQueueId)
        {
            lock (_lock)
            {
                int index = _entries.FindIndex(e => e.queue_id == queueId);
                if (index < 0) return null;

                var removed = _entries[index];
                _entries.RemoveAt(index);
                return removed;
            }
        }

        /// <summary>Убирает записи, заказанные раньше указанного времени. Возвращает, сколько убрано</summary>
        public int PurgeOlderThan(DateTimeOffset cutoff)
        {
            lock (_lock) return _entries.RemoveAll(e => e.redeemed_at < cutoff);
        }

        /// <summary>Срок хранения в днях из конфига: значения вне границ заменяются ближайшей допустимой границей, мусор значением по умолчанию</summary>
        public static int ClampRetentionDays(int days) => days <= 0 ? DefaultRetentionDays : Math.Min(days, MaxRetentionDays);

        /// <summary>Запись, добавляемая вручную: свой GUID, время добавления, источник «manual»</summary>
        public static QueueEntry NewManual(string rewardTitle, string? userName, string? userInput, DateTimeOffset now, string queueId = MainQueueId) => new QueueEntry
        {
            id = Guid.NewGuid().ToString("N"),
            source = QueueEntry.SourceManual,
            queue_id = queueId,
            reward_title = rewardTitle,
            user_name = userName ?? "",
            user_input = userInput ?? "",
            redeemed_at = now
        };

        // Чистая копия записи: тексты обрезаны, пустое заменено значениями по умолчанию. null, если записью это быть не может
        private static QueueEntry? Normalize(QueueEntry entry)
        {
            string id = (entry.id ?? "").Trim();
            string title = Limit(entry.reward_title, MaxTitleLength);
            if (id.Length == 0 || title.Length == 0) return null;

            string queueId = (entry.queue_id ?? "").Trim();
            return new QueueEntry
            {
                id = id,
                source = string.Equals(entry.source, QueueEntry.SourceManual, StringComparison.OrdinalIgnoreCase) ? QueueEntry.SourceManual : QueueEntry.SourceTwitch,
                queue_id = queueId.Length == 0 ? MainQueueId : queueId,
                reward_id = (entry.reward_id ?? "").Trim(),
                reward_title = title,
                user_name = Limit(entry.user_name, MaxNameLength),
                user_input = Limit(entry.user_input, MaxInputLength),
                redeemed_at = entry.redeemed_at
            };
        }

        private static string Limit(string? text, int max)
        {
            text = (text ?? "").Trim();
            return text.Length <= max ? text : text[..max].TrimEnd();
        }
    }
}
