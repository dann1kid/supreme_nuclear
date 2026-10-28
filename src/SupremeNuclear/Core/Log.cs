using BepInEx.Logging;

namespace SupremeNuclear
{
    internal static class Log
    {
        private static ManualLogSource _source;

        public static void Init(ManualLogSource source) => _source = source;

        public static void Info(object message) => _source?.LogInfo(message);
        public static void Warn(object message) => _source?.LogWarning(message);
        public static void Error(object message) => _source?.LogError(message);
    }
}
