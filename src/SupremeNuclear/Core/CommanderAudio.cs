using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Гашение эффекта Доплера в режиме командира.
    /// Стратегическая камера летает по карте на скоростях в тысячи м/с — AudioListener
    /// получает гигантскую относительную скорость, и источники с dopplerLevel&gt;0
    /// (двигатели, ракеты, аэро-звуки) «пилят» уши. Глобального выключателя в этой
    /// версии Unity нет (AudioSettings.dopplerFactor из движка удалён — в сборках игры
    /// только AudioSource.dopplerLevel), поэтому обнуляем dopplerLevel у активных
    /// источников сцены и держим нулём, пока режим активен: JetNozzle переписывает
    /// dopplerLevel каждый кадр ТОЛЬКО при значении &gt;0 (сверено по decompiled) —
    /// обнулённый не трогает; остальные писатели игры ставят его один раз при создании
    /// источника, их ловит дочист в Tick.
    /// Возврат «погашенного» доплера обратно &gt;0 детектится дочистом и логируется —
    /// это рантайм-проверка предположения об API игры (инвариант №13).
    /// Парность вход/выход — инвариант №9; страховка на смену сцены — sceneLoaded.</summary>
    internal static class CommanderAudio
    {
        private const float SweepIntervalSec = 0.5f;

        private static readonly Dictionary<AudioSource, float> _saved =
            new Dictionary<AudioSource, float>();
        private static bool _suppressed;
        private static float _nextSweepAt;
        private static bool _loggedRefight;

        /// <summary>Обнулить доплер у активных источников сцены, запомнив исходные значения.</summary>
        public static void Suppress()
        {
            if (_suppressed)
                return;
            _suppressed = true;
            _loggedRefight = false;
            Sweep();
        }

        /// <summary>Додержание нуля: новые источники появляются без событий (взлёт борта,
        /// пуск ракеты) — дочищаем раз в полсекунды. Звать из Update контроллера.</summary>
        public static void Tick()
        {
            if (!_suppressed || Time.unscaledTime < _nextSweepAt)
                return;
            Sweep();
        }

        /// <summary>Вернуть исходные значения. Безопасно звать всегда: без парного
        /// Suppress — ничего не делает. Мёртвые (Unity fake-null) источники пропускаем.</summary>
        public static void Restore()
        {
            if (!_suppressed)
                return;
            _suppressed = false;
            foreach (KeyValuePair<AudioSource, float> pair in _saved)
                if (pair.Key != null)
                    pair.Key.dopplerLevel = pair.Value;
            _saved.Clear();
        }

        private static void Sweep()
        {
            _nextSweepAt = Time.unscaledTime + SweepIntervalSec;
            PruneDead();
            int refought = 0;
            foreach (AudioSource src in Object.FindObjectsOfType<AudioSource>())
            {
                if (src == null || src.dopplerLevel <= 0f)
                    continue;
                if (_saved.ContainsKey(src))
                    refought++; // доплер вернулся после гашения — неизвестный писатель
                else
                    _saved.Add(src, src.dopplerLevel);
                src.dopplerLevel = 0f;
            }
            if (refought > 0 && !_loggedRefight)
            {
                _loggedRefight = true;
                Log.Info("CommanderAudio: " + refought +
                         " источник(ов) снова подняли доплер после гашения — глушим повторно");
            }
        }

        /// <summary>Мёртвые ключи из словаря: Unity fake-null нельзя ни восстанавливать,
        /// ни держать (инвариант №14).</summary>
        private static void PruneDead()
        {
            List<AudioSource> dead = null;
            foreach (AudioSource key in _saved.Keys)
                if (key == null)
                    (dead ??= new List<AudioSource>()).Add(key);
            if (dead != null)
                foreach (AudioSource key in dead)
                    _saved.Remove(key);
        }
    }
}
