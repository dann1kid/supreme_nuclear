using NuclearOption.MissionEditorScripts;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SupremeNuclear
{
    /// <summary>Сырой ввод без Rewired — не конфликтует с игровыми биндингами.</summary>
    internal static class RtsInput
    {
        public static bool Typing => InputFieldChecker.InsideInputField;

        public static bool PointerOverUi =>
            EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

        public static Vector3 MousePos => Input.mousePosition;

        public static float Wheel => Input.GetAxis("Mouse ScrollWheel");

        public static bool KeyDown(KeyCode key) => !Typing && Input.GetKeyDown(key);

        public static bool LeftDown => !Typing && Input.GetMouseButtonDown(0);
        public static bool LeftHeld => !Typing && Input.GetMouseButton(0);
        public static bool LeftUp => !Typing && Input.GetMouseButtonUp(0);

        public static bool RightDown => !Typing && Input.GetMouseButtonDown(1);
        public static bool RightHeld => !Typing && Input.GetMouseButton(1);

        public static bool MiddleHeld => !Typing && Input.GetMouseButton(2);

        public static bool RotateModifierHeld => !Typing && Input.GetKey(Settings.RotateModifierKey);

        public static bool Shift => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        public static bool Ctrl => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
    }
}
