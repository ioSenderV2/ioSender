# Tests

Lightweight, dependency-free unit tests for logic that can be verified without a controller
or the WPF UI stack. They are intentionally **not** part of the solution build (the app has no
test project / framework); compile and run them on demand with the Framework C# compiler.

## GrblFilesystemsTests.cs

Covers the pure helpers in `CNC Controls/CNC Controls/GrblFilesystems.cs` used by the combined
SD / LittleFS browser: `$FI` mount-line parsing, human-readable sizes, path qualification, and
the free-space summary.

Run (from the repo root):

```
copy "CNC Controls\CNC Controls\GrblFilesystems.cs" gfs.cs
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" -nologo -out:fstest.exe gfs.cs tests\GrblFilesystemsTests.cs
fstest.exe
del gfs.cs fstest.exe
```

Exit code 0 = all assertions passed (prints `RESULT: N passed, 0 failed`).
