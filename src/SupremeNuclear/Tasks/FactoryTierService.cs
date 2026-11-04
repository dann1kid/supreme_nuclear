using System.Collections.Generic;
using Newtonsoft.Json;
using NuclearOption;
using NuclearOption.Networking;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Тиры фабрик (канон SupCom, §2.3а/§2.13): фабрика ставится T1 и растёт
    /// апгрейдом НА МЕСТЕ; с v0.136 апгрейд — ПОЗИЦИЯ ОЧЕРЕДИ ProductionTasks
    /// (голова, занимает производство), этот сервис — только реестр тиров и цен
    /// (`Production/factory.json`). Тир фабрики рулит каталогом выпуска
    /// (CanProduce). Владельцы тира: депо (техника), верфь (флот), АЭРОДРОМ
    /// целиком (авиация: все его ангары наследуют — канон «фабрика одна»;
    /// авианосцы тем же путём через attached-airbase), площадка-недострой
    /// (AdoptSite переносит тир на живую здание). Ключи те же, что у
    /// ProductionTasks.Factories/Rally. Host-only; сцены состояние не переживает
    /// (Reset в sceneLoaded, §4).
    /// </summary>
    internal static class FactoryTierService
    {
        private sealed class FactoryProfile
        {
            public string comment = "Апгрейды фабрик (T1→T2→T3 наливом): масса/энергия/секунды. " +
                                    "Одна таблица на все виды фабрик (депо/аэродром/верфь). " +
                                    "HQ-модель (канон FAF, юзер 2026-11-03): ШТАБ = полная цена, " +
                                    "полный ростер, независим; дешёвая версия = hqSupportFactor цены, " +
                                    "полный ростер только пока жив HQ тира, иначе — только инженеры.";
            public bool enabled = true;
            public float tier2Mass = 1500f, tier2Energy = 800f, tier2Seconds = 60f;
            public float tier3Mass = 4000f, tier3Energy = 2000f, tier3Seconds = 120f;
            public float hqSupportFactor = 0.6f; // цена дешёвой версии от HQ-цены
        }

        private static readonly Dictionary<object, int> _tiers = new Dictionary<object, int>();
        // ШТАБ-тиры фабрики (до какого тира фабрика — HQ): нет записи — все дешёвые.
        // Канон FAF: HQ дорог, независим и держит ростер ДРУГИМ дешёвым фабрикам
        // тира; потеря всех HQ тира → дешёвые строят только инженеров.
        private static readonly Dictionary<object, int> _hqTiers = new Dictionary<object, int>();
        private static FactoryProfile _profile;
        private static float _nextPrune;

        /// <summary>Версия состояния (старт/отмена/завершение апгрейда): UI
        /// кладёт в хэш пересборки каталогов.</summary>
        public static int Version { get; private set; }

        private static bool IsHost
        {
            get
            {
                var net = NetworkManagerNuclearOption.i;
                return net != null && net.Server != null && net.Server.Active;
            }
        }

        /// <summary>Ключ тира — САМА фабрика: юзер-канон 2026-11-03 «раздели
        /// каждую фабрику»: каждый ангар (вертолётная площадка в том числе) —
        /// ОТДЕЛЬНАЯ фабрика со своим тиром/веткой/HQ, без склейки через
        /// аэродром. Ключи зеркальны ProductionTasks; публичен для очереди
        /// (EnqueueUpgrade/UpgradeItemOf).</summary>
        public static object KeyOfFactory(object factory) => factory;

        /// <summary>Тир фабрики (1..3; нет записи — 1). Ангар спрашивает тир
        /// своего аэродрома; площадка-недострой — свой (Adopt перенесёт).</summary>
        public static int TierOf(object factory)
        {
            object key = KeyOfFactory(factory);
            return key != null && _tiers.TryGetValue(key, out int tier)
                ? Mathf.Clamp(tier, 1, 3)
                : 1;
        }

        /// <summary>ШТАБ-тир фабрики (0 — дешёвые переходы; HQ не наследуется
        /// между тирами автоматически: _hqTiers хранит «до какого тира HQ»).</summary>
        public static int HqTierOf(object factory)
        {
            object key = KeyOfFactory(factory);
            return key != null && _hqTiers.TryGetValue(key, out int hq)
                ? Mathf.Clamp(hq, 0, 3)
                : 0;
        }

        /// <summary>Ветка фабрики выбрана (тир ≥2 или апгрейд в очереди) и это
        /// ШТАБ-ветка. Дальнейшие плитки — ТОЛЬКО этой ветки (юзер-канон
        /// 2026-11-03: «выбирает ветку, дальше подкладываются плитки ветки»);
        /// T1-фабрика без апгрейда в очереди — false (ветки ещё нет, выбор из двух).</summary>
        public static bool BranchIsHq(object factory)
        {
            object key = KeyOfFactory(factory);
            if (key == null)
                return false;
            int tier = TierOf(key);
            if (tier >= 2)
                return HqTierOf(key) >= tier;
            return ProductionTasks.PendingBranchHq(key);
        }

        /// <summary>Тип фабрики для HQ-модели: наземка/авиация/флот — HQ держит
        /// ростер и дешёвую ветку ТОЛЬКО фабрикам СВОЕГО типа (юзер-канон
        /// 2026-11-03 «HQ нужной фабрики»: воздушный HQ не открывает дешёвую
        /// наземку и наоборот).</summary>
        public static string KindOfKey(object key)
        {
            switch (key)
            {
                case Hangar _:
                case Airbase _:
                    return "air";
                case VehicleDepot _:
                    return "land";
                case BuildTasks.Task site:
                    return site.IsDock
                        ? "naval"
                        : site.Definition != null && BuildTasks.IsHangarDefinition(site.Definition)
                            ? "air"
                            : "land";
                case Building _:
                    return "naval"; // верфь-корпус (не депо — тот выше по кейсу)
                default:
                    return "land";
            }
        }

        /// <summary>Живой HQ тира у фракции (полный ростер держит ДРУГИМ
        /// фабрикам; сам HQ независим). kind ≠ null — только HQ своего типа
        /// фабрики. Перебор реестра — фабрик у фракции единицы, троттла не нужно.</summary>
        public static bool AnyHq(FactionHQ owner, int tier, string kind = null)
        {
            if (owner == null || tier < 2)
                return false;
            foreach (KeyValuePair<object, int> kv in _hqTiers)
                if (kv.Value >= tier && OwnerOf(kv.Key) == owner && Alive(kv.Key) &&
                    (kind == null || KindOfKey(kv.Key) == kind))
                    return true;
            return false;
        }

        /// <summary>Дешёвая ветка апгрейда доступна: у фракции есть живой HQ
        /// целевого тира ТОГО ЖЕ типа фабрики (юзер-канон 2026-11-03: «дешёвый
        /// апгрейд доступен только после наличия HQ нужной фабрики»). Первый HQ
        /// своей фракции строится только полной плиткой.</summary>
        public static bool CheapUpgradeAvailable(object factory, int toTier) =>
            AnyHq(OwnerOf(KeyOfFactory(factory)), toTier, KindOfKey(KeyOfFactory(factory)));

        /// <summary>Гейт выпуска: тир определения ≤ тиру фабрики И ростер —
        /// юниты T2+ требуют либо HQ у самой фабрики, либо живой HQ тира
        /// фракции; дешёвая фабрика без HQ строит ТОЛЬКО инженеров (канон
        /// FAF, юзер 2026-11-03). T1 — всегда полный.</summary>
        public static bool CanProduce(object factory, UnitDefinition def)
        {
            if (def == null)
                return false;
            int tier = UnitRoster.TierOf(def);
            if (tier <= 1 || tier <= TierOf(factory) && tier <= HqTierOf(factory))
                return tier <= TierOf(factory);
            if (tier > TierOf(factory))
                return false;
            // T2+: фабрика доросла — решает HQ-модель (HQ своего типа фабрики)
            object key = KeyOfFactory(factory);
            return AnyHq(OwnerOf(key), tier, KindOfKey(key)) || IsEngineerDef(def);
        }

        /// <summary>Определение — инженер (дешёвым фабрикам без HQ остаётся
        /// строить их): тип TRUCK/LC — тот же детект, что GameApi.IsEngineer
        /// (клоны-инженеры наследуют vehicleType донора-грузовика).</summary>
        private static bool IsEngineerDef(UnitDefinition def) =>
            def is VehicleDefinition vd && vd.vehicleType == VehicleType.TRUCK ||
            def is ShipDefinition sea && sea.shipType == ShipType.LC;

        /// <summary>Цена следующего тира фабрики (false — максимум/выключено).
        /// hq=true — ШТАБ (полная цена); false — дешёвая версия (hqSupportFactor).</summary>
        public static bool NextCost(int tier, bool hq, out float mass, out float energy, out float seconds)
        {
            mass = energy = seconds = 0f;
            if (_profile == null)
                _profile = Load(); // лениво: наливом владеет очередь, тик не грузит
            if (!_profile.enabled || tier < 1 || tier >= 3)
                return false;
            if (tier == 1)
            {
                mass = _profile.tier2Mass; energy = _profile.tier2Energy; seconds = _profile.tier2Seconds;
            }
            else
            {
                mass = _profile.tier3Mass; energy = _profile.tier3Energy; seconds = _profile.tier3Seconds;
            }
            if (!hq)
            {
                float f = Mathf.Clamp(_profile.hqSupportFactor, 0.1f, 1f);
                mass *= f;
                energy *= f;
                seconds *= f; // дешёвая — и наливается быстрее (та же скорость/сек)
            }
            return mass > 0f;
        }

        /// <summary>Завершение апгрейда (зовёт ProductionTasks.Tick, когда позиция
        /// апгрейда в очереди оплачена): тир растёт; hq — фабрика становится
        /// ШТАБОМ до этого тира (держит ростер себе и дешёвым фабрикам фракции).</summary>
        public static void Complete(object factory, int toTier, bool hq)
        {
            object key = KeyOfFactory(factory);
            if (key == null)
                return;
            _tiers[key] = Mathf.Clamp(toTier, 1, 3);
            if (hq)
                _hqTiers[key] = Mathf.Clamp(toTier, 1, 3);
            Version++;
            Log.Info("FactoryTier: фабрика → T" + toTier + (hq ? " ШТАБ" : "") +
                     " (" + Describe(key) + ")");
            FactionHQ owner = OwnerOf(key);
            if (owner != null && GameApi.TryGetLocalHQ(out FactionHQ local) && local == owner)
                CommanderHud.Notice(hq
                    ? Loc.F("Штаб-фабрика → T{0}", toTier)
                    : Loc.F("Фабрика → T{0}", toTier));
        }

        /// <summary>Построена фабрика ТИРОМ напрямую (пул: «Наземная фабрика
        /// T2» — юзер-канон 2026-11-04): тир ставится тихо, без Notice
        /// («Построено: …» уже сказал игроку). Только вверх — ручные апгрейды
        /// не сбиваем.</summary>
        public static void SpawnedAs(object factory, int tier)
        {
            object key = KeyOfFactory(factory);
            if (key == null || tier <= 1 || TierOf(key) >= tier)
                return;
            _tiers[key] = Mathf.Clamp(tier, 1, 3);
            Version++;
            Log.Info("FactoryTier: построена сразу T" + tier + " (" + Describe(key) + ")");
        }

        /// <summary>Чистка мёртвых ключей (каждую секунду, host); налив апгрейда
        /// с v0.136 живёт ПОЗИЦИЕЙ ОЧЕРЕДИ ProductionTasks — здесь только реестр.</summary>
        public static void Tick(float dt)
        {
            if (!GameApi.InMission || !IsHost)
            {
                if (_tiers.Count > 0)
                    Reset();
                return;
            }
            if (Time.unscaledTime >= _nextPrune)
            {
                _nextPrune = Time.unscaledTime + 1f;
                Prune();
            }
        }

        /// <summary>Владелец фабрики по ключу (null — не определить/мертва).</summary>
        private static FactionHQ OwnerOf(object key)
        {
            switch (key)
            {
                case VehicleDepot depot:
                    return depot != null ? depot.NetworkHQ : null;
                case Building building:
                    return building != null ? building.NetworkHQ : null;
                case Hangar hangar:
                    return hangar != null && hangar.attachedUnit != null
                        ? hangar.attachedUnit.NetworkHQ : null;
                case BuildTasks.Task site:
                    return site?.Faction;
                case Airbase airbase:
                    // Тир авиации живёт на аэродроме: владелец — первый живой ангар.
                    if (airbase?.hangars == null)
                        return null;
                    foreach (Hangar hangar in airbase.hangars)
                        if (hangar != null && hangar.attachedUnit != null)
                            return hangar.attachedUnit.NetworkHQ;
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>Мёртвые ключи (здание снесено/площадка кончилась/аэродром
        /// без ангаров) уходят вместе с их тиром; идущий апгрейд сгорает
        /// БЕЗ возврата (как экстракторы: смерть сжигает прогресс).</summary>
        private static void Prune()
        {
            bool touched = false;
            foreach (object key in new List<object>(_tiers.Keys))
                if (!Alive(key))
                {
                    _tiers.Remove(key);
                    _hqTiers.Remove(key); // HQ умер — дешёвые фабрики тира теряют ростер
                    touched = true;
                }
            foreach (object key in new List<object>(_hqTiers.Keys))
                if (!Alive(key))
                {
                    _hqTiers.Remove(key);
                    touched = true;
                }
            if (touched)
                Version++;
        }

        private static bool Alive(object key)
        {
            switch (key)
            {
                case BuildTasks.Task site:
                    return BuildTasks.IsActive(site);
                case Airbase airbase:
                    if (airbase?.hangars == null)
                        return false;
                    foreach (Hangar hangar in airbase.hangars)
                        if (hangar != null)
                            return true;
                    return false;
                case UnityEngine.Object u:
                    return u != null && !OwnerChangedDead(u);
                default:
                    return key != null;
            }
        }

        /// <summary>Юнит-компонент с отключенным GameObject мёртв для тиков,
        /// хотя Unity-ссылка жива (fake-null ещё не сработал).</summary>
        private static bool OwnerChangedDead(UnityEngine.Object u) =>
            u is Building b && b.disabled;

        private static string Describe(object key)
        {
            switch (key)
            {
                case Airbase _: return "аэродром";
                case BuildTasks.Task _: return "площадка";
                case Building _: return "верфь";
                default: return "фабрика";
            }
        }

        /// <summary>Площадка достроена: тир переезжает на живую фабрику (зеркало
        /// ProductionTasks.AdoptSite — Пауза/Цикл/ралли; позиции апгрейда в
        /// очереди переезжают там же своим переносом).</summary>
        public static void Adopt(BuildTasks.Task site, object factory)
        {
            if (site == null)
                return;
            object key = KeyOfFactory(factory);
            if (key == null)
                return;
            if (_tiers.TryGetValue(site, out int tier) && tier > 1 &&
                (!_tiers.TryGetValue(key, out int live) || live < tier))
                _tiers[key] = tier;
            if (_hqTiers.TryGetValue(site, out int siteHq) && siteHq > 0 &&
                (!_hqTiers.TryGetValue(key, out int liveHq) || liveHq < siteHq))
                _hqTiers[key] = siteHq;
            _tiers.Remove(site);
            _hqTiers.Remove(site);
            Version++;
        }

        /// <summary>Площадка умерла (снос/спавн не удался): тир умирает вместе с
        /// ней (зеркало ProductionTasks.DropSiteState; очередь чистит свои
        /// позиции сама — с возвратом уплаченного).</summary>
        public static void Drop(BuildTasks.Task site)
        {
            if (site == null)
                return;
            if (_tiers.Remove(site) | _hqTiers.Remove(site))
                Version++;
        }

        public static void Reset()
        {
            _tiers.Clear();
            _hqTiers.Clear();
            _nextPrune = 0f;
            Version++;
        }

        private static FactoryProfile Load()
        {
            try
            {
                string path = System.IO.Path.Combine(BepInEx.Paths.PluginPath,
                    "SupremeNuclear", "Production", "factory.json");
                if (System.IO.File.Exists(path))
                    return JsonConvert.DeserializeObject<FactoryProfile>(
                        System.IO.File.ReadAllText(path)) ?? new FactoryProfile();
                var fresh = new FactoryProfile();
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, JsonConvert.SerializeObject(fresh,
                    Formatting.Indented,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
                Log.Info("FactoryTier: образец создан — " + path);
                return fresh;
            }
            catch (System.Exception ex)
            {
                Log.Warn("FactoryTier: профиль не прочитан (" + ex.Message + ") — беру умолчания");
                return new FactoryProfile();
            }
        }
    }
}
