using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Ручной ракетный залп флота (SupCom-канон, DESIGN §2.11): стойка
    /// hold-fire запрещает кораблям пускать самим, а заряженный режим «Залп» (V)
    /// по клику ЛКМ кладёт ВОЛНУ В ОЧЕРЕДЬ (как пуски SupCom перечисляются
    /// очередью): по одной ракете с каждого выделенного корабля, каждый пускает
    /// КОГДА ТРУБА ГОТОВА (не готов — ждёт в волне), серия кликов = серия волн с
    /// дозированием расхода. Отметка волны (крест + линии «корабль → точка»)
    /// живёт до выстрела (по цели — следует за целью), после — угасает.
    /// Пуск по ЦЕЛИ — наведение на юнит; пуск по ТОЧКЕ (target=null) — ракета
    /// переприцеливается в координату отложенно (Missile.SetAimpoint: Steering
    /// ведёт по aimPoint, спавн в реестре опаздывает на кадр — LocalStart успеет
    /// поставить «100 км по курсу», наш тик после него). Класс-гейты и правила
    /// типов — закон и для ручного пуска (обходится только стойка:
    /// WeaponGateService.ManualOrder). Хост-only (§2): WeaponStation.Fire —
    /// нативный путь корабельных пусков (FireControl): пристёгнутые снаряды,
    /// магазин пополняется циклом перезарядки, логистика перевооружения — нативно.</summary>
    internal static class FleetSalvo
    {
        /// <summary>Ограничение волн ТИПОМ вооружения (ПКМ по чипу типа на панели:
        /// «пускать только этим» — ПВО прибережём для других целей); null — все
        /// подходящие. Сбрасывается каждым новым зарядом режима и sceneLoaded.</summary>
        internal static WeaponInfo Scope;

        // ---------- очередь волн (SupCom-перечисление пусков) ----------

        /// <summary>Волна в очереди: участники, ещё не пустившие (пустят когда
        /// труба готова), точка/цель и крайний срок жизни волны.</summary>
        internal struct QueuedWave
        {
            internal Unit Target;        // null = точка
            internal GlobalPosition Aim; // точка удара (для цели — на момент клика)
            internal List<Unit> Ships;   // ждут своей трубы
            internal float Deadline;
            internal int MarkId;         // отметка волны в Marks (крест до выстрела)
        }
        internal static readonly List<QueuedWave> Queue = new List<QueuedWave>();
        // Потолок очереди: в SupCom очередь пусков копится свободно; у нас каждая
        // волна тикает каждый кадр, 16 — с запасом (плейтест: 8 молча теряло волны
        // при «закликивании», лента пропадала).
        private const int QueueCap = 16;
        // Волна — СТОЯЩИЙ приказ обстрела точки (канон SupCom: подлодка подплыла,
        // обстреляла, ушла — точки никуда не денутся): живёт 10 минут, а не пока
        // «не надоело» (плейтест v0.124: медленные трубы не успевали за 180 с).
        private const float WaveTtlSec = 600f;

        /// <summary>Отметка волны для HUD (крест в точке + линии от участников).
        /// У угасающих — Until = now+MarkTtlSec; у ждущих в очереди — обновляется
        /// каждый тик (живут до выстрела).</summary>
        internal struct SalvoMark
        {
            internal int Id;
            internal GlobalPosition Point;
            internal float Until;
            internal readonly List<Unit> Ships;
            internal SalvoMark(int id, GlobalPosition point, float until, List<Unit> ships)
            {
                Id = id;
                Point = point;
                Until = until;
                Ships = ships;
            }
        }
        internal static readonly List<SalvoMark> Marks = new List<SalvoMark>();
        // Отметка-«вейпоинт» живет ПОКА ЛЕТЯТ РАКЕТЫ (SupCom: точки не исчезают
        // сразу — они указывают, куда стрелять; крылатая за 40+ км летит минуты):
        // угасает 3 минуты после отстрела волны, последние 10 секунд — фейд.
        // internal: HUD по Until−MarkTtlSec узнаёт «когда волна отстрелялась» —
        // голова цепи очереди (недавно отстрелянная точка, ракета ещё в полёте).
        internal const float MarkTtlSec = 180f;
        // Снятая волна (движение без Shift / Ctrl+Shift+ПКМ / таймаут труб) гаснет
        // БЫСТРО: ракета не летит, держать крест MarkTtlSec — игрок видит «снятия
        // не произошло» (жалоба 2026-11-06: бифуркация «снято/отстреляно» не
        // читалась). Отстрелянная живёт MarkTtlSec — пока ракета в полёте.
        internal const float CancelFadeSec = 3f;
        private const int MarkCap = 12;
        private static int _markIdSeq;

        // ---------- отложенное перенацеливание точечных пусков ----------

        /// <summary>Новорождённая ракета ищется в реестре ДО крайнего срока:
        /// спавн может опаздывать на кадр (плейтест v0.121 — перенацел в пустоту,
        /// ракета уходила по курсу корабля). Known — ракеты владельца ДО пуска.</summary>
        private struct PendingAim
        {
            internal PersistentID Owner;
            internal GlobalPosition Aim;
            internal HashSet<PersistentID> Known;
            internal float Until;
            internal int MarkId; // волна пуска: драг её точки обновит и этот прицел
        }
        private static readonly List<PendingAim> PendingAims = new List<PendingAim>();
        private static readonly HashSet<Missile> Aimed = new HashSet<Missile>(); // уже перенаправленные
        private static readonly HashSet<PersistentID> MissileSnapIds = new HashSet<PersistentID>();

        // ЛЕТЯЩИЕ ракеты волны по MarkId: драг точки волны перенацеливает и их
        // (жалоба 2026-11-06: «стрельба идёт по старым позициям» — выпущенные
        // готовыми трубами ракеты летели в точку на момент КЛИКА, пока игрок
        // тащил крест). Не отзывается только СНЯТАЯ волна — перевыдача прицела
        // отзываться обязана. Чистка — Tick (мёртвые/протухшие ключи).
        internal static readonly Dictionary<int, List<Missile>> WaveMissiles =
            new Dictionary<int, List<Missile>>();
        private static float _nextWaveMissilesPrune;
        private static readonly List<int> _pruneKeys = new List<int>(); // буфер чистки

        // ПРЕДОХРАНИТЕЛЬ РИТМА (плейтест v0.123: зависание): корабельная VLS-оружка
        // МНОГОЗАРЯДНА (одна Weapon = магазин: Fire() пускает ракету и остаётся на
        // пусковой), нативный ритм ей держит FireControl таймерами — наш тик без
        // кулдауна звал Fire() каждый кадр = пулемёт крылатых. После пуска корабль
        // отдыхает cooldown (fireInterval станции, минимум ManualCooldownSec),
        // за тик — не более двух пусков на всю очередь.
        private const float ManualCooldownSec = 5f;
        private const int LaunchesPerTick = 2;
        private static readonly Dictionary<Unit, float> Cooldowns = new Dictionary<Unit, float>();
        private static float _nextRhythmLog;

        // Внедрение точки в ГСН ракеты: Seek() ГСН без цели КАЖДЫЙ физ-кадр пишет
        // SetAimpoint(knownPos + knownVel·dt), перебивая наш прицел (вот почему
        // точечные пуски уходили «в небо» даже после SetAimpoint). Лечим как натив
        // лечит потерю захвата: knownPos = точка удара, knownVel = 0 — ГСН сама
        // ведёт ракету в точку. seeker приватен у Missile, knownPos/knownVel — у
        // каждого типа ГСН своё: рефлексия по имени, кэш по типу.
        private static System.Reflection.FieldInfo _seekerField;
        private static readonly Dictionary<System.Type, System.Reflection.FieldInfo> KnownPosFields =
            new Dictionary<System.Type, System.Reflection.FieldInfo>();
        private static readonly Dictionary<System.Type, System.Reflection.FieldInfo> KnownVelFields =
            new Dictionary<System.Type, System.Reflection.FieldInfo>();

        /// <summary>Внедрение точки удара в ГСН ракеты (shared: ЯО-пуски
        /// WarheadService ведут ракету в точку тем же приёмом).</summary>
        internal static void AimMissileAt(Missile m, GlobalPosition aim) =>
            AimMissileAtPoint(m, aim);

        private static void AimMissileAtPoint(Missile m, GlobalPosition aim)
        {
            m.SetAimpoint(aim, Vector3.zero);
            if (_seekerField == null)
                _seekerField = HarmonyLib.AccessTools.Field(typeof(Missile), "seeker");
            object seeker = _seekerField?.GetValue(m);
            if (seeker == null)
                return; // ГСН нет — прицел останется как есть
            System.Type t = seeker.GetType();
            if (!KnownPosFields.TryGetValue(t, out System.Reflection.FieldInfo posF))
            {
                posF = HarmonyLib.AccessTools.Field(t, "knownPos");
                KnownPosFields[t] = posF;
            }
            if (!KnownVelFields.TryGetValue(t, out System.Reflection.FieldInfo velF))
            {
                velF = HarmonyLib.AccessTools.Field(t, "knownVel");
                KnownVelFields[t] = velF;
            }
            posF?.SetValue(seeker, aim);
            velF?.SetValue(seeker, Vector3.zero);
        }

        private static readonly List<Unit> FiredBuf = new List<Unit>();

        /// <summary>Сброс по sceneLoaded (правило §4: статическое состояние — сбрасывать).</summary>
        public static void Reset()
        {
            Scope = null;
            Queue.Clear();
            Marks.Clear();
            PendingAims.Clear();
            Aimed.Clear();
            FiredBuf.Clear();
            Cooldowns.Clear();
            WaveMissiles.Clear();
            _nextWaveMissilesPrune = 0f;
        }

        /// <summary>Тик (CommanderHud.Update): волны очереди пускают по готовности
        /// труб, отметки ждущих живут, отложенные перенацеливания доводятся.</summary>
        public static void Tick()
        {
            if (Queue.Count == 0 && PendingAims.Count == 0 && WaveMissiles.Count == 0)
                return;
            float now = Time.unscaledTime;

            // чистка летящих ракет волн (раз в секунду): Unity fake-null +
            // disabled (долетела/сбита), ключ без живых ракет, без волны в
            // очереди и без живой отметки — удалить (реестр не должен течь)
            if (WaveMissiles.Count > 0 && now >= _nextWaveMissilesPrune)
            {
                _nextWaveMissilesPrune = now + 1f;
                _pruneKeys.Clear();
                foreach (KeyValuePair<int, List<Missile>> kv in WaveMissiles)
                {
                    List<Missile> list = kv.Value;
                    for (int mi = list.Count - 1; mi >= 0; mi--)
                        if (list[mi] == null || list[mi].disabled)
                            list.RemoveAt(mi);
                    if (list.Count == 0)
                        _pruneKeys.Add(kv.Key);
                }
                for (int ki = 0; ki < _pruneKeys.Count; ki++)
                {
                    bool alive = false;
                    int markId = _pruneKeys[ki];
                    for (int qi = 0; qi < Queue.Count && !alive; qi++)
                        alive = Queue[qi].MarkId == markId;
                    for (int mi = 0; mi < Marks.Count && !alive; mi++)
                        alive = Marks[mi].Id == markId && now < Marks[mi].Until;
                    if (!alive)
                        WaveMissiles.Remove(markId);
                }
            }

            // отложенные перенацеливания: новорождённая ракета владельца, которой
            // не было в снимке на момент пуска. РАЗБОР СТАРЕЙШИЙ-ПЕРВЫМ (FIFO):
            // обратный порядок отдавал первую новорождённую ПОСЛЕДНЕМУ клику —
            // серия быстрых кликов по точкам уводила ракеты в последнюю точку
            // (снимки Known пустые, спавн отложен — плейтест v0.123).
            for (int i = 0; i < PendingAims.Count; i++)
            {
                PendingAim e = PendingAims[i];
                if (now >= e.Until)
                {
                    PendingAims.RemoveAt(i);
                    i--;
                    continue;
                }
                foreach (Unit u in UnitRegistry.allUnits)
                    if (u is Missile m && m.ownerID == e.Owner && !e.Known.Contains(m.persistentID) &&
                        !Aimed.Contains(m))
                    {
                        AimMissileAtPoint(m, e.Aim);
                        Aimed.Add(m);
                        // летящая ракета волны: драг точки волны перенацелит её
                        if (e.MarkId != 0)
                        {
                            if (!WaveMissiles.TryGetValue(e.MarkId, out List<Missile> flying))
                                WaveMissiles[e.MarkId] = flying = new List<Missile>();
                            flying.Add(m);
                        }
                        PendingAims.RemoveAt(i);
                        i--;
                        break;
                    }
            }
            if (Aimed.Count > 128)
                Aimed.RemoveWhere(m => m == null); // Unity fake-null (правило 14)

            // очередь волн: каждый корабль пускает когда труба готова; цель умерла
            // или срок вышел — остаток волны снимается, отметка угасает.
            // БЮДЖЕТ пусков на тик + кулдаун корабля — предохранитель ритма (v0.124:
            // без него Fire() каждый кадр = пулемёт крылатых, зависание).
            int budget = LaunchesPerTick;
            for (int qi = Queue.Count - 1; qi >= 0 && budget > 0; qi--)
            {
                QueuedWave wave = Queue[qi];
                bool dead = wave.Target != null && (wave.Target == null || wave.Target.disabled) ||
                            now >= wave.Deadline;
                if (!dead)
                {
                    for (int si = wave.Ships.Count - 1; si >= 0 && budget > 0; si--)
                    {
                        Unit ship = wave.Ships[si];
                        GlobalPosition aim = wave.Target != null ? wave.Target.GlobalPosition() : wave.Aim;
                        bool launched;
                        try
                        {
                            launched = TryLaunchOne(ship, wave.Target, aim, wave.MarkId);
                        }
                        catch (System.Exception e)
                        {
                            // больная станция не должна крутить тик в исключениях —
                            // снимаем корабль с волны и говорим один раз
                            launched = true;
                            if (Time.unscaledTime >= _nextRhythmLog)
                            {
                                _nextRhythmLog = Time.unscaledTime + 5f;
                                Log.Warn("FleetSalvo: пуск не удался — " + e.GetType().Name + ": " + e.Message);
                            }
                        }
                        if (launched)
                        {
                            wave.Ships.RemoveAt(si);
                            if (budget-- > 0)
                                Log.Info($"FleetSalvo: волна #{wave.MarkId} — пуск с борта по готовности трубы");
                        }
                    }
                    if (wave.Ships.Count > 0)
                    {
                        // ждём трубы: отметка живёт (без угасания)
                        TouchMark(wave.MarkId, now + 5f);
                        Queue[qi] = wave;
                        continue;
                    }
                }
                // волна отстрелялась (или снята) — отметка угасает обычным порядком
                if (now >= wave.Deadline && wave.Ships.Count > 0)
                {
                    Log.Warn($"FleetSalvo: волна #{wave.MarkId} снята по таймауту {WaveTtlSec:F0} с — " +
                             $"трубы так и не стали готовы ({wave.Ships.Count} бортов)");
                    TouchMark(wave.MarkId, now + CancelFadeSec); // не стрелявшая — быстрый фейд
                }
                else
                    TouchMark(wave.MarkId, now + MarkTtlSec); // отстрелялась — ракета летит
                Queue.RemoveAt(qi);
            }
        }

        /// <summary>Клик «Залпа»: волна в очередь — готовые трубы пускают сразу,
        /// неготовые ждут в волне (SupCom-перечисление пусков). Возвращает число
        /// ракет, ушедших немедленно.</summary>
        public static int LaunchWave(List<Unit> ships, Unit target, GlobalPosition aimpoint)
        {
            var net = NuclearOption.Networking.NetworkManagerNuclearOption.i;
            if (net == null || net.Server == null || !net.Server.Active)
                return 0; // host-only: пуск оружия — серверное действие
            float now = Time.unscaledTime;

            // отметка волны: у цели — следует за ней (рисуется по Target, пока жива
            // в очереди), после выстрела — статичная точка
            int markId = ++_markIdSeq;
            var markShips = new List<Unit>(ships);
            Marks.Add(new SalvoMark(markId, aimpoint, now + 5f, markShips));
            if (Marks.Count > MarkCap)
                Marks.RemoveAt(0);

            int firedNow = 0;
            var waiting = new List<Unit>();
            for (int i = 0; i < ships.Count; i++)
            {
                Unit ship = ships[i];
                if (ship == null || ship.disabled) // Unity fake-null
                    continue;
                // Клик = ЗАЛП ВСЕМИ ГОТОВЫМИ ТРУБАМИ (плейтест v0.123: ракетные
                // корабли «стреляют большой очередью» — один пуск на клик заставлял
                // долбить кнопкой; дозирование серией кликов остаётся для ЧАСТИ
                // труб: недозаряженные уйдут следующим кликом или волной очереди).
                int before = firedNow;
                while (TryLaunchOne(ship, target, target != null ? target.GlobalPosition() : aimpoint, markId))
                    firedNow++;
                if (firedNow == before)
                    waiting.Add(ship);
            }
            if (waiting.Count > 0)
            {
                if (Queue.Count < QueueCap)
                    Queue.Add(new QueuedWave
                    {
                        Target = target,
                        Aim = aimpoint,
                        Ships = waiting,
                        Deadline = now + WaveTtlSec,
                        MarkId = markId
                    });
                else
                    TouchMark(markId, now + MarkTtlSec); // очередь полна — хотя бы отметка угаснет
            }
            else
                TouchMark(markId, now + MarkTtlSec); // вся волна ушла сразу
            return firedNow;
        }

        /// <summary>Один пуск корабля (готовая подходящая станция). Точечный пуск
        /// (target=null) регистрирует отложенное перенацеливание новорождённой
        /// ракеты в координату; markId — волна пуска (летящая ракета встанет в
        /// WaveMissiles: драг точки волны перенацелит и её).</summary>
        private static bool TryLaunchOne(Unit ship, Unit target, GlobalPosition aim, int markId)
        {
            // предохранитель ритма: после пуска корабль отдыхает cooldown
            if (Cooldowns.TryGetValue(ship, out float until) && Time.unscaledTime < until)
                return false;
            WeaponStation st = PickStation(ship, target);
            if (st == null)
                return false;
            // Слой 2 (§2.14): ракетный пуск стоит массу+энергию; ЯО-станция
            // платит ЗАРЯДОМ с носителя (§2.14-3) — нет оплаты, ракета не уходит.
            bool nuclear = st.WeaponInfo != null && st.WeaponInfo.nuclear;
            if (nuclear ? !WarheadService.TryConsumeLaunch(ship)
                        : !SupplyService.TryPaySalvo(ship))
            {
                if (nuclear)
                    CommanderHud.Notice(Loc.Tr("Нет заряда БЧ — пуск невозможен"));
                return false;
            }
            if (target == null)
            {
                // снимок живых ракет владельца ДО пуска — новорождённую опознаем
                // по отсутствию в нём (спавн может опоздать на кадр — тик добьёт)
                MissileSnapIds.Clear();
                foreach (Unit u in UnitRegistry.allUnits)
                    if (u is Missile m && m.ownerID == ship.persistentID)
                        MissileSnapIds.Add(m.persistentID);
            }
            WeaponGateService.ManualOrder = true;
            try
            {
                st.Fire(ship, target); // нативный путь корабельных пусков (FireControl):
            }                        // пристёгнутые снаряды, перезарядка/перевооружение — нативно
            finally
            {
                WeaponGateService.ManualOrder = false;
            }
            // кулдаун = нативный интервал станции (многозарядная VLS без него
            // перезаряжается мгновенно), минимум ManualCooldownSec
            float fi = st.WeaponInfo != null ? st.WeaponInfo.fireInterval : 0f;
            Cooldowns[ship] = Time.unscaledTime + Mathf.Max(fi, ManualCooldownSec);
            if (target == null)
                PendingAims.Add(new PendingAim
                {
                    Owner = ship.persistentID,
                    Aim = aim,
                    Known = new HashSet<PersistentID>(MissileSnapIds),
                    Until = Time.unscaledTime + 10f,
                    MarkId = markId
                });
            return true;
        }

        /// <summary>Продлить/начать угасание отметки волны (структура в списке).</summary>
        private static void TouchMark(int id, float until)
        {
            for (int i = 0; i < Marks.Count; i++)
                if (Marks[i].Id == id)
                {
                    SalvoMark m = Marks[i];
                    m.Until = until;
                    Marks[i] = m;
                    return;
                }
        }

        /// <summary>Сдвинуть точку обстрела волны (драг вейпоинта, канон тактичек
        /// SupCom): ждущие корабли пустят ракеты туда, где точка стоит на МОМЕНТ
        /// ВЫСТРЕЛА, ЛЕТЯЩИЕ ракеты волны и ждущие отложенные перенацелы ведутся
        /// в новое место немедленно (жалоба 2026-11-06: выпущенные готовыми
        /// трубами ракеты летели по старой точке, пока игрок тащил крест —
        /// «стрельба идёт по старым позициям»). Точка приводится к ПОВЕРХНОСТИ
        /// (вода — зеркало, не дно) и обновляет отметку-крест.</summary>
        internal static void MoveWavePoint(int markId, GlobalPosition point)
        {
            Vector3 l = point.ToLocalPosition();
            point = new Vector3(l.x, GameApi.RouteY(l.x, l.z), l.z).ToGlobalPosition();
            for (int i = 0; i < Queue.Count; i++)
                if (Queue[i].MarkId == markId && Queue[i].Target == null)
                {
                    QueuedWave wave = Queue[i];
                    wave.Aim = point;
                    Queue[i] = wave;
                    for (int mi = 0; mi < Marks.Count; mi++)
                        if (Marks[mi].Id == markId)
                        {
                            SalvoMark m = Marks[mi];
                            m.Point = point;
                            Marks[mi] = m;
                            break;
                        }
                    // ЛЕТЯЩИЕ ракеты волны → новая точка (перевыдача прицела
                    // отзываться обязана; не отзывается только снятая волна)
                    if (WaveMissiles.TryGetValue(markId, out List<Missile> flying))
                        for (int fi = flying.Count - 1; fi >= 0; fi--)
                        {
                            Missile msl = flying[fi];
                            if (msl == null || msl.disabled) // Unity fake-null (правило 14)
                            {
                                flying.RemoveAt(fi);
                                continue;
                            }
                            AimMissileAtPoint(msl, point);
                        }
                    // отложенные перенацелы (спавн опаздывает на кадр) — тоже
                    for (int pi = 0; pi < PendingAims.Count; pi++)
                        if (PendingAims[pi].MarkId == markId)
                        {
                            PendingAim pa = PendingAims[pi];
                            pa.Aim = point;
                            PendingAims[pi] = pa;
                        }
                    return;
                }
        }

        /// <summary>Снять волну обстрела (Ctrl+Shift+ПКМ по точке): ждущие корабли
        /// больше не пустят по ней; выпущенное не отзывается. Отметка гаснет
        /// быстро (CancelFadeSec) — снятие видно сразу, в отличие от отстрелянной.
        /// true — нашли.</summary>
        internal static bool CancelWave(int markId)
        {
            for (int i = 0; i < Queue.Count; i++)
                if (Queue[i].MarkId == markId)
                {
                    Queue.RemoveAt(i);
                    TouchMark(markId, Time.unscaledTime + CancelFadeSec);
                    return true;
                }
            return false;
        }

        /// <summary>Есть ли у юнита хоть одна рабочая ракетная станция (кнопка
        /// «Залп» для флотских/батарейных выделений).</summary>
        public static bool UnitHasMissiles(Unit unit)
        {
            if (unit == null || unit.weaponStations == null)
                return false;
            foreach (WeaponStation st in unit.weaponStations)
                if (st != null && st.WeaponInfo != null && st.WeaponInfo.missile && st.Ammo > 0)
                    return true;
            return false;
        }

        /// <summary>Участник общего ручного залпа (v0.133: флот + РСЗО/ПУ наземки;
        /// пушечная техника — нативным боем, авиация — своим залпом V).</summary>
        public static bool IsSalvoUnit(Unit unit) =>
            unit is Ship ||
            (unit is GroundVehicle && !GameApi.IsEngineer(unit) && UnitHasMissiles(unit));

        /// <summary>Снять юнита со ВСЕХ ждущих волн, вернуть их число. Новый приказ
        /// движения без Shift = НОВАЯ очередь у ЛЮБОГО участника залпа (наземная ПУ —
        /// v0.134; корабли уравнены 2026-11-06: прежде «веду и обстреливаю параллельно»
        /// — точки залпа вели себя не как остальные вейпоинты); опустевшие волны
        /// завершаются обычным порядком — отметка угасает.</summary>
        public static int CancelWavesOf(Unit unit)
        {
            if (unit == null)
                return 0;
            int cancelled = 0;
            for (int i = Queue.Count - 1; i >= 0; i--)
            {
                QueuedWave wave = Queue[i];
                if (wave.Ships.RemoveAll(u => u == unit) > 0)
                    cancelled++;
                if (wave.Ships.Count == 0)
                {
                    Queue.RemoveAt(i);
                    TouchMark(wave.MarkId, Time.unscaledTime + CancelFadeSec);
                }
                else
                    Queue[i] = wave;
            }
            return cancelled;
        }

        /// <summary>Станция для волны: ракетная, с боеприпасом, готовая (интервал/
        /// перезарядка, магазин не исчерпан), не запрещённая гейтами и правилами
        /// типов против ЭТОЙ цели (BlocksUse в режиме ManualOrder — стойка уже
        /// обойдена), и её домен подходит цели (веса эффективности: перевес ВВ —
        /// по воздуху, иначе — по земле/морю; правило зеркально кольцам дальности и
        /// Catalog.ClassOf; цель null = пуск по точке → Surface). Из подходящих —
        /// наибольшей дальности.</summary>
        private static WeaponStation PickStation(Unit ship, Unit target)
        {
            if (ship == null || ship.disabled || ship.weaponStations == null) // Unity fake-null
                return null;
            WeaponStation best = null;
            float bestRange = -1f;
            WeaponGateService.ManualOrder = true;
            try
            {
                foreach (WeaponStation st in ship.weaponStations)
                {
                    if (st == null || st.WeaponInfo == null || !st.WeaponInfo.missile)
                        continue;
                    if (Scope != null && st.WeaponInfo != Scope)
                        continue; // заряд с чипа типа: волны только этим вооружением
                    if (st.Ammo <= 0 || !st.Ready() || !HasAttached(st))
                        continue;
                    if (WeaponGateService.BlocksUse(ship, st, target))
                        continue;
                    var eff = st.WeaponInfo.effectiveness; // RoleIdentity-структура
                    if (!FitsDomain(eff.antiAir, eff.antiSurface,
                            WeaponGateService.DomainOf(target) == TargetDomain.Air))
                        continue;
                    float r = st.WeaponInfo.targetRequirements.maxRange;
                    if (r > bestRange)
                    {
                        bestRange = r;
                        best = st;
                    }
                }
            }
            finally
            {
                WeaponGateService.ManualOrder = false;
            }
            return best;
        }

        /// <summary>Домен станции подходит цели: перевес ВВ — только по воздуху,
        /// иначе (включая ничью) — по земле/морю. Чистая — тестируется.</summary>
        internal static bool FitsDomain(float antiAir, float antiSurface, bool targetAir) =>
            targetAir ? antiAir > antiSurface : antiAir <= antiSurface;

        // Нативный примитив пуска КОРАБЛЕЙ — WeaponStation.Fire (путь FireControl):
        // стреляет только ПРИСТЁГНУТЫЕ снаряды (VLS держит один на пусковой, следующий
        // встаёт циклом перезарядки — Ready() честно ждёт), сам дергает логистику
        // перевооружения при малом боезапасе. LaunchMount — путь пилота по пилонам
        // с бегущим weaponIndex: у кораблей исчерпывал магазин насмерть (плейтест
        // v0.122: «один запуск и всё»).
        private static bool HasAttached(WeaponStation st)
        {
            foreach (Weapon w in st.Weapons)
                if (w != null && w.IsAttached())
                    return true;
            return false;
        }
    }
}
