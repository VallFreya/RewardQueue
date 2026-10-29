using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace RewardQueue.Tests
{
    /// <summary>
    /// Общая подготовка: журнал плагина выключен, конфиг пишется во временную папку.
    /// Статическое состояние плагина общее, поэтому проверки не идут параллельно.
    /// </summary>
    public sealed class PluginFixture : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "RewardQueueTests", Guid.NewGuid().ToString("N"));
        public string ConfigPath => Path.Combine(Dir, "config.json");

        public PluginFixture()
        {
            Directory.CreateDirectory(Dir);
            Plugin.LogEnabled = false;
            Plugin.ConfigPathOverride = ConfigPath;
        }

        public void Dispose()
        {
            Plugin.ConfigPathOverride = null;
            Plugin.LogEnabled = true;
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }
}
