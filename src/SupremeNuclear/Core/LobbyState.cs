using System.Collections.Generic;
using Newtonsoft.Json;

namespace SupremeNuclear
{
    /// <summary>
    /// Состояние SN-лобби v3 «как в SupCom» (единое для сингла и MP): армии-слоты
    /// с управляющим (закрыто/открыто/ИИ/человек), командами-союзами и позициями
    /// старта; пул наблюдателей = члены без слота. Условие победы: захват всех
    /// чужих баз / уничтожение / бесконечная (соло — одна живая армия — всегда
    /// бесконечная). Модель полей — по структуре лобби Supreme Commander
    /// (армии: контроллер/команда/позиция), без чужого кода. Чистая логика —
    /// тесты. Синк: JSON в SnLobbyMessage + Steam-данные (DataKey).
    /// </summary>
    internal sealed class LobbyState
    {
        public const string DataKey = "sn_lobby";
        public const int Format = 3;

        public const string StatusWaiting = "waiting";
        public const string StatusStarting = "starting";
        public const string StatusRunning = "running";

        // Условия победы.
        public const string VictoryCapture = "capture";     // захват всех баз чужих команд
        public const string VictoryAnnihilate = "annihilate"; // уничтожение всех армий чужих команд
        public const string VictoryEndless = "endless";     // без условий (песочница)

        /// <summary>Максимум слотов = максимум пула позиций карт (players 8 → 8 баз).
        /// СЛОТ = ПОЗИЦИЯ (канон SupCom, юзер 2026-11-06: «слотов столько, сколько
        /// доступно для карты»): строк стола — по пулу выбранной карты.</summary>
        public const int MaxArmies = 8;

        public int v = Format;
        public string status = StatusWaiting;
        public string victory = VictoryCapture;
        public int hostId = -1;

        /// <summary>Выбранный пул позиций (слоты стола). Существует ДО выбора
        /// карты: хост крутит «Стороны» — слоты перестраиваются сразу; карта
        /// (map.players) обязана совпасть к СТАРТу.</summary>
        public int pool = 2;

        /// <summary>Выбранная карта (null — ещё не сгенерирована).</summary>
        public MapInfo map;

        /// <summary>Армии-слоты — ПО ПУЛУ КАРТЫ (armies.Count == PoolSize; слот =
        /// позиция, army.id == army.pos). Без field-initializer: Newtonsoft
        /// ДОПИСЫВАЕТ в инициализированный список — создатель ставит
        /// ArmiesForPool(pool) явно.</summary>
        public List<Army> armies;

        /// <summary>Члены комнаты (все подключённые; сторона выражается слотом армии).</summary>
        public List<Member> members = new List<Member>();

        public sealed class MapInfo
        {
            public string template;
            public int seed;
            public int players;   // пул позиций: 2×(players/2) баз
            public string saveName;
        }

        /// <summary>Контроллеры слота.</summary>
        public const string CtrlClosed = "closed";
        public const string CtrlOpen = "open";
        public const string CtrlAi = "ai";
        public const string CtrlHuman = "human";

        /// <summary>Армия-слот: контроллер, команда (союз), позиция старта (индекс
        /// в пуле баз), цвет-индекс палитры. humanId — PlayerIndex (стабилен).</summary>
        public sealed class Army
        {
            public int id;
            public string name;
            public int team = 1;
            public int colorIdx;
            public int pos;                          // 0..players-1 (пул баз)
            public string controller = CtrlOpen;     // closed|open|ai|human
            public int humanId = -1;
            public string aiType = "commander";      // типов ИИ пока один (на будущее)
        }

        public sealed class Member
        {
            public int id;
            public string name;
            public bool ready;
        }

        public string ToJson() => JsonConvert.SerializeObject(this);

        public static LobbyState FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            try
            {
                var s = JsonConvert.DeserializeObject<LobbyState>(json);
                return s != null && s.v == Format ? s : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Слоты под пул карты: N строк, слот = позиция (id==pos).
        /// Дефолт по канону SupCom: слот 1 — хост (human), ВСЕ остальные —
        /// открытые; ИИ ставится хостом вручную (плейтест 2026-11-06: AI по
        /// умолчанию съедал единственный второй слот — «доступных слотов нет»).
        /// Команды чередованием 1/2 (Dual Gap-раскладка).</summary>
        public static List<Army> ArmiesForPool(int players)
        {
            int pool = ClampPlayers(players);
            var list = new List<Army>(pool);
            for (int i = 0; i < pool; i++)
                list.Add(new Army
                {
                    id = i,
                    name = "Слот " + (i + 1),
                    team = i % 2 + 1,
                    colorIdx = i,
                    pos = i,
                    controller = i == 0 ? CtrlHuman : CtrlOpen,
                    humanId = -1,
                });
            return list;
        }

        // ---------- запросы (чистая логика, тесты) ----------

        /// <summary>Число живых армий (не closed).</summary>
        public static int LiveArmies(List<Army> armies)
        {
            int n = 0;
            foreach (Army a in armies)
                if (a != null && a.controller != CtrlClosed)
                    n++;
            return n;
        }

        /// <summary>Действующее условие победы: соло (одна живая армия) — всегда
        /// бесконечная (юзер-канон: без противника при уничтожении — бесконечно).</summary>
        public static string EffectiveVictory(LobbyState s) =>
            LiveArmies(s.armies) < 2 ? VictoryEndless : s.victory;

        /// <summary>Валидация к СТАРТУ: null/пусто = ок, иначе текст отказа.</summary>
        public static string ValidateForLaunch(LobbyState s)
        {
            if (s == null)
                return "нет состояния";
            if (s.map == null)
                return "карта не выбрана";
            int live = LiveArmies(s.armies);
            if (live < 1)
                return "нет живых армий";
            if (live > MaxArmies)
                return "армий больше максимума (" + MaxArmies + ")";
            var humans = new HashSet<int>();
            int pool = PoolSize(s);
            if (s.armies.Count != pool)
                return "слотов " + s.armies.Count + ", а пул карты " + pool;
            foreach (Army a in s.armies)
            {
                if (a.controller == CtrlClosed)
                    continue;
                if (a.pos != a.id || a.id < 0 || a.id >= pool)
                    return "слот '" + a.name + "' вне пула";
                if (a.controller == CtrlHuman)
                {
                    if (a.humanId < 0)
                        return "слот '" + a.name + "' без игрока";
                    if (!humans.Add(a.humanId))
                        return "игрок в двух слотах";
                }
            }
            return null;
        }

        /// <summary>Число игроков карты (пул позиций 2×(players/2)): чётные 2..8 —
        /// валидация перед уходом в ключ карты (MapGenKey «-pN»).</summary>
        public static int ClampPlayers(int players)
        {
            if (players <= 2) return 2;
            if (players >= 8) return 8;
            return players % 2 == 0 ? players : players - 1;
        }

        /// <summary>Пул позиций = выбранный размер стола (слоты существуют до карты).</summary>
        public static int PoolSize(LobbyState s) => s.pool < 2 ? 2 : s.pool; // чистая логика, без Mathf

        /// <summary>Союзники ли армии (одна команда; closed — никто).</summary>
        public static bool Allied(Army a, Army b) =>
            a != null && b != null && a != b && a.controller != CtrlClosed &&
            b.controller != CtrlClosed && a.team == b.team;

        /// <summary>Найти армию игрока (humanId).</summary>
        public static Army ArmyOf(LobbyState s, int humanId)
        {
            if (s == null)
                return null;
            foreach (Army a in s.armies)
                if (a.controller == CtrlHuman && a.humanId == humanId)
                    return a;
            return null;
        }

        /// <summary>Члены без слота — пул наблюдателей.</summary>
        public static List<Member> Observers(LobbyState s)
        {
            var result = new List<Member>();
            if (s == null)
                return result;
            foreach (Member m in s.members)
            {
                bool inSlot = false;
                foreach (Army a in s.armies)
                    if (a.controller == CtrlHuman && a.humanId == m.id)
                    {
                        inSlot = true;
                        break;
                    }
                if (!inSlot && m.id != s.hostId)
                    result.Add(m);
            }
            return result;
        }
    }

    /// <summary>Запрос гостя хосту (гость → хост, JSON в SnLobbyMessage):
    /// занять открытый слот / покинуть слот / готовность.</summary>
    internal sealed class LobbyRequest
    {
        public const string TakeSlot = "take_slot";
        public const string LeaveSlot = "leave_slot";
        public const string Ready = "ready";

        public string req;
        public int armyId = -1;
        public bool on;

        public string ToJson() => JsonConvert.SerializeObject(this);

        public static LobbyRequest FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            try
            {
                return JsonConvert.DeserializeObject<LobbyRequest>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
