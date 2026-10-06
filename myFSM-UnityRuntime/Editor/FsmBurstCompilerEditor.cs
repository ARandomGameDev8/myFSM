// myFSM Unity Runtime — inspector for the burst compiler.
//
// EDITOR ONLY (this folder never ships in builds): per-entry Compile, folder
// Sweep, and the Generate/Attach workflow. Generate creates a GameObject in
// the scene at the SceneView pivot with the entry's generated script
// attached — edit mode only, never in play mode.
//
// Compile ATTACHES: to the entry's Target right away when the class already
// exists, or automatically once Unity has imported the freshly written script
// (see the deferred-attach block at the bottom) on the first compile.

using System;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using MyFSM.Core;
using MyFSM.Compiler;
using MyFSM.Unity;

namespace MyFSM.Editor
{
    [CustomEditor(typeof(FsmBurstCompiler))]
    public sealed class FsmBurstCompilerEditor : UnityEditor.Editor
    {
        private SerializedProperty _entries;
        private SerializedProperty _defaultOut;
        private SerializedProperty _defaultScript;
        private SerializedProperty _inputFolder;

        private void OnEnable()
        {
            _entries = serializedObject.FindProperty("Entries");
            _defaultOut = serializedObject.FindProperty("DefaultOutputFolder");
            _defaultScript = serializedObject.FindProperty("DefaultScriptFolder");
            _inputFolder = serializedObject.FindProperty("InputFolder");
        }

        public override void OnInspectorGUI()
        {
            FsmBurstCompiler c = (FsmBurstCompiler)target;
            serializedObject.Update();

            if (!FsmCompiler.IsAvailable)
            {
                EditorGUILayout.HelpBox(
                    "Native compiler library '" + FsmCompiler.NativeLibName +
                    "' not found for this editor platform. Put the plugin in a Plugins " +
                    "folder (see Plugins/README.md), or precompile with the fsmc CLI.",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Compiler ready (native v" + FsmCompiler.NativeVersion + ").",
                    MessageType.Info);
            }

            EditorGUILayout.LabelField("Defaults", EditorStyles.boldLabel);
            DrawFolderRow(_defaultOut);
            DrawFolderRow(_defaultScript);
            DrawFolderRow(_inputFolder);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Sweep Folder"))
                {
                    RunGuiAction("Sweep failed", delegate
                    {
                        serializedObject.ApplyModifiedProperties();
                        Undo.RecordObject(c, "Sweep myFSM Folder");
                        int n = c.SweepFolder();
                        EditorUtility.SetDirty(c);
                        if (c.gameObject.scene.IsValid())
                            EditorSceneManager.MarkSceneDirty(c.gameObject.scene);
                        Debug.Log("[myFSM] sweep added " + n + " entr" +
                                  (n == 1 ? "y" : "ies") + ".");
                    });
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button("Compile All"))
                {
                    RunGuiAction("Compile failed", delegate
                    {
                        serializedObject.ApplyModifiedProperties();
                        FsmBurstStats s = c.CompileAll();
                        QueueAssetDatabaseRefresh();
                        Debug.Log("[myFSM] burst: " + s.Succeeded + "/" + s.Total + " compiled.");
                    });
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button("Sweep + Compile"))
                {
                    RunGuiAction("Sweep and compile failed", delegate
                    {
                        serializedObject.ApplyModifiedProperties();
                        Undo.RecordObject(c, "Sweep and Compile myFSM Folder");
                        int n = c.SweepFolder();
                        EditorUtility.SetDirty(c);
                        if (c.gameObject.scene.IsValid())
                            EditorSceneManager.MarkSceneDirty(c.gameObject.scene);
                        FsmBurstStats s = c.CompileAll();
                        QueueAssetDatabaseRefresh();
                        Debug.Log("[myFSM] sweep added " + n + "; burst: " +
                                  s.Succeeded + "/" + s.Total + " compiled.");
                    });
                    GUIUtility.ExitGUI();
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Entries (" + _entries.arraySize + ")",
                                       EditorStyles.boldLabel);
            for (int i = 0; i < _entries.arraySize; i++)
            {
                DrawEntry(c, i);
            }
            if (GUILayout.Button("Add Entry"))
            {
                int at = _entries.arraySize;
                _entries.InsertArrayElementAtIndex(at);
                SerializedProperty ep = _entries.GetArrayElementAtIndex(at);
                ep.FindPropertyRelative("Name").stringValue = "NewAI";
                ep.FindPropertyRelative("FsmPath").stringValue = "Assets/MyFSM/Fsm/NewAI.fsm";
                ep.FindPropertyRelative("OutputFolder").stringValue = "";
                ep.FindPropertyRelative("ClassName").stringValue = "";
                ep.FindPropertyRelative("ScriptFolder").stringValue = "";
                ep.FindPropertyRelative("Target").objectReferenceValue = null;
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawEntry(FsmBurstCompiler c, int i)
        {
            SerializedProperty ep = _entries.GetArrayElementAtIndex(i);
            FsmBurstEntry live = (c.Entries != null && i < c.Entries.Count)
                ? c.Entries[i] : null;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(ep.FindPropertyRelative("Name"));
                DrawFileRow(ep);
                EditorGUILayout.PropertyField(ep.FindPropertyRelative("OutputFolder"));
                EditorGUILayout.PropertyField(ep.FindPropertyRelative("ClassName"));
                EditorGUILayout.PropertyField(ep.FindPropertyRelative("ScriptFolder"));
                SerializedProperty targetP = ep.FindPropertyRelative("Target");
                EditorGUILayout.PropertyField(targetP);
                if (EditorGUI.EndChangeCheck())
                {
                    serializedObject.ApplyModifiedProperties();
                    GUIUtility.ExitGUI();
                }

                bool playing = Application.isPlaying;
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Compile", GUILayout.Width(90)))
                    {
                        RunGuiAction("Compile failed", delegate
                        {
                            serializedObject.ApplyModifiedProperties();
                            FsmBurstEntry toCompile = (c.Entries != null && i < c.Entries.Count)
                                ? c.Entries[i] : null;
                            if (toCompile != null && c.CompileEntry(toCompile) && !Application.isPlaying)
                            {
                                // Attach immediately on recompiles. On the first
                                // compile Unity must import the generated .cs;
                                // the reload hook completes the queued attach.
                                GameObject target = targetP.objectReferenceValue as GameObject;
                                if (target != null)
                                {
                                    if (TryAttach(toCompile, target))
                                    {
                                        Debug.Log("[myFSM] compiled + attached " +
                                                  EffectiveClassName(toCompile) + " to '" +
                                                  target.name + "'.");
                                    }
                                    else if (FindComponentType(EffectiveClassName(toCompile)) == null)
                                    {
                                        QueuePendingAttach(toCompile, target);
                                        Debug.Log("[myFSM] compiled " + EffectiveClassName(toCompile) +
                                                  " - attaching to '" + target.name +
                                                  "' as soon as Unity finishes importing the script.");
                                    }
                                }
                            }
                            // Import generated C# only after this inspector event
                            // has exited, so SerializedProperty/layout state is
                            // not invalidated by a script compile/domain reload.
                            QueueAssetDatabaseRefresh();
                        });
                        GUIUtility.ExitGUI();
                    }

                    if (!playing && targetP.objectReferenceValue == null)
                    {
                        if (GUILayout.Button("Generate", GUILayout.Width(90)))
                        {
                            RunGuiAction("Generate failed", delegate
                            {
                                serializedObject.ApplyModifiedProperties();
                                if (c.Entries != null && i < c.Entries.Count)
                                    GenerateForEntry(c.Entries[i], targetP);
                            });
                            GUIUtility.ExitGUI();
                        }
                    }
                    else if (!playing && targetP.objectReferenceValue != null)
                    {
                        GameObject go = (GameObject)targetP.objectReferenceValue;
                        Type t = FindComponentType(EffectiveClassName(live));
                        if (t != null && go.GetComponent(t) == null)
                        {
                            if (GUILayout.Button("Attach", GUILayout.Width(90)))
                            {
                                RunGuiAction("Attach failed", delegate
                                {
                                    serializedObject.ApplyModifiedProperties();
                                    FsmBurstEntry fresh = (c.Entries != null && i < c.Entries.Count)
                                        ? c.Entries[i] : null;
                                    TryAttach(fresh, go);
                                });
                                GUIUtility.ExitGUI();
                            }
                        }
                    }
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("X", GUILayout.Width(28)))
                    {
                        _entries.DeleteArrayElementAtIndex(i);
                        serializedObject.ApplyModifiedProperties();
                        // The entry/control count changed. End this IMGUI event
                        // instead of repainting against the previous layout.
                        GUIUtility.ExitGUI();
                        return;
                    }
                }

                FsmBurstEntry cur = (c.Entries != null && i < c.Entries.Count)
                    ? c.Entries[i] : null;
                if (cur != null)
                {
                    EditorGUILayout.HelpBox(cur.LastStatus,
                        cur.LastOk ? MessageType.Info : MessageType.None);

                    // Compile -> script written -> Unity must import + compile it
                    // before the component type exists. Show that state explicitly.
                    GameObject targetGo = targetP.objectReferenceValue as GameObject;
                    if (cur.LastOk && !playing && targetGo != null &&
                        FindComponentType(EffectiveClassName(cur)) == null)
                    {
                        EditorGUILayout.HelpBox(
                            "Script written, but '" + EffectiveClassName(cur) +
                            "' does not exist yet - Unity is still importing it. It " +
                            "will be attached to '" + targetGo.name +
                            "' automatically as soon as it compiles.",
                            MessageType.Warning);
                    }
                    else if (playing && targetGo != null &&
                             FindComponentType(EffectiveClassName(cur)) != null &&
                             targetGo.GetComponent(FindComponentType(EffectiveClassName(cur))) == null)
                    {
                        EditorGUILayout.HelpBox(
                            "Cannot attach while in play mode - exit play mode, then " +
                            "press Attach.", MessageType.Warning);
                    }
                }
            }
        }

        // ----------------------------------------------------------
        // Deferred attach
        //
        // The first Compile writes the .cs, and Unity only creates the type
        // after it reimports -- which reloads the domain and wipes statics.
        // So the request is parked in SessionState (survives a reload, dies
        // with the editor session) as "scenePath\u001fgameObjectName\u001fClass",
        // then acted on once the new type exists.
        // ----------------------------------------------------------

        private const string PendingAttachKey = "myFSM.pendingAttach";
        private const char PendingSep = '\u001f';

        [InitializeOnLoadMethod]
        private static void FlushPendingAttachesOnLoad()
        {
            // Runs after every domain reload; a pending attach may now resolve.
            EditorApplication.delayCall += FlushPendingAttaches;
        }

        private static void QueuePendingAttach(FsmBurstEntry e, GameObject go)
        {
            if (e == null || go == null) return;
            string scenePath = go.scene.IsValid() ? go.scene.path : "";
            string row = scenePath + PendingSep + go.name + PendingSep +
                         EffectiveClassName(e);
            string raw = SessionState.GetString(PendingAttachKey, "");
            if (raw.IndexOf(row, StringComparison.Ordinal) >= 0) return;
            SessionState.SetString(PendingAttachKey, raw + row + "\n");
        }

        private static void FlushPendingAttaches()
        {
            string raw = SessionState.GetString(PendingAttachKey, "");
            if (string.IsNullOrEmpty(raw)) return;
            try
            {
                string[] rows = raw.Split('\n');
                string keep = "";
                for (int i = 0; i < rows.Length; i++)
                {
                    if (rows[i].Length == 0) continue;
                    string[] parts = rows[i].Split(new char[] { PendingSep });
                    if (parts.Length < 3) continue;
                    Type t = FindComponentType(parts[2]);
                    if (t == null)
                    {
                        keep += rows[i] + "\n"; // not imported yet - try next reload
                        continue;
                    }
                    GameObject go = FindSceneObject(parts[0], parts[1]);
                    if (go == null)
                    {
                        // Gone, renamed, or two objects share the name: never
                        // guess (see FindSceneObject). Drop it and say so.
                        Debug.LogWarning("[myFSM] cannot resolve '" + parts[1] +
                                         "' in the scene to attach " + parts[2] +
                                         " - use the Attach button.");
                        continue;
                    }
                    if (go.GetComponent(t) == null)
                    {
                        Undo.AddComponent(go, t);
                        EditorSceneManager.MarkSceneDirty(go.scene);
                        Debug.Log("[myFSM] attached " + parts[2] + " to '" +
                                  go.name + "'.");
                    }
                }
                SessionState.SetString(PendingAttachKey, keep);
            }
            catch (Exception ex)
            {
                // Never let a stale pending row wedge the editor.
                SessionState.EraseString(PendingAttachKey);
                Debug.LogWarning("[myFSM] pending attach dropped: " + ex.Message);
            }
        }

        /// <summary>
        /// Finds a GameObject by name in the given scene. Returns null when it
        /// is absent or ambiguous (two objects with that name): guessing which
        /// one the author meant is worse than leaving it to the Attach button.
        /// </summary>
        private static GameObject FindSceneObject(string scenePath, string goName)
        {
            GameObject found = null;
            int scenes = UnityEngine.SceneManagement.SceneManager.sceneCount;
            for (int s = 0; s < scenes; s++)
            {
                UnityEngine.SceneManagement.Scene scene =
                    UnityEngine.SceneManagement.SceneManager.GetSceneAt(s);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                if (!string.IsNullOrEmpty(scenePath) && scene.path != scenePath) continue;
                GameObject[] roots = scene.GetRootGameObjects();
                for (int r = 0; r < roots.Length; r++)
                {
                    GameObject m = FindByName(roots[r].transform, goName);
                    if (m == null) continue;
                    if (found != null) return null; // ambiguous
                    found = m;
                }
            }
            return found;
        }

        private static GameObject FindByName(Transform t, string goName)
        {
            if (t.name == goName) return t.gameObject;
            for (int i = 0; i < t.childCount; i++)
            {
                GameObject m = FindByName(t.GetChild(i), goName);
                if (m != null) return m;
            }
            return null;
        }

        /// <summary>
        /// Adds the entry's generated component to <paramref name="go"/> when
        /// the type exists and the object does not already have it. Returns
        /// true when something was attached.
        /// </summary>
        private static bool TryAttach(FsmBurstEntry e, GameObject go)
        {
            if (e == null || go == null) return false;
            Type t = FindComponentType(EffectiveClassName(e));
            if (t == null || go.GetComponent(t) != null) return false;
            Undo.AddComponent(go, t);
            EditorSceneManager.MarkSceneDirty(go.scene);
            return true;
        }

        private void DrawFileRow(SerializedProperty ep)
        {
            SerializedProperty p = ep.FindPropertyRelative("FsmPath");
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(p);
                if (GUILayout.Button("...", GUILayout.Width(32)))
                {
                    string picked = EditorUtility.OpenFilePanel(
                        "Pick .fsm", Application.dataPath, "fsm");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        string asset = FsmBurstCompiler.ToAssetPath(picked);
                        if (asset == null)
                            Debug.LogError("[myFSM] pick a file inside the project (under Assets/).");
                        else
                            p.stringValue = asset;
                    }
                }
            }
        }

        private void DrawFolderRow(SerializedProperty p)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(p);
                if (GUILayout.Button("...", GUILayout.Width(32)))
                {
                    string picked = EditorUtility.OpenFolderPanel(
                        "Pick folder", Application.dataPath, "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        string asset = FsmBurstCompiler.ToAssetPath(picked);
                        if (asset == null)
                            Debug.LogError("[myFSM] pick a folder inside the project (under Assets/).");
                        else
                            p.stringValue = asset;
                    }
                }
            }
        }

        private static void RunGuiAction(string title, Action action)
        {
            try
            {
                action();
            }
            catch (ExitGUIException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog(title, ex.Message, "OK");
            }
        }

        private static void QueueAssetDatabaseRefresh()
        {
            // Refreshing generated .cs files can trigger compilation and a
            // domain reload. Do not invalidate this inspector's serialized
            // properties or IMGUI state in the middle of OnInspectorGUI.
            EditorApplication.delayCall += delegate
            {
                try
                {
                    AssetDatabase.Refresh();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            };
        }

        private static string EffectiveClassName(FsmBurstEntry e)
        {
            if (e == null) return "";
            if (!string.IsNullOrEmpty(e.ClassName)) return e.ClassName;
            string stem = Path.GetFileNameWithoutExtension(e.FsmPath ?? "");
            return ClassGenerator.SanitizeIdentifier(stem) + "AI";
        }

        // Edit mode only: creates the GameObject in front of the SceneView
        // camera pivot, links it to the entry, and attaches the generated
        // script when it is already compiled.
        private static void GenerateForEntry(FsmBurstEntry e, SerializedProperty targetP)
        {
            if (e == null) return;
            string className = EffectiveClassName(e);
            GameObject go = new GameObject(
                !string.IsNullOrEmpty(e.Name) ? e.Name : className);
            Undo.RegisterCreatedObjectUndo(go, "Generate " + go.name);
            SceneView view = SceneView.lastActiveSceneView;
            if (view != null) go.transform.position = view.pivot;
            else go.transform.position = new Vector3(0f, 1f, 0f);
            Type t = FindComponentType(className);
            if (t != null)
            {
                Undo.AddComponent(go, t);
            }
            else
            {
                Debug.LogWarning("[myFSM] '" + className +
                    "' is not compiled yet — press Compile, wait for Unity to finish " +
                    "compiling scripts, select '" + go.name + "' and press Attach.");
            }
            targetP.objectReferenceValue = go;
            targetP.serializedObject.ApplyModifiedProperties();
            EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeGameObject = go;
        }

        private static Type FindComponentType(string className)
        {
            if (string.IsNullOrEmpty(className)) return null;
            System.Reflection.Assembly[] asm = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asm.Length; i++)
            {
                try
                {
                    Type t = asm[i].GetType(className);
                    if (t != null && typeof(Component).IsAssignableFrom(t)) return t;
                }
                catch
                {
                }
            }
            return null;
        }
    }
}
