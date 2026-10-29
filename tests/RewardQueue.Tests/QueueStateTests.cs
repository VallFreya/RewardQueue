using Xunit;

namespace RewardQueue.Tests
{
    public class QueueStateTests
    {
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

        private static QueueEntry Entry(string id, int minutes = 0, string title = "Заказ техники", string user = "Зритель", string input = "") => new QueueEntry
        {
            id = id,
            reward_id = "reward-" + title,
            reward_title = title,
            user_name = user,
            user_input = input,
            redeemed_at = T0.AddMinutes(minutes)
        };

        private static string[] Ids(QueueState state) => state.Snapshot().Select(e => e.id).ToArray();

        [Fact]
        public void Add_KeepsOrderByRedeemTime()
        {
            var state = new QueueState();
            state.Add(Entry("b", 5));
            state.Add(Entry("c", 9));
            state.Add(Entry("a", 1));

            Assert.Equal(new[] { "a", "b", "c" }, Ids(state));
        }

        [Fact]
        public void Add_SameTimeKeepsArrivalOrder()
        {
            var state = new QueueState();
            state.Add(Entry("first", 3));
            state.Add(Entry("second", 3));
            state.Add(Entry("third", 3));

            Assert.Equal(new[] { "first", "second", "third" }, Ids(state));
        }

        [Fact]
        public void Add_RejectsRepeatedRedemptionId()
        {
            var state = new QueueState();
            Assert.True(state.Add(Entry("x", 1)));
            Assert.False(state.Add(Entry("x", 2, user: "Другой")));

            Assert.Single(state.Snapshot());
            Assert.Equal("Зритель", state.Snapshot()[0].user_name);
        }

        [Fact]
        public void Add_DoesNotMergeSameRewardFromSameViewer()
        {
            // два заказа одной награды одним зрителем это две строки
            var state = new QueueState();
            state.Add(Entry("1", 1));
            state.Add(Entry("2", 2));

            Assert.Equal(2, state.Count);
        }

        [Theory]
        [InlineData("", "Название")]
        [InlineData("  ", "Название")]
        [InlineData("id", "")]
        [InlineData("id", "   ")]
        public void Add_RejectsEntriesWithoutIdOrTitle(string id, string title)
        {
            var state = new QueueState();
            Assert.False(state.Add(Entry(id, title: title)));
            Assert.Equal(0, state.Count);
        }

        [Fact]
        public void Add_TrimsAndLimitsTexts()
        {
            var state = new QueueState();
            state.Add(Entry("1", title: new string('я', 300), user: "  Ник  ", input: new string('ы', 900)));

            var entry = state.Snapshot()[0];
            Assert.Equal(QueueState.MaxTitleLength, entry.reward_title.Length);
            Assert.Equal("Ник", entry.user_name);
            Assert.Equal(QueueState.MaxInputLength, entry.user_input.Length);
        }

        [Fact]
        public void Add_FillsDefaultsForQueueAndSource()
        {
            var state = new QueueState();
            var raw = Entry("1");
            raw.queue_id = "";
            raw.source = "что-то";
            state.Add(raw);

            var entry = state.Snapshot()[0];
            Assert.Equal(QueueState.MainQueueId, entry.queue_id);
            Assert.Equal(QueueEntry.SourceTwitch, entry.source);
        }

        [Fact]
        public void Snapshot_IsACopy()
        {
            var state = new QueueState();
            state.Add(Entry("1"));

            var snapshot = state.Snapshot();
            snapshot[0].reward_title = "изменено снаружи";

            Assert.Equal("Заказ техники", state.Snapshot()[0].reward_title);
        }

        [Fact]
        public void Remove_TakesOutOnlyThatEntry()
        {
            var state = new QueueState();
            state.Add(Entry("a", 1));
            state.Add(Entry("b", 2));
            state.Add(Entry("c", 3));

            Assert.NotNull(state.Remove("b"));
            Assert.Equal(new[] { "a", "c" }, Ids(state));
            Assert.Null(state.Remove("b"));
            Assert.Null(state.Remove("нет такой"));
        }

        [Fact]
        public void RemoveFirst_TakesTopEntryOfTheQueue()
        {
            var state = new QueueState();
            state.Add(Entry("a", 1));
            state.Add(Entry("b", 2));

            Assert.Equal("a", state.RemoveFirst()!.id);
            Assert.Equal("b", state.RemoveFirst()!.id);
            Assert.Null(state.RemoveFirst());
        }

        [Fact]
        public void RemoveFirst_LooksOnlyInTheGivenQueue()
        {
            var state = new QueueState();
            var other = Entry("other", 1);
            other.queue_id = "second";
            state.Add(other);
            state.Add(Entry("main-one", 2));

            Assert.Equal("main-one", state.RemoveFirst()!.id);
            Assert.Null(state.RemoveFirst());
            Assert.Equal("other", state.RemoveFirst("second")!.id);
        }

        [Fact]
        public void PurgeOlderThan_RemovesOnlyOldEntries()
        {
            var state = new QueueState();
            state.Add(Entry("old", -60 * 24 * 4));
            state.Add(Entry("edge", -60 * 24 * 3));
            state.Add(Entry("fresh", -10));

            int purged = state.PurgeOlderThan(T0.AddDays(-3));

            Assert.Equal(1, purged);
            Assert.Equal(new[] { "edge", "fresh" }, Ids(state));
        }

        [Fact]
        public void NewManual_HasOwnIdAndManualSource()
        {
            var a = QueueState.NewManual("Музыка", null, null, T0);
            var b = QueueState.NewManual("Музыка", "Ник", "Трек", T0);

            Assert.NotEqual(a.id, b.id);
            Assert.Equal(QueueEntry.SourceManual, a.source);
            Assert.Equal("", a.user_name);
            Assert.Equal("Трек", b.user_input);
            Assert.Equal(T0, a.redeemed_at);
        }

        [Theory]
        [InlineData(0, 3)]
        [InlineData(-5, 3)]
        [InlineData(1, 1)]
        [InlineData(7, 7)]
        [InlineData(30, 30)]
        [InlineData(500, 30)]
        public void ClampRetentionDays_KeepsValueInRange(int days, int expected)
        {
            Assert.Equal(expected, QueueState.ClampRetentionDays(days));
        }

        [Fact]
        public void Constructor_RestoresEntriesInFileOrderAndDropsRepeatsAndGarbage()
        {
            // порядок файла сохраняется как есть: он мог быть изменён кнопкой «Делаю»
            var state = new QueueState(new[] { Entry("b", 2), Entry("a", 1), Entry("a", 5), Entry("", 3) });

            Assert.Equal(new[] { "b", "a" }, Ids(state));
        }

        // ---- «Делаю» ----

        private static QueueState ThreeEntries()
        {
            var state = new QueueState();
            state.Add(Entry("a", 1));
            state.Add(Entry("b", 2));
            state.Add(Entry("c", 3));
            return state;
        }

        [Fact]
        public void StartWorking_MovesTheEntryFirstAndKeepsTheOthersInOrder()
        {
            var state = ThreeEntries();

            Assert.True(state.StartWorking("c", out bool changed));

            Assert.True(changed);
            Assert.Equal(new[] { "c", "a", "b" }, Ids(state));
            Assert.True(state.Snapshot()[0].in_progress);
            Assert.All(state.Snapshot().Skip(1), e => Assert.False(e.in_progress));
        }

        [Fact]
        public void StartWorking_OnTheFirstEntryOnlyMarksIt()
        {
            var state = ThreeEntries();

            Assert.True(state.StartWorking("a", out bool changed));

            Assert.True(changed);
            Assert.Equal(new[] { "a", "b", "c" }, Ids(state));
            Assert.True(state.Snapshot()[0].in_progress);
        }

        [Fact]
        public void StartWorking_Twice_ChangesNothingTheSecondTime()
        {
            var state = ThreeEntries();
            state.StartWorking("b", out _);

            Assert.True(state.StartWorking("b", out bool changed));

            Assert.False(changed);
            Assert.Equal(new[] { "b", "a", "c" }, Ids(state));
        }

        [Fact]
        public void StartWorking_AnotherEntry_PreviousOneStaysSecondWithoutTheMark()
        {
            var state = ThreeEntries();
            state.StartWorking("b", out _);

            state.StartWorking("c", out _);

            var snapshot = state.Snapshot();
            Assert.Equal(new[] { "c", "b", "a" }, snapshot.Select(e => e.id).ToArray());
            Assert.Equal(new[] { true, false, false }, snapshot.Select(e => e.in_progress).ToArray());
        }

        [Fact]
        public void StartWorking_UnknownId_IsNotFound()
        {
            var state = ThreeEntries();

            Assert.False(state.StartWorking("нет", out bool changed));
            Assert.False(changed);
            Assert.Equal(new[] { "a", "b", "c" }, Ids(state));
        }

        [Fact]
        public void ReleaseWorking_ReturnsTheEntryToItsPlaceByTime()
        {
            var state = ThreeEntries();
            state.StartWorking("c", out _);

            Assert.True(state.ReleaseWorking("c", out bool changed));

            Assert.True(changed);
            Assert.Equal(new[] { "a", "b", "c" }, Ids(state));
            Assert.All(state.Snapshot(), e => Assert.False(e.in_progress));
        }

        [Fact]
        public void ReleaseWorking_OfAnEntryThatIsNotWorkedOn_ChangesNothing()
        {
            var state = ThreeEntries();

            Assert.True(state.ReleaseWorking("b", out bool changed));
            Assert.False(changed);
            Assert.False(state.ReleaseWorking("нет", out _));
        }

        [Fact]
        public void NewEntry_IsNeverInsertedBeforeTheWorkedOnOne()
        {
            var state = ThreeEntries();
            state.StartWorking("c", out _);

            // заказана раньше всех, но перед «делаемой» первой записью не встаёт
            state.Add(Entry("old", -100));

            Assert.Equal(new[] { "c", "old", "a", "b" }, Ids(state));
        }

        [Fact]
        public void NewEntry_CannotComeInAlreadyMarkedAsWorkedOn()
        {
            var state = ThreeEntries();
            var sneaky = Entry("x", 10);
            sneaky.in_progress = true;

            state.Add(sneaky);

            Assert.All(state.Snapshot(), e => Assert.False(e.in_progress));
        }

        [Fact]
        public void Constructor_KeepsTheWorkedOnEntryFirstAndOnlyOne()
        {
            var a = Entry("a", 1);
            var b = Entry("b", 2);
            var c = Entry("c", 3);
            b.in_progress = true;
            c.in_progress = true;

            var state = new QueueState(new[] { a, b, c });

            var snapshot = state.Snapshot();
            Assert.Equal(new[] { "b", "a", "c" }, snapshot.Select(e => e.id).ToArray());
            Assert.Equal(new[] { true, false, false }, snapshot.Select(e => e.in_progress).ToArray());
        }

        [Fact]
        public void RemovingTheWorkedOnEntry_LeavesNobodyWorkedOn()
        {
            var state = ThreeEntries();
            state.StartWorking("b", out _);

            state.Remove("b");

            Assert.All(state.Snapshot(), e => Assert.False(e.in_progress));
            Assert.Equal(new[] { "a", "c" }, Ids(state));
        }

        [Fact]
        public void ConcurrentAdds_DoNotLoseOrDuplicateEntries()
        {
            var state = new QueueState();
            Parallel.For(0, 400, i =>
            {
                state.Add(Entry("e" + (i % 200), i));   // каждый id приходит дважды
            });

            Assert.Equal(200, state.Count);
            var times = state.Snapshot().Select(e => e.redeemed_at).ToList();
            Assert.Equal(times.OrderBy(t => t).ToList(), times);
        }
    }
}
