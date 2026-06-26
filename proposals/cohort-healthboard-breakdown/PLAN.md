# Implementation plan — Cohort → Health-board breakdown

Companion to `FEASIBILITY.md`. Builds a command + report that takes a built cohort and emits
per-health-board (and per-node) `COUNT(DISTINCT chi)` of the final inclusion list, joined to
`SHARE_Demography.Region`. Reuses the `ExecuteCommandExportCohortCounts` / `CohortCountReport`
pattern shipped in commit `c261c196c`.

## 0. Strategy

Build the logic in **`Rdmp.Core` first** (so it's exercisable by the existing
`Rdmp.Core.Tests` project against docker, exactly like `CohortCountReportDatabaseTests`), then
**port to the `RdmpCohortExport` plugin** + add the right-click UI hook as the NHS delivery step
— the same two-phase route used for the cohort-script commands. Everything below is tested in
docker with synthetic data before any packaging.

---

## 1. Files

**New (Rdmp.Core):**
- `Rdmp.Core/CohortCreation/HealthBoardLookup.cs` — the hardcoded Region→(HB_Name, HB_Code,
  Node) map + resolver. Single source of truth (supersedes `make_healthboard_filters.py`).
- `Rdmp.Core/CohortCreation/HealthBoardBreakdownReport.cs` — DataTable → records → CSV
  projection (DB-free, unit-testable). Mirrors `CohortCountReport`.
- `Rdmp.Core/CommandExecution/AtomicCommands/ExecuteCommandExportCohortHealthBoardBreakdown.cs`
  — the command (resolve demography, build SQL, execute, project, write).

**New (tests):**
- `Rdmp.Core.Tests/CohortCreation/HealthBoardBreakdownReportTests.cs` — no-DB unit tests +
  one `DatabaseTests` end-to-end fixture.

**New (plugin delivery — phase 2):**
- copy/move the command into `proposals/cohort-agent/plugin/` (or a new
  `proposals/cohort-healthboard-breakdown/plugin/`) and add the `IPluginUserInterface` hook.

---

## 2. `HealthBoardLookup`

```csharp
public sealed record HealthBoard(string Region, int? HbCode, string Name, string Node);

public static class HealthBoardLookup
{
    // keyed by the single-letter Region cipher held in SHARE_Demography.Region
    private static readonly Dictionary<string, HealthBoard> ByRegion = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = new("A", 11, "Ayrshire & Arran",        "West"),
        ["B"] = new("B",  6, "Borders",                 "South East"),
        ["Y"] = new("Y", 12, "Dumfries & Galloway",     "West"),
        ["F"] = new("F",  4, "Fife",                     "East"),
        ["V"] = new("V",  7, "Forth Valley",            "East"),
        ["N"] = new("N",  2, "Grampian",                "North"),
        ["G"] = new("G", 16, "Greater Glasgow & Clyde", "West"),
        ["H"] = new("H", 17, "Highland",                "North"),
        ["L"] = new("L", 10, "Lanarkshire",             "West"),
        ["S"] = new("S",  5, "Lothian",                 "South East"),
        ["R"] = new("R", 13, "Orkney",                  "North"),
        ["Z"] = new("Z", 14, "Shetland",                "North"),
        ["T"] = new("T",  3, "Tayside",                 "East"),
        ["W"] = new("W", 15, "Western Isles",           "North"),
        ["C"] = new("C", null, "Clyde",                 "West"), // legacy board: no numeric HB_Code (intentional)
    };

    public static HealthBoard Resolve(string region) =>
        region != null && ByRegion.TryGetValue(region.Trim(), out var hb)
            ? hb
            : new HealthBoard(region ?? "", null, "(unknown)", "Unknown");
}
```

Unknown/NULL region → `Node = "Unknown"`, never dropped (governance reconciliation).

---

## 3. `HealthBoardBreakdownReport`

Input: a `DataTable` with two columns `Region` (string) and `n` (int) — the GROUP-BY result.
Output: ordered records + CSV.

- Record: `Region | HbCode | HbName | Node | Count`.
- Ordering: by Node, then HbName; **Unknown last**.
- Append a per-Node subtotal row (`HbName = "<Node> — total"`) and a final grand-total row.
- `ToCsv` reuses the same CSV escaper as `CohortCountReport` (factor it into a shared helper or
  duplicate the 6-line `Escape`).
- A `BuildRecords(DataTable, out int total)` that also returns the grand total so the command can
  assert reconciliation (sum of board counts == cohort distinct count) and warn if not.

Header: `Region,HBCode,HBName,Node,Count`.

---

## 4. The command(s) — AS BUILT

> **Design change (implemented):** the two inputs ship as **two separate commands sharing a base
> class**, NOT one command with two constructors. The CLI `CommandInvoker` binds positionally to a
> single constructor, so two constructors on one command are ambiguous (`ExportCohortHealthBoardBreakdown
> CohortIdentificationConfiguration:4` failed with "Expected parameter at index 0 to be ExtractableCohort").
> Splitting gives two unambiguous CLI verbs and a clean GUI hook.

```csharp
// shared logic (demography resolution, GROUP BY Region join, CSV, reconciliation)
public abstract class ExecuteCommandExportHealthBoardBreakdownBase : BasicCommandExecution { ... }

// input B (committed cohort)  -> CLI verb: ExportCohortHealthBoardBreakdown
public class ExecuteCommandExportCohortHealthBoardBreakdown
    : ExecuteCommandExportHealthBoardBreakdownBase
{
    public ExecuteCommandExportCohortHealthBoardBreakdown(IBasicActivateItems activator,
        ExtractableCohort cohort, FileInfo toFile = null,
        string demographyCatalogue = "SHARE_Demography", string regionColumn = "Region",
        int timeout = 5000) ...
}

// input A (CIC)              -> CLI verb: ExportCicHealthBoardBreakdown
public class ExecuteCommandExportCicHealthBoardBreakdown
    : ExecuteCommandExportHealthBoardBreakdownBase
{
    public ExecuteCommandExportCicHealthBoardBreakdown(IBasicActivateItems activator,
        CohortIdentificationConfiguration cic, FileInfo toFile = null, ...) ...
}
```

The base supplies `BuildBreakdownSql` + the run/CSV/reconcile flow; each subclass only provides the
cohort identifier sub-query (committed cohort table vs `CohortQueryBuilder(cic).SQL`).

### Execute steps
1. **Resolve demography columns** — find the demography `Catalogue` by name
   (`activator.RepositoryLocator.CatalogueRepository.GetAllObjects<Catalogue>()` / by name),
   then its `ExtractionInformation` list:
   - `regionEi` = EI whose runtime name == `regionColumn`.
   - `idEi` = the EI with `IsExtractionIdentifier == true` (the CHI column).
   `SetImpossible(...)` if either is missing.
2. **Resolve the server** — `var point = idEi.ColumnInfo.TableInfo;` →
   `var db = DataAccessPortal.ExpectDatabase(point, DataAccessContext.InternalDataProcessing);`
   (template: `ExtractTableVerbatim.ExtractDataToFile`, `:217`).
3. **Build the cohort id-list subquery** (§5).
4. **Compose** the breakdown SQL (§5) and run it →
   `using var con = db.Server.GetConnection(); con.Open();`
   `using var cmd = db.Server.GetCommand(sql, con); cmd.CommandTimeout = timeout;`
   `var adapter = db.Server.GetDataAdapter(cmd); adapter.Fill(dt);`
5. **Project + write** via `HealthBoardBreakdownReport`; resolve `toFile` exactly like
   `ExecuteCommandExportCohortCounts` (interactive `SelectFile`, else
   `<cohort>-healthboard.csv`).
6. **Reconcile** — compute the cohort's own distinct count (input B:
   `cohort.GetCountDistinctFromDatabase(timeout)`; input A: scalar over the CIC SQL) and
   `BasicActivator.Show` a summary noting any shortfall (= the Unknown bucket).

---

## 5. The SQL

Same server (confirmed) → a single inline join, no cache/temp-table.

**Input B — committed `ExtractableCohort`:**
```sql
SELECT  d.<RegionCol>            AS Region,
        COUNT(DISTINCT d.<chi>)  AS n
FROM    <demographyTable> d
WHERE   d.<chi> IN (
            SELECT <priv>
            FROM   <cohortTable>
            WHERE  <ExtractableCohort.WhereSQL()>     -- e.g. cohortDefinition_ID=42
        )
GROUP BY d.<RegionCol>
```
- `<RegionCol>` = `regionEi.SelectSQL` (already qualified); `<chi>` = `idEi.SelectSQL`.
- `<demographyTable>` = `regionEi.ColumnInfo.TableInfo.Name`.
- `<priv>` = `cohort.GetPrivateIdentifier()`; `<cohortTable>` =
  `cohort.ExternalCohortTable.TableName`; predicate = `cohort.WhereSQL()`
  (`ExtractableCohort.cs:292`).

**Input A — `CohortIdentificationConfiguration`:**
```sql
... WHERE d.<chi> IN ( <CohortQueryBuilder(cic, childProvider).SQL> ) GROUP BY ...
```
`CohortQueryBuilder` emits `DECLARE @x …` param lines; run as a batch (don't wrap the whole
thing as a scalar subquery). If the CIC's own sets span servers it needs a query cache — out of
scope for v1 (input B is the primary path).

> Build the SQL in one private `BuildBreakdownSql(...)` method so it's unit-testable as a pure
> string. SQL Server dialect is fine for the NHS target; note DBMS-portability (quoting via
> `db.Server.GetQuerySyntaxHelper()`) as future work.

---

## 6. Plugin UI hook (phase 2)

In the plugin's `PluginUserInterface` subclass:
```csharp
public override IEnumerable<IAtomicCommand> GetAdditionalRightClickMenuItems(object o) =>
    o switch
    {
        ExtractableCohort ec                  => new[]{ new ExecuteCommandExportCohortHealthBoardBreakdown(BasicActivator, ec) },
        CohortIdentificationConfiguration cic => new[]{ new ExecuteCommandExportCicHealthBoardBreakdown(BasicActivator, cic) },
        _ => Array.Empty<IAtomicCommand>()
    };
```
`IPluginUserInterface.GetAdditionalRightClickMenuItems` — `Rdmp.Core/IPluginUserInterface.cs:26`.
`SetImpossible` auto-greys the item. Same MEF discovery means the command is also a CLI verb with
no extra work. (Optional in-builder button: wire like `miExportCounts` in
`CohortIdentificationConfigurationUI.cs`.)

---

## 7. Test plan — all in docker with synthetic data

### 7a. Bring up the environment
```bash
bash mac-test-env/setup.sh        # SQL Server 2022 container + TEST_ platform DBs + rdmp CLI
```
(README: `mac-test-env/README.md`. Server `localhost,1433`, sa / `YourStrong!Passw0rd`,
prefix `TEST_`. `teardown.sh` restores `Tests.Common/TestDatabases.txt` — do not commit it.)

### 7b. No-DB unit tests (`HealthBoardBreakdownReportTests`, plain class)
Mirror `CohortCountReportTests`'s no-DB tests:
- `Resolve` maps every cipher to the right Name/HbCode/Node; unknown → `Unknown`.
- `BuildRecords` orders by Node→HbName, places Unknown last, emits per-Node subtotals + grand
  total, and subtotals/total equal the sum of their members.
- `ToCsv` header == `Region,HBCode,HBName,Node,Count`; comma/quote/newline escaping.
- `BuildBreakdownSql` produces the expected join string for a known set of inputs (pure string
  assertion, no DB).

### 7c. DB-backed end-to-end (`DatabaseTests` fixture)
Model directly on `CohortCountReportDatabaseTests` (`CohortCountReportTests.cs:152`). Synthetic
data is created **in-test** via `GetCleanedServer` + `CreateTable` + `Import` — no external
seeding needed:

```csharp
public class HealthBoardBreakdownDatabaseTests : DatabaseTests
{
    [Test]
    public void Breakdown_RealCohort_CountsPerBoardAndNode()
    {
        // 1. synthetic SHARE_Demography: chi + Region, known board distribution
        var demo = new DataTable();
        demo.Columns.Add("chi"); demo.Columns.Add("Region");
        // 3 Tayside(T), 2 Glasgow(G), 1 Borders(B), 1 unknown(Q), 1 NULL
        foreach (var (chi,reg) in new[]{("1","T"),("2","T"),("3","T"),("4","G"),
                                        ("5","G"),("6","B"),("7","Q"),("8",null)})
            demo.Rows.Add(chi, reg);

        var db = GetCleanedServer(DatabaseType.MicrosoftSQLServer);
        var demoTbl = db.CreateTable("SHARE_Demography", demo);
        var demoCata = Import(demoTbl);
        MarkExtractionId(demoCata, "chi");            // helper: set IsExtractionIdentifier

        // 2. a committed ExtractableCohort over chis {1..8} (input B) -- or a CIC (input A)
        //    build via the DataExport test helpers used elsewhere in the suite
        var cohort = CreateCommittedCohort(db, new[]{"1","2","3","4","5","6","7","8"});

        // 3. run the command to a temp CSV
        var file = new FileInfo(Path.GetTempFileName());
        new ExecuteCommandExportCohortHealthBoardBreakdown(
            new ThrowImmediatelyActivator(RepositoryLocator, null),
            cohort, file, "SHARE_Demography", "Region").Execute();

        // 4. assert per-board + per-node + unknown counts
        var rows = HealthBoardBreakdownReport.Parse(File.ReadAllText(file));
        Assert.Multiple(() =>
        {
            Assert.That(Count(rows,"Tayside"),    Is.EqualTo(3));
            Assert.That(Count(rows,"Greater Glasgow & Clyde"), Is.EqualTo(2));
            Assert.That(Count(rows,"Borders"),    Is.EqualTo(1));
            Assert.That(NodeTotal(rows,"East"),   Is.EqualTo(3));   // Tayside only
            Assert.That(NodeTotal(rows,"West"),   Is.EqualTo(2));
            Assert.That(Unknown(rows),            Is.EqualTo(2));   // 'Q' + NULL
            Assert.That(GrandTotal(rows),         Is.EqualTo(8));   // reconciles to cohort
        });
    }
}
```
Key checks: distinct-patient counting (add a duplicate `("1","T")` row → Tayside stays 3, proving
`COUNT(DISTINCT chi)`), Unknown bucket captures both the unmapped code and NULL, and the grand
total reconciles to the committed cohort size.

> If wiring a real `ExtractableCohort` in-test is fiddly, the equivalent first cut uses a CIC
> (input A) built exactly as `CohortCountReportDatabaseTests` builds its CIC (lines 170–184) over
> the demography catalogue — the breakdown SQL then wraps `CohortQueryBuilder(cic).SQL`.

Run just this slice:
```bash
bash mac-test-env/run-tests.sh "FullyQualifiedName~HealthBoardBreakdown"
```

### 7d. CLI smoke test in docker (optional, proves end-user path)
After `dotnet build Tools/rdmp/rdmp.csproj -c Release`, seed a tiny demography table + a cohort
the same way `.cohort-test/fulltest_build.sh` seeds fake data, then:
```bash
dotnet Tools/rdmp/bin/Release/net10.0/rdmp.dll cmd \
    ExportCohortHealthBoardBreakdown ExtractableCohort:1 ./hb.csv
docker exec rdmp-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa \
    -P 'YourStrong!Passw0rd' -C -Q "SELECT Region,COUNT(DISTINCT chi) FROM ... GROUP BY Region"
# compare hb.csv to the hand-run GROUP BY
```

### 7e. Teardown
```bash
bash mac-test-env/teardown.sh            # keep volume;  --purge to wipe
```

---

## 8. Delivery (phase 2, after tests green)

1. Port the command into the plugin project + add the `IPluginUserInterface` hook (§6).
2. Rebuild the **9.2.3** `.rdmp` per `proposals/cohort-agent/BUILD-STANDALONE-FOR-NHS.md`
   (the existing runbook covers worktree bake, self-contained publish, zip, rclone upload +
   byte-verify).
3. Smoke-test `rdmp.exe cmd ListSupportedCommands | grep -i healthboard` on the target.

---

## 9. Execution checklist

- [x] `HealthBoardLookup` + no-DB tests
- [x] `HealthBoardBreakdownReport` + no-DB tests (CSV, ordering, subtotals)
- [x] `BuildBreakdownSql` + string unit test
- [x] `ExecuteCommandExportCohortHealthBoardBreakdown` (input B)
- [x] `DatabaseTests` end-to-end green under `mac-test-env`
      (`HealthBoardBreakdownDatabaseTests` — synthetic SHARE_Demography joined to a real
      committed cohort: distinct counts, node subtotals, Unknown bucket, non-cohort exclusion,
      reconciliation shortfall all asserted; passes)
- [x] input A (CIC) overload + test
      (`HealthBoardBreakdownCicDatabaseTests` — cohort built over a synthetic patients catalogue,
      broken down against a separate synthetic SHARE_Demography on the same server; DECLARE
      parameters hoisted out of the IN-subquery; per-board/node counts + reconciliation asserted;
      passes)
- [x] CLI smoke test in docker
      (both verbs `ExportCohortHealthBoardBreakdown` + `ExportCicHealthBoardBreakdown` discovered
      + argument-bound; full data run blocked only by the known Mac+docker SQL TLS cert limitation
      (FAnsi err 35), execution correctness covered by the NUnit DB tests)
- [x] plugin hook + 9.2.3 `.rdmp` build + upload
      (`proposals/cohort-healthboard-breakdown/plugin/` — RdmpHealthBoardBreakdown.csproj/.nuspec +
      HealthBoardBreakdownPluginUserInterface (right-click on ExtractableCohort / CIC) + copies of
      the 3 sources; built against the vanilla v9.2.3 worktree; .rdmp VERIFIED to load into the
      9.2.3 CLI (both commands appear; absent without the plugin); uploaded + byte-verified to
      onedrive:rdmp/healthboard_breakdown/ with INSTALL.md)
```
