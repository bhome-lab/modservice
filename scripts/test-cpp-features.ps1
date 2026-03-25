param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$executor = Join-Path $root "artifacts\native\NativeExecutor\x64\$Configuration\NativeExecutor.dll"
$dep      = Join-Path $root "artifacts\native\DepModule\x64\$Configuration\DepModule.dll"
$feature  = Join-Path $root "artifacts\native\CppFeatureTest\x64\$Configuration\CppFeatureTest.dll"
$testApp  = Join-Path $root "artifacts\native\TestApp\x64\$Configuration\TestApp.exe"

foreach ($f in @($executor, $dep, $feature, $testApp)) {
    if (-not (Test-Path $f)) { throw "Missing: $f" }
}

# Copy DepModule next to CppFeatureTest (dependency resolution)
Copy-Item $dep (Split-Path $feature) -Force

Add-Type -TypeDefinition @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class TestHarness {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint MmExec(ref Req r, IntPtr eb, uint ec, out uint ew);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibrary(string p);
    [DllImport("kernel32.dll",CharSet=CharSet.Ansi,SetLastError=true)] static extern IntPtr GetProcAddress(IntPtr h, string n);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] struct V { public IntPtr P; public uint L; }
    [StructLayout(LayoutKind.Sequential)] struct EV { public V N; public V Val; }
    [StructLayout(LayoutKind.Sequential)] struct Req {
        public uint pid; public ulong ct; public V exe;
        public IntPtr mods; public uint mc; public IntPtr env; public uint ec;
        public IntPtr opts; public uint oc; public uint tms;
    }
    static V Mk(string s, List<IntPtr> a) { var p=Marshal.StringToHGlobalUni(s); a.Add(p); return new V{P=p,L=(uint)s.Length}; }
    public static string Run(string dll, uint pid, ulong ct, string exe, string[] mods, string[] eN, string[] eV, uint tms) {
        var a=new List<IntPtr>(); GCHandle? mh=null,eh=null; IntPtr lib=IntPtr.Zero,eb=IntPtr.Zero;
        try {
            var ev=Mk(exe,a); var mv=new V[mods.Length]; for(int i=0;i<mods.Length;i++) mv[i]=Mk(mods[i],a);
            var envs=new EV[eN.Length]; for(int i=0;i<eN.Length;i++) envs[i]=new EV{N=Mk(eN[i],a),Val=Mk(eV[i],a)};
            if(mv.Length>0) mh=GCHandle.Alloc(mv,GCHandleType.Pinned);
            if(envs.Length>0) eh=GCHandle.Alloc(envs,GCHandleType.Pinned);
            var r=new Req{pid=pid,ct=ct,exe=ev,
                mods=mh.HasValue?mh.Value.AddrOfPinnedObject():IntPtr.Zero,mc=(uint)mv.Length,
                env=eh.HasValue?eh.Value.AddrOfPinnedObject():IntPtr.Zero,ec=(uint)envs.Length,
                opts=IntPtr.Zero,oc=0,tms=tms};
            lib=LoadLibrary(dll); var p=GetProcAddress(lib,"mm_execute");
            var fn=(MmExec)Marshal.GetDelegateForFunctionPointer(p,typeof(MmExec));
            eb=Marshal.AllocHGlobal(2048); uint ew;
            var st=fn(ref r,eb,1024,out ew); var msg=ew>0?Marshal.PtrToStringUni(eb,(int)ew):"";
            return "status="+st+" error=["+msg+"]";
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

function Run-Test($targetName, $targetExe) {
    $outFile = Join-Path $env:TEMP ("mm_cpptest_" + [guid]::NewGuid().ToString('N') + '.txt')
    Write-Host "`n=== C++ Feature Test on $targetName ===" -ForegroundColor Cyan
    Write-Host "Output: $outFile"

    $proc = Start-Process -FilePath $targetExe -PassThru
    Start-Sleep -Milliseconds 1000
    $proc.Refresh()
    Write-Host "PID: $($proc.Id)"

    try {
        $r = [TestHarness]::Run($executor, [uint32]$proc.Id,
            [uint64]$proc.StartTime.ToUniversalTime().ToFileTimeUtc(),
            $proc.MainModule.FileName,
            @($dep, $feature),
            @('MODSERVICE_TEST_OUTPUT'),
            @($outFile),
            15000)
        Write-Host "Injection result: $r"

        Start-Sleep -Milliseconds 500
        if (Test-Path $outFile) {
            $lines = [IO.File]::ReadAllText($outFile).Split("`r`n", [StringSplitOptions]::RemoveEmptyEntries)
            $passed = 0; $failed = 0
            foreach ($line in $lines) {
                if ($line -match ':PASS$') {
                    Write-Host "  [PASS] $($line -replace ':PASS$','')" -ForegroundColor Green
                    $passed++
                } elseif ($line -match ':FAIL:') {
                    Write-Host "  [FAIL] $line" -ForegroundColor Red
                    $failed++
                } else {
                    Write-Host "  [????] $line" -ForegroundColor Yellow
                }
            }
            Write-Host "`n  Results: $passed passed, $failed failed" -ForegroundColor $(if($failed -gt 0){'Red'}else{'Green'})
        } else {
            Write-Host "  No output file created!" -ForegroundColor Red
        }
    } finally {
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
        Remove-Item $outFile -ErrorAction SilentlyContinue
    }
}

Run-Test "TestApp" $testApp
Run-Test "Notepad" "notepad.exe"
