using Xunit;

namespace RewardQueue.Tests
{
    public class QueueStoreTests : IClassFixture<PluginFixture>
    {
        private readonly string _path;

        public QueueStoreTests(PluginFixture fixture)
        {
            _path = Path.Combine(fixture.Dir, "queue_store_" + Guid.NewGuid().ToString("N") + ".json");
        }

        [Fact]
        public void Load_MissingFileGivesEmptyQueue()
        {
            Assert.Empty(QueueStore.Load(_path));
        }

        [Fact]
        public void SaveThenLoad_KeepsEverythingIncludingCyrillic()
        {
            var entry = new QueueEntry
            {
                id = "abc",
                source = QueueEntry.SourceManual,
                queue_id = "main",
                reward_id = "r1",
                reward_title = "Заказ техники",
                user_name = "Зритель",
                user_input = "Хочу «клавиатуру» с подсветкой",
                redeemed_at = new DateTimeOffset(2026, 10, 29, 12, 30, 0, TimeSpan.FromHours(3))
            };

            QueueStore.Save(_path, new[] { entry });
            string text = File.ReadAllText(_path);
            var loaded = QueueStore.Load(_path);

            Assert.Contains("Заказ техники", text);   // кириллица без \uXXXX
            Assert.Single(loaded);
            Assert.Equal(entry.id, loaded[0].id);
            Assert.Equal(entry.reward_title, loaded[0].reward_title);
            Assert.Equal(entry.user_input, loaded[0].user_input);
            Assert.Equal(entry.source, loaded[0].source);
            Assert.Equal(entry.redeemed_at, loaded[0].redeemed_at);
        }

        [Theory]
        [InlineData("{ не json")]
        [InlineData("")]
        [InlineData("null")]
        public void Load_UnreadableFileIsKeptAsBrokenCopy(string content)
        {
            File.WriteAllText(_path, content);

            Assert.Empty(QueueStore.Load(_path));
            Assert.Equal(content, File.ReadAllText(_path + ".broken"));
        }

        [Fact]
        public void Load_SkipsNullElements()
        {
            File.WriteAllText(_path, "{ \"entries\": [ null, { \"id\": \"a\", \"reward_title\": \"X\" } ] }");

            var loaded = QueueStore.Load(_path);

            Assert.Single(loaded);
            Assert.Equal("a", loaded[0].id);
        }
    }
}
