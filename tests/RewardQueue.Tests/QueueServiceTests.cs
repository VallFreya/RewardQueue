using Xunit;

namespace RewardQueue.Tests
{
    public class QueueServiceTests : IClassFixture<PluginFixture>
    {
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

        private readonly string _path;
        private DateTimeOffset _now = Now;

        public QueueServiceTests(PluginFixture fixture)
        {
            _path = Path.Combine(fixture.Dir, "queue_service_" + Guid.NewGuid().ToString("N") + ".json");
        }

        private QueueService Open(int retentionDays = 3) => new QueueService(_path, () => _now, retentionDays);

        private QueueEntry Twitch(string id, int minutesAgo = 0, string title = "Заказ техники") => new QueueEntry
        {
            id = id,
            reward_id = "r1",
            reward_title = title,
            user_name = "Зритель",
            redeemed_at = _now.AddMinutes(-minutesAgo)
        };

        [Fact]
        public void EveryChangeIsWrittenToFileAndRestoredAfterRestart()
        {
            var service = Open();
            service.Add(Twitch("a", 10));
            service.Add(Twitch("b", 5));
            string? manual = service.AddManual("Музыка", "Ник", "трек");
            service.Remove("a");

            var restored = Open();
            var entries = restored.Snapshot();

            Assert.Equal(new[] { "b", manual }, entries.Select(e => e.id).ToArray());
            Assert.Equal(QueueEntry.SourceManual, entries[1].source);
        }

        [Fact]
        public void RepeatedRedemption_IsIgnoredAndDoesNotRaiseChanged()
        {
            var service = Open();
            int changes = 0;
            service.Changed += () => changes++;

            Assert.True(service.Add(Twitch("a")));
            Assert.False(service.Add(Twitch("a")));

            Assert.Equal(1, changes);
        }

        [Fact]
        public void Changed_IsRaisedAfterTheFileIsWritten()
        {
            var service = Open();
            int entriesInFileAtNotification = -1;
            service.Changed += () => entriesInFileAtNotification = QueueStore.Load(_path).Count;

            service.Add(Twitch("a"));

            Assert.Equal(1, entriesInFileAtNotification);
        }

        [Fact]
        public void FailingSubscriberDoesNotBreakTheChange()
        {
            var service = Open();
            service.Changed += () => throw new InvalidOperationException("сбой подписчика");

            Assert.True(service.Add(Twitch("a")));
            Assert.Equal(1, service.Count);
        }

        [Fact]
        public void AddManual_RejectsEmptyTitle()
        {
            var service = Open();

            Assert.Null(service.AddManual("  ", "Ник", null));
            Assert.Equal(0, service.Count);
        }

        [Fact]
        public void CompleteFirst_RemovesTopEntryAndReportsEmptyQueue()
        {
            var service = Open();
            service.Add(Twitch("a", 10));
            service.Add(Twitch("b", 5));

            Assert.True(service.CompleteFirst());
            Assert.Equal("b", service.Snapshot().Single().id);
            Assert.True(service.CompleteFirst());
            Assert.False(service.CompleteFirst());
        }

        [Fact]
        public void Start_DropsEntriesOlderThanRetention()
        {
            var service = Open(retentionDays: 3);
            service.Add(Twitch("old", minutesAgo: 60 * 24 * 4));
            service.Add(Twitch("fresh", minutesAgo: 60));

            var restarted = Open(retentionDays: 3);

            Assert.Equal("fresh", restarted.Snapshot().Single().id);
            // и в файле старой записи уже нет
            Assert.Equal("fresh", QueueStore.Load(_path).Single().id);
        }

        [Fact]
        public void PurgeExpired_RemovesEntriesThatBecameOldWhileRunning()
        {
            var service = Open(retentionDays: 3);
            service.Add(Twitch("a", minutesAgo: 60 * 24 * 2));
            int changes = 0;
            service.Changed += () => changes++;

            Assert.Equal(0, service.PurgeExpired());
            Assert.Equal(0, changes);

            _now = _now.AddDays(2);   // прошло ещё два дня: запись старше трёх суток
            Assert.Equal(1, service.PurgeExpired());
            Assert.Equal(0, service.Count);
            Assert.Equal(1, changes);
        }

        [Fact]
        public void SaveFailure_IsReportedButQueueKeepsWorking()
        {
            // путь, по которому записать нельзя: вместо файла папка
            string blocked = Path.Combine(Path.GetDirectoryName(_path)!, "blocked_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(blocked);
            var service = new QueueService(blocked, () => _now, 3);

            Assert.True(service.Add(Twitch("a")));

            Assert.True(service.LastSaveFailed);
            Assert.Equal(1, service.Count);
        }

        [Fact]
        public void StartWorking_IsSavedAndRestoredAfterRestart()
        {
            var service = Open();
            service.Add(Twitch("a", 30));
            service.Add(Twitch("b", 20));
            service.Add(Twitch("c", 10));

            Assert.True(service.StartWorking("c"));

            var restored = Open().Snapshot();
            Assert.Equal(new[] { "c", "a", "b" }, restored.Select(e => e.id).ToArray());
            Assert.True(restored[0].in_progress);
        }

        [Fact]
        public void StartWorking_RaisesChangedOnlyWhenSomethingChanged()
        {
            var service = Open();
            service.Add(Twitch("a", 30));
            service.Add(Twitch("b", 20));
            int changes = 0;
            service.Changed += () => changes++;

            service.StartWorking("b");
            service.StartWorking("b");   // повтор ничего не меняет
            Assert.Equal(1, changes);

            Assert.False(service.StartWorking("нет"));
            Assert.Equal(1, changes);
        }

        [Fact]
        public void ReleaseWorking_PutsTheEntryBackAndSaves()
        {
            var service = Open();
            service.Add(Twitch("a", 30));
            service.Add(Twitch("b", 20));
            service.StartWorking("b");

            Assert.True(service.ReleaseWorking("b"));

            Assert.Equal(new[] { "a", "b" }, Open().Snapshot().Select(e => e.id).ToArray());
            Assert.False(service.ReleaseWorking("нет"));
        }

        [Fact]
        public void CompleteFirst_RemovesTheWorkedOnEntry()
        {
            var service = Open();
            service.Add(Twitch("a", 30));
            service.Add(Twitch("b", 20));
            service.StartWorking("b");

            Assert.True(service.CompleteFirst());

            Assert.Equal("a", service.Snapshot().Single().id);
        }

        [Fact]
        public void BrokenFileAtStart_GivesEmptyQueueAndKeepsCopy()
        {
            File.WriteAllText(_path, "{ мусор");

            var service = Open();

            Assert.Equal(0, service.Count);
            Assert.Equal("{ мусор", File.ReadAllText(_path + ".broken"));
        }
    }
}
