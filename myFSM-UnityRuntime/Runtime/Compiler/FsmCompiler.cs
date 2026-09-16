// myFSM Unity Runtime — C# wrapper around the native fsmc compiler API.
//
// Manual compile (one file), dynamic compile (any script, any time), and the
// burst compiler component all go through here. The native library
// (myfsmc.dll / libmyfsmc.so / libmyfsmc.dylib — built from fsmc/ via the
// `myfsmc` CMake target) must sit in a Plugins folder. When it is missing,
// every call fails gracefully with Error set: this wrapper never throws and
// never crashes Unity, it just reports.
//
// Threading: the native side holds no shared state, so concurrent compiles
// are safe. Availability is probed once and cached.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MyFSM.Compiler
{
    /// <summary>Result of one compile. Never null — check Ok first.</summary>
    public sealed class FsmCompileResult
    {
        public bool Ok;
        public byte[] Module;      // .fsmb bytes; set only when Ok
        public string Diagnostics; // compiler warnings (Ok) or errors (!Ok); "" when clean
        public string Error;       // wrapper-level failure (missing lib, I/O...); null otherwise

        public static FsmCompileResult Fail(string error)
        {
            return new FsmCompileResult
            {
                Ok = false,
                Diagnostics = string.Empty,
                Error = error
            };
        }
    }

    /// <summary>
    /// Static compile API. Manual: CompileFile(path). Dynamic: CompileSource
    /// from any script at edit time or at runtime (desktop players ship the
    /// native library; constrained platforms must use precompiled .fsmb).
    /// </summary>
    public static class FsmCompiler
    {
        public const string NativeLibName = "myfsmc";
        public const int RcOk = 1;
        public const int RcCompileError = 0;
        public const int RcInternalError = -1;

        private static bool _probed;
        private static bool _available;
        private static string _version;

        /// <summary>True when the native library loads and answers. Cached.</summary>
        public static bool IsAvailable
        {
            get { EnsureProbed(); return _available; }
        }

        /// <summary>Native format version ("0.5"), or null when unavailable.</summary>
        public static string NativeVersion
        {
            get { EnsureProbed(); return _version; }
        }

        private static void EnsureProbed()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                IntPtr v = Native.myfsm_version();
                _version = Utf8PtrToString(v); // static string: do NOT free
                _available = !string.IsNullOrEmpty(_version);
            }
            catch (DllNotFoundException) { _available = false; }
            catch (EntryPointNotFoundException) { _available = false; }
            catch (BadImageFormatException) { _available = false; }
            catch (Exception) { _available = false; } // a probe must never throw
        }

        /// <summary>Compiles .fsm SOURCE TEXT. sourceName labels diagnostics.</summary>
        public static FsmCompileResult CompileSource(string source, string sourceName)
        {
            if (string.IsNullOrEmpty(source))
                return FsmCompileResult.Fail("no source text given");
            if (!IsAvailable)
                return FsmCompileResult.Fail("native compiler library '" + NativeLibName +
                    "' not found for this platform; precompile with the fsmc CLI instead");
            IntPtr srcPtr = IntPtr.Zero;
            IntPtr namePtr = IntPtr.Zero;
            IntPtr bytesPtr = IntPtr.Zero;
            IntPtr diagsPtr = IntPtr.Zero;
            try
            {
                srcPtr = StringToUtf8Ptr(source);
                if (!string.IsNullOrEmpty(sourceName))
                    namePtr = StringToUtf8Ptr(sourceName);
                UIntPtr len;
                int rc = Native.myfsm_compile(srcPtr, namePtr, out bytesPtr,
                                              out len, out diagsPtr);
                string diags = Utf8PtrToString(diagsPtr);
                if (diags == null) diags = string.Empty;
                if (rc == RcOk)
                {
                    ulong n = len.ToUInt64();
                    if (bytesPtr == IntPtr.Zero || n == 0 || n > int.MaxValue)
                        return FsmCompileResult.Fail("native compiler returned no bytes");
                    byte[] module = new byte[(int)n];
                    Marshal.Copy(bytesPtr, module, 0, (int)n);
                    return new FsmCompileResult
                    {
                        Ok = true,
                        Module = module,
                        Diagnostics = diags
                    };
                }
                if (rc == RcCompileError)
                    return new FsmCompileResult { Ok = false, Diagnostics = diags };
                return FsmCompileResult.Fail("native compiler internal error" +
                    (diags.Length > 0 ? ": " + diags : string.Empty));
            }
            catch (Exception ex)
            {
                return FsmCompileResult.Fail("compiler call failed: " + ex.Message);
            }
            finally
            {
                if (srcPtr != IntPtr.Zero) Marshal.FreeHGlobal(srcPtr);
                if (namePtr != IntPtr.Zero) Marshal.FreeHGlobal(namePtr);
                if (bytesPtr != IntPtr.Zero) Native.myfsm_free(bytesPtr);
                if (diagsPtr != IntPtr.Zero) Native.myfsm_free(diagsPtr);
            }
        }

        /// <summary>
        /// Compiles one .fsm FILE and writes the .fsmb to fsmbPath. Nothing is
        /// written unless compilation succeeds; a failed write deletes the
        /// partial file (mirrors the CLI contract).
        /// </summary>
        public static FsmCompileResult CompileFile(string fsmPath, string fsmbPath)
        {
            if (string.IsNullOrEmpty(fsmPath))
                return FsmCompileResult.Fail("no .fsm path given");
            if (string.IsNullOrEmpty(fsmbPath))
                return FsmCompileResult.Fail("no .fsmb output path given");
            string source;
            try
            {
                source = File.ReadAllText(fsmPath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                return FsmCompileResult.Fail("cannot read '" + fsmPath + "': " + ex.Message);
            }
            FsmCompileResult r = CompileSource(source, fsmPath);
            if (!r.Ok) return r;
            try
            {
                string dir = Path.GetDirectoryName(fsmbPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(fsmbPath, r.Module);
            }
            catch (Exception ex)
            {
                try
                {
                    if (File.Exists(fsmbPath)) File.Delete(fsmbPath);
                }
                catch
                {
                }
                r.Ok = false;
                r.Module = null;
                r.Error = "cannot write '" + fsmbPath + "': " + ex.Message;
                return r;
            }
            return r;
        }

        private static class Native
        {
            [DllImport(FsmCompiler.NativeLibName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int myfsm_compile(IntPtr sourceUtf8, IntPtr sourceNameUtf8,
                out IntPtr outBytes, out UIntPtr outLen, out IntPtr outDiagnostics);

            [DllImport(FsmCompiler.NativeLibName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void myfsm_free(IntPtr ptr);

            [DllImport(FsmCompiler.NativeLibName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr myfsm_version();
        }

        private static IntPtr StringToUtf8Ptr(string s)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(s);
            IntPtr p = Marshal.AllocHGlobal(utf8.Length + 1);
            Marshal.Copy(utf8, 0, p, utf8.Length);
            Marshal.WriteByte(p, utf8.Length, 0);
            return p;
        }

        // Marshal.PtrToStringUTF8 does not exist on Unity 2019's
        // netstandard2.0, so NUL-terminated UTF-8 is decoded manually.
        private static string Utf8PtrToString(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            int n = 0;
            while (Marshal.ReadByte(p, n) != 0) n++;
            if (n == 0) return string.Empty;
            byte[] utf8 = new byte[n];
            Marshal.Copy(p, utf8, 0, n);
            return Encoding.UTF8.GetString(utf8);
        }
    }
}
