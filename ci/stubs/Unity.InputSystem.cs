// Compile-time stand-in for the game's Unity.InputSystem.dll (Input System 1.7), used only by CI.
// Declares just the members the plugin calls, on the same types/namespaces, so the compiled IL
// binds to the real assembly at runtime. The stub itself is never shipped.
using System.Reflection;
[assembly: AssemblyVersion("1.7.0.0")]

namespace UnityEngine.InputSystem
{
    public enum Key { None = 0 }
    public class InputControl { }
    public class InputDevice : InputControl { }

    public class Gamepad : InputDevice
    {
        public static Gamepad current { get { return null; } }
        public Controls.ButtonControl buttonSouth { get { return null; } }
        public Controls.ButtonControl buttonEast { get { return null; } }
        public Controls.ButtonControl leftStickButton { get { return null; } }
        public Controls.ButtonControl rightStickButton { get { return null; } }
        public Controls.ButtonControl selectButton { get { return null; } }
        public Controls.DpadControl dpad { get { return null; } }
    }

    public class Keyboard : InputDevice
    {
        public static Keyboard current { get { return null; } }
        public Controls.KeyControl this[Key key] { get { return null; } }
    }
}

namespace UnityEngine.InputSystem.Controls
{
    public class AxisControl : InputControl { }
    public class ButtonControl : AxisControl
    {
        public bool isPressed { get { return false; } }
        public bool wasPressedThisFrame { get { return false; } }
    }
    public class KeyControl : ButtonControl { }
    public class Vector2Control : InputControl { }
    public class DpadControl : Vector2Control
    {
        public ButtonControl up { get { return null; } }
        public ButtonControl down { get { return null; } }
        public ButtonControl left { get { return null; } }
        public ButtonControl right { get { return null; } }
    }
}
