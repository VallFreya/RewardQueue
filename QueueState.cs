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

        /// <summary>
        /// Очереди из сохранённых записей. Порядок файла сохраняется как есть (запись, поднятая кнопкой «Делаю», остаётся первой
        /// в своей очереди), повторы по id отбрасываются. Если в одной очереди «делаемых» записей несколько, остаётся первая из них,
        /// и она ставится на первое место этой очереди
        /// </summary>
        public QueueState(IEnumerable<QueueEntry>? initial = null)
        {
            if (initial == null) return;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in initial)
            {
                var clean = entry == null ? null : Normalize(entry);
                if (clean == null || !seen.Add(clean.id)) continue;
                _entries.Add(clean);
            }

            foreach (string queueId in _entries.Select(e => e.queue_id).Distinct().ToList())
            {
                int working = _entries.FindIndex(e => e.queue_id == queueId && e.in_progress);
                foreach (var e in _entries.Where(e => e.queue_id == queueId)) e.in_progress = false;
                if (working < 0) continue;

                var current = _entries[working];
                current.in_progress = true;
                _entries.RemoveAt(working);
                int first = _entries.FindIndex(e => e.queue_id == queueId);
                _entries.Insert(first >= 0 ? first : working, current);
            }
        }

        public int Count
        {
            get { lock (_lock) return _entries.Count; }
        }

        /// <summary>
        /// Копия всех записей. Порядок внутри каждой очереди: кто заказал раньше, тот выше; «делаемая» запись первая в своей очереди.
        /// Записи разных очередей идут вперемешку, страницы разбирают их по queue_id
        /// </summary>
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
            // новая запись никогда не «делается сейчас»: это решает только кнопка
            clean.in_progress = false;

            lock (_lock)
            {
                if (_entries.Any(e => e.id == clean.id)) return false;

                InsertByTime(clean);
                return true;
            }
        }

        // Запись встаёт после всех записей своей очереди, заказанных не позже неё: при одинаковом времени порядок добавления
        // сохраняется. Перед «делаемой» первой записью очереди новая не встаёт, даже если заказана раньше
        private void InsertByTime(QueueEntry entry)
        {
            string queueId = entry.queue_id;
            int first = _entries.FindIndex(e => e.queue_id == queueId);
            if (first < 0)
            {
                _entries.Add(entry);
                return;
            }

            int floor = _entries[first].in_progress ? first + 1 : first;
            int index = floor;
            for (int i = _entries.Count - 1; i >= floor; i--)
            {
                if (_entries[i].queue_id == queueId && _entries[i].redeemed_at <= entry.redeemed_at)
                {
                    index = i + 1;
                    break;
                }
            }
            _entries.Insert(index, entry);
        }

        /// <summary>
        /// Переносит записи по новому соответствию наград и очередей. mapping возвращает очередь для записи (по её награде)
        /// или null, если запись остаётся где была; запись из несуществующей очереди уходит в fallbackQueueId.
        /// Переехавшая запись теряет пометку «делаю» и встаёт в новой очереди по времени заказа. Возвращает, изменилось ли что-то
        /// </summary>
        public bool Reassign(Func<QueueEntry, string?> mapping, ICollection<string> queueIds, string fallbackQueueId)
        {
            lock (_lock)
            {
                bool changed = false;
                foreach (var entry in _entries.ToList())
                {
                    string? target = mapping(entry);
                    if (target == null || !queueIds.Contains(target)) target = queueIds.Contains(entry.queue_id) ? entry.queue_id : fallbackQueueId;
                    if (target == entry.queue_id) continue;

                    _entries.Remove(entry);
                    entry.queue_id = target;
                    entry.in_progress = false;
                    InsertByTime(entry);
                    changed = true;
                }
                return changed;
            }
        }

        /// <summary>
        /// Кнопка «Делаю»: запись поднимается на первое место своей очереди и помечается «делаю сейчас», порядок остальных
        /// не меняется. Прежняя «делаемая» запись той же очереди остаётся на втором месте уже без пометки. found: запись есть;
        /// changed: что-то действительно изменилось (повторное нажатие ничего не меняет)
        /// </summary>
        public bool StartWorking(string id, out bool changed)
        {
            changed = false;
            lock (_lock)
            {
                int index = _entries.FindIndex(e => e.id == id);
                if (index < 0) return false;

                var target = _entries[index];
                foreach (var other in _entries)
                {
                    if (other != target && other.queue_id == target.queue_id && other.in_progress)
                    {
                        other.in_progress = false;
                        changed = true;
                    }
                }

                if (!target.in_progress)
                {
                    target.in_progress = true;
                    changed = true;
                }

                int first = _entries.FindIndex(e => e.queue_id == target.queue_id);
                if (index != first)
                {
                    _entries.RemoveAt(index);
                    _entries.Insert(first, target);
                    changed = true;
                }
                return true;
            }
        }

        /// <summary>«Вернуть в очередь»: пометка снимается, запись возвращается на место по времени заказа</summary>
        public bool ReleaseWorking(string id, out bool changed)
        {
            changed = false;
            lock (_lock)
            {
                int index = _entries.FindIndex(e => e.id == id);
                if (index < 0) return false;

                var target = _entries[index];
                if (!target.in_progress) return true;

                _entries.RemoveAt(index);
                target.in_progress = false;
                InsertByTime(target);
                changed = true;
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
        public static QueueEntry NewManual(string rewardTitle, string? userName, string? userInput, DateTimeOffset now,
            string queueId = MainQueueId, string rewardId = "") => new QueueEntry
        {
            id = Guid.NewGuid().ToString("N"),
            source = QueueEntry.SourceManual,
            queue_id = queueId,
            reward_id = rewardId,
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
                redeemed_at = entry.redeemed_at,
                in_progress = entry.in_progress
            };
        }

        private static string Limit(string? text, int max)
        {
            text = (text ?? "").Trim();
            return text.Length <= max ? text : text[..max].TrimEnd();
        }
    }
}
