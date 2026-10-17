using System.Diagnostics;
using System.Runtime.CompilerServices;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

internal static class TestDiagnostics
{
    [ModuleInitializer]
    internal static void Initialize() => StorageOperationTiming.Sink = static entry => Trace.WriteLine(entry);
}
