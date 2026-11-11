using System.Collections.Generic;
using UnityEngine;


namespace SupremeNuclear
{
    // Часть CommanderHud: спрайты юнитов, HP-бары, строй-маркеры, стратегические/вражеские иконки.
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; OnGUI-диспетчер — CommanderHud.cs.
    public sealed partial class CommanderHud
    {
        // ---------- панели ----------





        // ---------- иконки ----------

        /// <summary>ЕДИНАЯ отрисовка спрайта-иконки юнита (консолидация v0.48):
        /// аспект сохранён, тень опциональна. Использовать во всех иконочных циклах
        /// вместо копий uv/aspect/shadow-кода.</summary>
        private static void DrawUnitSprite(Vector2 center, float sizePx, Sprite sprite,
            Color tint, bool withShadow)
        {
            if (sprite == null || sprite.texture == null)
            {
                DrawRect(new Rect(center.x - sizePx * 0.5f, center.y - sizePx * 0.5f, sizePx, sizePx), tint);
                return;
            }
            var t = sprite.texture;
            var uv = new Rect(
                sprite.textureRect.x / t.width, sprite.textureRect.y / t.height,
                sprite.textureRect.width / t.width, sprite.textureRect.height / t.height);
            float aspect = sprite.textureRect.width / Mathf.Max(sprite.textureRect.height, 1f);
            float w = Mathf.Min(sizePx * aspect, sizePx * 2.4f);
            var r = new Rect(center.x - w * 0.5f, center.y - sizePx * 0.5f, w, sizePx);
            Color prev = GUI.color;
            if (withShadow)
            {
                GUI.color = new Color(0f, 0f, 0f, tint.a > 0.7f ? 0.55f : tint.a);
                GUI.DrawTextureWithTexCoords(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), t, uv);
            }
            GUI.color = tint;
            GUI.DrawTextureWithTexCoords(r, t, uv);
            GUI.color = prev;
        }

        /// <summary>Точечный мировой элемент под окном миникарты? IMGUI рисуется ПОВЕРХ
        /// uGUI-окна — без маски экранные иконки мира штампуются на миникарту и
        /// смешиваются с её блипами (позиция — в GUI-координатах, y сверху).</summary>
        private static bool UnderMinimapWindow(Vector2 guiPos)
        {
            Rect m = MinimapPanel.HudMaskRect;
            return m.width > 1f && m.height > 1f && m.Contains(guiPos);
        }

        /// <summary>HP-полоски над повреждёнными своими юнитами (доля HP — из кэша GameApi,
        /// агрегат по деталям; рисуются только у повреждённых, цвет зелёный→жёлтый→красный).</summary>
        private static void DrawHpBars()
        {
            Camera cam = GameApi.MainCam;
            if (cam == null || !GameApi.TryGetLocalHQ(out FactionHQ hq))
                return;
            float k = Screen.height / 1440f;
            float w = 26f * k;
            float h = 3f * k;
            int drawn = 0;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (drawn > Settings.IconMaxUnits)
                    break;
                if (unit == null || unit.disabled || !GameApi.IsFriendlyUnit(unit, hq))
                    continue;
                float hp = GameApi.HpFraction(unit);
                if (hp >= 0.995f)
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z <= 0f || sp.x < -30f || sp.x > Screen.width + 30f || sp.y < -30f || sp.y > Screen.height + 30f)
                    continue;
                float guiY = Screen.height - sp.y;
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;
                var bg = new Rect(sp.x - w * 0.5f, guiY - 14f * k, w, h);
                DrawRect(bg, new Color(0f, 0f, 0f, 0.6f));
                Color c = hp > 0.5f
                    ? new Color(0.4f, 1f, 0.5f, 0.95f)
                    : (hp > 0.25f ? new Color(1f, 0.8f, 0.3f, 0.95f) : new Color(1f, 0.35f, 0.3f, 0.95f));
                DrawRect(new Rect(bg.x, bg.y, bg.width * hp, h), c);
                drawn++;
            }
        }

        /// <summary>Маркеры Task-API: площадки стройки (рамка + заливка прогресса) и
        /// призрак устанавливаемого здания под курсором.</summary>
        private static void DrawBuildMarkers()
        {
            Camera cam = GameApi.MainCam;
            if (cam == null || !GameApi.TryGetLocalHQ(out FactionHQ hq))
                return;
            float k = Screen.height / 1440f;
            Color c = new Color(0.4f, 0.9f, 1f, 0.95f);

            // Превью ЛИНИИ постановки (Ctrl+drag, v0.171): кресты в точках копий.
            if (BuildTasks.PendingLine && BuildTasks.LinePreview.Count > 0)
            {
                Color lc = new Color(0.5f, 1f, 0.6f, 0.9f);
                foreach (Vector3 p in BuildTasks.LinePreview)
                {
                    Vector3 g = p;
                    g.y = GameApi.GroundY(g.x, g.z);
                    Vector3 sp = cam.WorldToScreenPoint(g + Vector3.up * 6f);
                    if (sp.z <= 0f)
                        continue;
                    DrawCross(new Vector2(sp.x, Screen.height - sp.y), 7f * k, lc);
                }
            }

            // Маркеры очередей только СВОИХ площадок: AI строит через тот же
            // BuildTasks, чужие рамки/прогресс игроку не видны (v0.143.8).
            foreach (BuildTasks.Task t in BuildTasks.All)
            {
                if (t.Faction != hq)
                    continue;
                Vector3 site = t.Site.ToLocalPosition();
                site.y = GameApi.GroundY(site.x, site.z);
                Vector3 sp = cam.WorldToScreenPoint(site + Vector3.up * 6f);
                if (sp.z <= 0f || sp.x < -60f || sp.x > Screen.width + 60f || sp.y < -60f || sp.y > Screen.height + 60f)
                    continue;
                float gy = Screen.height - sp.y;
                float s = 34f * k;
                var r = new Rect(sp.x - s * 0.5f, gy - s * 0.5f, s, s);
                DrawBoxOutline(r, 2f * k, t.ZoneBusy ? new Color(1f, 0.5f, 0.35f, 0.95f) : c);
                if (t.ZoneBusy)
                    GUI.Label(new Rect(sp.x - 140f, gy + s * 0.5f + 2f, 280f, 18f * k),
                        Loc.Tr("зона не расчищена"), _small);
                float p = t.Progress01;
                var fill = new Rect(r.x + 3f * k, r.yMax - 3f * k - (r.height - 6f * k) * p,
                    r.width - 6f * k, (r.height - 6f * k) * p);
                DrawRect(fill, new Color(c.r, c.g, c.b, 0.28f));
                GUI.Label(new Rect(sp.x - 140f, gy - s * 0.5f - 20f * k, 280f, 18f * k),
                    Loc.F("{0} {1}",
                        t.Definition != null ? t.Definition.unitName : t.IsRunway ? Loc.Tr("ВПП") : "?",
                        p.ToString("P0")), _small);
            }

            // Призраки шаблона: весь набор у курсора с сохранёнными смещениями;
            // красный = точка не пригодна (вода/склон) или зона занята.
            var tpl = OrderTemplates.PendingTemplate.template;
            if (tpl != null && !RtsInput.PointerOverUi &&
                GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 tplGround))
            {
                foreach ((string key, float dx, float dz) in OrderTemplates.OffsetsOf(tpl))
                {
                    Vector3 site = new Vector3(tplGround.x + dx, 0f, tplGround.z + dz);
                    site.y = GameApi.GroundY(site.x, site.z);
                    bool ok = GameApi.IsSiteBuildable(site.x, site.z) &&
                              !BuildTasks.IsZoneBlockedAt(site, null);
                    Vector3 tsp = cam.WorldToScreenPoint(site + Vector3.up * 6f);
                    if (tsp.z <= 0f)
                        continue;
                    float tgy = Screen.height - tsp.y;
                    Color tc = ok ? new Color(0.4f, 0.9f, 1f, 0.95f) : new Color(1f, 0.45f, 0.3f, 0.95f);
                    float fw2 = 24f * k;
                    DrawBoxOutline(new Rect(tsp.x - fw2 * 0.5f, tgy - fw2 * 0.5f, fw2, fw2), 1.5f * k, tc);
                    DrawCross(new Vector2(tsp.x, tgy), 14f * k, tc);
                }
                Vector3 csp = cam.WorldToScreenPoint(tplGround + Vector3.up * 6f);
                if (csp.z > 0f)
                    GUI.Label(new Rect(csp.x - 220f, Screen.height - csp.y + 16f * k, 440f, 18f * k),
                        Loc.F("{0} · ЛКМ — построить (Shift — серия) · ПКМ — отмена", tpl.name), _small);
            }

            if (BuildTasks.Pending == null || RtsInput.PointerOverUi)
                return;
            if (!GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 ground))
                return;
            Vector3 gsp = cam.WorldToScreenPoint(ground + Vector3.up * 6f);
            if (gsp.z <= 0f)
                return;
            float ggy = Screen.height - gsp.y;
            // Радиус зоны стройки: честный мир→px (как у колец выделения).
            float pxPerRad = Screen.height / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            float dist = Mathf.Max(Vector3.Distance(cam.transform.position, ground), 1f);
            float r0 = Settings.BuildSiteRadiusM / dist * pxPerRad;
            r0 = Mathf.Clamp(r0, 18f * k, Screen.height * 0.6f);
            bool zoneBlocked = BuildTasks.IsZoneBlockedAt(ground, null);
            Color gc = zoneBlocked ? new Color(1f, 0.45f, 0.3f, 0.95f) : c;
            for (int i = 0; i < 32; i++)
            {
                float a1 = i / 32f * Mathf.PI * 2f;
                Vector2 p1 = new Vector2(gsp.x + Mathf.Cos(a1) * r0, ggy + Mathf.Sin(a1) * r0);
                DrawRect(new Rect(p1.x - 1.5f, p1.y - 1.5f, 3f, 3f), gc);
            }
            // Футпринт здания (упрощённо ~24 м).
            float fw = 24f / dist * pxPerRad;
            fw = Mathf.Clamp(fw, 8f * k, 200f);
            DrawBoxOutline(new Rect(gsp.x - fw * 0.5f, ggy - fw * 0.5f, fw, fw), 1.5f * k, gc);
            DrawCross(new Vector2(gsp.x, ggy), 26f * k, gc);
            GUI.Label(new Rect(gsp.x - 220f, ggy + 16f * k, 440f, 18f * k),
                Loc.F("{0} — {1} · ЛКМ — поставить (удержание — поворот) · ПКМ — отмена",
                    BuildTasks.Pending.unitName, BuildTasks.Pending.value.ToString("F0")), _small);
        }

        private static bool IsExtractor(Unit unit) => EconomyService.IsExtractor(unit);

        /// <summary>SupCom-стиль: юниты-точки рисуются 2D-иконками. Свои — визорные маркеры,
        /// авиация — ориентированные по курсу силуэты, свои экстракторы — зелёные.</summary>
        private static readonly HashSet<Unit> IconSelected = new HashSet<Unit>();

        private static void DrawStrategicIcons(CommanderModeController controller)
        {
            Camera cam = GameApi.MainCam;
            if (cam == null || !GameApi.TryGetLocalHQ(out FactionHQ hq))
                return;

            // База от ВЫСОТЫ ЭКРАНА (пропорция FHD/1440p/4K одинакова) × множитель
            // WorldIconScale: FA-иконки несут ~30% полей в клетке, канон FA —
            // иконка ~32px на FHD при полном отдалении (юзер: «вопросики» на 20px).
            float px = Settings.IconSizePx * (Screen.height / 1440f) * Settings.WorldIconScale;
            var selected = IconSelected; // без new HashSet на каждый Repaint
            selected.Clear();
            selected.UnionWith(controller.Selection.Selected);
            int drawn = 0;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (drawn > Settings.IconMaxUnits)
                    break;
                if (!GameApi.IsFriendlyUnit(unit, hq) ||
                    !(unit is GroundVehicle || unit is Ship || unit is Aircraft || unit is Building))
                    continue;
                if (!Settings.IconsAlwaysOn && GameApi.ApparentSizePx(unit, cam) >= Settings.IconMinPx)
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z <= 0f || sp.x < -60f || sp.x > Screen.width + 60f || sp.y < -60f || sp.y > Screen.height + 60f)
                    continue;
                float guiY = Screen.height - sp.y;
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;
                bool isSelected = selected.Contains(unit);
                float size = unit is Ship ? px * 1.4f : (unit is Building ? px * 1.3f : (unit is Aircraft ? px * 1.15f : px));

                // Стратегическая иконка SupCom FA (канон мира, v0.153) — одна на
                // все типы; фолбэки: 3D-превью → плоский спрайт; авиация без
                // иконок — ориентированный силуэт mapIcon (виден курс).
                Sprite strategic = unit.definition != null
                    ? (StrategicIcons.Of(unit) ?? UnitPreview.PreviewOf(unit.definition))
                    : null;
                if (strategic == null && unit is Aircraft && unit.definition != null &&
                    unit.definition.mapOrient && unit.definition.mapIcon != null)
                {
                    float ang = HeadingScreenAngle(unit, cam);
                    Color tint = isSelected ? new Color(0.7f, 1f, 0.7f, 1f) : new Color(0.45f, 0.9f, 0.5f, 1f);
                    DrawOrientedSprite(new Vector2(sp.x + 1.5f, guiY + 1.5f), size, unit.definition.mapIcon, new Color(0f, 0f, 0f, 0.55f), ang);
                    DrawOrientedSprite(new Vector2(sp.x, guiY), size, unit.definition.mapIcon, tint, ang);
                    if (isSelected)
                        DrawBoxOutline(new Rect(sp.x - size * 0.9f, guiY - size * 0.9f, size * 1.8f, size * 1.8f), 1.5f, new Color(0.6f, 1f, 0.6f, 1f));
                    drawn++;
                    continue;
                }

                Sprite sprite = strategic ?? (unit.definition != null
                    ? (unit.definition.friendlyIcon ?? unit.definition.mapIcon)
                    : null);
                Color friendTint = isSelected ? new Color(0.7f, 1f, 0.7f, 1f) : new Color(0.45f, 0.9f, 0.5f, 1f);
                DrawUnitSprite(new Vector2(sp.x, guiY), size, sprite, friendTint, true);
                // ТИР-чип инженеров (юзер-канон 2026-11-04 «в SupCom это разные
                // юниты»): T2/T3 — цветная метка под иконкой, в мире видно
                // различие без всматривания в плитки очереди.
                if (GameApi.IsEngineer(unit))
                {
                    int engTier = EngineerService.TierOf(unit.definition);
                    if (engTier > 1)
                    {
                        Color prev = GUI.color;
                        GUI.color = engTier >= 3
                            ? new Color(1f, 0.55f, 0.25f, 1f)   // T3 — оранжевый
                            : new Color(1f, 0.85f, 0.3f, 1f);    // T2 — жёлтый
                        GUI.Label(new Rect(sp.x - 14f, guiY + size * 0.5f + 1f, 28f, 13f),
                            "T" + engTier, _small);
                        GUI.color = prev;
                    }
                }
                if (isSelected)
                    DrawBoxOutline(new Rect(sp.x - size * 0.7f - 4f, guiY - size * 0.5f - 4f,
                        size * 1.4f + 8f, size + 8f), 1.5f, new Color(0.6f, 1f, 0.6f, 1f));
                drawn++;
            }

            DrawAirbaseDiamonds(cam, hq, px);
            DrawHostileIcons(cam, hq, px);
            DrawMissileWarnings(cam, hq, px);
        }

        /// <summary>Аэродромы — ромбы: свой зелёный; чужой красный ТОЛЬКО под разведкой,
        /// иначе жёлтый «неизвестно» (нейтральные тоже жёлтые — владельца не видно).</summary>
        private static void DrawAirbaseDiamonds(Camera cam, FactionHQ hq, float px)
        {
            if (FactionRegistry.airbaseLookup == null)
                return;
            float d = px * 1.7f;
            foreach (Airbase airbase in FactionRegistry.airbaseLookup.Values)
            {
                if (airbase == null)
                    continue;
                Vector3 pos = airbase.transform.position;
                Vector3 sp = cam.WorldToScreenPoint(new Vector3(pos.x, GameApi.GroundY(pos.x, pos.z) + 20f, pos.z));
                if (sp.z <= 0f || sp.x < -60f || sp.x > Screen.width + 60f || sp.y < -60f || sp.y > Screen.height + 60f)
                    continue;
                float gx = sp.x;
                float gy = Screen.height - sp.y;
                if (UnderMinimapWindow(new Vector2(gx, gy)))
                    continue;
                FactionHQ owner = airbase.CurrentHQ;
                Color c = owner == hq
                    ? new Color(0.45f, 0.95f, 0.5f, 0.95f)
                    : owner != null && GameApi.IsAirbaseScouted(airbase, hq)
                        ? new Color(1f, 0.35f, 0.3f, 0.95f)
                        : new Color(1f, 0.85f, 0.2f, 0.95f);
                // Ромб.
                var pts = new[]
                {
                    new Vector2(gx, gy - d * 0.6f), new Vector2(gx + d * 0.45f, gy),
                    new Vector2(gx, gy + d * 0.6f), new Vector2(gx - d * 0.45f, gy)
                };
                for (int i = 0; i < 4; i++)
                {
                    Vector2 a1 = pts[i];
                    Vector2 a2 = pts[(i + 1) % 4];
                    int dots = Mathf.Clamp((int)(Vector2.Distance(a1, a2) / 8f), 1, 12);
                    for (int k = 0; k <= dots; k++)
                    {
                        Vector2 p = Vector2.Lerp(a1, a2, k / (float)dots);
                        DrawRect(new Rect(p.x - 1.2f, p.y - 1.2f, 2.4f, 2.4f), c);
                    }
                }
                DrawRect(new Rect(gx - 2f, gy - 2f, 4f, 4f), c);
                // Тумблеры аэродрома (DESIGN §2.11b): своё закрытие видно на ромбе —
                // внизу метки «посадка закрыта»/«взлёт закрыт» (только свои, не разведка).
                if (owner == hq)
                {
                    int line = 0;
                    if (!AirbaseAccess.IsLandingOpen(airbase))
                        ClosedLabel(gx, gy + d * 0.7f + line++ * 14f, Loc.Tr("посадка закрыта"));
                    if (!AirbaseAccess.IsTakeoffOpen(airbase))
                        ClosedLabel(gx, gy + d * 0.7f + line * 14f, Loc.Tr("взлёт закрыт"));
                }
            }
        }

        /// <summary>Цвет метки закрытого режима аэродрома (тумблеры §2.11b).</summary>
        private static readonly Color ClosedTint = new Color(1f, 0.6f, 0.15f, 0.95f);

        /// <summary>Подпись под ромбом аэродрома: стиль возвращаем (общий _small).</summary>
        private static void ClosedLabel(float cx, float y, string text)
        {
            var prevColor = _small.normal.textColor;
            var prevAlign = _small.alignment;
            _small.normal.textColor = ClosedTint;
            _small.alignment = TextAnchor.MiddleCenter;
            GUI.Label(new Rect(cx - 70f, y, 140f, 16f), text, _small);
            _small.normal.textColor = prevColor;
            _small.alignment = prevAlign;
        }

        /// <summary>Чужие юниты и здания — по разведке, бинарно (канон SupCom,
        /// DESIGN §2.15): опознанная оптикой сигнатура — полная иконка (подвижная
        /// техника — пока контакт жив, здания — пока живы, приглушённым красным
        /// «в памяти»); радар-ONLY контакт — БЕЗЫМЯННАЯ отметка-точка без типа/
        /// владельца (v0.147); протухших «серых тающих» нет; погибшее вне
        /// наблюдения здание держит ghost-иконка (FogOfWarService) до
        /// переразведки. У статики размер иконки не отключается по apparent-size
        /// — это стратегический слой.</summary>
        private static void DrawHostileIcons(Camera cam, FactionHQ hq, float px)
        {
            int drawn = 0;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (drawn > Settings.IconMaxUnits)
                    break;
                if (unit.NetworkHQ == null || unit.NetworkHQ == hq)
                    continue;
                if (!(unit is GroundVehicle || unit is Ship || unit is Aircraft || unit is Building))
                    continue;
                bool isStatic = unit is Building;

                TrackingInfo info = hq.GetTrackingData(unit.persistentID);
                Util.ContactBlip blip = Util.ClassifyBlip(isMissile: false, ownOrNeutral: false,
                    isBuilding: isStatic, hasContact: info != null,
                    contactAgeSec: info != null ? Time.timeSinceLevelLoad - info.lastSpottedTime : float.MaxValue,
                    liveWindowSec: GameApi.ContactLiveSec,
                    identified: FogOfWarService.IsIdentified(unit));
                if (blip == Util.ContactBlip.None)
                    continue;

                Vector3 staleAnchor = info.GetPosition().ToLocalPosition() + Vector3.up * (unit.maxRadius * 0.7f);
                Vector3 sp = cam.WorldToScreenPoint(staleAnchor);
                if (sp.z <= 0f || sp.x < -60f || sp.x > Screen.width + 60f || sp.y < -60f || sp.y > Screen.height + 60f)
                    continue;
                float guiY = Screen.height - sp.y;
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;

                // Безымянная радар-отметка: маленькая тускло-красная точка без
                // спрайта/типа — «что-то там есть» (SupCom unidentified blip).
                if (blip == Util.ContactBlip.Anonymous)
                {
                    DrawUnitSprite(new Vector2(sp.x, guiY), px * 0.6f, null,
                        new Color(0.9f, 0.5f, 0.4f, 0.85f), false);
                    drawn++;
                    continue;
                }

                if (!isStatic && !Settings.IconsAlwaysOn && GameApi.ApparentSizePx(unit, cam) >= Settings.IconMinPx)
                    continue;
                float size = isStatic ? px * 1.3f : (unit is Ship ? px * 1.4f : px);
                // Живой контакт — яркий красный; здание «в памяти» (разведано,
                // но вне обзора сейчас) — приглушённый: стоим по последней разведке.
                Color tint = GameApi.IsLiveContact(info)
                    ? new Color(1f, 0.35f, 0.3f, 1f)
                    : new Color(1f, 0.35f, 0.3f, 0.75f);

                Sprite strategic = unit.definition != null
                    ? (StrategicIcons.Of(unit) ?? UnitPreview.PreviewOf(unit.definition))
                    : null;
                if (strategic == null && unit is Aircraft && unit.definition != null &&
                    unit.definition.mapOrient && unit.definition.mapIcon != null)
                {
                    // Фолбэк авиации без иконок: ориентированный силуэт (виден курс).
                    float ang = HeadingScreenAngle(unit, cam);
                    DrawOrientedSprite(new Vector2(sp.x + 1.5f, guiY + 1.5f), size, unit.definition.mapIcon, new Color(0f, 0f, 0f, 0.5f), ang);
                    DrawOrientedSprite(new Vector2(sp.x, guiY), size, unit.definition.mapIcon, tint, ang);
                    drawn++;
                    continue;
                }

                Sprite sprite = strategic ?? (unit.definition != null
                    ? (unit.definition.hostileIcon ?? unit.definition.friendlyIcon)
                    : null);
                DrawUnitSprite(new Vector2(sp.x, guiY), size, sprite, tint, true);
                drawn++;
            }

            DrawGhostBuildings(cam, hq, px);
        }

        /// <summary>Призраки разведанных зданий (SupCom-память): здание погибло
        /// ВНЕ наблюдения — иконка на последней известной позиции с крестиком
        /// «не подтверждено», пока сенсоры не переразведают точку.</summary>
        private static void DrawGhostBuildings(Camera cam, FactionHQ hq, float px)
        {
            if (FogOfWarService.BuildingGhosts.Count == 0)
                return;
            float size = px * 1.3f;
            Color tint = new Color(0.85f, 0.45f, 0.4f, 0.8f);
            foreach (KeyValuePair<PersistentID, FogOfWarService.BuildingGhost> kv in FogOfWarService.BuildingGhosts)
            {
                FogOfWarService.BuildingGhost g = kv.Value;
                if (g == null || g.Hq == hq)
                    continue; // своё здание (захвачено) — не призрак
                Vector3 anchor = g.Position.ToLocalPosition() + Vector3.up * 20f;
                Vector3 sp = cam.WorldToScreenPoint(anchor);
                if (sp.z <= 0f || sp.x < -60f || sp.x > Screen.width + 60f || sp.y < -60f || sp.y > Screen.height + 60f)
                    continue;
                float guiY = Screen.height - sp.y;
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;
                Sprite sprite = g.Definition != null
                    ? (StrategicIcons.OfDef(g.Definition) ?? UnitPreview.PreviewOf(g.Definition)
                        ?? g.Definition.hostileIcon ?? g.Definition.friendlyIcon)
                    : null;
                DrawUnitSprite(new Vector2(sp.x, guiY), size, sprite, tint, true);
                DrawCross(new Vector2(sp.x, guiY + size * 0.7f), size * 0.5f, tint);
            }
        }

        private static void DrawMissileWarnings(Camera cam, FactionHQ hq, float px)
        {
            float phase = Mathf.Repeat(Time.unscaledTime * 2.5f, 1f);
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (!(unit is Missile))
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z <= 0f || sp.x < -30f || sp.x > Screen.width + 30f || sp.y < -30f || sp.y > Screen.height + 30f)
                    continue;
                float guiY = Screen.height - sp.y;
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;

                bool friendly = unit.NetworkHQ != null && unit.NetworkHQ == hq;
                if (!friendly && phase > 0.7f)
                    continue;

                Color tint = friendly
                    ? new Color(0.5f, 0.9f, 0.55f, 0.95f)
                    : new Color(1f, 0.25f, 0.2f, 1f);
                float size = px * 0.9f;

                Sprite sprite = unit.definition != null
                    ? (unit.definition.hostileIcon != null ? unit.definition.hostileIcon : unit.definition.friendlyIcon)
                    : null;
                DrawUnitSprite(new Vector2(sp.x, guiY), size, sprite, tint, false);
            }
        }

        private static float HeadingScreenAngle(Unit unit, Camera cam)
        {
            Vector3 f = unit.transform.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-4f)
                return 0f;
            f.Normalize();
            float yaw = cam.transform.eulerAngles.y * Mathf.Deg2Rad;
            float cx = f.x * Mathf.Cos(yaw) - f.z * Mathf.Sin(yaw);
            float cz = f.x * Mathf.Sin(yaw) + f.z * Mathf.Cos(yaw);
            return Mathf.Atan2(cx, cz) * Mathf.Rad2Deg;
        }

        private static void DrawOrientedSprite(Vector2 center, float size, Sprite sprite, Color tint, float angleDeg)
        {
            if (sprite == null || sprite.texture == null)
                return;
            var t = sprite.texture;
            var uv = new Rect(
                sprite.textureRect.x / t.width, sprite.textureRect.y / t.height,
                sprite.textureRect.width / t.width, sprite.textureRect.height / t.height);
            float aspect = sprite.textureRect.width / Mathf.Max(sprite.textureRect.height, 1f);
            float w = Mathf.Min(size * aspect, size * 4f);
            var r = new Rect(center.x - w * 0.5f, center.y - size * 0.5f, w, size);

            Matrix4x4 prevMatrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(angleDeg, r.center);
            Color prevColor = GUI.color;
            GUI.color = tint;
            GUI.DrawTextureWithTexCoords(r, t, uv);
            GUI.color = prevColor;
            GUI.matrix = prevMatrix;
        }
    }
}
