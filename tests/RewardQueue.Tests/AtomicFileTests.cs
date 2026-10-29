using Xunit;

namespace RewardQueue.Tests
{
    public class AtomicFileTests : IClassFixture<PluginFixture>
    {
        private readonly PluginFixture _fx;

        public AtomicFileTests(PluginFixture fixture) => _fx = fixture;

        [Fact]
        public void WriteAllText_ReplacesFileAndLeavesNoTempFile()
        {
            string path = Path.Combine(_fx.Dir, "atomic.txt");
            AtomicFile.WriteAllText(path, "один");
            AtomicFile.WriteAllText(path, "два");

            Assert.Equal("два", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
    }
}
