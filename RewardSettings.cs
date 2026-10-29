namespace RewardQueue
{
    /// <summary>
    /// Какие награды отслеживаются, в каких очередях они лежат и как очереди показываются на трансляции.
    /// Неизменяемый снимок: читается из потоков Twitch, окна и сервера, любое изменение даёт новый снимок. Без OBS и сети.
    /// </summary>
    internal sealed class RewardSettings
    {
        public const int MaxQueueNameLength = 50;
        public const int MaxShowLimit = 999;

        public IReadOnlyList<RewardInfo> Known { get; }
        public IReadOnlyList<QueueConfig> Queues { get; }

        /// <summary>Сколько записей всего показывать на трансляции по всем очередям вместе; 0 без ограничения</summary>
        public int ShowTotal { get; }

        private RewardSettings(IReadOnlyList<RewardInfo> known, IReadOnlyList<QueueConfig> queues, int showTotal)
        {
            Known = known;
            Queues = queues;
            ShowTotal = showTotal;
        }

        public static RewardSettings Empty { get; } = FromConfig(null, null);

        /// <summary>Настройки из config.json. Мусор отбрасывается, очередей всегда хотя бы одна (основная), границы чисел приводятся к допустимым</summary>
        public static RewardSettings FromConfig(IEnumerable<RewardInfo?>? known, IEnumerable<QueueConfig?>? queues, int showTotal = 0)
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
            var trackedRewards = new HashSet<string>(StringComparer.Ordinal);
            foreach (var queue in queues ?? Enumerable.Empty<QueueConfig?>())
            {
                string id = (queue?.id ?? "").Trim();
                if (id.Length == 0 || !queueIds.Add(id)) continue;

                // награда лежит ровно в одной очереди: повтор из ручной правки конфига отбрасывается
                var rewards = new List<string>();
                foreach (string? reward in queue!.reward_ids ?? new List<string>())
                {
                    string rewardId = (reward ?? "").Trim();
                    if (rewardId.Length > 0 && trackedRewards.Add(rewardId)) rewards.Add(rewardId);
                }

                queueList.Add(new QueueConfig
                {
                    id = id,
                    name = Limit((queue.name ?? "").Trim(), MaxQueueNameLength),
                    show_limit = ClampLimit(queue.show_limit),
                    reward_ids = rewards
                });
            }

            if (queueList.Count == 0) queueList.Add(new QueueConfig { id = QueueState.MainQueueId });
            return new RewardSettings(knownList, queueList, ClampLimit(showTotal));
        }

        private static int ClampLimit(int value) => Math.Clamp(value, 0, MaxShowLimit);

        private static string Limit(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd();

        private RewardSettings Rebuild(IEnumerable<RewardInfo>? known = null, IEnumerable<QueueConfig>? queues = null, int? showTotal = null) =>
            FromConfig(known ?? Known, queues ?? CloneQueues(), showTotal ?? ShowTotal);

        private List<QueueConfig> CloneQueues() => Queues.Select(q => new QueueConfig
        {
            id = q.id,
            name = q.name,
            show_limit = q.show_limit,
            reward_ids = q.reward_ids.ToList()
        }).ToList();

        /// <summary>Id первой очереди: в неё попадают отмеченные награды и награды удалённых очередей</summary>
        public string FirstQueueId => Queues[0].id;

        public QueueConfig? Queue(string id) => Queues.FirstOrDefault(q => q.id == id);

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
        public RewardSettings WithKnown(IEnumerable<RewardInfo> rewards) => Rebuild(known: rewards);

        /// <summary>
        /// Включает или выключает отслеживание награды. Новая отмеченная награда попадает в первую очередь;
        /// уже отмеченная остаётся в своей очереди
        /// </summary>
        public RewardSettings WithTracked(string rewardId, bool tracked)
        {
            if (tracked && IsTracked(rewardId)) return this;

            var queues = CloneQueues();
            foreach (var queue in queues) queue.reward_ids.RemoveAll(r => r == rewardId);
            if (tracked) queues[0].reward_ids.Add(rewardId);
            return Rebuild(queues: queues);
        }

        // ---- очереди ----

        /// <summary>Добавляет пустую очередь без названия в конец</summary>
        public RewardSettings AddQueue()
        {
            var queues = CloneQueues();
            var ids = new HashSet<string>(queues.Select(q => q.id), StringComparer.Ordinal);
            string id;
            do { id = "q" + Guid.NewGuid().ToString("N")[..8]; } while (!ids.Add(id));
            queues.Add(new QueueConfig { id = id });
            return Rebuild(queues: queues);
        }

        /// <summary>
        /// Удаляет очередь, её награды переходят в первую из оставшихся (в конец её списка). Последнюю очередь удалить нельзя.
        /// Записи удалённой очереди переносит QueueState.Reassign по новому соответствию
        /// </summary>
        public RewardSettings RemoveQueue(string id)
        {
            if (Queues.Count <= 1) return this;
            var queues = CloneQueues();
            int index = queues.FindIndex(q => q.id == id);
            if (index < 0) return this;

            var removed = queues[index];
            queues.RemoveAt(index);
            queues[0].reward_ids.AddRange(removed.reward_ids);
            return Rebuild(queues: queues);
        }

        /// <summary>Ставит очередь на новое место в списке (перетаскивание). Индекс считается в списке без неё</summary>
        public RewardSettings MoveQueue(string id, int newIndex)
        {
            var queues = CloneQueues();
            int index = queues.FindIndex(q => q.id == id);
            if (index < 0) return this;

            var queue = queues[index];
            queues.RemoveAt(index);
            queues.Insert(Math.Clamp(newIndex, 0, queues.Count), queue);
            return Rebuild(queues: queues);
        }

        /// <summary>Переносит награду в другую очередь (в конец её списка). Неотмеченная награда этим становится отмеченной</summary>
        public RewardSettings MoveReward(string rewardId, string toQueueId)
        {
            var queues = CloneQueues();
            var target = queues.Find(q => q.id == toQueueId);
            if (target == null) return this;

            foreach (var queue in queues) queue.reward_ids.RemoveAll(r => r == rewardId);
            target.reward_ids.Add(rewardId);
            return Rebuild(queues: queues);
        }

        public RewardSettings RenameQueue(string id, string name) => Update(id, q => q.name = name ?? "");

        /// <summary>Сколько записей очереди показывать на трансляции; 0 без ограничения</summary>
        public RewardSettings SetShowLimit(string id, int limit) => Update(id, q => q.show_limit = limit);

        public RewardSettings WithShowTotal(int total) => Rebuild(showTotal: total);

        private RewardSettings Update(string id, Action<QueueConfig> change)
        {
            var queues = CloneQueues();
            var queue = queues.Find(q => q.id == id);
            if (queue == null) return this;

            change(queue);
            return Rebuild(queues: queues);
        }

        /// <summary>Сколько известных наград сейчас отслеживается (для подписи в окне)</summary>
        public int TrackedKnownCount => Known.Count(r => IsTracked(r.id));
    }
}
