using System.Collections.Generic;
using NuclearOption.MissionEditorScripts;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Проверки предположений об API игры (паттерн Ground Control):
    /// стартовые — без сцены, активационные — при входе в режим командира.
    /// </summary>
    internal static class SelfCheck
    {
        public static readonly List<string> Failures = new List<string>();

        public static void RunStartup()
        {
            Failures.Clear();
            Check("PhysicsLayers.StaticsMask", () => { var _ = PhysicsLayers.StaticsMask; });
            Check("Datum.LocalSeaY", () => { var _ = Datum.LocalSeaY; });
            Check("InputFieldChecker.InsideInputField", () => { var _ = InputFieldChecker.InsideInputField; });
            Check("GlobalPositionExtensions", () =>
            {
                var v = new Vector3(1234.5f, 67.8f, -910.1f);
                var round = v.ToGlobalPosition().ToLocalPosition();
                if ((round - v).magnitude > 0.01f) throw new System.Exception("roundtrip mismatch: " + round);
            });
            Log.Info("SelfCheck(startup): " + (Failures.Count == 0 ? "все проверки пройдены" : "ПРОВАЛОВ: " + Failures.Count));
        }

        public static void RunActivation()
        {
            Check("CameraStateManager в сцене", () =>
            {
                if (GameApi.CamManager == null) throw new System.Exception("singleton отсутствует");
            });
            Check("CameraStateManager.mainCamera", () =>
            {
                if (GameApi.MainCam == null) throw new System.Exception("камера отсутствует");
            });
            Check("CameraStateManager.freeState", () =>
            {
                if (GameApi.CamManager.freeState == null) throw new System.Exception("freeState == null");
            });
            Check("GameManager.GetLocalHQ", () =>
            {
                if (!GameManager.GetLocalHQ(out _)) throw new System.Exception("локальная фракция не найдена");
            });
            Log.Info("SelfCheck(activation): " + (Failures.Count == 0 ? "ОК" : "ПРОВАЛОВ: " + Failures.Count));
        }

        private static void Check(string name, System.Action probe)
        {
            try
            {
                probe();
                Log.Info("  [OK]   " + name);
            }
            catch (System.Exception e)
            {
                Log.Error("  [FAIL] " + name + ": " + e.Message);
                if (!Failures.Contains(name))
                    Failures.Add(name);
            }
        }
    }
}
