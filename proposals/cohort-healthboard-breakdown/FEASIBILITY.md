# Cohort → Health-board breakdown plugin — feasibility analysis

**Goal.** Given a built RDMP cohort, break the *final inclusion list* down by Scottish
health board and emit per-board patient counts, for a SHARE sharing project. The board of
each patient comes from the **SHARE_Demography** catalogue's `Region` column (a single-letter
board cipher: `T`=Tayside, `G`=Greater Glasgow & Clyde, …).

**Verdict: feasible, and low-risk.** RDMP already ships the exact join engine
(`CohortSummaryQueryBuilder`) and a GUI-only version of this feature ("Graph All Records For
Matching Patients"). The work is a thin headless/plugin driver, closely modelled on the
`ExecuteCommandExportCohortCounts` command added on this branch. No core changes required;
it can ship as a `.rdmp` plugin like `RdmpCohortExport`.

---

## 1. What "breakdown" means in SQL

The whole feature reduces to one query:

```sql
SELECT  d.Region, COUNT(DISTINCT d.<chi>) AS n
FROM    SHARE_Demography d
WHERE   d.<chi> IN ( <cohort identifier list> )   -- the final inclusion list
GROUP BY d.Region
```

`<cohort identifier list>` is the distinct patient-id query produced from the cohort.
Region codes are then mapped to human board names (mapping already exists in
`make_healthboard_filters.py`). Everything else is plumbing RDMP already has.

---

## 2. Two valid inputs — pick one (or support both)

| Input | What it is | Pros | Cons |
|---|---|---|---|
| **A. CohortIdentificationConfiguration (CIC)** | the *build query* (Cohort Builder) | always current; no commit needed; same object your other tools consume | `<id list>` = `CohortQueryBuilder(cic).SQL`; needs a query-caching server if the CIC spans servers |
| **B. ExtractableCohort** (committed) | the *released* list in the external cohort table | the actual frozen list that was shared; trivial `IN (SELECT priv FROM cohortTbl WHERE WhereSQL())`; **no query cache needed** | requires the cohort to have been committed first |

"final inclusion list" most naturally means the committed/released list → **B is the
truest "final inclusion list"**, and it's also the simpler SQL. Recommendation: build B
first (committed `ExtractableCohort`), add A (CIC) as a sibling command. (As built, the two inputs are
two separate commands sharing a base class — a single command with two constructors is ambiguous for the
CLI; see PLAN.md §4.)

---

## 3. Reusable RDMP machinery (verified in tree)

- **`CohortQueryBuilder`** — `Rdmp.Core/QueryBuilding/CohortQueryBuilder.cs`.
  `new CohortQueryBuilder(cic, childProvider).SQL` = the distinct-identifier list for a CIC
  (input A). Wires the cache server / root container automatically.
- **`ExtractableCohort`** — `Rdmp.Core/DataExport/Data/ExtractableCohort.cs`.
  `WhereSQL()`, `GetPrivateIdentifier()`, `GetDatabaseServer()` let you assemble the
  `IN (SELECT <priv> FROM <cohortTbl> WHERE <WhereSQL()>)` subquery for input B — **no cache
  required**.
- **`CohortSummaryQueryBuilder`** — `Rdmp.Core/QueryBuilding/CohortSummaryQueryBuilder.cs`.
  RDMP's native "constrain a GROUP-BY aggregate to a cohort" engine
  (`CohortSummaryAdjustment.WhereExtractionIdentifiersIn` → `chi IN (cohortSql)`).
  ⚠️ **Caveat that rules it out for the direct route:** it requires the breakdown aggregate and
  the cohort set to live on the **same Catalogue**, and (for `WhereExtractionIdentifiersIn`) a
  configured **query cache** with the cohort 100% cached. Our breakdown is on `SHARE_Demography`
  while the cohort is built from clinical catalogues → same-catalogue constraint fails. So we do
  **not** reuse this class directly; we build the `IN (...)` predicate ourselves (mirroring its
  internal logic). Worth citing as proof RDMP blesses the pattern.
- **`AggregateConfiguration` + `AddDimension` + default `CountSQL`** —
  `Rdmp.Core/Curation/Data/Aggregation/AggregateConfiguration.cs`. Optional: define a reusable
  "Region breakdown" graph object on `SHARE_Demography` rather than hand-writing the GROUP BY.
- **SQL → DataTable headlessly** — `DataAccessPortal.ExpectDatabase(point, ctx)` then
  `server.GetCommand/GetDataAdapter`; clean template in
  `Rdmp.Core/DataExport/DataExtraction/ExtractTableVerbatim.cs:217`
  (`ExtractDataToFile(collection, file, ctx)`).
- **Existing GUI feature (proof + reference)** — `CohortSummaryAggregateGraphUI`,
  `ExecuteCommandViewCohortAggregateGraph`, and the
  "Graph All Records For Matching Patients" menu in `AggregateConfigurationMenu.cs`. This is the
  same feature, WinForms-only. We're making a headless/CSV version.
- **Output/command shape** — copy `ExecuteCommandExportCohortCounts` +
  `CohortCountReport` (this branch) almost verbatim: a `BasicCommandExecution` that runs,
  builds a CSV, and `BasicActivator.Show`s a summary. Same CSV-escape helper, same
  `[DemandsInitialization]` arg pattern (works as both CLI verb and GUI menu item).

---

## 4. Proposed design

A new atomic command (CLI + GUI, MEF-discovered, shippable in the existing plugin):

```
ExecuteCommandExportCohortHealthBoardBreakdown(
    IBasicActivateItems activator,
    <ExtractableCohort | CohortIdentificationConfiguration> cohort,
    FileInfo toFile = null,                 // <name>-healthboard.csv
    string demographyCatalogue = "SHARE_Demography",
    string regionColumn = "Region",
    int timeout = 5000)
```

Execution:
1. Resolve the demography catalogue + `Region` `ExtractionInformation` and its `TableInfo`
   (→ the `DiscoveredDatabase`/server to run on, via `DataAccessPortal`).
2. Build `<id list>` SQL: input B = `IN (SELECT <priv> FROM <cohortTbl> WHERE WhereSQL())`;
   input A = `IN ( CohortQueryBuilder(cic).SQL )`.
3. Compose `SELECT Region, COUNT(DISTINCT chi) … GROUP BY Region`, run it → `DataTable`.
4. Map each `Region` code → `HB_Name`, `HB_Code`, and `Node` via the hardcoded lookup (§4a).
   Emit one row per board: `Region | HB_Code | HB_Name | Node | Count`, then a per-`Node`
   subtotal row, an `Unknown` row for any unmapped/NULL region, and a grand-total row. An
   unmapped `Region` code is reported (Node = `Unknown`), never silently dropped.
5. Write CSV (reuse the `CohortCountReport` escaper) + show a summary.

### 4a. Hardcoded board lookup (confirmed with user)

`Region` (the single-letter cipher in `SHARE_Demography.Region`) is the join/group key; each maps
to a board name, the numeric `HB_Code`, and the `Node` (= `SafeHaven_Region`). Ported to a C#
`record`/dictionary keyed by the Region cipher:

| HB_Name | HB_Code | Region | Node (SafeHaven_Region) |
|---|---|---|---|
| Ayrshire & Arran | 11 | A | West |
| Borders | 6 | B | South East |
| Dumfries & Galloway | 12 | Y | West |
| Fife | 4 | F | East |
| Forth Valley | 7 | V | East |
| Grampian | 2 | N | North |
| Greater Glasgow & Clyde | 16 | G | West |
| Highland | 17 | H | North |
| Lanarkshire | 10 | L | West |
| Lothian | 5 | S | South East |
| Orkney | 13 | R | North |
| Shetland | 14 | Z | North |
| Tayside | 3 | T | East |
| Western Isles | 15 | W | North |
| Clyde | — | C | West |

Nodes: **North** {N,H,R,Z,W} · **West** {A,Y,G,L,C} · **East** {F,V,T} · **South East** {B,S}.
This supersedes the Region-code map in `make_healthboard_filters.py` (same ciphers, now extended
with `HB_Code` + `Node`; "Argyll & Clyde (legacy)" → "Clyde").

A `HealthBoardBreakdownReport` helper (like `CohortCountReport`) holds the
DataTable→rows→CSV projection so it's unit-testable with no DB.

---

## 5. UI activation

The command must be launchable from the GUI (right-click), not just the CLI. RDMP gives us two
proven hooks; both reuse the *same* `IAtomicCommand` so there is no extra logic to duplicate.

**Recommended — plugin right-click menu (`IPluginUserInterface`).** Matches how the existing
`RdmpCohortExport` plugin surfaces its commands and keeps everything shippable as one `.rdmp`
with no core edits. Implement `GetAdditionalRightClickMenuItems(object treeObject)`
(`Rdmp.Core/IPluginUserInterface.cs:26`) to return the breakdown command when the user
right-clicks the relevant object:

```csharp
public override IEnumerable<IAtomicCommand> GetAdditionalRightClickMenuItems(object o) =>
    o switch
    {
        ExtractableCohort ec                    => new[] { new ExecuteCommandExportCohortHealthBoardBreakdown(_activator, ec) },
        CohortIdentificationConfiguration cic   => new[] { new ExecuteCommandExportCohortHealthBoardBreakdown(_activator, cic) },
        _ => Array.Empty<IAtomicCommand>()
    };
```

The command's constructor `SetImpossible(...)` (when there's no root container / no demography
catalogue) automatically greys the menu item out, and the `[DemandsInitialization]` args drive
an auto-generated argument prompt for the optional file path — exactly as
`ExecuteCommandExportCohortCounts` already behaves in the GUI.

**Alternative / additional — a button in the Cohort Builder editor.** If you also want it inside
the open Cohort Builder tab (next to the "Export Counts to File…" item you just added), wire a
`ToolStripMenuItem` in `Rdmp.UI/SubComponents/CohortIdentificationConfigurationUI.cs` the same
way commit `c261c196c` did (`miExportCounts` → `CommonFunctionality.AddToMenu(...)`). This edits
core UI (not plugin-only), so prefer the plugin hook unless an in-builder button is specifically
wanted.

Either way the GUI flow is: right-click cohort → "Export Health Board Breakdown…" → file picker
(`Activator.SelectFile`) → run → `Activator.Show` summary (and optionally `Activator.ShowData`
the DataTable inline before saving).

---

## 6. Resolved decisions (confirmed with user, 2026-06-23)

- **Same server** — `SHARE_Demography` is on the **same SQL server** as the cohort/identifier
  store. ⇒ the inline `chi IN (<cohort SQL>)` join runs directly; **no query cache or temp-table
  staging needed**. (Still handle cross-server defensively as a guarded error, not a crash.)
- **Identifier = CHI** on both sides. ⇒ join `ExtractableCohort.GetPrivateIdentifier()` (input B)
  / the CIC's extraction id (input A) to `SHARE_Demography`'s `IsExtractionIdentifier` column
  directly; no cross-walk.
- **Count = patients, `COUNT(DISTINCT chi)`** (not `count(*)` rows) — so the number per board is
  distinct people in the cohort, regardless of how many demography rows each has.
- **One board per patient** — `SHARE_Demography.Region` is 1:1 with the patient (each CHI maps to
  a single board). ⇒ no multi-board double-counting and **no tie-break rule needed**; the per-board
  patient counts (+ the unknown-region bucket below) sum exactly to the cohort total. The report
  can assert this reconciliation as a sanity check.
- **No suppression in the tool.** Output the raw `COUNT(DISTINCT chi)` per board; small-number
  disclosure control is handled case-by-case downstream by the user, not baked into the command.

- **Node rollup** — each board row also reports its `Node` (= `SafeHaven_Region`, see §4a), with
  per-node subtotal rows. Mapping is a hardcoded lookup keyed by the `Region` cipher.

## 7. Remaining open questions

- **Region completeness** — patients absent from `SHARE_Demography`, or with NULL/unknown
  `Region`, are reported in an `Unknown` row (Node = `Unknown`) so the per-board counts + that
  bucket reconcile to the cohort total. Governance will want this reconciliation visible.
  *(Decided; flagged only so it stays visible.)*

---

## 8. Effort estimate

- Core command + report helper + Region map: **~1 day**.
- No-DB unit tests (map + CSV) + one DB-backed end-to-end test (mirrors
  `CohortCountReportTests`): **~0.5 day**.
- Wire into `RdmpCohortExport` plugin / build 9.2.3 `.rdmp` deliverable: **~0.5 day** (runbook
  already exists in `proposals/cohort-agent/`).

**Total ≈ 2 days.** Reuses an established pattern end-to-end; the only genuinely new code is
the demography join SQL and the Region→board mapping.
