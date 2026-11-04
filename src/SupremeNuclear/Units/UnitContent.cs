using System.Collections.Generic;
using BepInEx;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Контент-конвейер юнитов (v0.150): китбаш-пропсы из units.bundle —
    /// надстройки/краны/корпуса СОБСТВЕННЫХ моделей поверх НАТИВНЫХ префабов
    /// (unitPrefab донора остаётся: физика/сеть/части урона/руление нативные).
    /// Пайплайн сборки — unitcontent/README.md (Unity 2022.3.62, как splat.bundle).
    /// Бандла нет — тихий фолбэк на тинт тиров (EngineerService.Decorate).
    /// Ключи-конвенция (GameObject-наборы пропсов в ЛОКАЛЬНЫХ координатах
    /// донора, габариты — README): SN_ENG_T2_LAND / SN_ENG_T2_SEA /
    /// SN_ENG_T3_LAND / SN_ENG_T3_SEA; расширяется новыми SN_* ключами
    /// (будущие ховер-юниты и эксперименталы).
    /// Бандл грузится один раз за запуск; кэш переживает сцены СОЗНАННО
    /// (как сплат-шейдер TerrainSplat). Пропсы — host-локальный визуал
    /// (класс проблемы §12-58а, MP-долг).
    /// </summary>
    internal static class UnitContent
    {
        private static AssetBundle _bundle;
        private static bool _tried;
        private static readonly Dictionary<string, GameObject> Cache =
            new Dictionary<string, GameObject>();

        private static AssetBundle Bundle()
        {
            if (_tried)
                return _bundle;
            _tried = true;
            try
            {
                string path = System.IO.Path.Combine(Paths.PluginPath, "SupremeNuclear", "units.bundle");
                if (!System.IO.File.Exists(path))
                {
                    Log.Info("UnitContent: units.bundle не найден — китбаш-пропсы выключены " +
                             "(различие тиров работает тинтом)");
                    return null;
                }
                _bundle = AssetBundle.LoadFromFile(path);
                if (_bundle == null)
                {
                    Log.Warn("UnitContent: units.bundle не загрузился (версия редактора?) — работаем на тинте");
                    return null;
                }
                Log.Info("UnitContent: бандл юнит-контента загружен — " +
                         _bundle.GetAllAssetNames().Length + " ассетов");
            }
            catch (System.Exception e)
            {
                Log.Warn("UnitContent: " + e.Message);
            }
            return _bundle;
        }

        /// <summary>Привить пропсы свежеспавненному юниту (host-only визуал):
        /// Instantiate набора дочерним объектом, локальные координаты — в системе
        /// донора (worldPositionStays: false). true — привито; нет бандла/ключа —
        /// false (не ошибка: контент опционален).</summary>
        public static bool TryAttachProps(Unit unit, string key)
        {
            if (unit == null)
                return false;
            AssetBundle bundle = Bundle();
            if (bundle == null)
                return false;
            try
            {
                if (!Cache.TryGetValue(key, out GameObject prefab))
                {
                    prefab = bundle.LoadAsset<GameObject>(key);
                    Cache[key] = prefab; // null кэшируем тоже: отсутствие не логируем на каждом спавне
                }
                if (prefab == null)
                    return false;
                UnityEngine.Object.Instantiate(prefab, unit.transform, false);
                return true;
            }
            catch (System.Exception e)
            {
                Log.Warn("UnitContent.Attach(" + key + "): " + e.Message);
                return false;
            }
        }
    }
}
