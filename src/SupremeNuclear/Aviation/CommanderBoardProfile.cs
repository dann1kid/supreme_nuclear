using System.IO;
using BepInEx;
using Newtonsoft.Json;

namespace SupremeNuclear
{
    /// <summary>
    /// Баланс командирского борта (DESIGN §2.1 «летающий ACU», этап 8 §4):
    /// все числа здесь, не в коде (инвариант §7.11). Файл:
    /// BepInEx/plugins/SupremeNuclear/Aviation/board.json; при отсутствии
    /// создаётся образец с дефолтами.
    /// </summary>
    public sealed class CommanderBoardProfile
    {
        public bool enabled = true;        // борт спавнится сценарием и обслуживается сервисом

        // Детонация при смерти (нативный DamageEffects.BlastFrag; радиус = yield^(1/3)·20 м).
        public float blastYield = 8000f;   // ≈ 400 м радиуса — выжигает «гнилую» победу раша

        // EMCON (радиомолчание): ЭПР множится, радар гаснет, накопленные контакты
        // врага стираются. Штраф — борт слеп (нет своей разведки).
        public float emconRcsFactor = 0.3f;

        // Живучесть: HP деталей × множитель (SupCom-ACU — «трудно сбить»: одна
        // Fox-2 не должна решать матч; плейтест 2026-10-31 — борт гибнет с одного
        // попадания). Доля HP сервиса/ремонта считается от УСИЛЕННОГО максимума.
        public float partHpMultiplier = 8f;

        // Орбита над своей базой (правильный n-угольник, высота AGL).
        public float orbitRadiusM = 2500f;
        public float orbitAltM = 1500f;
        public int orbitPoints = 4;

        // Спавн в воздухе над базой (сценарий).
        public float spawnSpeed = 180f;    // начальная скорость, м/с — не падать с нулевой

        // Авто-возврат на ремонт: ниже доли HP борт сам идёт на свою базу.
        public float autoRtbHpFraction = 0.35f;

        // Ремонт на земле у своей базы («окно уязвимости» — борт стоит).
        public float repairHpPerSec = 3f;  // HP деталей в секунду (деталь полная = 100)
        public float repairMassPerSec = 4f;
        public float repairRadiusM = 400f; // «у базы» — дистанция до центра аэродрома

        public static CommanderBoardProfile Load()
        {
            string dir = Path.Combine(Paths.PluginPath, "SupremeNuclear", "Aviation");
            string path = Path.Combine(dir, "board.json");
            try
            {
                if (File.Exists(path))
                {
                    var profile = JsonConvert.DeserializeObject<CommanderBoardProfile>(File.ReadAllText(path));
                    if (profile != null)
                    {
                        Log.Info("Board: профиль загружен — " + path);
                        return profile;
                    }
                }

                var fallback = new CommanderBoardProfile();
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonConvert.SerializeObject(fallback, Formatting.Indented));
                Log.Info("Board: профиль не найден, создан образец с дефолтами — " + path);
                return fallback;
            }
            catch (System.Exception e)
            {
                Log.Warn("Board: профиль не прочитан (" + e.Message + ") — работаю на дефолтах");
                return new CommanderBoardProfile();
            }
        }
    }
}
