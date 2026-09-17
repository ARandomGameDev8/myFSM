// myFSM Unity Runtime — burst compiler component.
//
// Edit-mode workflow, runtime-safe code (no UnityEditor references): each
// entry maps one .fsm file -> one .fsmb file (+ validates it through the
// REAL loader) -> one auto-generated AI script, linked to a GameObject.
// The custom inspector (Editor/) adds per-entry Compile, folder Sweep, and
// the Generate/Attach buttons; at runtime any script can call CompileEntry
// / CompileAll / SweepFolder directly wherever files + the native library
// exist (desktop).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using MyFSM.Compiler;
using MyFSM.Core;

namespace MyFSM.Unity
{
    /// <summary>One fsm -> fsmb -> generated-script mapping.</summary>
    [Serializable]
    public sealed class FsmBurstEntry
    {
        public string Name = "NewAI";
        [Tooltip("Project-relative .fsm path, e.g. Assets/MyFSM/Fsm/Guard.fsm")]
        public string FsmPath = "Assets/MyFSM/Fsm/NewAI.fsm";
        [Tooltip("Empty = component default. Should contain /Resources/ so generated classes can load it by path.")]
        public string OutputFolder = "";
        [Tooltip("Empty = derived <FileStem>AI.")]
        public string ClassName = "";
        [Tooltip("Empty = component default.")]
        public string ScriptFolder = "";
        [Tooltip("GameObject to attach the generated script to. Empty -> use Generate in the inspector (edit mode).")]
        public GameObject Target;
        [NonSerialized] public bool LastOk;
        [NonSerialized] public string LastStatus = "not compiled yet";
    }

    public struct FsmBurstStats
    {
        public int Total;
        public int Succeeded;
    }

    public sealed class FsmBurstCompiler : MonoBehaviour
    {
        [Tooltip("Compiled .fsmb files land here unless an entry overrides it. Keep under Resources/ so generated classes load by path.")]
        public string DefaultOutputFolder = "Assets/MyFSM/Resources/MyFSM";
        [Tooltip("Generated C# scripts land here unless an entry overrides it.")]
        public string DefaultScriptFolder = "Assets/MyFSM";
        [Tooltip("SweepFolder() adds one entry per *.fsm found here.")]
        public string InputFolder = "Assets/MyFSM/Fsm";
        public List<FsmBurstEntry> Entries = new List<FsmBurstEntry>();

        /// <summary>
        /// Project-relative "Assets/..." -> absolute path (editor): joins the
        /// path with the PROJECT root (the parent of Application.dataPath's
        /// trailing "Assets"), keeping the prefix, so "Assets/A/B.fsm" ->
        /// "&lt;project&gt;/Assets/A/B.fsm". Absolute paths pass through
        /// untouched: runtime callers outside the project must pass absolute
        /// paths, since asset-relative paths are an editor convention.
        /// </summary>
        public static string ResolveProjectPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return assetPath;
            string p = assetPath.Replace('\\', '/');
            if (Path.IsPathRooted(p)) return p;
            string assets = Application.dataPath.Replace('\\', '/');
            string project = assets;
            int i = assets.LastIndexOf("/Assets", StringComparison.Ordinal);
            if (i >= 0) project = assets.Substring(0, i);
            return project + "/" + p;
        }

        /// <summary>Absolute path -> "Assets/..." when inside the project, else null.</summary>
        public static string ToAssetPath(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return null;
            string a = absolutePath.Replace('\\', '/');
            string assets = Application.dataPath.Replace('\\', '/');
            if (a == assets) return "Assets";
            if (a.StartsWith(assets + "/", StringComparison.Ordinal))
                return "Assets" + a.Substring(assets.Length);
            return null;
        }

        public static string CombineAssetPath(string folder, string file)
        {
            string f = (folder ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            if (f.Length == 0) return file;
            return f + "/" + file;
        }

        /// <summary>
        /// "Assets/X/Resources/MyFSM/Guard.fsmb" -> "MyFSM/Guard"; null when
        /// the path is not under a Resources folder (generated class then
        /// needs the .fsmb assigned in the inspector instead).
        /// </summary>
        public static string ResourcePathOf(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            string p = assetPath.Replace('\\', '/');
            const string marker = "/Resources/";
            int i = p.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return null;
            string rel = p.Substring(i + marker.Length);
            if (rel.EndsWith(".fsmb", StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring(0, rel.Length - ".fsmb".Length);
            return rel;
        }

        public FsmBurstEntry FindEntryByFsm(string fsmAssetPath)
        {
            if (Entries == null) return null;
            for (int i = 0; i < Entries.Count; i++)
            {
                FsmBurstEntry e = Entries[i];
                if (e == null || e.FsmPath == null) continue;
                if (e.FsmPath.Replace('\\', '/').Equals(
                        (fsmAssetPath ?? string.Empty).Replace('\\', '/'),
                        StringComparison.OrdinalIgnoreCase))
                    return e;
            }
            return null;
        }

        /// <summary>
        /// Adds one entry per *.fsm in InputFolder (skips files that already
        /// have an entry). Returns entries added.
        /// </summary>
        public int SweepFolder()
        {
            if (Entries == null) Entries = new List<FsmBurstEntry>();
            string dir = ResolveProjectPath(InputFolder);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.fsm");
            }
            catch
            {
                return 0;
            }
            Array.Sort(files, StringComparer.Ordinal);
            int added = 0;
            for (int i = 0; i < files.Length; i++)
            {
                string asset = ToAssetPath(files[i]);
                if (asset == null) continue;
                if (FindEntryByFsm(asset) != null) continue;
                FsmBurstEntry e = new FsmBurstEntry();
                e.Name = Path.GetFileNameWithoutExtension(asset);
                e.FsmPath = asset;
                Entries.Add(e);
                added++;
            }
            return added;
        }

        /// <summary>
        /// Compiles ONE entry: .fsm -> .fsmb (+ validates it through the real
        /// loader) -> generated C# script. Returns true on success;
        /// entry.LastOk/LastStatus always describe the outcome.
        /// </summary>
        public bool CompileEntry(FsmBurstEntry e)
        {
            if (e == null) return false;
            e.LastOk = false;
            if (string.IsNullOrEmpty(e.FsmPath))
            {
                e.LastStatus = "no .fsm path";
                return false;
            }
            string fsmAbs = ResolveProjectPath(e.FsmPath);
            if (!File.Exists(fsmAbs))
            {
                e.LastStatus = "missing .fsm: " + e.FsmPath +
                               " (looked for " + fsmAbs + ")";
                return false;
            }
            string stem = Path.GetFileNameWithoutExtension(fsmAbs);
            string className = string.IsNullOrEmpty(e.ClassName)
                ? ClassGenerator.SanitizeIdentifier(stem) + "AI"
                : e.ClassName;
            string outFolder = string.IsNullOrEmpty(e.OutputFolder)
                ? DefaultOutputFolder : e.OutputFolder;
            string scriptFolder = string.IsNullOrEmpty(e.ScriptFolder)
                ? DefaultScriptFolder : e.ScriptFolder;
            string fsmbAsset = CombineAssetPath(outFolder, stem + ".fsmb");
            FsmCompileResult r = FsmCompiler.CompileFile(fsmAbs, ResolveProjectPath(fsmbAsset));
            if (!r.Ok)
            {
                e.LastStatus = !string.IsNullOrEmpty(r.Error) ? r.Error : r.Diagnostics;
                return false;
            }
            FsmbModule module;
            string readErr;
            if (!FsmbReader.Read(r.Module, out module, out readErr) ||
                !FsmbReader.Validate(module, out readErr))
            {
                e.LastStatus = "fresh .fsmb failed loader validation: " + readErr;
                return false;
            }
            string resourcePath = ResourcePathOf(fsmbAsset);
            string src = ClassGenerator.GenerateSource(module, stem, className,
                resourcePath != null ? resourcePath : string.Empty);
            string csAsset = CombineAssetPath(scriptFolder, className + ".cs");
            try
            {
                string dir = Path.GetDirectoryName(ResolveProjectPath(csAsset));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(ResolveProjectPath(csAsset), src, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                e.LastStatus = "cannot write script: " + ex.Message;
                return false;
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("ok: ").Append(r.Module.Length).Append(" bytes, ")
              .Append(module.States.Count).Append(" states -> ")
              .Append(fsmbAsset).Append(" + ").Append(csAsset);
            if (resourcePath == null)
                sb.Append(" [NOT under Resources/ - assign the .fsmb in the inspector]");
            if (!string.IsNullOrEmpty(r.Diagnostics)) sb.Append(" [warnings]");
            e.LastStatus = sb.ToString();
            e.LastOk = true;
            return true;
        }

        /// <summary>Compiles every entry in order. Returns tallies.</summary>
        public FsmBurstStats CompileAll()
        {
            FsmBurstStats s = new FsmBurstStats();
            if (Entries == null) return s;
            s.Total = Entries.Count;
            s.Succeeded = 0;
            for (int i = 0; i < Entries.Count; i++)
            {
                if (CompileEntry(Entries[i])) s.Succeeded++;
            }
            return s;
        }
    }
}
