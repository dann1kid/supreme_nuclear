using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Туман войны (DESIGN §2.15, юзер-решение 2026-11-03, канон SupCom): в режиме
    /// командира 3D-модели неразведанных чужих юнитов гасятся — «банальной
    /// видимостью» (Renderer/Light enabled=false со снапшотом исходных состояний;
    /// НЕ SetActive — он остановил бы AI/NetworkBehaviour, НЕ слои — физика игры
    /// завязана на Default/Statics). Видимость — единый гейт GameApi.
    /// IsVisibleToCommander поверх нативной trackingDatabase: подвижная техника —
    /// живой контакт И опознанная сигнатура, здания — с момента оптической
    /// разведки (статика). ОПОЗНАНИЕ (v0.147, «неопознанные радар-отметки»):
    /// радар-ONLY контакт — безымянная точка без типа/модели (реестр
    /// _identified: оптика опознаёт, сброс при потере контакта — разведанная
    /// сигнатура не теряется, пока контакт жив). Память разведанных зданий:
    /// гибель вне наблюдения не раскрывает себя — иконку держит ghost-реестр до
    /// «переразведки» позиции своими сенсорами.
    /// Свои/нейтралы/ракеты не трогаем; спектатор (нет HQ) — туман не действует.
    /// Только ЛОКАЛЬНАЯ ВИЗУАЛИЗАЦИЯ: игровых действий не выдаёт, состояние не
    /// синхронизирует (сетевые данные игра всё равно реплицирует всем — §12),
    /// host-only не нужен. Тик/Leave — CommanderModeController (активная часть
    /// UpdateSafe), Reset — sceneLoaded.
    /// </summary>
    internal static class FogOfWarService
    {
        private const float ScanInterval = 0.25f;

        /// <summary>Минимальный круг «самоосмотра» вокруг КАЖДОГО своего юнита:
        /// собственная база не тонет в тумане, даже если у юнита нет детекторов
        /// (VisualCheck игры гоняется не для всех типов).</summary>
        private const float MinOwnVisionM = 800f;

        // Погашенные нами компоненты + исходные enabled: восстановление по снапшоту
        // (слепое true включило бы выключенное игрой — форсажные факелы, мигалки).
        private sealed class Shaded
        {
            public Renderer[] Renderers;
            public bool[] RendererOn;
            public Light[] Lights;
            public bool[] LightOn;
        }

        private static readonly Dictionary<Unit, Shaded> _shaded = new Dictionary<Unit, Shaded>();
        private static readonly List<Unit> _pruneShaded = new List<Unit>();
        private static readonly List<Renderer> ShadeRenderers = new List<Renderer>();
        private static readonly List<Light> ShadeLights = new List<Light>();
        private static float _nextScan;

        /// <summary>Призрак разведанного здания (SupCom-память): юнит погиб вне
        /// наблюдения — иконка/блип на последней известной позиции, пока свои
        /// сенсоры не «переразведают» точку. GlobalPosition — правило №1.</summary>
        internal sealed class BuildingGhost
        {
            public GlobalPosition Position;
            public UnitDefinition Definition;
            public FactionHQ Hq;
            public bool WasLive;      // контакт был живым в последний скан (гибель наблюдалась)
            public bool SeenThisScan; // здание живо и разведано в текущем скане
        }

        private static readonly Dictionary<PersistentID, BuildingGhost> _ghosts =
            new Dictionary<PersistentID, BuildingGhost>();
        private static readonly List<PersistentID> _ghostDrop = new List<PersistentID>();

        /// <summary>Иконки-память разведанных зданий (чтение: CommanderHud/MinimapPanel).</summary>
        public static IReadOnlyDictionary<PersistentID, BuildingGhost> BuildingGhosts => _ghosts;

        // Сенсоры текущего скана: источник покрытия для затемнения (FogOverlay)
        // и «переразведки» ghost-позиций. Локальные XZ — внутри сцены точности
        // float хватает (карта — десятки км).
        private static readonly List<Vector2> _sensorPos = new List<Vector2>();
        private static readonly List<float> _sensorRange = new List<float>();

        // ОПТИЧЕСКИЕ сенсоры (только GetVisualRange, без радара) — опознание
        // сигнатур: радар-ONLY контакт остаётся безымянной отметкой (SupCom).
        private static readonly List<Vector2> _visualPos = new List<Vector2>();
        private static readonly List<float> _visualRange = new List<float>();

        // Опознанные оптикой чужие сигнатуры (SupCom: разведанная сигнатура не
        // теряется, пока контакт жив; потеря контакта — сброс, новый радар-ONLY
        // контакт снова безымянный).
        private static readonly HashSet<PersistentID> _identified = new HashSet<PersistentID>();

        /// <summary>Сигнатура ОПОЗНАНА оптикой (полная видимость: модель/иконка/
        /// карточка осмотра)? Ракеты/свои/нейтралы — всегда true.</summary>
        public static bool IsIdentified(Unit unit) =>
            unit == null || unit is Missile || unit.NetworkHQ == null ||
            _identified.Contains(unit.persistentID);

        public static void Tick()
        {
            if (Time.time < _nextScan)
                return;
            _nextScan = Time.time + ScanInterval;

            if (!GameApi.TryGetLocalHQ(out FactionHQ hq))
            {
                // Спектатор/лобби: туман не действует, память разведки не копим.
                RestoreAll();
                _ghosts.Clear();
                _identified.Clear();
                FogOverlay.SetEnabled(false);
                return;
            }

            float now = Time.timeSinceLevelLoad;
            foreach (KeyValuePair<PersistentID, BuildingGhost> kv in _ghosts)
                kv.Value.SeenThisScan = false;

            // Проход 1: собрать сенсоры своих (покрытие затемнения + оптика
            // опознания) — до классификации чужих, опознание сравнивает со ВСЕМИ.
            _sensorPos.Clear();
            _sensorRange.Clear();
            _visualPos.Clear();
            _visualRange.Clear();
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || unit is Missile ||
                    unit.NetworkHQ != hq)
                    continue;
                // ПРОСТРАНСТВО: сетка затемнения и границы карты живут в ТЕКУЩИХ
                // мировых координатах (TryGetMapBounds читает transform.position,
                // переживший сдвиги origin), поэтому сенсор берём transform.position,
                // НЕ GlobalPosition (глобальное пространство разъезжается с миром
                // после первого же сдвига origin — штампы уходили в чужие ячейки,
                // плейтест 2026-11-03 «слой не отражает разведку»).
                Vector3 p = unit.transform.position;
                _sensorPos.Add(new Vector2(p.x, p.z));
                _sensorRange.Add(Mathf.Max(RangeRings.VisionRangeOf(unit), MinOwnVisionM));
                float vis = Mathf.Max(RangeRings.VisualRangeOf(unit), MinOwnVisionM);
                _visualPos.Add(new Vector2(p.x, p.z));
                _visualRange.Add(vis);
            }

            // Проход 2: опознание + классификация + применение.
            bool hideModels = Settings.FogOfWar;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null)
                    continue;
                bool own = unit.NetworkHQ == hq;
                bool ownOrNeutral = own || unit.NetworkHQ == null;
                TrackingInfo info = null;
                if (!ownOrNeutral && !(unit is Missile))
                    info = hq.GetTrackingData(unit.persistentID);
                float age = info != null ? now - info.lastSpottedTime : float.MaxValue;
                bool isBuilding = unit is Building;

                // Опознание: оптика видит цель → сигнатура опознана; контакт
                // потерян (подвижный — не живой, здание — снят) → сброс (новый
                // радар-ONLY контакт снова безымянный).
                if (!ownOrNeutral && !(unit is Missile))
                {
                    if (WithinVisual(unit.transform.position))
                        _identified.Add(unit.persistentID);
                    else if (info == null || (!isBuilding && !GameApi.IsLiveContact(info)))
                        _identified.Remove(unit.persistentID);
                }

                bool visible = Util.ClassifyFog(unit is Missile, ownOrNeutral, isBuilding,
                                   info != null, age, GameApi.ContactLiveSec,
                                   IsIdentified(unit))
                               == Util.FogVisibility.Visible;

                // Память зданий: живо, есть контакт И опознано (неопознанный
                // радар-дет погибшей базы призраком не помнится — мы не знали,
                // ЧТО там стояло).
                if (info != null && !own && isBuilding && _identified.Contains(unit.persistentID))
                    TouchGhost((Building)unit, info);

                if (!visible && hideModels)
                    Shade(unit);
                else if (_shaded.TryGetValue(unit, out Shaded s))
                    Restore(unit, s);
            }

            PruneShaded();
            UpdateGhosts();
            FogOverlay.Update(_sensorPos, _sensorRange);
        }

        /// <summary>Цель в зоне оптики любого своего юнита (геометрия, как у
        /// сетки затемнения — согласовано с вуалью и белыми кольцами).</summary>
        private static bool WithinVisual(Vector3 world)
        {
            for (int i = 0; i < _visualPos.Count; i++)
            {
                float dx = world.x - _visualPos[i].x;
                float dz = world.z - _visualPos[i].y;
                float r = _visualRange[i];
                if (dx * dx + dz * dz <= r * r)
                    return true;
            }
            return false;
        }

        private static void TouchGhost(Building b, TrackingInfo info)
        {
            PersistentID id = b.persistentID;
            if (!_ghosts.TryGetValue(id, out BuildingGhost g))
            {
                g = new BuildingGhost();
                _ghosts.Add(id, g);
            }
            g.Position = info.GetPosition();
            g.Definition = b.definition;
            g.Hq = b.NetworkHQ; // захват чужой стороной меняет владельца записи
            g.WasLive = GameApi.IsLiveContact(info);
            g.SeenThisScan = true;
        }

        /// <summary>Гасим призраки: здание живо-разведано (вернулось в скан),
        /// гибель НАБЛЮДАЛАСЬ (контакт был живым в прошлый скан — видели взрыв),
        /// либо позицию переразвели своими сенсорами, а здания там нет.</summary>
        private static void UpdateGhosts()
        {
            _ghostDrop.Clear();
            foreach (KeyValuePair<PersistentID, BuildingGhost> kv in _ghosts)
            {
                BuildingGhost g = kv.Value;
                if (g.SeenThisScan)
                    continue;
                if (g.WasLive || CoveredBySensors(g.Position))
                    _ghostDrop.Add(kv.Key);
            }
            for (int i = 0; i < _ghostDrop.Count; i++)
                _ghosts.Remove(_ghostDrop[i]);
        }

        private static bool CoveredBySensors(GlobalPosition p)
        {
            // ghost-позиция хранится глобально (№1) — сравнение в текущем мире
            // сенсоров ведём через ToLocalPosition.
            Vector3 w = p.ToLocalPosition();
            for (int i = 0; i < _sensorPos.Count; i++)
            {
                float dx = w.x - _sensorPos[i].x;
                float dz = w.z - _sensorPos[i].y;
                float r = _sensorRange[i];
                if (dx * dx + dz * dz <= r * r)
                    return true;
            }
            return false;
        }

        // ---------- погашение/восстановление компонентов отрисовки ----------

        private static void Shade(Unit unit)
        {
            // Юнит уже скрыт: переподтверждаем (игра могла включить факел/мигалку)
            // и ловим новые компоненты (счётчик изменился — пересобираем снапшот).
            if (_shaded.TryGetValue(unit, out Shaded s))
            {
                Collect(unit);
                if (ShadeRenderers.Count != s.Renderers.Length || ShadeLights.Count != s.Lights.Length)
                    _shaded[unit] = SnapshotAndDisable();
                else
                {
                    for (int i = 0; i < s.Renderers.Length; i++)
                        if (s.Renderers[i] != null)
                            s.Renderers[i].enabled = false;
                    for (int i = 0; i < s.Lights.Length; i++)
                        if (s.Lights[i] != null)
                            s.Lights[i].enabled = false;
                }
                return;
            }
            Collect(unit);
            _shaded[unit] = SnapshotAndDisable();
        }

        private static void Collect(Unit unit)
        {
            ShadeRenderers.Clear();
            ShadeLights.Clear();
            unit.GetComponentsInChildren(true, ShadeRenderers);
            unit.GetComponentsInChildren(true, ShadeLights);
        }

        private static Shaded SnapshotAndDisable()
        {
            var s = new Shaded
            {
                Renderers = ShadeRenderers.ToArray(),
                RendererOn = new bool[ShadeRenderers.Count],
                Lights = ShadeLights.ToArray(),
                LightOn = new bool[ShadeLights.Count],
            };
            for (int i = 0; i < s.Renderers.Length; i++)
            {
                if (s.Renderers[i] == null)
                    continue;
                s.RendererOn[i] = s.Renderers[i].enabled;
                s.Renderers[i].enabled = false;
            }
            for (int i = 0; i < s.Lights.Length; i++)
            {
                if (s.Lights[i] == null)
                    continue;
                s.LightOn[i] = s.Lights[i].enabled;
                s.Lights[i].enabled = false;
            }
            return s;
        }

        private static void Restore(Unit unit, Shaded s)
        {
            for (int i = 0; i < s.Renderers.Length; i++)
                if (s.Renderers[i] != null)
                    s.Renderers[i].enabled = s.RendererOn[i];
            for (int i = 0; i < s.Lights.Length; i++)
                if (s.Lights[i] != null)
                    s.Lights[i].enabled = s.LightOn[i];
            _shaded.Remove(unit);
        }

        /// <summary>Мёртвые (fake-null) юниты из реестра скрытия убираем: их
        /// компоненты уже уничтожены сценой, восстанавливать нечего.</summary>
        private static void PruneShaded()
        {
            if (_shaded.Count == 0)
                return;
            _pruneShaded.Clear();
            foreach (KeyValuePair<Unit, Shaded> kv in _shaded)
                if (kv.Key == null)
                    _pruneShaded.Add(kv.Key);
            for (int i = 0; i < _pruneShaded.Count; i++)
                _shaded.Remove(_pruneShaded[i]);
        }

        private static void RestoreAll()
        {
            foreach (KeyValuePair<Unit, Shaded> kv in _shaded)
            {
                if (kv.Key == null)
                    continue;
                Shaded s = kv.Value;
                for (int i = 0; i < s.Renderers.Length; i++)
                    if (s.Renderers[i] != null)
                        s.Renderers[i].enabled = s.RendererOn[i];
                for (int i = 0; i < s.Lights.Length; i++)
                    if (s.Lights[i] != null)
                        s.Lights[i].enabled = s.LightOn[i];
            }
            _shaded.Clear();
        }

        /// <summary>Выход из режима командира (инвариант №9: что погасили на входе —
        /// вернули на любом выходе). Память разведанных зданий переживает выход:
        /// разведданные не стёрлись от закрытия стола командира.</summary>
        public static void Leave()
        {
            RestoreAll();
            FogOverlay.SetEnabled(false);
            _nextScan = 0f;
        }

        /// <summary>Сцена умерла — реестры скрытия, призраков, опознаний и сенсоров тоже.</summary>
        public static void Reset()
        {
            RestoreAll();
            _ghosts.Clear();
            _ghostDrop.Clear();
            _identified.Clear();
            _sensorPos.Clear();
            _sensorRange.Clear();
            _visualPos.Clear();
            _visualRange.Clear();
            _nextScan = 0f;
            FogOverlay.Reset();
        }
    }
}
