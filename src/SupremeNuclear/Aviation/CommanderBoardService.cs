using System.Collections.Generic;
using NuclearOption;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using NuclearOption.SavedMission.Outcomes;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Командирский борт («летающий ACU», DESIGN §2.1, этап 8 §4): юнит-условие
    /// победы. Борта кладутся сценарием (SavedAircraft SN-ACU-A/B) и адоптруются
    /// здесь. Обязанности: орбита над своей базой (патрульный цикл AircraftOrders),
    /// смерть → детонация нативной БЧ + DeclareEndGame(Defeat) погибшей стороне
    /// (Rpc разворачивает результат для всех остальных), EMCON-молчание (радар
    /// выкл + ЭПР вниз + стирание контактов), авто-RTB при тяжёлом уроне и ремонт
    /// на земле у своей базы («окно уязвимости»). Хост-only; MP — §12.
    /// </summary>
    internal static class CommanderBoardService
    {
        public const string BoardMarker = "SN-ACU";

        public sealed class BoardRec
        {
            public Unit Board;
            public FactionHQ Hq;
            public bool Emcon;          // радиомолчание включено
            public float BaseRcs;       // ЭПР до EMCON (восстановление при выкл)
            public bool DeadHandled;    // детонация/победа обработаны
            public bool RtbIssued;      // авто-возврат выдан (не спамить)
            public bool Landed;         // стоит на своей базе — ремонт
            public bool RepairNoticed;  // Notice «на ремонте» показан
            public Airbase HomeBase;    // аэродром возврата (RTB/ремонт)
            public float HpScale = 100f; // усиленный максимум детали (partHpMultiplier)
        }

        private static readonly Dictionary<Unit, BoardRec> _boards =
            new Dictionary<Unit, BoardRec>();
        private static readonly List<BoardRec> _boardList = new List<BoardRec>();
        private static readonly List<Unit> _adopt = new List<Unit>();

        private static CommanderBoardProfile _profile;
        private static float _nextAdopt;
        private static float _nextLogic;
        private static bool _warnedMissing;

        /// <summary>Реестр бортов (HUD рисует маркеры, хоткей ищет свой).</summary>
        public static IReadOnlyList<BoardRec> Boards => _boardList;

        public static BoardRec BoardOf(Unit unit) =>
            unit != null && _boards.TryGetValue(unit, out BoardRec rec) ? rec : null;

        /// <summary>Борт локальной фракции (хоткей EMCON).</summary>
        public static BoardRec LocalBoard()
        {
            GameApi.TryGetLocalHQ(out FactionHQ hq);
            if (hq == null)
                return null;
            for (int i = 0; i < _boardList.Count; i++)
                if (_boardList[i].Hq == hq)
                    return _boardList[i];
            return null;
        }

        public static void Tick()
        {
            if (!GameApi.InMission)
            {
                if (_boards.Count > 0)
                    Reset();
                return;
            }
            var net = NetworkManagerNuclearOption.i;
            if (net == null || net.Server == null || !net.Server.Active)
                return;
            if (_profile == null)
                _profile = CommanderBoardProfile.Load();
            if (!_profile.enabled)
                return;

            if (Time.time >= _nextAdopt)
            {
                _nextAdopt = Time.time + 1f;
                AdoptBoards();
                WarnIfDeclaredButMissing();
            }
            if (Time.time >= _nextLogic)
            {
                _nextLogic = Time.time + 0.5f;
                RunLogic(0.5f);
            }
        }

        /// <summary>Поиск бортов сцены по имени SN-ACU-* (маркер кладёт ScenarioBuild;
        /// спавн нативный). Первая адопция — орбита и подписка смерти.</summary>
        private static void AdoptBoards()
        {
            _adopt.Clear();
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled)
                    continue;
                string name = unit.NetworkUniqueName;
                if (name == null || !name.StartsWith(BoardMarker + "-"))
                    continue;
                if (_boards.ContainsKey(unit))
                    continue;
                _adopt.Add(unit);
            }
            for (int i = 0; i < _adopt.Count; i++)
            {
                Unit board = _adopt[i];
                var rec = new BoardRec
                {
                    Board = board,
                    Hq = board.NetworkHQ,
                    BaseRcs = board.RCS,
                    HpScale = 100f * Mathf.Max(1f, _profile.partHpMultiplier),
                };
                // Живучесть ACU: детали усиливаются в adoption-кадр (юнит свежий,
                // повторной адопции в той же сцене нет — словарь не пуст).
                List<UnitPart> parts = board.partLookup;
                if (parts != null)
                    foreach (UnitPart p in parts)
                        if (p != null)
                            p.hitPoints *= Mathf.Max(1f, _profile.partHpMultiplier);
                _boards[board] = rec;
                _boardList.Add(rec);
                board.onDisableUnit += OnBoardDead;
                if (rec.Hq != null)
                    Log.Info("Board: адопция борта '" + board.NetworkUniqueName + "' фракции " +
                             rec.Hq.faction.factionName + " — " + board.definition.jsonKey +
                             " (живучесть ×" + _profile.partHpMultiplier.ToString("F0") + ")");
                IssueOrbit(rec);
            }
        }

        /// <summary>Патруль-орбита: правильный n-угольник вокруг своей базы, высота
        /// профиля (перезапись AltAgl+Points — сгенерированный патруль идёт на
        /// высоте профиля приказов, борт должен круглять высоко).</summary>
        private static void IssueOrbit(BoardRec rec)
        {
            Unit board = rec.Board;
            if (board == null || rec.Hq == null)
                return;
            GlobalPosition? basePos = AiCommanderService.BasePosOf(rec.Hq);
            if (basePos == null)
                return;
            Vector3 center = basePos.Value.ToLocalPosition();
            List<Vector3> pts = Util.CommanderOrbitPoints(
                new Vector2(center.x, center.z), _profile.orbitRadiusM, _profile.orbitAltM,
                _profile.orbitPoints);
            AircraftOrders.SetPatrol(board, pts[0]);
            for (int i = 1; i < pts.Count; i++)
                AircraftOrders.Enqueue(board, AirOrderMode.Patrol, pts[i]);
            // Высота орбиты: y точек патруля хранит AGL сегмента.
            List<AircraftOrders.OrderSegment> plan = AircraftOrders.PlanOf(board);
            if (plan != null)
            {
                for (int s = 0; s < plan.Count; s++)
                {
                    AircraftOrders.OrderSegment seg = plan[s];
                    seg.AltAgl = _profile.orbitAltM;
                    for (int p = 0; p < seg.Points.Count; p++)
                    {
                        GlobalPosition gp = seg.Points[p];
                        seg.Points[p] = new GlobalPosition(gp.x, _profile.orbitAltM, gp.z);
                    }
                }
                AircraftOrders.Version++;
            }
            rec.RtbIssued = false;
            rec.Landed = false;
            rec.RepairNoticed = false;
        }

        /// <summary>Смерть борта: детонация БЧ в точке + конец матча погибшей
        /// стороне (DeclareEndGame(Defeat) — Rpc разворачивает результат всем
        /// остальным). Двойной-KO в один кадр: второй FinishGame нативно
        /// игнорируется (gameResolution != Ongoing).</summary>
        private static void OnBoardDead(Unit unit)
        {
            BoardRec rec = BoardOf(unit);
            if (rec == null || rec.DeadHandled)
                return;
            rec.DeadHandled = true;
            if (!GameApi.InMission)
                return;
            var net = NetworkManagerNuclearOption.i;
            if (net == null || net.Server == null || !net.Server.Active)
                return;
            Vector3 pos = unit != null ? unit.transform.position : Vector3.zero;
            DamageEffects.BlastFrag(_profile != null ? _profile.blastYield : 8000f,
                pos, PersistentID.None, PersistentID.None);
            Log.Info("Board: борт '" + (unit != null ? unit.NetworkUniqueName : "?") +
                     "' уничтожен — детонация " + (_profile != null ? _profile.blastYield.ToString("F0") : "8000") +
                     " и конец матча");
            if (rec.Hq != null)
                rec.Hq.DeclareEndGame(EndType.Defeat);
            CommanderHud.Notice(Loc.Tr("Командирский борт уничтожен"));
        }

        /// <summary>Авто-RTB при тяжёлом уроне; ремонт на земле у своей базы
        /// (медленный, за массу — окно уязвимости); полное HP → орбита заново.</summary>
        private static void RunLogic(float elapsed)
        {
            GameApi.TryGetLocalHQ(out FactionHQ localHq);
            for (int i = 0; i < _boardList.Count; i++)
            {
                BoardRec rec = _boardList[i];
                Unit board = rec.Board;
                if (board == null || board.disabled || rec.DeadHandled || rec.Hq == null)
                    continue;
                bool own = rec.Hq == localHq; // Notice — только владельцу борта
                float hp = BoardHpFraction(rec);
                float speedSqr = board.rb != null ? board.rb.velocity.sqrMagnitude : 0f;

                if (!rec.Landed)
                {
                    if (!rec.RtbIssued && hp < _profile.autoRtbHpFraction)
                    {
                        rec.HomeBase = rec.HomeBase != null && rec.HomeBase.CurrentHQ == rec.Hq
                            ? rec.HomeBase
                            : SortieService.NearestOwnAirbase(board as Aircraft);
                        if (rec.HomeBase != null)
                        {
                            AircraftOrders.InsertHeadSegment(
                                board, AircraftOrders.MakeRtb(board, rec.HomeBase));
                            rec.RtbIssued = true;
                            if (own)
                                CommanderHud.Notice(Loc.Tr("Борт тяжело повреждён — возврат на базу"));
                            Log.Info("Board: авто-RTB борта '" + board.NetworkUniqueName +
                                     "' (HP " + (hp * 100f).ToString("F0") + "%)");
                        }
                    }
                    if (rec.RtbIssued && rec.HomeBase != null &&
                        Util.BoardParked(
                            new Vector2(board.transform.position.x, board.transform.position.z),
                            new Vector2(rec.HomeBase.transform.position.x, rec.HomeBase.transform.position.z),
                            _profile.repairRadiusM, speedSqr))
                    {
                        rec.Landed = true;
                        rec.RepairNoticed = false;
                    }
                    continue;
                }

                // Стоит на базе: ремонт, пока есть урон; полное HP → снова орбита.
                if (speedSqr > 4f)
                {
                    rec.Landed = false; // взлетел/уехал (в т.ч. приказом игрока)
                    continue;
                }
                if (hp >= 0.999f)
                {
                    if (rec.RtbIssued)
                    {
                        IssueOrbit(rec);
                        if (own)
                            CommanderHud.Notice(Loc.Tr("Борт восстановлен — возвращается на орбиту"));
                        Log.Info("Board: борт '" + board.NetworkUniqueName + "' отремонтирован — орбита");
                    }
                    continue;
                }
                if (!rec.RepairNoticed)
                {
                    rec.RepairNoticed = true;
                    if (own)
                        CommanderHud.Notice(Loc.Tr("Борт командира на ремонте — окно уязвимости"));
                }
                Repair(rec, elapsed);
            }
        }

        /// <summary>Доля HP борта от УСИЛЕННОГО максимума (GameApi.HpFraction
        /// делит на «голые» 100/деталь — усиленный борт вечно «здоров», авто-RTB
        /// не сработал бы до самого конца).</summary>
        private static float BoardHpFraction(BoardRec rec)
        {
            List<UnitPart> parts = rec.Board != null ? rec.Board.partLookup : null;
            if (parts == null || parts.Count == 0)
                return 1f;
            float sum = 0f;
            int n = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                UnitPart p = parts[i];
                if (p == null)
                    continue;
                sum += p.hitPoints;
                n++;
            }
            return n > 0 ? Mathf.Clamp01(sum / (n * rec.HpScale)) : 1f;
        }

        /// <summary>Починка деталей (полная = HpScale — усиленный максимум) за
        /// массу; вне экономики (стоковая карта) — бесплатно, сервис не должен
        /// стопорить).</summary>
        private static void Repair(BoardRec rec, float elapsed)
        {
            Unit board = rec.Board;
            List<UnitPart> parts = board.partLookup;
            if (parts == null || parts.Count == 0)
                return;
            float massCost = EconomyService.Running ? _profile.repairMassPerSec * elapsed : 0f;
            if (massCost > 0f)
            {
                EcoFlow.Paid paid = EconomyService.Drain(rec.Hq, massCost, 0f);
                if (paid.Mass <= 0f)
                    return; // масса кончилась — ремонт встал (EcoFlow режет по дефициту)
            }
            for (int i = 0; i < parts.Count; i++)
            {
                UnitPart p = parts[i];
                if (p == null)
                    continue;
                p.hitPoints = Util.RepairedHitPoints(p.hitPoints, _profile.repairHpPerSec, elapsed, rec.HpScale);
            }
        }

        /// <summary>EMCON (Ctrl+E): радар выкл, ЭПР × фактор, накопленные контакты
        /// у врага стерты. Штраф — борт слеп. Повтор — вернуть всё.</summary>
        public static void ToggleEmcon()
        {
            if (!GameApi.InMission)
                return;
            var net = NetworkManagerNuclearOption.i;
            if (net == null || net.Server == null || !net.Server.Active)
                return;
            if (_profile == null)
                _profile = CommanderBoardProfile.Load();
            BoardRec rec = LocalBoard();
            if (rec == null || rec.Board == null || rec.Board.disabled)
            {
                CommanderHud.Notice(Loc.Tr("Командирский борт не найден"));
                return;
            }
            rec.Emcon = !rec.Emcon;
            Unit board = rec.Board;
            Aircraft aircraft = board as Aircraft;
            if (rec.Emcon)
            {
                board.RCS = rec.BaseRcs * _profile.emconRcsFactor;
                if (aircraft != null && aircraft.radar != null)
                    aircraft.radar.activated = false;
                foreach (FactionHQ hq in FactionRegistry.HQLookup.Values)
                    if (hq != null && hq != rec.Hq)
                        hq.DeregisterTrackedUnit(board);
                CommanderHud.Notice(Loc.Tr("EMCON: радиомолчание включено"));
            }
            else
            {
                board.RCS = rec.BaseRcs;
                if (aircraft != null && aircraft.radar != null)
                    aircraft.radar.activated = true;
                CommanderHud.Notice(Loc.Tr("EMCON: радиомолчание выключено"));
            }
            Log.Info("Board: EMCON " + (rec.Emcon ? "вкл" : "выкл") + " — '" + board.NetworkUniqueName + "'");
        }

        /// <summary>Миссия ОБЪЯВИЛА борта (SavedAircraft SN-ACU-*), но юниты не
        /// спавнятся — Spawner молча режет заблокированный тип (плейтест 2026-10-31:
        /// UFO — ивентовый контент). Один warning на сцену.</summary>
        private static void WarnIfDeclaredButMissing()
        {
            if (_warnedMissing || _boards.Count > 0 || Time.time < 15f)
                return;
            Mission mission = MissionManager.CurrentMission;
            int declared = 0;
            if (mission?.aircraft != null)
                foreach (SavedAircraft sa in mission.aircraft)
                    if (sa != null && sa.UniqueName != null &&
                        sa.UniqueName.StartsWith(BoardMarker + "-"))
                        declared++;
            if (declared <= 0)
                return;
            _warnedMissing = true;
            Log.Warn("Board: миссия объявила " + declared + " борт(а) " + BoardMarker +
                     "-*, но юниты не найдены — спавн не прошёл? (Spawner режет тип: " +
                     "'disabled or blocked by AllowEventContent' — сменит модель в шаблоне commanderJsonKey)");
        }

        /// <summary>Сцена умерла — борта и подписки тоже (юниты уничтожены).</summary>
        public static void Reset()
        {
            _boards.Clear();
            _boardList.Clear();
            _profile = null;
            _nextAdopt = 0f;
            _nextLogic = 0f;
            _warnedMissing = false;
        }
    }
}
