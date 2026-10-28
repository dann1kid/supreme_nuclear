using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Персистентные шаблоны заказов (DESIGN 2.10): снимок очереди фабрики,
    /// сохраняется в BepInEx/plugins/SupremeNuclear/Templates.json — живут между
    /// сессиями без полноценных сейвов. Позиции хранят jsonKey определения.</summary>
    public static class OrderTemplates
    {
        public sealed class Item
        {
            public string key;
            public int count = 1;
            public float dx; // смещение от первой постройки (шаблоны зданий)
            public float dz;
        }

        public sealed class Template
        {
            public string name;
            public List<Item> items = new List<Item>();
            public string iconKey; // иконка-представитель: самое дорогое в наборе
        }

        private static List<Template> _all;
        private static string FilePath => Path.Combine(Paths.PluginPath, "SupremeNuclear", "Templates.json");

        public static int Count
        {
            get
            {
                EnsureLoaded();
                return _all.Count;
            }
        }

        public static IReadOnlyList<Template> All
        {
            get
            {
                EnsureLoaded();
                return _all;
            }
        }

        /// <summary>Смещения точек относительно первой (чистая функция — в тестах).</summary>
        public static List<Vector2> ComputeOffsets(List<Vector3> positions)
        {
            var offsets = new List<Vector2>(positions.Count);
            for (int i = 0; i < positions.Count; i++)
                offsets.Add(new Vector2(positions[i].x - positions[0].x, positions[i].z - positions[0].z));
            return offsets;
        }

        private static void EnsureLoaded()
        {
            if (_all != null)
                return;
            try
            {
                if (File.Exists(FilePath))
                {
                    _all = JsonConvert.DeserializeObject<List<Template>>(File.ReadAllText(FilePath)) ??
                           new List<Template>();
                    Log.Info("Templates: загружено " + _all.Count);
                    return;
                }
            }
            catch (System.Exception e)
            {
                Log.Error("Templates: ошибка загрузки — " + e.Message);
            }
            _all = new List<Template>();
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(_all, Formatting.Indented));
            }
            catch (System.Exception e)
            {
                Log.Error("Templates: ошибка сохранения — " + e.Message);
            }
        }

        /// <summary>Захват набора ПОСТРОЕК с относительными позициями: выделенные здания
        /// (shift+клик набор) или очередь площадок инженера. Иконка — самое дорогое.</summary>
        public static Template CaptureBuildings(List<(string key, float value, Vector3 pos)> set)
        {
            EnsureLoaded();
            if (set == null || set.Count == 0)
                return null;
            var positions = new List<Vector3>(set.Count);
            foreach (var entry in set)
                positions.Add(entry.pos);
            List<Vector2> offsets = ComputeOffsets(positions);

            var template = new Template { name = Loc.F("Шаблон {0}", _all.Count + 1) };
            string bestKey = set[0].key;
            float bestValue = float.MinValue;
            for (int i = 0; i < set.Count; i++)
            {
                template.items.Add(new Item { key = set[i].key, count = 1, dx = offsets[i].x, dz = offsets[i].y });
                if (set[i].value > bestValue)
                {
                    bestValue = set[i].value;
                    bestKey = set[i].key;
                }
            }
            template.iconKey = bestKey;
            _all.Add(template);
            Save();
            Log.Info("Templates: сохранён '" + template.name + "' (" + template.items.Count + " построек)");
            return template;
        }

        /// <summary>Режим установки шаблона: призраки всех построек у курсора;
        /// ЛКМ строит пригодные, занятые/неудобные пропускаются (Shift — серия).</summary>
        public static (Template template, List<Unit> engineers) PendingTemplate;

        /// <summary>Поставить шаблон инженерами: позиции = центр + сохранённые смещения.
        /// Возвращает число поставленных площадок.</summary>
        public static int PlacePending(Vector3 center, FactionHQ hq)
        {
            if (PendingTemplate.template == null || PendingTemplate.engineers == null ||
                PendingTemplate.engineers.Count == 0)
                return 0;
            var catalog = new Dictionary<string, BuildingDefinition>();
            if (Encyclopedia.i != null && Encyclopedia.i.buildings != null)
                foreach (BuildingDefinition b in Encyclopedia.i.buildings)
                    if (b != null && !string.IsNullOrEmpty(b.jsonKey))
                        catalog[b.jsonKey] = b;

            int placed = 0, skipped = 0;
            foreach (Item item in PendingTemplate.template.items)
            {
                if (!catalog.TryGetValue(item.key, out BuildingDefinition def))
                {
                    skipped++;
                    continue;
                }
                Vector3 site = new Vector3(center.x + item.dx, 0f, center.z + item.dz);
                bool buildable = GameApi.IsSiteBuildable(site.x, site.z) &&
                                 !BuildTasks.IsZoneBlockedAt(site, null);
                if (!buildable)
                {
                    skipped++;
                    continue;
                }
                // Весь набор дописывается в очередь КАЖДОГО инженера пачки (SupCom):
                // группа едет вместе и строит здания шаблона по очереди.
                if (BuildTasks.Place(def, site, PendingTemplate.engineers, hq))
                    placed++;
                else
                    skipped++;
            }
            CommanderHud.Notice(Loc.F("{0}: площадок {1}{2}", PendingTemplate.template.name, placed,
                skipped > 0 ? Loc.F(", пропущено {0}", skipped) : ""));
            Log.Info("Templates: размещён '" + PendingTemplate.template.name + "' — " + placed +
                     " поставлено, " + skipped + " пропущено");
            return placed;
        }

        public static Template Capture(object factory)
        {
            EnsureLoaded();
            var template = new Template { name = Loc.F("Шаблон {0}", _all.Count + 1) };
            foreach (ProductionTasks.QueueItem item in ProductionTasks.All)
            {
                if (item.UnitDef == null)
                    continue;
                // Аэродром-агрегат (вышка/авианосец): позиции живут пер-ангарно —
                // снимаем очередь ВСЕХ его ангаров, не пустышку самого Airbase.
                object f = item.Factory;
                bool ours = f == factory ||
                    (factory is Airbase airbase && f is Hangar hangar &&
                     hangar.parentAirbase == airbase);
                if (!ours)
                    continue;
                Item existing = template.items.FirstOrDefault(i => i.key == item.UnitDef.jsonKey);
                if (existing != null)
                    existing.count++;
                else
                    template.items.Add(new Item { key = item.UnitDef.jsonKey, count = 1 });
            }
            if (template.items.Count == 0)
                return null;
            // Иконка-представитель: самое дорогое определение набора.
            string bestKey = null;
            float bestValue = float.MinValue;
            if (Encyclopedia.i != null && Encyclopedia.i.vehicles != null)
                foreach (VehicleDefinition v in Encyclopedia.i.vehicles)
                    if (v != null && template.items.Exists(i => i.key == v.jsonKey) && v.value > bestValue)
                    {
                        bestValue = v.value;
                        bestKey = v.jsonKey;
                    }
            template.iconKey = bestKey ?? template.items[0].key;
            _all.Add(template);
            Save();
            Log.Info("Templates: сохранён '" + template.name + "' (" + template.items.Count + " позиций)");
            return template;
        }

        /// <summary>Смещения шаблона (для призраков).</summary>
        public static List<(string key, float dx, float dz)> OffsetsOf(Template template)
        {
            var result = new List<(string, float, float)>();
            if (template == null)
                return result;
            foreach (Item item in template.items)
                result.Add((item.key, item.dx, item.dz));
            return result;
        }

        /// <summary>Поставить шаблон в очередь фабрики (по jsonKey: техника и авиация).</summary>
        public static int Enqueue(Template template, object factory)
        {
            if (template == null || factory == null || Encyclopedia.i == null)
                return 0;
            int enqueued = 0;
            foreach (Item item in template.items)
            {
                UnitDefinition def = null;
                if (Encyclopedia.i.vehicles != null)
                    foreach (VehicleDefinition v in Encyclopedia.i.vehicles)
                        if (v != null && v.jsonKey == item.key)
                        {
                            def = v;
                            break;
                        }
                if (def == null && Encyclopedia.i.aircraft != null)
                    foreach (AircraftDefinition a in Encyclopedia.i.aircraft)
                        if (a != null && a.jsonKey == item.key)
                        {
                            def = a;
                            break;
                        }
                if (def == null)
                {
                    Log.Warn("Templates: в каталоге нет '" + item.key + "' — пропущено");
                    continue;
                }
                int count = Mathf.Max(1, item.count);
                for (int n = 0; n < count; n++)
                {
                    if (def is VehicleDefinition vd && factory is VehicleDepot depot)
                        ProductionTasks.EnqueueGround(depot, vd);
                    else if (def is AircraftDefinition ad && factory is Airbase airbase)
                    {
                        if (!ProductionTasks.EnqueueAirDistributed(airbase, ad))
                            break;
                    }
                    else if (factory is BuildTasks.Task site)
                    {
                        // Недострой-фабрика: неподходящий роду площадки позиции
                        // ПРОПУСКАЕМ (смешанный набор), а не рвём шаблон.
                        if (!ProductionTasks.EnqueueSite(site, def))
                            break;
                    }
                    else
                        break; // тип фабрики не подходит под позицию
                    enqueued++;
                }
            }
            return enqueued;
        }
    }
}
