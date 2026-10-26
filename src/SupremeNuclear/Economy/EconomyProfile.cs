using System.Collections.Generic;
using System.IO;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Внешний балансный профиль экономики: все числа здесь, не в коде.
    /// Файл: BepInEx/plugins/SupremeNuclear/Economy/&lt;Profile&gt;.json.
    /// deposits (опционально) — ручная разметка месторождений на карту; без неё — авторазметка.
    /// Формат сознательно совпадает с будущим выходом генератора карт.
    /// </summary>
    public sealed class EconomyProfile
    {
        public sealed class Deposit
        {
            public float x;
            public float z;
            public int tier = 1;
        }

        public List<Deposit> deposits;
        public int depositCount = 12;
        public float depositSpacing = 4000f;
        public float tickSeconds = 5f;
        public float[] incomePerMinute = { 150f, 400f, 1000f }; // T1/T2/T3
        public float[] tierUpgradeCost = { 0f, 800f, 2400f };   // тиры в данных с первого дня, UI — позже

        public static EconomyProfile Load(string name)
        {
            string dir = Path.Combine(Paths.PluginPath, "SupremeNuclear", "Economy");
            string path = Path.Combine(dir, name + ".json");
            try
            {
                if (File.Exists(path))
                {
                    var profile = JsonConvert.DeserializeObject<EconomyProfile>(File.ReadAllText(path));
                    if (profile != null)
                    {
                        Log.Info("Economy: профиль загружен — " + path);
                        return profile;
                    }
                }

                var fallback = new EconomyProfile();
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonConvert.SerializeObject(fallback, Formatting.Indented));
                Log.Info("Economy: профиль не найден, создан образец с дефолтами — " + path);
                return fallback;
            }
            catch (System.Exception e)
            {
                Log.Error("Economy: ошибка профиля " + path + ": " + e.Message + " — использую дефолты");
                return new EconomyProfile();
            }
        }

        public float Rate(int tier) =>
            incomePerMinute != null && incomePerMinute.Length > 0
                ? incomePerMinute[Mathf.Clamp(tier, 1, incomePerMinute.Length) - 1]
                : 150f;
    }
}
