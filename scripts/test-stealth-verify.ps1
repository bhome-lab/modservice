param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$executor = Join-Path $root "artifacts\native\NativeExecutor\x64\$Configuration\NativeExecutor.dll"
$dep      = Join-Path $root "artifacts\native\DepModule\x64\$Configuration\DepModule.dll"
$sample   = Join-Path $root "artifacts\native\SampleModule\x64\$Configuration\SampleModule.dll"
$testApp  = Join-Path $root "artifacts\native\TestApp\x64\$Configuration\TestApp.exe"

Add-Type -TypeDefinition @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
using System.Diagnostics;
public static class Inject {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint X(ref R r, IntPtr eb, uint ec, out uint ew);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibrary(string p);
    [DllImport("kernel32.dll",CharSet=CharSet.Ansi,SetLastError=true)] static extern IntPtr GetProcAddress(IntPtr h, string n);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] struct V { public IntPtr P; public uint L; }
    [StructLayout(LayoutKind.Sequential)] struct E { public V N; public V Val; }
    [StructLayout(LayoutKind.Sequential)] struct R {
        public uint pid; public ulong ct; public V exe;
        public IntPtr mods; public uint mc; public IntPtr env; public uint ec;
        public IntPtr opts; public uint oc; public uint tms;
    }
    static V Mk(string s, List<IntPtr> a) { var p=Marshal.StringToHGlobalUni(s); a.Add(p); return new V{P=p,L=(uint)s.Length}; }
    public static uint Run(string dll, uint pid, ulong ct, string exe, string[] mods, string[] eN, string[] eV, uint tms) {
        var a=new List<IntPtr>(); GCHandle? mh=null,eh=null; IntPtr lib=IntPtr.Zero,eb=IntPtr.Zero;
        try {
            var ev=Mk(exe,a); var mv=new V[mods.Length]; for(int i=0;i<mods.Length;i++) mv[i]=Mk(mods[i],a);
            var es=new E[eN.Length]; for(int i=0;i<eN.Length;i++) es[i]=new E{N=Mk(eN[i],a),Val=Mk(eV[i],a)};
            if(mv.Length>0) mh=GCHandle.Alloc(mv,GCHandleType.Pinned);
            if(es.Length>0) eh=GCHandle.Alloc(es,GCHandleType.Pinned);
            var r=new R{pid=pid,ct=ct,exe=ev,
                mods=mh.HasValue?mh.Value.AddrOfPinnedObject():IntPtr.Zero,mc=(uint)mv.Length,
                env=eh.HasValue?eh.Value.AddrOfPinnedObject():IntPtr.Zero,ec=(uint)es.Length,
                opts=IntPtr.Zero,oc=0,tms=tms};
            lib=LoadLibrary(dll); var p=GetProcAddress(lib,"mm_execute");
            var fn=(X)Marshal.GetDelegateForFunctionPointer(p,typeof(X));
            eb=Marshal.AllocHGlobal(2048); uint ew;
            return fn(ref r,eb,1024,out ew);
        } finally {
            if(mh.HasValue&&mh.Value.IsAllocated) mh.Value.Free();
            if(eh.HasValue&&eh.Value.IsAllocated) eh.Value.Free();
            foreach(var p in a) Marshal.FreeHGlobal(p);
            if(eb!=IntPtr.Zero) Marshal.FreeHGlobal(eb);
            if(lib!=IntPtr.Zero) FreeLibrary(lib);
        }
    }
}
"@

Write-Host "=== Stealth Verification Test ===" -ForegroundColor Cyan
$outFile = Join-Path $env:TEMP ("mm_stealth_" + [guid]::NewGuid().ToString('N') + '.txt')

foreach ($targetExe in @($testApp, 'notepad.exe')) {
    $targetName = [IO.Path]::GetFileNameWithoutExtension($targetExe)
    Write-Host "`n--- $targetName ---"

    $proc = Start-Process -FilePath $targetExe -PassThru
    Start-Sleep -Milliseconds 1000
    $proc.Refresh()

    try {
        # Snapshot modules BEFORE injection
        $modulesBefore = (Get-Process -Id $proc.Id).Modules | ForEach-Object { $_.ModuleName.ToLower() }

        # Inject
        $st = [Inject]::Run($executor, [uint32]$proc.Id,
            [uint64]$proc.StartTime.ToUniversalTime().ToFileTimeUtc(),
            $proc.MainModule.FileName,
            @($dep, $sample),
            @('MODSERVICE_SAMPLE_OUTPUT', 'MODSERVICE_SAMPLE_MARKER'),
            @($outFile, 'stealth-test'),
            15000)
        Write-Host "  Injection status: $st"

        Start-Sleep -Milliseconds 500

        # Verify output (proves DLLs executed)
        $output = if (Test-Path $outFile) {
            [Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes($outFile)).Replace([string][char]0,'').Trim()
        } else { '<no output>' }
        $executed = ($output -eq 'stealth-test:42')
        Write-Host "  DLLs executed: $executed (output='$output')"

        # Snapshot modules AFTER injection
        $modulesAfter = (Get-Process -Id $proc.Id).Modules | ForEach-Object { $_.ModuleName.ToLower() }

        # Check: SampleModule, DepModule, CppFeatureTest should NOT appear
        $leaked = @()
        foreach ($name in @('samplemodule.dll', 'depmodule.dll', 'cppfeaturetest.dll', 'nativeexecutor.dll')) {
            if ($modulesAfter -contains $name) {
                if ($modulesBefore -notcontains $name) {
                    $leaked += $name
                }
            }
        }

        if ($leaked.Count -eq 0) {
            Write-Host "  [PASS] STEALTH: No injected DLLs visible in module list" -ForegroundColor Green
        } else {
            Write-Host "  [FAIL] STEALTH: Found in module list: $($leaked -join ', ')" -ForegroundColor Red
        }

        # Show any NEW modules that appeared (for analysis)
        $newMods = $modulesAfter | Where-Object { $modulesBefore -notcontains $_ }
        if ($newMods.Count -gt 0) {
            Write-Host "  New modules after injection: $($newMods -join ', ')" -ForegroundColor Yellow
        } else {
            Write-Host "  No new modules appeared in PEB" -ForegroundColor Green
        }

    } finally {
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
        Remove-Item $outFile -ErrorAction SilentlyContinue
    }
}
