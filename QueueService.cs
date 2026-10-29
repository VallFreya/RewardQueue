namespace RewardQueue
{
    /// <summary>
    /// Очередь вместе с файлом: каждое изменение сразу записывается в queue.json, и только потом о нём узнают подписчики
    /// (страницы и окно). Состояние и файл меняются под одной блокировкой, чтобы в файле не оказалась устаревшая очередь.
    /// </summary>
    internal sealed class QueueService
    {
        private readonly object _gate = new object();
        private readonly QueueState _state;
        private readonly string _path;
        private readonly Func<DateTimeOffset> _clock;
        private readonly TimeSpan _retention;

        /// <summary>Действующая очередь (создаётся при загрузке плагина)</summary>
        public static QueueService? Current { get; set; }

        /// <summary>Очередь изменилась. Вызывается вне блокировок, из потока, который внёс изменение</summary>
        public event Action? Changed;

        /// <summary>Последняя запись в файл не удалась (диск полон, файл занят): очередь работает, но после перезапуска может потеряться</summary>
        public bool LastSaveFailed { get; private set; }

        public QueueService(string path, Func<DateTimeOffset> clock, int retentionDays)
        {
            _path = path;
            _clock = clock;
            _retention = TimeSpan.FromDays(QueueState.ClampRetentionDays(retentionDays));
            _state = new QueueState(QueueStore.Load(path));

            // Записи, не выполненные за срок хранения, при запуске не восстанавливаются
            int purged = _state.PurgeOlderThan(_clock() - _retention);
            if (purged > 0)
            {
                Plugin.LogInfo($"Queue: removed {purged} expired entries at start");
                Save();
            }
        }

        public int Count => _state.Count;

        public IReadOnlyList<QueueEntry> Snapshot() => _state.Snapshot();

        /// <summary>Новый заказ Twitch. false, если это повтор уже известного заказа или запись пустая</summary>
        public bool Add(QueueEntry entry) => Mutate(() => _state.Add(entry));

        /// <summary>
        /// Запись, добавленная вручную, в указанную очередь (награда из списка определяет очередь сама, это решает вызывающий).
        /// Возвращает её id или null, если название пустое
        /// </summary>
        public string? AddManual(string rewardTitle, string? userName, string? userInput, string queueId = QueueState.MainQueueId, string rewardId = "")
        {
            var entry = QueueState.NewManual(rewardTitle, userName, userInput, _clock(), queueId, rewardId);
            return Mutate(() => _state.Add(entry)) ? entry.id : null;
        }

        /// <summary>«Делаю»: запись становится первой и «делаемой». false, если такой записи нет</summary>
        public bool StartWorking(string id)
        {
            bool found = false;
            Mutate(() =>
            {
                found = _state.StartWorking(id, out bool changed);
                return changed;
            });
            return found;
        }

        /// <summary>«Вернуть в очередь»: запись снова обычная и встаёт на место по времени. false, если записи нет</summary>
        public bool ReleaseWorking(string id)
        {
            bool found = false;
            Mutate(() =>
            {
                found = _state.ReleaseWorking(id, out bool changed);
                return changed;
            });
            return found;
        }

        /// <summary>Награды переехали между очередями или очередь удалена: записи переносятся следом</summary>
        public bool Reassign(Func<QueueEntry, string?> mapping, ICollection<string> queueIds, string fallbackQueueId) =>
            Mutate(() => _state.Reassign(mapping, queueIds, fallbackQueueId));

        /// <summary>«Выполнено» или «Убрать»</summary>
        public bool Remove(string id) => Mutate(() => _state.Remove(id) != null);

        /// <summary>Хоткей: выполнена первая награда. false, если очередь пуста</summary>
        public bool CompleteFirst(string queueId = QueueState.MainQueueId) => Mutate(() => _state.RemoveFirst(queueId) != null);

        /// <summary>Убирает записи старше срока хранения (раз в час). Возвращает, сколько убрано</summary>
        public int PurgeExpired()
        {
            int purged = 0;
            Mutate(() =>
            {
                purged = _state.PurgeOlderThan(_clock() - _retention);
                return purged > 0;
            });
            if (purged > 0) Plugin.LogInfo($"Queue: removed {purged} expired entries");
            return purged;
        }

        // Выполняет изменение; если оно что-то поменяло, записывает файл и сообщает подписчикам
        private bool Mutate(Func<bool> change)
        {
            bool changed;
            lock (_gate)
            {
                changed = change();
                if (changed) Save();
            }

            if (changed) RaiseChanged();
            return changed;
        }

        private void Save()
        {
            try
            {
                QueueStore.Save(_path, _state.Snapshot());
                LastSaveFailed = false;
            }
            catch (Exception ex)
            {
                LastSaveFailed = true;
                Plugin.LogError($"Failed to save the queue to '{_path}': {ex.Message}");
            }
        }

        private void RaiseChanged()
        {
            try
            {
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                // сбой подписчика не должен ломать того, кто менял очередь (поток Twitch, хоткей)
                Plugin.LogError($"Queue change handler failed: {ex.Message}");
            }
        }
    }
}
