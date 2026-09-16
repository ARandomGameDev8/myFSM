// myFSM compiler C API — stable ABI for hosts (the Unity C# wrapper calls
// this via P/Invoke; any other engine/FFI can do the same).
//
// Runs the exact CLI pipeline (lex -> parse -> pass1..pass6) on in-memory
// .fsm source and hands malloc'd results to the caller. No file I/O, no
// stdout/stderr, no exceptions escape, no shared state (callable from any
// thread; just don't free one call's buffers from another thread).
//
// Strings are UTF-8. Result codes:
//   MYFSM_OK            (1) compiled; warnings allowed, bytes set
//   MYFSM_COMPILE_ERROR (0) source failed; diagnostics set, no bytes
//   MYFSM_INTERNAL_ERROR(-1) bad args / out of memory / internal fault
#ifndef MYFSM_C_API_H
#define MYFSM_C_API_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) || defined(__CYGWIN__)
#ifdef MYFSM_BUILDING_LIB
#define MYFSM_API __declspec(dllexport)
#else
#define MYFSM_API __declspec(dllimport)
#endif
#else
#define MYFSM_API __attribute__((visibility("default")))
#endif

#define MYFSM_OK 1
#define MYFSM_COMPILE_ERROR 0
#define MYFSM_INTERNAL_ERROR -1

// Emitted .fsmb format version. Guarded by static_assert in c_api.cpp.
#define MYFSM_FSMB_MAJOR 0
#define MYFSM_FSMB_MINOR 5

// Compile .fsm SOURCE TEXT (NUL-terminated UTF-8) to a .fsmb module.
// source_name is used in diagnostic positions (may be NULL -> "<source>").
// On MYFSM_OK: *out_bytes = malloc'd module bytes, *out_len = size.
// *out_diagnostics is ALWAYS a malloc'd NUL string on OK/compile-error
// (warnings, errors, or "" when clean).
// On MYFSM_INTERNAL_ERROR all outs are NULL/0.
// Free out_bytes/out_diagnostics with myfsm_free() (same allocator).
// Never free() them directly, never free the version string.
MYFSM_API int myfsm_compile(const char* source_utf8, const char* source_name,
                            unsigned char** out_bytes, size_t* out_len,
                            char** out_diagnostics);

// Releases buffers returned by myfsm_compile. NULL-safe.
MYFSM_API void myfsm_free(void* ptr);

// Static "major.minor" string, e.g. "0.5". Do NOT free.
MYFSM_API const char* myfsm_version(void);

#ifdef __cplusplus
}
#endif

#endif // MYFSM_C_API_H
