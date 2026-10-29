namespace RewardQueue
{
    /// <summary>
    /// Какие награды отслеживаются и в какую очередь попадают. Неизменяемый снимок: читается из потоков Twitch и окна,
    /// любое изменение даёт новый снимок. Без OBS и сети.
    /// </summary>
    internal sealed class RewardSettings
    {
        public IReadOnlyList<RewardInfo> Known { get; }
        public IReadOnlyList<QueueConfig> Queues { get; }

        private RewardSettings(IReadOnlyList<RewardInfo> known, IReadOnlyList<QueueConfig> queues)
        {
            Known = known;
            Queues = queues;
        }

        public static RewardSettings Empty { get; } = FromConfig(null, null);

        /// <summary>Настройки из config.json. Мусор отбрасывается, очередей всегда хотя бы одна (основная)</summary>
        public static RewardSettings FromConfig(IEnumerable<RewardInfo?>? known, IEnumerable<QueueConfig?>? queues)
        {
            var knownList = new List<RewardInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reward in known ?? Enumerable.Empty<RewardInfo?>())
            {
                string id = (reward?.id ?? "").Trim();
                if (id.Length == 0 || !seen.Add(id)) continue;
                knownList.Add(new RewardInfo { id = id, title = (reward!.title ?? "").Trim() });
            }

            var queueList = new List<QueueConfig>();
            var queueIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var queue in queues ?? Enumerable.Empty<QueueConfig?>())
            {
                string id = (queue?.id ?? "").Trim();
                if (id.Length == 0 || !queueIds.Add(id)) continue;
                queueList.Add(new QueueConfig
                {
                    id = id,
                    name = (queue!.name ?? "").Trim(),
                    reward_ids = (queue.reward_ids ?? new List<string>())
                        .Select(r => (r ?? "").Trim()).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList()
                });
            }

            if (queueList.Count == 0) queueList.Add(new QueueConfig { id = QueueState.MainQueueId });
            return new RewardSettings(knownList, queueList);
        }

        /// <summary>В какую очередь попадает заказ этой награды; null, если награду не отслеживаем</summary>
        public string? QueueFor(string rewardId)
        {
            foreach (var queue in Queues)
            {
                if (queue.reward_ids.Contains(rewardId, StringComparer.Ordinal)) return queue.id;
            }
            return null;
        }

        public bool IsTracked(string rewardId) => QueueFor(rewardId) != null;

        /// <summary>
        /// Новый список наград канала (после «Обновить список»). Отметки сохраняются по id: переименованная награда
        /// остаётся отмеченной, а удалённая с Twitch просто пропадает из списка (отметка о ней в конфиге остаётся без последствий)
        /// </summary>
        public RewardSettings WithKnown(IEnumerable<RewardInfo> rewards) => FromConfig(rewards, Queues);

        /// <summary>Включает или выключает отслеживание награды (в основной очереди, первой в списке)</summary>
        public RewardSettings WithTracked(string rewardId, bool tracked)
        {
            var queues = Queues.Select(q => new QueueConfig { id = q.id, name = q.name, reward_ids = q.reward_ids.ToList() }).ToList();
            var main = queues[0];
            foreach (var queue in queues) queue.reward_ids.RemoveAll(r => r == rewardId);
            if (tracked) main.reward_ids.Add(rewardId);

            return FromConfig(Known, queues);
        }

        /// <summary>Сколько известных наград сейчас отслеживается (для подписи в окне)</summary>
        public int TrackedKnownCount => Known.Count(r => IsTracked(r.id));
    }
}
