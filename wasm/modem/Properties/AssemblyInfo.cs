using System.Runtime.Versioning;

// This assembly only ever runs in the WebAssembly runtime, which is what makes the
// [JSExport] surface in Program.cs legal (CA1416).
[assembly: SupportedOSPlatform("browser")]
