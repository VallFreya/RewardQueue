using Xunit;

namespace RewardQueue.Tests
{
    public class RewardSettingsTests
    {
        private static RewardInfo Reward(string id, string title) => new RewardInfo { id = id, title = title };

        [Fact]
        public void Empty_HasOneMainQueueAndTracksNothing()
        {
            var settings = RewardSettings.Empty;

            Assert.Single(settings.Queues);
            Assert.Equal(QueueState.MainQueueId, settings.Queues[0].id);
            Assert.Empty(settings.Known);
            Assert.Null(settings.QueueFor("any"));
            Assert.False(settings.IsTracked("any"));
        }

        [Fact]
        public void WithTracked_TurnsTrackingOnAndOffById()
        {
            var settings = RewardSettings.Empty.WithKnown(new[] { Reward("a", "Заказ техники"), Reward("b", "Заказ музыки") });

            settings = settings.WithTracked("a", true);
            Assert.Equal(QueueState.MainQueueId, settings.QueueFor("a"));
            Assert.Null(settings.QueueFor("b"));
            Assert.Equal(1, settings.TrackedKnownCount);

            settings = settings.WithTracked("a", false);
            Assert.False(settings.IsTracked("a"));
        }

        [Fact]
        public void WithTracked_TwiceDoesNotDuplicateId()
        {
            var settings = RewardSettings.Empty.WithTracked("a", true).WithTracked("a", true);

            Assert.Single(settings.Queues[0].reward_ids);
        }

        [Fact]
        public void WithTracked_ReturnsNewSnapshotAndLeavesOldUntouched()
        {
            var before = RewardSettings.Empty;

            var after = before.WithTracked("a", true);

            Assert.False(before.IsTracked("a"));
            Assert.True(after.IsTracked("a"));
        }

        [Fact]
        public void WithKnown_KeepsTrackingOfRenamedReward()
        {
            var settings = RewardSettings.Empty.WithKnown(new[] { Reward("a", "Старое имя") }).WithTracked("a", true);

            settings = settings.WithKnown(new[] { Reward("a", "Новое имя") });

            Assert.True(settings.IsTracked("a"));
            Assert.Equal("Новое имя", settings.Known.Single().title);
        }

        [Fact]
        public void WithKnown_RewardRemovedFromTwitchDisappearsFromListButKeepsMark()
        {
            var settings = RewardSettings.Empty.WithKnown(new[] { Reward("a", "A"), Reward("b", "B") }).WithTracked("a", true);

            settings = settings.WithKnown(new[] { Reward("b", "B") });

            Assert.Equal(new[] { "b" }, settings.Known.Select(r => r.id).ToArray());
            Assert.True(settings.IsTracked("a"));
        }

        [Fact]
        public void FromConfig_DropsGarbageAndDuplicates()
        {
            var settings = RewardSettings.FromConfig(
                new RewardInfo?[] { null, Reward("", "без id"), Reward(" a ", " A "), Reward("a", "дубль") },
                new QueueConfig?[]
                {
                    null,
                    new QueueConfig { id = "", reward_ids = new List<string> { "x" } },
                    new QueueConfig { id = "main", name = " Основная ", reward_ids = new List<string> { "a", " a ", "", "b" } },
                    new QueueConfig { id = "main", reward_ids = new List<string> { "z" } }
                });

            Assert.Equal(new[] { "a" }, settings.Known.Select(r => r.id).ToArray());
            Assert.Equal("A", settings.Known[0].title);
            Assert.Single(settings.Queues);
            Assert.Equal("Основная", settings.Queues[0].name);
            Assert.Equal(new[] { "a", "b" }, settings.Queues[0].reward_ids.ToArray());
        }

        [Fact]
        public void QueueFor_FindsTheQueueThatTracksTheReward()
        {
            var settings = RewardSettings.FromConfig(null, new[]
            {
                new QueueConfig { id = "first", reward_ids = new List<string> { "a" } },
                new QueueConfig { id = "second", reward_ids = new List<string> { "b" } }
            });

            Assert.Equal("first", settings.QueueFor("a"));
            Assert.Equal("second", settings.QueueFor("b"));
            Assert.Null(settings.QueueFor("c"));
        }

        [Fact]
        public void WithTracked_NewRewardGoesToTheFirstQueue_AlreadyTrackedStaysWhereItIs()
        {
            var settings = RewardSettings.FromConfig(null, new[]
            {
                new QueueConfig { id = "first" },
                new QueueConfig { id = "second", reward_ids = new List<string> { "a" } }
            });

            settings = settings.WithTracked("a", true).WithTracked("b", true);

            Assert.Equal("second", settings.QueueFor("a"));
            Assert.Equal("first", settings.QueueFor("b"));
        }

        [Fact]
        public void ConfigRoundTrip_ThroughJson()
        {
            var settings = RewardSettings.Empty.WithKnown(new[] { Reward("a", "Заказ техники") }).WithTracked("a", true);
            var config = new PluginConfig { known_rewards = settings.Known.ToList(), queues = settings.Queues.ToList() };

            string json = System.Text.Json.JsonSerializer.Serialize(config, PluginConfigJsonContext.Default.PluginConfig);
            Assert.True(Plugin.TryParseConfig(json, out var loaded, out _));
            var restored = RewardSettings.FromConfig(loaded!.known_rewards, loaded.queues);

            Assert.True(restored.IsTracked("a"));
            Assert.Equal("Заказ техники", restored.Known.Single().title);
        }
    }
}
