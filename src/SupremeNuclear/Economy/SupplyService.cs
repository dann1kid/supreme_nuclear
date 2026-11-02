using System.Collections.Generic;
using NuclearOption;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Снабжение v1 (DESIGN §2.14, юзер-канон 2026-10-31): СЛОЙ 1 — обычные
    /// боеприпасы наземки/флота/турелей БЕСКОНЕЧНЫ (SupCom: механика без «неудобных
    /// вопросов»; ресток — событийный постфикс AmmoFreePatch на WeaponStation.
    /// UpdateLastFired, НЕ поллинг — юзер подсказал: патчить расход дешевле, окон
    /// высушивания нет); авиация — исключение: её сервис за деньги часть ценности
    /// борта (SortieService). СЛОЙ 2 — ракетные залпы стоят массу+энергию за пуск
    /// (Drain, «жечь или копить на узел»): FleetSalvo.TryLaunchOne (ракета) и
    /// SalvoService.TryFireStation (залп станции). Числа — EconomyProfile JSON.
    /// </summary>
    internal static class SupplyService
    {
        // Кэш «станция принадлежит авиации» (постфикс не знает владельца —
        // WeaponStation не компонент; строим по Unit.weaponStations, промах → проход).
        private static readonly Dictionary<WeaponStation, bool> _airCache =
            new Dictionary<WeaponStation, bool>();

        private static readonly Dictionary<FactionHQ, float> _noticeAt =
            new Dictionary<FactionHQ, float>();

        /// <summary>Слой 1 включён (профиль экономики, дефолт true).</summary>
        public static bool FreeAmmo => EconomyService.Profile == null ||
                                       EconomyService.Profile.freeAmmoForGround;

        /// <summary>Станция НЕ авиации → постфиксу можно бесплатного рестока.</summary>
        internal static bool GroundStation(WeaponStation st)
        {
            if (_airCache.TryGetValue(st, out bool air))
                return !air;
            Sweep();
            return _airCache.TryGetValue(st, out air) && !air;
        }

        /// <summary>Разовая проходка: пометить все станции живых юнитов.</summary>
        private static void Sweep()
        {
            _airCache.Clear();
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.weaponStations == null)
                    continue;
                bool air = unit is Aircraft;
                foreach (WeaponStation st in unit.weaponStations)
                    if (st != null)
                        _airCache[st] = air;
            }
        }

        /// <summary>Смена сцены — станции умерли.</summary>
        public static void Reset()
        {
            _airCache.Clear();
            _noticeAt.Clear();
        }

        // ---------- Слой 2: залпы за ресурс ----------

        /// <summary>Чистый гейт «хватает ли на залп» (тестируется): масса из фонда,
        /// энергия — только когда экономика активна (стоковые карты — массы нет?).</summary>
        internal static bool SalvoAllowed(float funds, float energy, float massCost,
            float energyCost, bool energyActive) =>
            (massCost <= 0f || funds >= massCost) &&
            (!energyActive || energyCost <= 0f || energy >= energyCost);

        /// <summary>Оплатить пуск (ракета/залп станции): хватает — Drain (нальётся
        /// полностью, проверили), не хватает — false + Notice с троттлом. Профиль
        /// не задал цен (0/0) или юнит без фракции — бесплатно.</summary>
        public static bool TryPaySalvo(Unit owner)
        {
            EconomyProfile p = EconomyService.Profile;
            float mass = p != null ? p.salvoMassPerLaunch : 0f;
            float energy = p != null ? p.salvoEnergyPerLaunch : 0f;
            if (mass <= 0f && energy <= 0f)
                return true;
            FactionHQ hq = owner != null ? owner.NetworkHQ : null;
            if (hq == null)
                return true;
            if (!SalvoAllowed(hq.factionFunds, EconomyService.EnergyStore(hq),
                    mass, energy, EconomyService.EnergyActive))
            {
                if (!_noticeAt.TryGetValue(hq, out float at) || Time.time >= at)
                {
                    _noticeAt[hq] = Time.time + 5f;
                    CommanderHud.Notice(Loc.Tr("Залп отменён — не хватает массы/энергии"));
                }
                return false;
            }
            EconomyService.Drain(hq, mass, energy);
            return true;
        }
    }
}
