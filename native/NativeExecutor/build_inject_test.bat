@echo off
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
cd /d "S:\Michael\ModMonitor"
cl.exe /nologo /std:c++20 /EHsc /MDd /utf-8 /DUNICODE /D_UNICODE /I "native\NativeExecutor" /Fo"artifacts\native\NativeExecutor\x64\Debug\inject_test.obj" /Fe"artifacts\native\NativeExecutor\x64\Debug\inject_test.exe" "native\NativeExecutor\inject_test.cpp" /link /LIBPATH:"artifacts\native\NativeExecutor\x64\Debug" NativeExecutor.lib
echo Exit code: %ERRORLEVEL%
