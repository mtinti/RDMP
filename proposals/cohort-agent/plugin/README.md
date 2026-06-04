# RdmpCohortExport plugin — capture cohorts as data-free training triples

Since nothing can leave NHS, we don't export a database. Instead this RDMP plugin
"decompiles" any **already-built** Cohort Identification Configuration (CIC) into three
small text files — the exact training triple the cohort-agent prototype consumes:

```
<out>/<cohort name>/requirement.md     # EMPTY placeholder - you paste the NL requirement here later
<out>/<cohort name>/build.script.yaml  # equivalent `rdmp cmd` script, rebuilt from the tree
<out>/<cohort name>/query.sql          # the SQL RDMP generates (CohortQueryBuilder)
```

The plugin runs **inside** NHS; you review the (small, schema-only, no-patient-data) text
files and move just those out. Works on your whole back-catalogue of cohorts — no need to
re-build anything by hand, and RDMP has no click-recorder so this is the way to capture them.

## How it works
- `ExecuteCommandExportCohortAsScript` — the command. Takes a `CohortIdentificationConfiguration`
  (+ optional output dir), walks `RootCohortAggregateContainer` → sub-containers → cohort-set
  aggregates → filters, and emits the script; gets the SQL from `new CohortQueryBuilder(cic, null).SQL`.
- `CohortExportPluginUserInterface` — adds it to the right-click menu of any CIC in the UI.
- Because RDMP auto-discovers commands, it's **also** runnable from the CLI with no UI:
  ```
  rdmp cmd ExportCohortAsScript CohortIdentificationConfiguration:5 ./export
  ```

## Build & package
```bash
cd proposals/cohort-agent/plugin
dotnet add package HIC.RDMP.Plugin            # pin to your NHS RDMP major.minor
dotnet build -c Release
nuget pack RdmpCohortExport.nuspec            # produces RdmpCohortExport.0.0.1.nupkg
```
Install the `.nupkg` via RDMP (UI: the Plugins node → add; or drop into the plugins folder),
restart, and the command appears.

## Two version things to match (find these in NHS)
1. **RDMP version** (`rdmp --version`): set the `HIC.RDMP.Plugin` package + nuspec dependency
   to the same major.minor, and the `.csproj` `TargetFramework` to that build's TFM.
2. If your NHS RDMP predates the `GetOrderedContents()` / API names used here, adjust — see below.

## Status — VERIFIED round-trip (develop, net10.0, SQL Server in docker)
The traversal logic in this command was compiled into the RDMP CLI and tested end-to-end:
built a cohort via `rdmp cmd` (root `EXCEPT`, an `INTERSECT` sub-container, two catalogues,
two WHERE filters), exported it, and confirmed every issued command — operations, catalogues
and exact filter SQL — reappears in `build.script.yaml`, including the auto-created
Inclusion/Exclusion containers. See the transcript in the project notes. What was tested is
the **command class logic**; the nuget packaging + UI menu registration here are mechanical.

Findings folded in from that test:
- Containers/aggregates are emitted as stable `$c<id>`/`$a<id>` handles (not names): an
  unnamed sub-container otherwise reports its *operation* as its name. Handles are unambiguous;
  a replay engine resolves them in creation order (as the prototype's `$root`/`$set1` do).
- `CohortQueryBuilder.SQL` can need the data server + a QueryCache for multi-set cohorts; the
  export guards it in try/catch so the script (the training target) always writes.

## Still to handle inside NHS (add if your cohorts use them)
- patient-index tables / joinables,
  cohort parameters (`ISqlParameter`), filter parameters, and `OverrideFiltersByUsingParent…`
  shortcuts. Nested AND/OR filter groups are flagged in a comment rather than fully scripted.
- **Bulk export:** loop over `CatalogueRepository.GetAllObjects<CohortIdentificationConfiguration>()`
  to dump every cohort at once (easy to add as a second command).

## Alternative: standalone console tool (no plugin install)
If installing a plugin in NHS is awkward, the same traversal code can live in a tiny console
exe that references `Rdmp.Core` and takes a CIC id — identical logic, run as
`dotnet ExportCohort.dll 5 ./export`. The plugin route is nicer (UI right-click + CLI), but
the console route needs no plugin permissions. Say which you prefer and I'll provide it.
