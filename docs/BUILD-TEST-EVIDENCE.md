# Build and test evidence

## October 22, 2026 post-review host fix

The local web host now requires the documented `127.0.0.1:5080` authority as
well as a loopback peer, with hostile-Host regression cases. A disposable copy
of this candidate was checked with .NET SDK 10.0.302 on macOS:

- Locked restore passed. NuGet's network advisory query was disabled for this
  run after a sandboxed restore stalled; package locks remained enforced.
- Release build passed with warnings treated as errors: 0 warnings, 0 errors.
- Focused host-admission suite: 16 passed, 0 failed.
- Full Release suite: 334 passed, 0 failed, 0 skipped (Core 71,
  Presentation 36, Simulation/application/persistence 227).
- Real loopback smoke test: `Host: attacker.example:5080` returned HTTP 403;
  the documented `http://127.0.0.1:5080/` returned HTTP 200.

This changed candidate has not yet been requalified on Windows. The earlier
Windows result below applies only to its recorded source commit. Browser-level
DNS-rebinding behavior was not reproduced; the HTTP Host boundary was tested
directly.

## October 22, 2026 clean qualification

- Source: exact public-release candidate
- SDK: .NET SDK 10.0.302 (`osx-arm64`)
- Restore: locked restore passed
- Build: Release build passed with warnings treated as errors
- Build result: 0 warnings, 0 errors
- Tests: 321 passed, 0 failed, 0 skipped
  - Core: 71
  - Presentation: 36
  - Simulation/application/persistence: 214
- Runtime smoke test: loopback host returned HTTP 200 with the expected content
  security policy and safety headers
- GUI smoke test: created synthetic case `SIM-QD-000001`, displayed the
  generated case, next actor, safe-stop code and append-only audit event
- Dependency advisory query: no vulnerable packages reported

The first macOS test attempt used the platform's symlinked `/var` temporary
path and was rejected by `simulation_store_reparse_path`. The successful test
used the ignored repository-local `artifacts/tmp` directory. The application
control was not weakened.

## October 22, 2026 Windows qualification

- Environment: disposable isolated Windows Server 2025 test VM
- Exact public candidate commit: `1c6241059f06b0177dfd3bb7ef5e7f22728418b5`
- SDK: .NET SDK 10.0.302 (`win-x64`)
- Restore: locked restore passed with NuGet's network vulnerability audit
  disabled because the qualification VM is intentionally isolated
- Build: Release build passed with warnings treated as errors
- Build result: 0 warnings, 0 errors
- Tests: 321 passed, 0 failed, 0 skipped
  - Core: 71
  - Presentation: 36
  - Simulation/application/persistence: 214

The dependency and vulnerability review remains supported by the separate
repository security scans and the connected macOS advisory query. Disabling
NuGet's network audit on the isolated Windows VM did not relax package-lock
enforcement or the warning-as-error build.

This is a bounded author-produced qualification of the exact source commit on
a second operating system. It is not an independent third-party reproduction
or a production-readiness claim.
