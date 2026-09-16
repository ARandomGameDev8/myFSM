// myFSM Unity Runtime — inspector for the burst compiler.
//
// EDITOR ONLY (this folder never ships in builds): per-entry Compile, folder
// Sweep, and the Generate/Attach workflow. Generate creates a GameObject in
// the scene at the SceneView pivot with the entry's generated script
// attached — edit mode only, never in play mode.

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

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Sweep Folder"))
            {
                serializedObject.ApplyModifiedProperties();
                int n = c.SweepFolder();
                Debug.Log("[myFSM] sweep added " + n + " entr" +
                          (n == 1 ? "y" : "ies") + ".");
            }
            if (GUILayout.Button("Compile All"))
            {
                serializedObject.ApplyModifiedProperties();
                FsmBurstStats s = c.CompileAll();
                AssetDatabase.Refresh();
                Debug.Log("[myFSM] burst: " + s.Succeeded + "/" + s.Total + " compiled.");
            }
            if (GUILayout.Button("Sweep + Compile"))
            {
                serializedObject.ApplyModifiedProperties();
                int n = c.SweepFolder();
                FsmBurstStats s = c.CompileAll();
                AssetDatabase.Refresh();
                Debug.Log("[myFSM] sweep added " + n + "; burst: " +
                          s.Succeeded + "/" + s.Total + " compiled.");
            }
            EditorGUILayout.EndHorizontal();

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
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.PropertyField(ep.FindPropertyRelative("Name"));
            DrawFileRow(ep);
            EditorGUILayout.PropertyField(ep.FindPropertyRelative("OutputFolder"));
            EditorGUILayout.PropertyField(ep.FindPropertyRelative("ClassName"));
            EditorGUILayout.PropertyField(ep.FindPropertyRelative("ScriptFolder"));
            SerializedProperty targetP = ep.FindPropertyRelative("Target");
            EditorGUILayout.PropertyField(targetP);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Compile", GUILayout.Width(90)))
            {
                serializedObject.ApplyModifiedProperties();
                if (c.Entries != null && i < c.Entries.Count)
                    c.CompileEntry(c.Entries[i]);
                AssetDatabase.Refresh();
            }
            bool playing = Application.isPlaying;
            if (!playing && targetP.objectReferenceValue == null)
            {
                if (GUILayout.Button("Generate", GUILayout.Width(90)))
                {
                    serializedObject.ApplyModifiedProperties();
                    if (c.Entries != null && i < c.Entries.Count)
                        GenerateForEntry(c.Entries[i], targetP);
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
                        serializedObject.ApplyModifiedProperties();
                        FsmBurstEntry fresh = (c.Entries != null && i < c.Entries.Count)
                            ? c.Entries[i] : null;
                        Type t2 = FindComponentType(EffectiveClassName(fresh));
                        if (t2 != null && go.GetComponent(t2) == null)
                        {
                            Undo.AddComponent(go, t2);
                            EditorSceneManager.MarkSceneDirty(go.scene);
                        }
                    }
                }
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("X", GUILayout.Width(28)))
            {
                _entries.DeleteArrayElementAtIndex(i);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                return;
            }
            EditorGUILayout.EndHorizontal();

            FsmBurstEntry cur = (c.Entries != null && i < c.Entries.Count)
                ? c.Entries[i] : null;
            if (cur != null)
            {
                EditorGUILayout.HelpBox(cur.LastStatus,
                    cur.LastOk ? MessageType.Info : MessageType.None);
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawFileRow(SerializedProperty ep)
        {
            SerializedProperty p = ep.FindPropertyRelative("FsmPath");
            EditorGUILayout.BeginHorizontal();
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
            EditorGUILayout.EndHorizontal();
        }

        private void DrawFolderRow(SerializedProperty p)
        {
            EditorGUILayout.BeginHorizontal();
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
            EditorGUILayout.EndHorizontal();
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
