using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Клик-выделение и рамка; пока только своя наземка/флот.</summary>
    public sealed class SelectionService
    {
        private static readonly Dictionary<int, List<Unit>> Groups = new Dictionary<int, List<Unit>>();

        /// <summary>Аэродром, выделенный кликом по его башне/зданию (не Unit).</summary>
        public Airbase SelectedAirbase { get; set; }

        public readonly List<Unit> Selected = new List<Unit>();

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

            if (RouteEditor.Consumed || RouteEditor.IsDragging || BuildTasks.Consumed)
            {
                _leftPressed = false;
                IsDragging = false;
                return; // ЛКМ занят подтверждением приказа или перетаскиванием точки
            }

            // Space+ЛКМ — это вращение камеры, выделение не стартуем.
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

            // Клик по зданию аэродрома = выделение аэродрома (панель покупки авиации);
            // при Shift аэродром не перехватывает — работает режим добавления.
            if (!RtsInput.Shift && hitUnit is Building clickedBuilding &&
                clickedBuilding.Networkairbase != null &&
                clickedBuilding.Networkairbase.CurrentHQ == localHq)
            {
                SelectedAirbase = clickedBuilding.Networkairbase;
                Selected.Clear();
                _lastClickDefinition = null;
                return;
            }
            // Shift+клик = ДОБАВИТЬ В ГРУППУ (режим добавления, SupCom): юниты и здания
            // накапливаются в выделение (двойной клик без shift — как раньше).
            if (RtsInput.Shift && hitUnit != null && GameApi.IsFriendlyUnit(hitUnit, localHq) &&
                (hitUnit is Building || GameApi.IsSelectableFriendly(hitUnit, localHq)))
            {
                if (!Selected.Contains(hitUnit))
                    Selected.Add(hitUnit);
                SelectedAirbase = null;
                _lastClickDefinition = null;
                return;
            }

            SelectedAirbase = null;

            if (hitUnit != null && GameApi.IsSelectableFriendly(hitUnit, localHq))
            {
                LastClickDebug = "выделен: " + hitUnit.definition?.unitName;
                bool doubleClick = Time.unscaledTime - _lastClickTime < Settings.DoubleClickSec
                    && hitUnit.definition == _lastClickDefinition;
                _lastClickTime = Time.unscaledTime;
                _lastClickDefinition = hitUnit.definition;

                Selected.Clear();
                Selected.Add(hitUnit);
                if (doubleClick)
                    SelectAllOfTypeOnScreen(hitUnit.definition, localHq);
            }
            else if (RtsInput.Shift)
            {
                // Shift = режим добавления: клик по пустому месту группу НЕ сбрасывает.
                LastClickDebug = "мимо (shift): выделение сохранено";
            }
            else
            {
                LastClickDebug = "мимо: луч=" + (hitUnit != null ? hitUnit.name : "нет") +
                                 " (выделение сброшено)";
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

        private void BoxSelect(Vector2 a, Vector2 b, bool additive)
        {
            GameApi.TryGetLocalHQ(out FactionHQ localHq);
            var cam = GameApi.MainCam;
            if (cam == null || localHq == null)
                return;

            float minX = Mathf.Min(a.x, b.x), maxX = Mathf.Max(a.x, b.x);
            float minY = Mathf.Min(a.y, b.y), maxY = Mathf.Max(a.y, b.y);

            if (!additive)
                Selected.Clear();
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (Selected.Count >= Settings.MaxSelected)
                    break;
                if (!GameApi.IsSelectableFriendly(unit, localHq))
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z <= 0f)
                    continue;
                if (sp.x >= minX && sp.x <= maxX && sp.y >= minY && sp.y <= maxY &&
                    (additive || !Selected.Contains(unit)))
                    Selected.Add(unit);
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
                if (unit.definition != definition || !GameApi.IsSelectableFriendly(unit, localHq))
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z > 0f && sp.x >= 0f && sp.x <= Screen.width && sp.y >= 0f && sp.y <= Screen.height)
                    Selected.Add(unit);
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
