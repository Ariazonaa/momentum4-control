// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Runtime;
using System.Runtime.InteropServices;

namespace Momentum4.App;

/// <summary>
/// Nach dem Schließen des Fensters: verwaisten Speicher einsammeln und den Arbeitssatz verkleinern (docs/architecture.md
/// §1 Punkt 8, §14 Stufe 2). Windows lädt zurückgegebene Seiten bei Bedarf wieder; im Tray-Betrieb wird kaum etwas
/// davon gebraucht.
/// </summary>
internal static partial class MemoryTrim
{
    public static void Run()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSize(nint process, nint minimum, nint maximum);
}
