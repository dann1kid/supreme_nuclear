using System;

namespace SupremeNuclear
{
    /// <summary>
    /// Чистое ядро потоковой экономики (DESIGN §2.2): без Unity-вызовов,
    /// тестируется консольным раннером. Масса живёт в нативных factionFunds,
    /// энергия — в стоке EconomyService; здесь только арифметика налива и стока.
    /// </summary>
    public static class EcoFlow
    {
        /// <summary>Фактически налитые масса/энергия за шаг.</summary>
        public readonly struct Paid
        {
            public readonly float Mass;
            public readonly float Energy;

            public Paid(float mass, float energy)
            {
                Mass = mass;
                Energy = energy;
            }
        }

        /// <summary>
        /// Шаг налива задачи: хочет массу/энергию, доступно massAvail/energyAvail.
        /// Дефицит любой валюты пропорционально режет ОБЕ (SupCom-семантика:
        /// задача едет со скоростью min(1, доступно/требуется)), задача не может
        /// «выпить» больше, чем есть, и не наливает массу вперёд при простое энергии.
        /// </summary>
        public static Paid DrainStep(float massWant, float energyWant, float massAvail, float energyAvail)
        {
            float eff = 1f;
            if (massWant > 0f && massAvail < massWant)
                eff = massAvail / massWant;
            if (energyWant > 0f && energyAvail < energyWant)
                eff = Math.Min(eff, energyAvail / energyWant);
            eff = Clamp01(eff);
            return new Paid(massWant * eff, energyWant * eff);
        }

        /// <summary>Пополнение стока с капом: перелив копится до capacity, не дальше.</summary>
        public static float AddCapped(float store, float income, float capacity)
        {
            float v = store + Math.Max(0f, income);
            float cap = Math.Max(0f, capacity);
            return v > cap ? cap : v;
        }

        /// <summary>
        /// Цена сервиса вылета с наценкой за ремонт (DESIGN §3-10):
        /// базовая цена + доля урона от ценности борта.
        /// </summary>
        public static float ServiceCost(float baseCost, float hp01, float unitValue, float damageFactor)
        {
            hp01 = Clamp01(hp01);
            return Math.Max(0f, baseCost) + (1f - hp01) * Math.Max(0f, unitValue) * Math.Max(0f, damageFactor);
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
