param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$executor = Join-Path $root "artifacts\native\NativeExecutor\x64\$Configuration\NativeExecutor.dll"
$dep      = Join-Path $root "artifacts\native\DepModule\x64\$Configuration\DepModule.dll"
$sample   = Join-Path $root "artifacts\native\SampleModule\x64\$Configuration\SampleModule.dll"

Add-Type -TypeDefinition @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class MM {
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
    public static string Go(string dll, uint pid, ulong ct, string exe, string[] mods, string[] eN, string[] eV, uint tms) {
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
            var st=fn(ref r,eb,1024,out ew); var msg=ew>0?Marshal.PtrToStringUni(eb,(int)ew):"";
            return "status="+st+(msg.Length>0?" err=["+msg+"]":"");
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

function Test-Target($name, $exe) {
    $outFile = Join-Path $env:TEMP ("mm_multi_" + [guid]::NewGuid().ToString('N') + '.txt')
    $proc = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Milliseconds 1500
    $proc.Refresh()
    try {
        $r = [MM]::Go($executor, [uint32]$proc.Id,
            [uint64]$proc.StartTime.ToUniversalTime().ToFileTimeUtc(),
            $proc.MainModule.FileName,
            @($dep, $sample),
            @('MODSERVICE_SAMPLE_OUTPUT', 'MODSERVICE_SAMPLE_MARKER'),
            @($outFile, "$name-injected"),
            15000)

        Start-Sleep -Milliseconds 500
        $out = if (Test-Path $outFile) {
            [Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes($outFile)).Replace([string][char]0,'').Trim()
        } else { '<no output>' }

        $expected = "$name-injected:42"
        $pass = ($out -eq $expected)
        $icon = if ($pass) { 'PASS' } else { 'FAIL' }
        Write-Host "  [$icon] $name (PID $($proc.Id)): $r | output='$out'" -ForegroundColor $(if($pass){'Green'}else{'Red'})
        return $pass
    } finally {
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
        Remove-Item $outFile -ErrorAction SilentlyContinue
    }
}

Write-Host "=== Multi-Target Manual Mapping Test ===" -ForegroundColor Cyan
Write-Host "Testing: DepModule (dependency) + SampleModule (cross-module import)" -ForegroundColor Cyan
Write-Host ""

$targets = @(
    @{ Name='TestApp';  Exe=(Join-Path $root "artifacts\native\TestApp\x64\$Configuration\TestApp.exe") },
    @{ Name='Notepad';  Exe='notepad.exe' },
    @{ Name='MSPaint';  Exe='mspaint.exe' },
    @{ Name='Calc';     Exe='calc.exe' }
)

$passed = 0; $failed = 0
foreach ($t in $targets) {
    try {
        if (Test-Target $t.Name $t.Exe) { $passed++ } else { $failed++ }
    } catch {
        Write-Host "  [SKIP] $($t.Name): $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Results: $passed passed, $failed failed" -ForegroundColor $(if($failed -gt 0){'Red'}else{'Green'})
