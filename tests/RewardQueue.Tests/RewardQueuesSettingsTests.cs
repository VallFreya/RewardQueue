using Xunit;

namespace RewardQueue.Tests
{
    // Несколько очередей: добавление, удаление, порядок, перенос наград, лимиты показа
    public class RewardQueuesSettingsTests
    {
        private static RewardInfo Reward(string id) => new RewardInfo { id = id, title = "Награда " + id };

        private static RewardSettings WithRewards(params string[] ids) =>
            ids.Aggregate(RewardSettings.Empty.WithKnown(ids.Select(Reward)), (s, id) => s.WithTracked(id, true));

        private static string[] RewardsOf(RewardSettings s, int queueIndex) => s.Queues[queueIndex].reward_ids.ToArray();

        [Fact]
        public void ByDefault_OneQueueWithoutNameAndNoLimits()
        {
            var settings = RewardSettings.Empty;

            Assert.Single(settings.Queues);
            Assert.Equal("", settings.Queues[0].name);
            Assert.Equal(0, settings.Queues[0].show_limit);
            Assert.Equal(0, settings.ShowTotal);
        }

        [Fact]
        public void AddQueue_AppendsAnEmptyUnnamedQueueWithUniqueId()
        {
            var settings = RewardSettings.Empty.AddQueue().AddQueue();

            Assert.Equal(3, settings.Queues.Count);
            Assert.Equal(3, settings.Queues.Select(q => q.id).Distinct().Count());
            Assert.All(settings.Queues.Skip(1), q =>
            {
                Assert.Equal("", q.name);
                Assert.Empty(q.reward_ids);
                Assert.Equal(0, q.show_limit);
            });
        }

        [Fact]
        public void NewlyTickedReward_GoesToTheFirstQueue()
        {
            var settings = RewardSettings.Empty.AddQueue().WithKnown(new[] { Reward("a") }).WithTracked("a", true);

            Assert.Equal(new[] { "a" }, RewardsOf(settings, 0));
            Assert.Empty(RewardsOf(settings, 1));
        }

        [Fact]
        public void TickingAnAlreadyTrackedReward_KeepsItInItsQueue()
        {
            var settings = RewardSettings.Empty.AddQueue().WithKnown(new[] { Reward("a") }).WithTracked("a", true);
            settings = settings.MoveReward("a", settings.Queues[1].id);

            settings = settings.WithTracked("a", true);

            Assert.Equal(new[] { "a" }, RewardsOf(settings, 1));
        }

        [Fact]
        public void UntickingAReward_RemovesItFromItsQueue()
        {
            var settings = WithRewards("a", "b").AddQueue();
            settings = settings.MoveReward("b", settings.Queues[1].id);

            settings = settings.WithTracked("b", false);

            Assert.Equal(new[] { "a" }, RewardsOf(settings, 0));
            Assert.Empty(RewardsOf(settings, 1));
            Assert.False(settings.IsTracked("b"));
        }

        [Fact]
        public void RemoveQueue_MovesItsRewardsToTheFirstOne()
        {
            // две очереди по две награды: после удаления второй все четыре в первой
            var settings = WithRewards("a", "b", "c", "d").AddQueue();
            string second = settings.Queues[1].id;
            settings = settings.MoveReward("c", second).MoveReward("d", second);
            Assert.Equal(new[] { "a", "b" }, RewardsOf(settings, 0));
            Assert.Equal(new[] { "c", "d" }, RewardsOf(settings, 1));

            settings = settings.RemoveQueue(second);

            Assert.Single(settings.Queues);
            Assert.Equal(new[] { "a", "b", "c", "d" }, RewardsOf(settings, 0));
        }

        [Fact]
        public void RemoveQueue_TheFirstOneHandsItsRewardsToTheNewFirst()
        {
            var settings = WithRewards("a", "b").AddQueue();
            string first = settings.Queues[0].id;
            string second = settings.Queues[1].id;
            settings = settings.MoveReward("b", second);

            settings = settings.RemoveQueue(first);

            Assert.Equal(second, settings.FirstQueueId);
            Assert.Equal(new[] { "b", "a" }, RewardsOf(settings, 0));
        }

        [Fact]
        public void RemoveQueue_TheLastQueueCannotBeRemoved()
        {
            var settings = WithRewards("a");

            var after = settings.RemoveQueue(settings.FirstQueueId);

            Assert.Single(after.Queues);
            Assert.True(after.IsTracked("a"));
        }

        [Fact]
        public void RemoveQueue_UnknownIdChangesNothing()
        {
            var settings = WithRewards("a").AddQueue();

            Assert.Equal(2, settings.RemoveQueue("нет такой").Queues.Count);
        }

        [Fact]
        public void MoveQueue_ChangesTheOrder()
        {
            var settings = RewardSettings.Empty.AddQueue().AddQueue();
            string[] ids = settings.Queues.Select(q => q.id).ToArray();

            var moved = settings.MoveQueue(ids[2], 0);
            Assert.Equal(new[] { ids[2], ids[0], ids[1] }, moved.Queues.Select(q => q.id).ToArray());

            moved = settings.MoveQueue(ids[0], 2);
            Assert.Equal(new[] { ids[1], ids[2], ids[0] }, moved.Queues.Select(q => q.id).ToArray());

            // индекс вне границ упирается в край
            Assert.Equal(ids[0], settings.MoveQueue(ids[0], -5).Queues[0].id);
            Assert.Equal(ids[0], settings.MoveQueue(ids[0], 99).Queues[2].id);
            // неизвестная очередь ничего не меняет
            Assert.Equal(ids, settings.MoveQueue("нет", 1).Queues.Select(q => q.id).ToArray());
        }

        [Fact]
        public void MoveReward_TakesItOutOfTheOldQueueAndPutsItLastInTheNew()
        {
            var settings = WithRewards("a", "b", "c").AddQueue();
            string second = settings.Queues[1].id;

            settings = settings.MoveReward("a", second).MoveReward("c", second);

            Assert.Equal(new[] { "b" }, RewardsOf(settings, 0));
            Assert.Equal(new[] { "a", "c" }, RewardsOf(settings, 1));
            Assert.Equal(second, settings.QueueFor("a"));
        }

        [Fact]
        public void MoveReward_ToUnknownQueueChangesNothing()
        {
            var settings = WithRewards("a");

            Assert.Equal(new[] { "a" }, RewardsOf(settings.MoveReward("a", "нет"), 0));
        }

        [Fact]
        public void Rename_TrimsAndLimitsTheName()
        {
            var settings = RewardSettings.Empty;

            Assert.Equal("Танки", settings.RenameQueue(settings.FirstQueueId, "  Танки  ").Queues[0].name);
            Assert.Equal(RewardSettings.MaxQueueNameLength, settings.RenameQueue(settings.FirstQueueId, new string('я', 200)).Queues[0].name.Length);
            Assert.Equal("", settings.RenameQueue(settings.FirstQueueId, "").Queues[0].name);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(5, 5)]
        [InlineData(-3, 0)]
        [InlineData(100000, RewardSettings.MaxShowLimit)]
        public void ShowLimitAndTotal_AreClampedToTheAllowedRange(int value, int expected)
        {
            var settings = RewardSettings.Empty;

            Assert.Equal(expected, settings.SetShowLimit(settings.FirstQueueId, value).Queues[0].show_limit);
            Assert.Equal(expected, settings.WithShowTotal(value).ShowTotal);
        }

        [Fact]
        public void OtherChanges_KeepLimitsAndNames()
        {
            var settings = WithRewards("a").AddQueue();
            string second = settings.Queues[1].id;
            settings = settings.RenameQueue(second, "Музыка").SetShowLimit(second, 4).WithShowTotal(7);

            settings = settings.WithKnown(new[] { Reward("a"), Reward("b") }).WithTracked("b", true).MoveReward("a", second);

            Assert.Equal("Музыка", settings.Queues[1].name);
            Assert.Equal(4, settings.Queues[1].show_limit);
            Assert.Equal(7, settings.ShowTotal);
        }

        [Fact]
        public void FromConfig_RewardInTwoQueuesStaysInTheFirstOnly()
        {
            var settings = RewardSettings.FromConfig(null, new[]
            {
                new QueueConfig { id = "one", reward_ids = new List<string> { "a" } },
                new QueueConfig { id = "two", reward_ids = new List<string> { "a", "b" } }
            });

            Assert.Equal("one", settings.QueueFor("a"));
            Assert.Equal(new[] { "b" }, RewardsOf(settings, 1));
        }

        [Fact]
        public void ConfigRoundTrip_KeepsQueuesLimitsAndNames()
        {
            var settings = WithRewards("a", "b").AddQueue();
            string second = settings.Queues[1].id;
            settings = settings.MoveReward("b", second).RenameQueue(second, "Музыка").SetShowLimit(second, 3).WithShowTotal(8);
            var config = new PluginConfig { known_rewards = settings.Known.ToList(), queues = settings.Queues.ToList(), show_total = settings.ShowTotal };

            string json = System.Text.Json.JsonSerializer.Serialize(config, PluginConfigJsonContext.Default.PluginConfig);
            Assert.True(Plugin.TryParseConfig(json, out var loaded, out _));
            var restored = RewardSettings.FromConfig(loaded!.known_rewards, loaded.queues, loaded.show_total);

            Assert.Equal(2, restored.Queues.Count);
            Assert.Equal("Музыка", restored.Queues[1].name);
            Assert.Equal(3, restored.Queues[1].show_limit);
            Assert.Equal(8, restored.ShowTotal);
            Assert.Equal(restored.Queues[1].id, restored.QueueFor("b"));
        }
    }
}
