using Xunit;

namespace RewardQueue.Tests
{
    // Несколько очередей: у каждой свой порядок и своя запись «Сейчас»
    public class QueueStateMultiQueueTests
    {
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

        private static QueueEntry Entry(string id, int minutes, string queue, string reward = "") => new QueueEntry
        {
            id = id,
            queue_id = queue,
            reward_id = reward,
            reward_title = "Награда " + reward,
            user_name = "Ник " + id,
            redeemed_at = T0.AddMinutes(minutes)
        };

        private static string[] InQueue(QueueState state, string queue) =>
            state.Snapshot().Where(e => e.queue_id == queue).Select(e => e.id).ToArray();

        private static QueueState TwoQueues()
        {
            // A: a1 a2 a3, B: b1 b2; в общем списке вперемешку по времени
            var state = new QueueState();
            state.Add(Entry("a1", 1, "A"));
            state.Add(Entry("b1", 2, "B"));
            state.Add(Entry("a2", 3, "A"));
            state.Add(Entry("b2", 4, "B"));
            state.Add(Entry("a3", 5, "A"));
            return state;
        }

        [Fact]
        public void EachQueueKeepsItsOwnOrderByTime()
        {
            var state = TwoQueues();

            Assert.Equal(new[] { "a1", "a2", "a3" }, InQueue(state, "A"));
            Assert.Equal(new[] { "b1", "b2" }, InQueue(state, "B"));
        }

        [Fact]
        public void LateOrderIsInsertedByTimeOnlyAmongItsOwnQueue()
        {
            var state = TwoQueues();

            state.Add(Entry("b0", 0, "B"));      // старше всех
            state.Add(Entry("a15", 2, "A"));     // между a1 и a2

            Assert.Equal(new[] { "b0", "b1", "b2" }, InQueue(state, "B"));
            Assert.Equal(new[] { "a1", "a15", "a2", "a3" }, InQueue(state, "A"));
        }

        [Fact]
        public void StartWorking_MovesTheEntryFirstInItsOwnQueueOnly()
        {
            var state = TwoQueues();

            Assert.True(state.StartWorking("a3", out bool changed));

            Assert.True(changed);
            Assert.Equal(new[] { "a3", "a1", "a2" }, InQueue(state, "A"));
            Assert.Equal(new[] { "b1", "b2" }, InQueue(state, "B"));
        }

        [Fact]
        public void EveryQueueCanHaveItsOwnWorkedOnEntry()
        {
            var state = TwoQueues();

            state.StartWorking("a2", out _);
            state.StartWorking("b2", out _);

            var snapshot = state.Snapshot();
            Assert.Equal(new[] { "a2", "b2" }, snapshot.Where(e => e.in_progress).Select(e => e.id).OrderBy(x => x).ToArray());
            Assert.Equal(new[] { "a2", "a1", "a3" }, InQueue(state, "A"));
            Assert.Equal(new[] { "b2", "b1" }, InQueue(state, "B"));
        }

        [Fact]
        public void StartWorking_AnotherEntryOfTheSameQueue_ClearsOnlyThatQueuesMark()
        {
            var state = TwoQueues();
            state.StartWorking("a2", out _);
            state.StartWorking("b1", out _);

            state.StartWorking("a3", out _);

            var snapshot = state.Snapshot().ToDictionary(e => e.id);
            Assert.True(snapshot["a3"].in_progress);
            Assert.False(snapshot["a2"].in_progress);
            Assert.True(snapshot["b1"].in_progress);   // чужая очередь не затронута
        }

        [Fact]
        public void NewEntry_NeverGoesBeforeItsQueuesWorkedOnEntry()
        {
            var state = TwoQueues();
            state.StartWorking("a3", out _);

            state.Add(Entry("a0", -10, "A"));

            Assert.Equal(new[] { "a3", "a0", "a1", "a2" }, InQueue(state, "A"));
        }

        [Fact]
        public void ReleaseWorking_ReturnsTheEntryToItsPlaceInItsQueue()
        {
            var state = TwoQueues();
            state.StartWorking("a3", out _);
            state.StartWorking("b2", out _);

            Assert.True(state.ReleaseWorking("a3", out bool changed));

            Assert.True(changed);
            Assert.Equal(new[] { "a1", "a2", "a3" }, InQueue(state, "A"));
            Assert.Equal(new[] { "b2", "b1" }, InQueue(state, "B"));   // у другой очереди пометка осталась
        }

        [Fact]
        public void RemoveFirst_TakesTheTopOfTheGivenQueue()
        {
            var state = TwoQueues();
            state.StartWorking("b2", out _);

            Assert.Equal("b2", state.RemoveFirst("B")!.id);
            Assert.Equal("a1", state.RemoveFirst("A")!.id);
        }

        [Fact]
        public void Constructor_KeepsOneWorkedOnEntryPerQueueAndPutsItFirst()
        {
            var a1 = Entry("a1", 1, "A");
            var a2 = Entry("a2", 2, "A");
            var b1 = Entry("b1", 3, "B");
            var b2 = Entry("b2", 4, "B");
            a2.in_progress = true;
            b2.in_progress = true;
            b1.in_progress = true;   // лишняя пометка: в очереди B остаётся первая из помеченных

            var state = new QueueState(new[] { a1, a2, b1, b2 });

            Assert.Equal(new[] { "a2", "a1" }, InQueue(state, "A"));
            Assert.Equal(new[] { "b1", "b2" }, InQueue(state, "B"));
            Assert.Equal(new[] { "a2", "b1" }, state.Snapshot().Where(e => e.in_progress).Select(e => e.id).OrderBy(x => x).ToArray());
        }

        [Fact]
        public void Reassign_MovesEntriesOfARewardToTheNewQueueByTime()
        {
            var state = new QueueState();
            state.Add(Entry("a1", 1, "A", "r1"));
            state.Add(Entry("a2", 3, "A", "r2"));
            state.Add(Entry("b1", 2, "B", "r3"));
            state.Add(Entry("b2", 4, "B", "r3"));

            // награда r2 переехала в очередь B
            bool changed = state.Reassign(e => e.reward_id == "r2" ? "B" : null, new[] { "A", "B" }, "A");

            Assert.True(changed);
            Assert.Equal(new[] { "a1" }, InQueue(state, "A"));
            Assert.Equal(new[] { "b1", "a2", "b2" }, InQueue(state, "B"));   // по времени: 2, 3, 4
        }

        [Fact]
        public void Reassign_MovedEntryLosesTheWorkedOnMark()
        {
            var state = new QueueState();
            state.Add(Entry("a1", 1, "A", "r1"));
            state.Add(Entry("b1", 2, "B", "r2"));
            state.StartWorking("a1", out _);

            state.Reassign(e => e.reward_id == "r1" ? "B" : null, new[] { "A", "B" }, "A");

            Assert.All(state.Snapshot(), e => Assert.False(e.in_progress));
            Assert.Equal(new[] { "a1", "b1" }, InQueue(state, "B"));
        }

        [Fact]
        public void Reassign_EntriesOfARemovedQueueGoToTheFallback()
        {
            var state = TwoQueues();

            // очередь B удалена: в списке остались только A
            bool changed = state.Reassign(e => null, new[] { "A" }, "A");

            Assert.True(changed);
            Assert.Equal(new[] { "a1", "b1", "a2", "b2", "a3" }, InQueue(state, "A"));
            Assert.Empty(InQueue(state, "B"));
        }

        [Fact]
        public void Reassign_EntriesWithoutMappingStayInTheirQueues()
        {
            var state = TwoQueues();

            // награда перестала отслеживаться (mapping null): записи остаются, где были
            Assert.False(state.Reassign(e => null, new[] { "A", "B" }, "A"));

            Assert.Equal(new[] { "a1", "a2", "a3" }, InQueue(state, "A"));
            Assert.Equal(new[] { "b1", "b2" }, InQueue(state, "B"));
        }

        [Fact]
        public void Reassign_RepeatedWithTheSameMappingChangesNothing()
        {
            var state = TwoQueues();
            state.Reassign(e => e.id == "a2" ? "B" : null, new[] { "A", "B" }, "A");

            Assert.False(state.Reassign(e => e.id == "a2" ? "B" : null, new[] { "A", "B" }, "A"));
        }

        [Fact]
        public void NewManual_CarriesTheRewardId()
        {
            var entry = QueueState.NewManual("Музыка", null, null, T0, "B", "r7");

            Assert.Equal("B", entry.queue_id);
            Assert.Equal("r7", entry.reward_id);
            Assert.Equal(QueueEntry.SourceManual, entry.source);
        }
    }

    public class QueueServiceMultiQueueTests : IClassFixture<PluginFixture>
    {
        private readonly string _path;
        private readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

        public QueueServiceMultiQueueTests(PluginFixture fixture)
        {
            _path = Path.Combine(fixture.Dir, "queue_multi_" + Guid.NewGuid().ToString("N") + ".json");
        }

        private QueueService Open() => new QueueService(_path, () => _now, 3);

        [Fact]
        public void Reassign_IsSavedAndAnnouncedOnlyWhenSomethingMoved()
        {
            var service = Open();
            service.Add(new QueueEntry { id = "x", queue_id = "A", reward_id = "r", reward_title = "R", redeemed_at = _now });
            int changes = 0;
            service.Changed += () => changes++;

            Assert.False(service.Reassign(e => null, new[] { "A", "B" }, "A"));
            Assert.Equal(0, changes);

            Assert.True(service.Reassign(e => "B", new[] { "A", "B" }, "A"));
            Assert.Equal(1, changes);
            Assert.Equal("B", Open().Snapshot().Single().queue_id);
        }

        [Fact]
        public void AddManual_GoesToTheGivenQueueWithItsReward()
        {
            var service = Open();

            string? id = service.AddManual("Музыка", "Ник", "трек", queueId: "B", rewardId: "r9");

            var entry = service.Snapshot().Single(e => e.id == id);
            Assert.Equal("B", entry.queue_id);
            Assert.Equal("r9", entry.reward_id);
        }
    }
}
