// Test 01 — keyboard reading that works with EITHER Unity input backend.
//
// This exists because "the keys do nothing" has a very common cause that looks
// like a broken test: Project Settings -> Player -> Active Input Handling. If it
// is set to "Input System Package (New)", then UnityEngine.Input (the class this
// code used to call) throws at runtime:
//
//   InvalidOperationException: You are trying to read Input using the
//   UnityEngine.Input class, but you have switched active Input handling to
//   Input System package in Player Settings.
//
// ...and no key press ever reaches the game. With "Both" the old API keeps
// working. So: read the legacy Input Manager when it is compiled in, otherwise
// read the Input System package directly. Which one is in use is reported in the
// console at start, and `Backend` is what tells you.
//
// The `#if` symbols come from Unity itself (they are set from Active Input
// Handling), so a project without the Input System package never compiles the
// second branch, and a project with it never compiles a broken call.

using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

namespace MyFSM.Tests
{
    public static class ZoneInput
    {
        /// <summary>Which backend this build reads, for the startup log line.</summary>
        public static string Backend
        {
            get
            {
#if ENABLE_LEGACY_INPUT_MANAGER
                return "legacy Input Manager (UnityEngine.Input)";
#elif ENABLE_INPUT_SYSTEM
                return Keyboard.current != null
                    ? "Input System package (Keyboard.current)"
                    : "Input System package, but no keyboard device is present";
#else
                return "NONE - neither input backend is compiled into this project";
#endif
            }
        }

        /// <summary>True when at least one backend can actually be read.</summary>
        public static bool Available
        {
            get
            {
#if ENABLE_LEGACY_INPUT_MANAGER
                return true;
#elif ENABLE_INPUT_SYSTEM
                return Keyboard.current != null;
#else
                return false;
#endif
            }
        }

        /// <summary>The remedy to print when Available is false.</summary>
        public static string Fix
        {
            get
            {
                return "Set Project Settings > Player > Active Input Handling to 'Input Manager (Old)' "
                     + "or 'Both' (Unity restarts the editor when you change it), or use the on-screen "
                     + "`zone` field: this component pushes a value typed into it while playing.";
            }
        }

        public static bool GetKey(KeyCode key)
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKey(key);
#elif ENABLE_INPUT_SYSTEM
            KeyControl control = Resolve(key);
            return control != null && control.isPressed;
#else
            return false;
#endif
        }

        public static bool GetKeyDown(KeyCode key)
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(key);
#elif ENABLE_INPUT_SYSTEM
            KeyControl control = Resolve(key);
            return control != null && control.wasPressedThisFrame;
#else
            return false;
#endif
        }

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        private static readonly System.Collections.Generic.HashSet<KeyCode> Reported =
            new System.Collections.Generic.HashSet<KeyCode>();

        /// <summary>
        /// KeyCode -> Input System control. Only the keys the tests use are
        /// mapped; anything else is reported once instead of silently ignored.
        /// </summary>
        private static KeyControl Resolve(KeyCode key)
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return null;

            switch (key)
            {
                case KeyCode.W: return keyboard.wKey;
                case KeyCode.A: return keyboard.aKey;
                case KeyCode.S: return keyboard.sKey;
                case KeyCode.D: return keyboard.dKey;
                case KeyCode.R: return keyboard.rKey;
                case KeyCode.C: return keyboard.cKey;
                case KeyCode.UpArrow: return keyboard.upArrowKey;
                case KeyCode.DownArrow: return keyboard.downArrowKey;
                case KeyCode.LeftArrow: return keyboard.leftArrowKey;
                case KeyCode.RightArrow: return keyboard.rightArrowKey;
                default:
                    if (Reported.Add(key))
                        Debug.LogWarning("[input] " + key + " has no Input System mapping in "
                                         + "ZoneInput.Resolve — add it there if this test needs it.");
                    return null;
            }
        }
#endif
    }
}
