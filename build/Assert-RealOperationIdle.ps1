Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ('WinPoolBuildSqliteProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class WinPoolBuildSqliteProbe
{
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out IntPtr database, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr database);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr database,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length,
        out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern long sqlite3_column_int64(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_errmsg(IntPtr database);

    private static string Error(IntPtr database) =>
        Marshal.PtrToStringUTF8(sqlite3_errmsg(database)) ?? "SQLite error";

    private static long Scalar(IntPtr database, string sql)
    {
        IntPtr statement;
        int result = sqlite3_prepare_v2(database, sql, -1, out statement, IntPtr.Zero);
        if (result != 0) throw new InvalidOperationException(Error(database));
        try
        {
            if (sqlite3_step(statement) != 100)
                throw new InvalidOperationException(Error(database));
            return sqlite3_column_int64(statement, 0);
        }
        finally { sqlite3_finalize(statement); }
    }

    private static string ScalarText(IntPtr database, string sql)
    {
        IntPtr statement;
        int result = sqlite3_prepare_v2(database, sql, -1, out statement, IntPtr.Zero);
        if (result != 0) throw new InvalidOperationException(Error(database));
        try
        {
            if (sqlite3_step(statement) != 100)
                throw new InvalidOperationException(Error(database));
            return Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 0)) ?? "";
        }
        finally { sqlite3_finalize(statement); }
    }

    public static bool HasUnfinishedRealOperation(string path)
    {
        IntPtr database;
        int result = sqlite3_open_v2(path, out database, 1, IntPtr.Zero);
        if (result != 0)
        {
            string message = database == IntPtr.Zero ? "Could not open database" : Error(database);
            if (database != IntPtr.Zero) sqlite3_close(database);
            throw new InvalidOperationException(message);
        }
        try
        {
            if (ScalarText(database, "PRAGMA quick_check;") != "ok")
                throw new InvalidOperationException("SQLite quick_check failed.");
            long version = Scalar(database,
                "SELECT schema_version FROM schema_info WHERE singleton = 1;");
            if (version != 17 && version != 18)
                throw new InvalidOperationException("Unsupported core schema " + version + ".");
            return Scalar(database,
                "SELECT COUNT(*) FROM operation_plans WHERE risk >= 4 " +
                "AND state NOT IN (4,5,6,7,10);") != 0;
        }
        finally { sqlite3_close(database); }
    }
}
'@
}

function Assert-WinPoolRealOperationIdle {
    param([Parameter(Mandatory)][string]$RuntimeRoot,
          [string]$StandardRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WinPool'))

    $runtime = [IO.Path]::GetFullPath($RuntimeRoot)
    $standard = [IO.Path]::GetFullPath($StandardRoot)
    $portable = Join-Path $runtime 'Data'
    $externalPointer = $standard + '.storage-location.json'
    $legacyPointer = Join-Path $standard 'storage-location.json'
    foreach ($path in @($standard, $portable, $externalPointer, $legacyPointer)) {
        $cursor = [IO.Path]::GetFullPath($path)
        while ($cursor) {
            $item = Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
            if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "WinPool data location uses a link: $cursor"
            }
            $parent = Split-Path -Parent $cursor
            if (-not $parent -or $parent -eq $cursor) { break }
            $cursor = $parent
        }
    }
    $pointerPaths = @(@($externalPointer, $legacyPointer) | Where-Object { Test-Path -LiteralPath $_ })
    if ($pointerPaths.Count -gt 1) {
        throw 'Both WinPool data-location pointers exist; runtime replacement requires resolving this ambiguity.'
    }
    $mode = $null
    if ($pointerPaths.Count -eq 1) {
        try {
            $pointer = Get-Content -LiteralPath $pointerPaths[0] -Raw | ConvertFrom-Json -ErrorAction Stop
            if ($null -eq $pointer.mode -or [string]$pointer.mode -notin @('Standard', 'Portable', '0', '1')) {
                throw 'Invalid data-location mode.'
            }
            $mode = if ([string]$pointer.mode -in @('Portable', '1')) { 'Portable' } else { 'Standard' }
        } catch {
            throw "WinPool data-location pointer is unreadable: $($pointerPaths[0]). $($_.Exception.Message)"
        }
    } elseif (Test-Path -LiteralPath (Join-Path $portable 'settings.json')) {
        $mode = 'Portable'
    } else {
        $mode = 'Standard'
    }

    if ($pointerPaths.Count -eq 0 -and
        (Test-Path -LiteralPath (Join-Path $standard 'winpool.db')) -and
        (Test-Path -LiteralPath (Join-Path $portable 'winpool.db'))) {
        throw 'Both WinPool data roots contain a database without an authoritative pointer.'
    }
    if ($pointerPaths.Count -eq 0 -and
        (Test-Path -LiteralPath (Join-Path $portable 'winpool.db')) -and
        -not (Test-Path -LiteralPath (Join-Path $portable 'settings.json'))) {
        throw 'Portable WinPool database exists without its settings or an authoritative pointer.'
    }

    $activeRoot = if ($mode -eq 'Portable') { $portable } else { $standard }
    $database = Join-Path $activeRoot 'winpool.db'
    if (-not (Test-Path -LiteralPath $database -PathType Leaf)) {
        $hasContent = (Test-Path -LiteralPath $activeRoot) -and
            (@(Get-ChildItem -LiteralPath $activeRoot -Force -ErrorAction Stop).Count -gt 0)
        $hasProcess = @(Get-Process -Name 'WinPool.App', 'WinPool.Agent' -ErrorAction SilentlyContinue).Count -gt 0
        if ($pointerPaths.Count -gt 0 -or $hasContent -or $hasProcess) {
            throw "Active WinPool data root has no readable core database: $activeRoot"
        }
        return
    }
    if ((Get-Item -LiteralPath $database -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Core database is a link: $database"
    }
    try {
        if ([WinPoolBuildSqliteProbe]::HasUnfinishedRealOperation($database)) {
            throw "Unfinished real operation in active WinPool database: $database"
        }
    } catch {
        throw "Runtime replacement refused: $($_.Exception.Message)"
    }
}
