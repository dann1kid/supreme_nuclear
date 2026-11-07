using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    // Часть MoveOrderService: пикеры и приказы эскорт/охрана/ремонт/сбор (ПКМ по своим и обломкам).
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; сводка и вход Tick — MoveOrderService.cs.
    public sealed partial class MoveOrderService
    {
        // Сопровождающие текущего клика (веер секторов вокруг цели) — наземка и авиация.
        private readonly List<Unit> _escortFan = new List<Unit>();
        /// <summary>Сопровождение/ассист союзного юнита (v0.97): боевые наземка/флот —
        /// следование своим сектором веера + защита stop-and-fire; инженеры — следование
        /// и вливание в стройки ЦЕЛИ (эскорт-ассист BuildTasks, без Shift — новая связь);
        /// авиация — эскорт-сегмент плана (Shift дописывает в план, наземке Shift не
        /// кладёт сопровождение в очередь — цепочка одна, Notice). Цель сама себе
        /// сопроводителем быть не может.</summary>
        private bool TryIssueEscortOrders(FactionHQ localHq)
        {
            Unit friend = TryPickFriendlyAt(localHq);
            if (friend == null)
                return false;
            Vector3 fp = friend.transform.position;

            _escortFan.Clear();
            int airCount = 0, groundCount = 0, engCount = 0;
            foreach (Unit unit in _controller.Selection.Selected)
            {
                if (unit == null || unit == friend)
                    continue; // сам себя не сопровождает
                if (unit is Aircraft)
                {
                    _escortFan.Add(unit);
                    airCount++;
                    continue;
                }
                if (unit is GroundVehicle || unit is Ship)
                {
                    _escortFan.Add(unit);
                    if (GameApi.IsEngineer(unit))
                        engCount++;
                    else
                        groundCount++;
                }
            }
            if (_escortFan.Count == 0)
            {
                CommanderHud.Notice(Loc.Tr("Сопровождение: цель — всё выделение"));
                return true; // клик съеден: приказов движению по самому себе не выдаём
            }

            // Сортировка по азимутам вокруг цели: соседние по кругу получают соседние
            // сектора веера — эскорты распределяются вокруг цели, а не в кучу (как
            // полукруг групповой атаки).
            _escortFan.Sort((a, b) => Mathf.Atan2(a.transform.position.x - fp.x,
                    a.transform.position.z - fp.z)
                .CompareTo(Mathf.Atan2(b.transform.position.x - fp.x,
                    b.transform.position.z - fp.z)));
            float groundStandoff = Settings.EscortStandoffM + friend.maxRadius + 10f;
            float airStep = Settings.FormationSpacing * 4f;
            int issued = 0;
            for (int i = 0; i < _escortFan.Count; i++)
            {
                Unit unit = _escortFan[i];
                if (unit is Aircraft aircraft)
                {
                    float bearing = i * airStep / Settings.EscortAirDistM;
                    if (RtsInput.Shift)
                        AircraftOrders.EnqueueEscort(aircraft, friend, bearing);
                    else
                        AircraftOrders.SetEscort(aircraft, friend, bearing);
                    issued++;
                    continue;
                }
                if (RtsInput.Shift)
                {
                    // Очередь сопровождения (§12-36а, зеркально авиации): в линейной
                    // цепочке связь активируется после пройденных точек, в эскортирующей —
                    // по смерти текущей цели. Инженеру легально наравне с боевыми.
                    GroundOrders.EnqueueEscort(unit, friend);
                    issued++;
                    continue;
                }
                if (GameApi.IsEngineer(unit))
                    BuildTasks.ClearFor(unit); // новая связь = новая очередь (как патруль)
                Vector3 up = unit.transform.position;
                float bearingFull = Mathf.Atan2(up.x - fp.x, up.z - fp.z) +
                    GroundOrders.FanBearingOffset(i, _escortFan.Count,
                        Settings.FormationSpacing, groundStandoff);
                GroundOrders.SetEscort(unit, friend, bearingFull);
                issued++;
            }
            if (issued > 0)
                CommanderHud.Notice(Loc.F("Сопровождение: {0} → {1}", issued,
                    friend.definition != null ? friend.definition.unitName : "?"));
            return true;
        }

        /// <summary>Союзный юнит/здание под курсором для ОХРАНЫ — экранный пик
        /// (свои видны всегда): мобильная техника, флот, авиация И здания (канон
        /// SupCom: охрана радара/завода = защита + авто-ремонт инженером).
        /// Зеркало TryPickFriendlyAt с расширенным набором типов.</summary>
        private Unit TryPickGuardTargetAt(FactionHQ localHq)
        {
            float radiusPx = Settings.EnemyPickRadiusPx;
            Camera cam = GameApi.MainCam;
            if (cam == null || localHq == null)
                return null;
            Unit best = null;
            float bestDist = float.MaxValue;
            Vector2 mouse = new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y);
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || unit.NetworkHQ == null || unit.NetworkHQ != localHq)
                    continue;
                if (!(unit is GroundVehicle || unit is Ship || unit is Aircraft || unit is Building))
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z <= 0f)
                    continue;
                float d = Vector2.Distance(new Vector2(sp.x, sp.y), mouse);
                float limit = Mathf.Max(radiusPx, GameApi.ApparentSizePx(unit, cam) * 0.5f);
                if (d < limit && d < bestDist)
                {
                    bestDist = d;
                    best = unit;
                }
            }
            return best;
        }

        /// <summary>Охрана союзного юнита/здания (v0.158, SupCom guard, OrderMode.Y):
        /// наземка/флот — follow-модель эскорта с веером секторов; инженер — охрана
        /// с авто-ремонтом (повреждённая цель временно уходит в Repair); авиация
        /// охраны не имеет (план-модель: escort-сегменты, ПКМ по союзнику) —
        /// пропускается с Notice. Цель сама себе стражем быть не может.
        /// true — клик по цели съеден (цель найдена или выделение только авиация).</summary>
        private bool IssueGuardOrders(FactionHQ localHq)
        {
            Unit friend = TryPickGuardTargetAt(localHq);
            if (friend == null)
                return false;
            Vector3 fp = friend.transform.position;

            _escortFan.Clear();
            int airCount = 0;
            foreach (Unit unit in _controller.Selection.Selected)
            {
                if (unit == null || unit == friend)
                    continue; // сам себя не охраняет
                if (unit is Aircraft)
                {
                    airCount++;
                    continue;
                }
                if (unit is GroundVehicle || unit is Ship)
                    _escortFan.Add(unit);
            }
            if (_escortFan.Count == 0)
            {
                CommanderHud.Notice(airCount > 0
                    ? Loc.Tr("Охрана: авиации не назначается — сопровождение (ПКМ по союзнику)")
                    : Loc.Tr("Охрана: цель — всё выделение"));
                return true; // клик съеден: приказов движению по самой цели не выдаём
            }

            // Сортировка по азимутам вокруг цели: соседние сектора веера соседям
            // (как полукруг групповой атаки и веер эскорта).
            _escortFan.Sort((a, b) => Mathf.Atan2(a.transform.position.x - fp.x,
                    a.transform.position.z - fp.z)
                .CompareTo(Mathf.Atan2(b.transform.position.x - fp.x,
                    b.transform.position.z - fp.z)));
            float guardStandoff = Settings.EscortStandoffM + friend.maxRadius + 10f;
            int issued = 0;
            for (int i = 0; i < _escortFan.Count; i++)
            {
                Unit unit = _escortFan[i];
                if (GameApi.IsEngineer(unit))
                    BuildTasks.ClearFor(unit); // новая связь = новая очередь (как патруль)
                Vector3 up = unit.transform.position;
                float bearing = Mathf.Atan2(up.x - fp.x, up.z - fp.z) +
                    GroundOrders.FanBearingOffset(i, _escortFan.Count,
                        Settings.FormationSpacing, guardStandoff);
                GroundOrders.SetGuard(unit, friend, bearing);
                issued++;
            }
            if (issued > 0)
                CommanderHud.Notice(Loc.F("Охрана: {0} → {1}", issued,
                    friend.definition != null ? friend.definition.unitName : "?"));
            return true;
        }

        /// <summary>Союзный мобильный юнит под курсором — экранный пик (свои видны
        /// всегда, даталинк не нужен). Зеркало TryPickEnemyAt: движущаяся техника,
        /// флот и авиация (здания статичны — сопровождать нечего).</summary>
        private Unit TryPickFriendlyAt(FactionHQ localHq)
        {
            float radiusPx = Settings.EnemyPickRadiusPx;
            Camera cam = GameApi.MainCam;
            if (cam == null || localHq == null)
                return null;
            Unit best = null;
            float bestDist = float.MaxValue;
            Vector2 mouse = new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y);
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || unit.NetworkHQ == null || unit.NetworkHQ != localHq)
                    continue;
                if (!(unit is GroundVehicle || unit is Ship || unit is Aircraft))
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z <= 0f)
                    continue;
                float d = Vector2.Distance(new Vector2(sp.x, sp.y), mouse);
                float limit = Mathf.Max(radiusPx, GameApi.ApparentSizePx(unit, cam) * 0.5f);
                if (d < limit && d < bestDist)
                {
                    bestDist = d;
                    best = unit;
                }
            }
            return best;
        }

        /// <summary>ПКМ инженерами по РАНЕНОЙ своей технике/зданию = РЕМОНТ (v0.149,
        /// канон SupCom): стоящая связь Repair (как сопровождение — в очередь точек
        /// не кладётся), ведение к цели и налив HP — GroundOrders/RepairService.
        /// Целый юнит не пикнется — его подберёт сопровождение ниже. true — клик
        /// съеден (в выделении были инженеры).</summary>
        private bool TryIssueRepairOrders(FactionHQ localHq)
        {
            Unit target = TryPickRepairTargetAt(localHq);
            if (target == null)
                return false;
            int issued = 0, skippedShift = 0;
            foreach (Unit unit in _controller.Selection.Selected)
            {
                if (unit == null || unit == target || !GameApi.IsEngineer(unit))
                    continue;
                if (RtsInput.Shift)
                {
                    // Ремонт — стоящий приказ (как сопровождение): в очередь не кладётся.
                    skippedShift++;
                    continue;
                }
                BuildTasks.ClearFor(unit); // новая связь = новая очередь (канон)
                GroundOrders.SetRepair(unit, target);
                issued++;
            }
            if (issued == 0 && skippedShift == 0)
                return false; // инженеров в выделении нет — пусть клик идёт дальше (эскорт/движение)
            if (issued > 0)
                CommanderHud.Notice(Loc.F("Ремонт: {0} → {1}", issued,
                    target.definition != null ? target.definition.unitName : "?"));
            if (skippedShift > 0)
                CommanderHud.Notice(Loc.Tr("Ремонт не ставится в очередь — выдай без Shift"));
            return true;
        }

        /// <summary>РАНЕНЫЙ свой юнит/здание под курсором — экранный пик для ремонта
        /// (техника/флот/здания; авиацию чинит аэродромный сервис — §3-10).
        /// Расширение TryPickFriendlyAt: здания включены, раненость обязательна.</summary>
        private Unit TryPickRepairTargetAt(FactionHQ localHq)
        {
            float radiusPx = Settings.EnemyPickRadiusPx;
            Camera cam = GameApi.MainCam;
            if (cam == null || localHq == null)
                return null;
            Unit best = null;
            float bestDist = float.MaxValue;
            Vector2 mouse = new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y);
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || unit.NetworkHQ == null || unit.NetworkHQ != localHq)
                    continue;
                if (!(unit is GroundVehicle || unit is Ship || unit is Building))
                    continue;
                if (GameApi.HpFraction(unit) >= 0.995f)
                    continue; // целый — не цель ремонта (его подберёт эскорт)
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z <= 0f)
                    continue;
                float d = Vector2.Distance(new Vector2(sp.x, sp.y), mouse);
                float limit = Mathf.Max(radiusPx, GameApi.ApparentSizePx(unit, cam) * 0.5f);
                if (d < limit && d < bestDist)
                {
                    bestDist = d;
                    best = unit;
                }
            }
            return best;
        }

        /// <summary>ПКМ инженерами по обломку = адресный сбор (v0.149, §12-46г):
        /// точка очереди у обломка (сальваж — движение, а не стоящая связь) —
        /// доедет и встанет, автосбор SalvageService разбирает сам. Без Shift —
        /// новая очередь, с Shift — дописать точку (групповой вейпоинт).</summary>
        private bool TryIssueSalvageOrder()
        {
            Wreckage wreck = TryPickWreckAt();
            if (wreck == null)
                return false;
            Vector3 wp = wreck.transform.position;
            wp.y = GameApi.GroundY(wp.x, wp.z);
            var point = wp.ToGlobalPosition();
            int groupId = BuildTasks.NextGroupId();
            int issued = 0;
            foreach (Unit unit in _controller.Selection.Selected)
            {
                if (unit == null || !GameApi.IsEngineer(unit))
                    continue;
                if (!RtsInput.Shift)
                    BuildTasks.ClearFor(unit); // новый приказ = новая очередь (канон)
                BuildTasks.AppendMove(unit, point, groupId);
                issued++;
            }
            if (issued == 0)
                return false; // инженеров нет — не съедаем клик по земле рядом с обломком
            CommanderHud.Notice(Loc.F("Сбор обломка: {0}", issued));
            return true;
        }

        /// <summary>Обломок под курсором — экранный пик по MissionManager.listWrecks
        /// (Wreckage — не Unit, в реестре юнитов его нет; зеркало TryPickEnemyAt).</summary>
        private Wreckage TryPickWreckAt()
        {
            var mm = MissionManager.i;
            Camera cam = GameApi.MainCam;
            if (mm == null || cam == null || mm.listWrecks.Count == 0)
                return null;
            float radiusPx = Settings.EnemyPickRadiusPx;
            Wreckage best = null;
            float bestDist = float.MaxValue;
            Vector2 mouse = new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y);
            foreach (Wreckage wreck in mm.listWrecks)
            {
                if (wreck == null)
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(wreck.transform.position);
                if (sp.z <= 0f)
                    continue;
                float d = Vector2.Distance(new Vector2(sp.x, sp.y), mouse);
                if (d < radiusPx && d < bestDist)
                {
                    bestDist = d;
                    best = wreck;
                }
            }
            return best;
        }
    }
}
