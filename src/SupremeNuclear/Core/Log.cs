using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Обёртка над BepInEx-логом + счётчик ошибок сессии (F11-диагностика,
    /// v0.167.1). Хук Unity считает ВСЕ ошибки/предупреждения сцены — включая
    /// игровые NRE, которые наш Log не видит; сброс — sceneLoaded (плагин).</summary>
    internal static class Log
    {
        private static ManualLogSource _source;

        /// <summary>Ошибки (Error/Exception/Assert) с момента загрузки сцены.</summary>
        internal static int SessionErrors;
        /// <summary>Предупреждения с момента загрузки сцены.</summary>
        internal static int SessionWarnings;
        /// <summary>Последние сообщения об ошибках (кольцо, кап 5) — первая строка
        /// трейса для F11; без полного стека.</summary>
        internal static readonly List<string> LastErrors = new List<string>();

        public static void Init(ManualLogSource source) => _source = source;

        /// <summary>Подписка на Unity-лог: считать ошибки/предупреждения сессии.
        /// ВНИМАНИЕ: из обработчика НЕ логировать (рекурсия).</summary>
        public static void SubscribeUnity()
        {
            Application.logMessageReceived += (condition, _, type) =>
            {
                if (type == LogType.Warning)
                    SessionWarnings++;
                else if (type == LogType.Error || type == LogType.Exception ||
                         type == LogType.Assert)
                {
                    SessionErrors++;
                    // первая строка условия (у Exception — «Type: message»), без стека
                    string first = condition ?? "";
                    int nl = first.IndexOf('\n');
                    if (nl >= 0)
                        first = first.Substring(0, nl);
                    LastErrors.Add(first);
                    if (LastErrors.Count > 5)
                        LastErrors.RemoveAt(0);
                }
            };
        }

        /// <summary>Сброс счётчиков (sceneLoaded: ошибки умершей сцены не копятся).</summary>
        internal static void ResetSessionCounters()
        {
            SessionErrors = 0;
            SessionWarnings = 0;
            LastErrors.Clear();
        }

        public static void Info(object message) => _source?.LogInfo(message);
        public static void Warn(object message) => _source?.LogWarning(message);
        public static void Error(object message) => _source?.LogError(message);
    }
}
