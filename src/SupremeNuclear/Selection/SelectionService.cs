using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Клик-выделение и рамка: своя техника, флот, авиация и здания
    /// (фабрики/захваченные экстракторы — см. GameApi.IsSelectableFriendly).</summary>
    public sealed class SelectionService
    {
        private static readonly Dictionary<int, List<Unit>> Groups = new Dictionary<int, List<Unit>>();

        /// <summary>Аэродром, выделенный кликом по его башне/зданию (не Unit).</summary>
        public Airbase SelectedAirbase { get; set; }

        public readonly List<Unit> Selected = new List<Unit>();

        /// <summary>Осматриваемая чужая цель/ракета (ЛКМ по видимому контакту):
        /// показывает инфопанель без выделения — приказы чужим не выдаются.
        /// Живёт, пока цель жива и видима (prune в Tick).</summary>
        public Unit Inspected { get; set; }

        /// <summary>Радиус пика осматриваемой цели по иконке (как пик врага у ПКМ).</summary>
        private const float InspectPickRadiusPx = 16f;

        /// <summary>Подвыделение по типу (клик по группе в списке UI).</summary>
        public static void SelectTypeStatic(CommanderModeController controller, UnitDefinition definition)
        {
            var list = controller.Selection.Selected;
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i] == null || list[i].definition != definition)
                    list.RemoveAt(i);
        }

        public static void AssignGroup(int id, List<Unit> units) =>
            Groups[id] = new List<Unit>(units);

        /// <summary>Сброс всех групп (смена сцены): юниты умершей сцены недействительны.</summary>
        public static void ResetGroups() => Groups.Clear();

        /// <summary>Вызвать группу: чистим мёртвых, выделяем. null — группы нет/пуста.</summary>
        public List<Unit> RecallGroup(int id)
        {
            if (!Groups.TryGetValue(id, out List<Unit> list))
                return null;
            list.RemoveAll(u => u == null || u.disabled);
            if (list.Count == 0)
            {
                Groups.Remove(id);
                return null;
            }
            Selected.Clear();
            Selected.AddRange(list);
            _lastClickDefinition = null;
            return list;
        }


        /// <summary>Диагностика последнего клика-выделения (строка F11).</summary>
        public string LastClickDebug = "-";

        public bool IsDragging { get; private set; }
        public Vector2 DragStart { get; private set; }
        public Vector2 DragCurrent { get; private set; }

        private bool _leftPressed;
        private float _lastClickTime;
        private UnitDefinition _lastClickDefinition;

        public void Reset()
        {
            Selected.Clear();
            SelectedAirbase = null;
            Inspected = null;
            IsDragging = false;
            _leftPressed = false;
        }

        public void Tick()
        {
            // Мёртвые (destroyed/disabled) не копятся в выделении: иначе врёт счётчик
            // «Выбрано: N», а приказы по ним могут кинуть MissingReferenceException.
            if (Selected.Count > 0)
            {
                Selected.RemoveAll(u => u == null || u.disabled);
                BuildTasks.SelectedTask = null; // обычное выделение сменяет недострой
            }
            if (SelectedAirbase != null)
                BuildTasks.SelectedTask = null;

            // Осмотр живёт, пока цель жива и видима: погибла или ушла из разведки —
            // панель гаснет сама (слепое слежение за протухшим контактом — чит).
            if (Inspected != null && (Inspected == null || Inspected.disabled ||
                                      !GameApi.TryGetLocalHQ(out FactionHQ inspectHq) ||
                                      !GameApi.TryGetContact(Inspected, inspectHq, out _)))
                Inspected = null;

            if (RouteEditor.Consumed || RouteEditor.IsDragging || BuildTasks.Consumed)
            {
                _leftPressed = false;
                IsDragging = false;
                return; // ЛКМ занят подтверждением приказа или перетаскиванием точки
            }

            // Space — осмотр камеры (мышь крутит вид), выделение не стартуем.
            if (RtsInput.RotateModifierHeld)
            {
                _leftPressed = false;
                IsDragging = false;
                return;
            }

            if (RtsInput.LeftDown && !RtsInput.PointerOverUi)
            {
                _leftPressed = true;
                DragStart = RtsInput.MousePos;
                DragCurrent = DragStart;
                IsDragging = false;
            }

            if (_leftPressed && RtsInput.LeftHeld)
            {
                DragCurrent = RtsInput.MousePos;
                if (!IsDragging && Vector2.Distance(DragStart, DragCurrent) > Settings.BoxDragThresholdPx)
                    IsDragging = true;
            }

            if (_leftPressed && RtsInput.LeftUp)
            {
                _leftPressed = false;
                if (IsDragging)
                {
                    IsDragging = false;
                    BoxSelect(DragStart, DragCurrent, RtsInput.Shift);
                }
                else
                {
                    ClickSelect();
                }
            }
        }

        private void ClickSelect()
        {
            Ray ray = GameApi.ScreenRay(RtsInput.MousePos);
            Unit hitUnit = null;
            UnitPart part = null;
            RaycastHit[] hits = Physics.RaycastAll(ray, 60000f);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (RaycastHit hit in hits)
            {
                part = hit.collider != null ? hit.collider.GetComponentInParent<UnitPart>() : null;
                Unit unit = part != null ? part.parentUnit : (hit.collider != null ? hit.collider.GetComponentInParent<Unit>() : null);
                if (unit == null)
                    continue;
                hitUnit = unit;
                break;
            }

            GameApi.TryGetLocalHQ(out FactionHQ localHq);

            // Пик ПО ТЕЛУ модели СРАЗУ (до веток): клик в любую часть выделяет юнит
            // (фабрики/расчёты); иначе результат body-pick попадал в «простой» путь
            // и СБРАСЫВАЛ выделение вместо shift-добавления.
            if (hitUnit == null || !GameApi.IsSelectableFriendly(hitUnit, localHq))
            {
                // Заменяем хит ТОЛЬКО при находке: затирание здания аэродрома на null
                // ломало выбор аэродрома (оно не «выделяемо-дружественное»).
                Unit bodyPick = GameApi.TryPickUnitByBody(
                    GameApi.MainCam, new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y), localHq);
                Unit pick = bodyPick != null ? bodyPick : TryPickIconUnit(localHq);
                if (pick != null)
                    hitUnit = pick;
            }

            // Клик по зданию аэродрома = панель АЭРОПОРТА (агрегаты/резерв) — но только
            // по НЕ-ангару (вышка/склад): ангар — индивидуальная фабрия, клик по нему
            // выделяет его самого (пер-ангарные очереди, плейтест v0.87: пачка ангаров
            // + групповой заказ = паттерн SupCom). Shift — режим добавления, как было.
            if (!RtsInput.Shift && hitUnit is Building clickedBuilding &&
                clickedBuilding.GetComponent<Hangar>() == null &&
                clickedBuilding.Networkairbase != null &&
                clickedBuilding.Networkairbase.CurrentHQ == localHq)
            {
                SelectedAirbase = clickedBuilding.Networkairbase;
                Selected.Clear();
                Inspected = null;
                _lastClickDefinition = null;
                return;
            }
            // Shift+клик = ДОБАВИТЬ В ГРУППУ (режим добавления, SupCom): юниты и здания
            // накапливаются в выделение; Shift+даблклик — добавить ВСЕХ того же типа
            // на экране (ангар — все ангары аэродрома). Серия кликов считается и под
            // shift: раньше ветка возвращалась ДО детекции даблклика и гасила
            // _lastClickDefinition — быстрые shift-клики добавляли по одному юниту.
            if (RtsInput.Shift && hitUnit != null && GameApi.IsFriendlyUnit(hitUnit, localHq) &&
                (hitUnit is Building || GameApi.IsSelectableFriendly(hitUnit, localHq)))
            {
                bool doubleClick = Time.unscaledTime - _lastClickTime < Settings.DoubleClickSec
                    && hitUnit.definition == _lastClickDefinition;
                _lastClickTime = Time.unscaledTime;
                _lastClickDefinition = hitUnit.definition;
                if (!Selected.Contains(hitUnit))
                    Selected.Add(hitUnit);
                if (doubleClick)
                {
                    if (hitUnit is Building hb && hb.Networkairbase != null)
                        SelectAllHangarsOf(hb.Networkairbase, localHq);
                    else
                        SelectAllOfTypeOnScreen(hitUnit.definition, localHq);
                }
                SelectedAirbase = null;
                Inspected = null;
                return;
            }

            // Клик по СВОЕМУ аэродрому БЕЗ вышки (построенная ВПП рождается без
            // зданий — панель аэропорта была недостижима, v0.102): ромб на карте
            // или полотно полосы — «представители» аэродрома. Осмотр чужих
            // контактов приоритетнее ромба (красная иконка может его накрыть).
            if (!RtsInput.Shift && hitUnit == null && TryPickInspectableAt(localHq) == null)
            {
                Airbase picked = TryPickAirbaseDiamond(localHq) ?? TryPickAirbaseRunway(localHq, hits);
                if (picked != null)
                {
                    SelectedAirbase = picked;
                    Selected.Clear();
                    Inspected = null;
                    _lastClickDefinition = null;
                    LastClickDebug = "аэродром (ромб/полоса): " + picked.name;
                    return;
                }
            }

            SelectedAirbase = null;

            if (hitUnit != null && GameApi.IsSelectableFriendly(hitUnit, localHq))
            {
                LastClickDebug = "выделен: " + hitUnit.definition?.unitName;
                Inspected = null;
                bool doubleClick = Time.unscaledTime - _lastClickTime < Settings.DoubleClickSec
                    && hitUnit.definition == _lastClickDefinition;
                _lastClickTime = Time.unscaledTime;
                _lastClickDefinition = hitUnit.definition;

                Selected.Clear();
                Selected.Add(hitUnit);
                if (doubleClick)
                {
                    // Даблклик по АНГАРУ = все ангары его аэродрома (группа под
                    // пакетный заказ); прочее — все того же типа на экране.
                    if (hitUnit is Building hb && hb.Networkairbase != null)
                        SelectAllHangarsOf(hb.Networkairbase, localHq);
                    else
                        SelectAllOfTypeOnScreen(hitUnit.definition, localHq);
                }
            }
            else if (RtsInput.Shift)
            {
                // Shift = режим добавления: клик по пустому месту группу НЕ сбрасывает.
                LastClickDebug = "мимо (shift): выделение сохранено";
            }
            else
            {
                // ЛКМ по ВИДИМОМУ чужому юниту/ракете — осмотр (инфопанель без выделения,
                // приказы чужим не выдаются): луч по телу, затем пик по иконке контакта.
                // Ракеты обеих сторон, чужие — только под разведкой (как вражеские иконки).
                Unit inspect = hitUnit != null && GameApi.IsInspectableForeign(hitUnit, localHq)
                    ? hitUnit
                    : TryPickInspectableAt(localHq);
                if (inspect != null)
                {
                    Inspected = inspect;
                    LastClickDebug = "осмотр: " + (inspect.definition != null
                        ? inspect.definition.unitName : inspect.name);
                    Selected.Clear();
                    _lastClickDefinition = null;
                    BuildTasks.SelectedTask = null; // карточку недостроя сменяет осмотр
                    return;
                }

                LastClickDebug = "мимо: луч=" + (hitUnit != null ? hitUnit.name : "нет") +
                                 " (выделение сброшено)";
                Inspected = null;
                Selected.Clear();
                _lastClickDefinition = null;
                BuildTasks.SelectedTask = null; // карточку недостроя тоже снимаем
            }
        }

        /// <summary>Клик по 2D-иконке на стратегическом зуме (ближайшая в радиусе 14 px).</summary>
        private Unit TryPickIconUnit(FactionHQ localHq)
        {
            var cam = GameApi.MainCam;
            if (cam == null)
                return null;
            Vector3 mouse = RtsInput.MousePos;
            Unit best = null;
            float bestDist = 14f;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (!GameApi.IsSelectableFriendly(unit, localHq))
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z <= 0f)
                    continue;
                float d = Vector2.Distance(new Vector2(sp.x, sp.y), new Vector2(mouse.x, mouse.y));
                if (d < bestDist)
                {
                    bestDist = d;
                    best = unit;
                }
            }
            return best;
        }

        /// <summary>Пик СВОЕГО аэродрома по ромбу карты (якорь тот же, что рисует
        /// CommanderHud.DrawAirbaseDiamonds: центр + GroundY+20; радиус — как у
        /// иконок юнитов). Для построенной ВПП без вышки ромб — единственный
        /// «представитель» базы на стратегическом зуме.</summary>
        private Airbase TryPickAirbaseDiamond(FactionHQ localHq)
        {
            if (FactionRegistry.airbaseLookup == null || localHq == null)
                return null;
            var cam = GameApi.MainCam;
            if (cam == null)
                return null;
            Vector2 mouse = new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y);
            Airbase best = null;
            float bestDist = 14f;
            foreach (Airbase airbase in FactionRegistry.airbaseLookup.Values)
            {
                if (airbase == null || airbase.CurrentHQ != localHq)
                    continue;
                Vector3 pos = airbase.transform.position;
                Vector3 sp = cam.WorldToScreenPoint(
                    new Vector3(pos.x, GameApi.GroundY(pos.x, pos.z) + 20f, pos.z));
                if (sp.z <= 0f)
                    continue;
                float d = Vector2.Distance(new Vector2(sp.x, sp.y), mouse);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = airbase;
                }
            }
            return best;
        }

        /// <summary>Пик СВОЕГО аэродрома по ПОЛОТНУ полосы: точка первого
        /// не-юнит попадания клика внутри прямоугольника торцов×ширина какой-нибудь
        /// своей полосы (Util.InRectXZ, запас на кромки). Осознанный трейд-офф:
        /// клик приказов по земле, попавший на полосу, выделяет аэродром.</summary>
        private Airbase TryPickAirbaseRunway(FactionHQ localHq, RaycastHit[] hits)
        {
            if (localHq == null || FactionRegistry.airbaseLookup == null)
                return null;
            Vector3? ground = null;
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider == null)
                    continue;
                if (hit.collider.GetComponentInParent<Unit>() != null)
                    continue; // юниты распиканы выше — клик по телу не наш случай
                ground = hit.point;
                break;
            }
            if (ground == null)
                return null;
            Vector2 p = new Vector2(ground.Value.x, ground.Value.z);
            foreach (Airbase airbase in FactionRegistry.airbaseLookup.Values)
            {
                if (airbase == null || airbase.CurrentHQ != localHq || airbase.runways == null)
                    continue;
                foreach (Airbase.Runway runway in airbase.runways)
                {
                    if (runway == null || runway.Start == null || runway.End == null)
                        continue;
                    if (Util.InRectXZ(p,
                        new Vector2(runway.Start.position.x, runway.Start.position.z),
                        new Vector2(runway.End.position.x, runway.End.position.z),
                        runway.GetWidth() * 1.25f, 5f))
                        return airbase;
                }
            }
            return null;
        }

        /// <summary>Пик осматриваемой цели по 2D-иконке контакта: якорь совпадает с
        /// отрисовкой вражеских иконок (живой контакт — актуальная позиция, протухший —
        /// последняя известная), ракеты — якорь корпуса. Гейт видимости — тот же,
        /// что у иконок: невидимого чужого кликом не «прощупать».</summary>
        private Unit TryPickInspectableAt(FactionHQ localHq)
        {
            var cam = GameApi.MainCam;
            if (cam == null || localHq == null)
                return null;
            Vector2 mouse = new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y);
            Unit best = null;
            float bestDist = InspectPickRadiusPx;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || !GameApi.IsInspectableForeign(unit, localHq))
                    continue;
                TrackingInfo info = unit is Missile ? null : localHq.GetTrackingData(unit.persistentID);
                Vector3 sp = cam.WorldToScreenPoint(GameApi.ContactAnchorWorld(unit, info));
                if (sp.z <= 0f)
                    continue;
                float d = Vector2.Distance(new Vector2(sp.x, sp.y), mouse);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = unit;
                }
            }
            return best;
        }

        // Категории рамки: меньше — раньше в выделении (порядок Shift-добавления:
        // инженеры → логистика → оборонительные здания → производящие → боевые).
        private const int CatEngineer = 0;
        private const int CatLogistics = 1;
        private const int CatDefense = 2;
        private const int CatProduction = 3;
        private const int CatCombat = 4;

        private readonly List<Unit> _boxUnits = new List<Unit>();
        private readonly List<int> _boxCats = new List<int>();

        private static bool IsMobile(Unit unit) =>
            unit is GroundVehicle || unit is Ship || unit is Aircraft;

        /// <summary>Категория юнита для рамки: инженеры (TRUCK), логистика (безоружные
        /// мобильные — небоевые машины/разведка), оборонительные здания (вооружённая
        /// стационара), производящие (фабрики/депо и прочие безоружные здания),
        /// боевые (вооружённые мобильные). «Вооружённость» = GetMaxRange() ≥ 1 —
        /// та же предпосылка, что у приказа атаки наземки.</summary>
        private static int CategoryOf(Unit unit)
        {
            if (GameApi.IsEngineer(unit))
                return CatEngineer;
            if (IsMobile(unit))
                return unit.GetMaxRange() >= 1f ? CatCombat : CatLogistics;
            return unit.GetMaxRange() >= 1f ? CatDefense : CatProduction;
        }

        private void BoxSelect(Vector2 a, Vector2 b, bool additive)
        {
            GameApi.TryGetLocalHQ(out FactionHQ localHq);
            var cam = GameApi.MainCam;
            if (cam == null || localHq == null)
                return;

            float minX = Mathf.Min(a.x, b.x), maxX = Mathf.Max(a.x, b.x);
            float minY = Mathf.Min(a.y, b.y), maxY = Mathf.Max(a.y, b.y);

            // Кандидаты рамки с категорией; отдельно — есть ли в рамке боевые.
            _boxUnits.Clear();
            _boxCats.Clear();
            bool anyCombat = false;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (!GameApi.IsSelectableFriendly(unit, localHq))
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z <= 0f || sp.x < minX || sp.x > maxX || sp.y < minY || sp.y > maxY)
                    continue;
                int cat = CategoryOf(unit);
                _boxUnits.Add(unit);
                _boxCats.Add(cat);
                if (cat == CatCombat)
                    anyCombat = true;
            }

            if (!additive)
                Selected.Clear();
            // Дефолтная рамка: боевые есть — берём ТОЛЬКО их; нет — всех движущихся
            // (инженеры/логистика). Здания рамкой не выделяются вовсе — их берут
            // кликом. Shift — режим ДОБАВЛЕНИЯ: берём всё, но в порядке категорий
            // (голова выделения — инженеры, потом логистика, оборонка, производящие).
            for (int cat = CatEngineer; cat <= CatCombat; cat++)
                for (int i = 0; i < _boxUnits.Count; i++)
                {
                    if (_boxCats[i] != cat || Selected.Contains(_boxUnits[i]))
                        continue;
                    if (!additive && (anyCombat ? cat != CatCombat : !IsMobile(_boxUnits[i])))
                        continue;
                    if (Selected.Count >= Settings.MaxSelected)
                        return;
                    Selected.Add(_boxUnits[i]);
                }
        }

        private void SelectAllOfTypeOnScreen(UnitDefinition definition, FactionHQ localHq)
        {
            var cam = GameApi.MainCam;
            if (cam == null)
                return;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (Selected.Count >= Settings.MaxSelected)
                    break;
                if (unit.definition != definition || !GameApi.IsSelectableFriendly(unit, localHq) ||
                    Selected.Contains(unit))
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z > 0f && sp.x >= 0f && sp.x <= Screen.width && sp.y >= 0f && sp.y <= Screen.height)
                    Selected.Add(unit);
            }
        }

        /// <summary>Все живые ангары аэродрома (даблклик по ангару — группа под
        /// пакетный заказ; экран не фильтруем: аэродром — один «объект»).
        /// ДОБАВЛЯЕТ к выделению, сброс — обязанность вызывающего: shift-даблклик
        /// докладывает ангары в накопленную группу.</summary>
        private void SelectAllHangarsOf(Airbase airbase, FactionHQ localHq)
        {
            foreach (Building building in airbase.buildings)
            {
                if (Selected.Count >= Settings.MaxSelected)
                    break;
                if (building == null || building.GetComponent<Hangar>() == null ||
                    Selected.Contains(building))
                    continue;
                if (!GameApi.IsSelectableFriendly(building, localHq))
                    continue;
                Selected.Add(building);
            }
        }

        public Rect DragRectGui
        {
            get
            {
                float x = Mathf.Min(DragStart.x, DragCurrent.x);
                float y = Screen.height - Mathf.Max(DragStart.y, DragCurrent.y);
                return new Rect(x, y, Mathf.Abs(DragCurrent.x - DragStart.x), Mathf.Abs(DragCurrent.y - DragStart.y));
            }
        }
    }
}
