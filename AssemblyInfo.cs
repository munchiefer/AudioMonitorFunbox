using System.Runtime.InteropServices;

// Native DLLs (Mmdevapi, kernel32, user32, ...) are only ever loaded from System32, never from the
// folder the exe sits in, so a DLL planted next to the exe (e.g. in Downloads) can't be picked up.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
