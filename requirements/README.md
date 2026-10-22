# Public requirements

These requirements are the safe, public subset used to guide the runnable
workflow simulation. They demonstrate the pattern used across the private R&D:
a requirement states what must be true, implementation traces to it, and tests
or evidence show whether it was met.

| ID | Requirement | Verification |
|---|---|---|
| SIM-001 | The public application shall perform no live AD, CA, Broker, PIV, card, printer or Windows-logon operation. | Composition and project-reference tests |
| SIM-002 | The web host shall listen only on loopback. | Host-factory tests |
| SIM-003 | The system shall use synthetic identities and a fake authority. | Composition and workflow tests |
| SIM-004 | Approval shall bind to the exact request subject and version. | Domain and stale-approval tests |
| SIM-005 | Caller-owned mutable data shall not alter trusted role or subject state after admission. | Authorization-boundary tests |
| SIM-006 | A persistence failure before dispatch shall prevent the fake external action. | Intent-coordinator fault tests |
| SIM-007 | Ambiguous crash recovery shall fail closed and require explicit reconciliation. | Crash-probe and recovery tests |
| SIM-008 | Workflow and audit state shall persist in SQLite across restart. | Repository and connection tests |
| SIM-009 | Backup shall produce a consistent SQLite copy without relying on raw file copying. | Backup tests |
| SIM-010 | User-visible guidance shall prohibit real identity data and secrets. | Razor content/accessibility tests |
| SIM-011 | Issued, verified and delivered shall remain separate lifecycle facts. | Projection and lifecycle tests |
| SIM-012 | Returned or rejected work shall preserve truthful prior state and an auditable reason. | Workflow repository and projection tests |

The private V2 and V3 requirement sets are broader because they also cover lab
integration. They are not copied wholesale: doing so would publish operational
details unrelated to this simulation and blur which claims this repository can
actually support.
