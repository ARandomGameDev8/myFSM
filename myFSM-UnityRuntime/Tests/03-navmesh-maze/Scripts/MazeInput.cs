// Test support — keyboard reading that works with EITHER Unity input backend.
//
// MazeGeneratorController reads G (new maze) and B (re-bake) through it.
//
// Why this file exists: UnityEngine.Input THROWS at runtime when a project's
// Active Input Handling is set to "Input System Package (New)":
//
//   InvalidOperationException: You are trying to read Input using the
//   UnityEngine.Input class, but you have switched active Input handling to
//   Input System package in Player Settings.
//
// No key ever reaches the game, and it looks exactly like a broken test. So the
// keys are read through whichever backend the project actually compiled:
//
//   ENABLE_LEGACY_INPUT_MANAGER -> UnityEngine.Input (the old Input Manager)
//   ENABLE_INPUT_SYSTEM         -> Keyboard.current (the Input System package)
//
// Both symbols come from Unity (Active Input Handling), so neither branch is
// compiled when it would not work.
//
// The class name is specific to this test folder on purpose: switch, maze and
// chase each carry their own copy (ZoneInput, StressInput and ChaseInput),
// so several test cases can live in the same Unity project without colliding -
// and because every test script is in namespace MyFSM.Tests, none of them needs
// a `using` to reach its own helper.
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

namespace MyFSM.Tests
{
    public static class MazeInput
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

        /// <summary>Remedy to print when Available is false.</summary>
        public const string Fix =
            "Set Project Settings > Player > Active Input Handling to 'Input Manager (Old)' or "
            + "'Both' (Unity restarts the editor when you change it).";

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

        /// <summary>Any of two keys held (either spelling of WASD, for example).</summary>
        public static bool GetKeyEither(KeyCode a, KeyCode b)
        {
            return GetKey(a) || GetKey(b);
        }

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        private static readonly System.Collections.Generic.HashSet<KeyCode> Reported =
            new System.Collections.Generic.HashSet<KeyCode>();

        /// <summary>
        /// KeyCode -> Input System control. The Input System package names almost
        /// all of its keys exactly like KeyCode does (Key.W, Key.Space,
        /// Key.Backspace, Key.UpArrow, ...), so the mapping is a name lookup
        /// instead of a hand-written table. Anything the package has no name for
        /// is reported once, rather than silently ignored.
        /// </summary>
        private static KeyControl Resolve(KeyCode key)
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return null;

            Key mapped;
            if (!System.Enum.TryParse(key.ToString(), out mapped))
            {
                if (Reported.Add(key))
                    Debug.LogWarning("[input] " + key + " has no Input System equivalent "
                                     + "(Key." + key + " does not exist) - add a mapping for it "
                                     + "where the test reads keys.");
                return null;
            }
            return keyboard[mapped];
        }
#endif
    }
}
