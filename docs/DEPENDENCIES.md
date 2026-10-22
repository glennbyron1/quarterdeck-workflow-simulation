# Dependency inventory

The application targets .NET 10 and uses locked NuGet dependencies. The
authoritative versions are in each project's `packages.lock.json` file.

Direct third-party dependencies used by the extracted projects include:

| Package | Locked direct version | Declared license | Use |
|---|---:|---|---|
| `Microsoft.Data.Sqlite` | 10.0.10 | MIT | SQLite provider |
| `SQLitePCLRaw.bundle_e_sqlite3` | 2.1.12 | Apache-2.0 | Native SQLite bundle |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | MIT | Test infrastructure |
| `xunit` | 2.9.3 | Apache-2.0 | Test framework |
| `xunit.runner.visualstudio` | 3.1.4 | Apache-2.0 | Test runner adapter |

License expressions were read from the restored NuGet package metadata. On
October 22, 2026, `dotnet list package --vulnerable --include-transitive`
reported no vulnerable packages in the nine-project solution. This is a dated
observation, not a guarantee about future advisories.

This file is a human-readable index, not a substitute for an SBOM. A machine-
generated dependency/SBOM artifact should accompany any tagged public release.
