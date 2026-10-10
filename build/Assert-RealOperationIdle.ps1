Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ('WinPoolBuildSqliteProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class WinPoolBuildSqliteProbe
{
    public sealed class UnfinishedRealOperation
    {
        public string OperationId { get; }
        public string StateName { get; }

        internal UnfinishedRealOperation(string operationId, long state)
        {
            Guid parsed;
            OperationId = Guid.TryParse(operationId, out parsed)
                ? parsed.ToString("D") : "(invalid OperationId)";
            StateName = state switch
            {
                0 => "Planned",
                1 => "AwaitingAuthorization",
                2 => "Authorized",
                3 => "Running",
                8 => "Prepared",
                9 => "Accepted",
                11 => "OutcomeUnknown",
                _ => "Unknown(" + state + ")"
            };
        }
    }

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

    public static bool HasUnfinishedRealOperation(string path) =>
        ProbeUnfinishedRealOperations(path).Length != 0;

    public static UnfinishedRealOperation[] ProbeUnfinishedRealOperations(string path, bool includeUnknown = true)
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
            if (version != 17 && version != 18 && version != 19)
                throw new InvalidOperationException("Unsupported core schema " + version + ".");
            // An unknown result remains a product write barrier, but once the
            // runtime is stopped it must not prevent installing a repair.
            // Filter before LIMIT so unknown rows cannot hide an active call.
            IntPtr statement;
            result = sqlite3_prepare_v2(database,
                "SELECT substr(operation_id,1,64), state FROM operation_plans WHERE risk >= 4 " +
                "AND state NOT IN (4,5,6,7,10" + (includeUnknown ? "" : ",11") +
                ") ORDER BY operation_id LIMIT 10;",
                -1, out statement, IntPtr.Zero);
            if (result != 0) throw new InvalidOperationException(Error(database));
            try
            {
                var operations = new List<UnfinishedRealOperation>();
                while ((result = sqlite3_step(statement)) == 100)
                {
                    operations.Add(new UnfinishedRealOperation(
                        Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 0)) ?? "",
                        sqlite3_column_int64(statement, 1)));
                }
                if (result != 101) throw new InvalidOperationException(Error(database));
                return operations.ToArray();
            }
            finally { sqlite3_finalize(statement); }
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
        $hasLiveWinPool = @(Get-Process -Name 'WinPool.App', 'WinPool.Agent' -ErrorAction SilentlyContinue).Count -gt 0
        $operations = @([WinPoolBuildSqliteProbe]::ProbeUnfinishedRealOperations($database, $hasLiveWinPool))
        if ($operations.Count -gt 0) {
            $details = ($operations | ForEach-Object { "OperationId=$($_.OperationId), state=$($_.StateName)" }) -join '; '
            throw "Unfinished real operation in active WinPool database: $database. Blocking operations (up to 10): $details. Start WinPool from the existing runtime tree and query by OperationId for read-only reconciliation before retrying runtime replacement."
        }
    } catch {
        throw "Runtime replacement refused: $($_.Exception.Message)"
    }
}
