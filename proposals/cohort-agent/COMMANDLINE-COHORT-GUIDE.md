# Building an RDMP cohort from the command line — A to Z

A complete, verified recipe for constructing a Cohort Identification Configuration (CIC)
using only `rdmp cmd`. This is the "language" the Builder agent emits and the export
(decompiler) reproduces, so the two match. Every command below was run and verified against
RDMP 9.2.3.

## 1. The command-line model
```
rdmp cmd <CommandName> <arg0> <arg1> ... [--skip-patching]
```
- `rdmp cmd ListSupportedCommands` lists everything available.
- **Object references**: `Type:ID` (e.g. `Catalogue:18210`) or `Type:Name` (e.g. `Catalogue:"CHI Residency"`). Quote names with spaces.
- **Enums** by name (e.g. `INTERSECT`). **Null** arg is the literal `null`.
- `--skip-patching` = read-only against the platform DB (never migrates schema). Use it for
  anything you don't want to alter the platform.
- Connection comes from `Databases.yaml` next to the exe (or `--CatalogueConnectionString` etc).
- **IDs are assigned at runtime.** When scripting, either (a) issue commands one at a time and
  read back the new object's ID, or (b) use `rdmp cmd -f script.yaml` whose `UseScope: true`
  tracks objects created earlier in the same script so you can reference them by name.

## 2. The cohort object model
```
CohortIdentificationConfiguration            "the cohort"
└── root CohortAggregateContainer            set op: UNION | INTERSECT | EXCEPT
    ├── AggregateConfiguration  "cohort set"  built from ONE Catalogue; identifier = its dimension
    │   └── AggregateFilterContainer (AND/OR)
    │       └── AggregateFilter(s)            WHERE clauses, each may carry parameters (@x)
    └── nested CohortAggregateContainer(s)    to mix AND/OR/EXCEPT logic
```

## 3. A-to-Z commands

| Step | Command |
|------|---------|
| Create the cohort (also makes Root + Inclusion + Exclusion containers) | `CreateNewCohortIdentificationConfiguration "<name>"` |
| Set a container's set operation | `SetContainerOperation CohortAggregateContainer:<id> UNION\|INTERSECT\|EXCEPT` |
| Add a nested container | `AddCohortSubContainer CohortAggregateContainer:<parentId>` |
| Add a cohort set from a Catalogue (auto-sets the patient-identifier dimension) | `AddCatalogueToCohortIdentificationSetContainer CohortAggregateContainer:<id> Catalogue:<id|name>` |
| **Add a published filter** (brings its WHERE SQL **and** parameters + values) | `CreateNewFilter AggregateConfiguration:<id> ExtractionFilter:<id>` |
| Add a custom (hand-written) filter | `CreateNewFilter AggregateConfiguration:<id> "<Name>" "<WHERE SQL>"` |
| Override a parameter value | `Set AggregateFilterParameter:<id> Value "<newValue>"` |
| Add a patient-index/joinable table | `AddCatalogueToCohortIdentificationAsPatientIndexTable Catalogue:<id> CohortIdentificationConfiguration:<id>` |

## 4. Published filters & parameters — the key part (verified)
RDMP catalogues publish reusable filters (`ExtractionFilter`, e.g. "Tayside",
"Aged Between 18-200"). Each can declare parameters (`@indexDate`, `@studyIndexStart`, ...)
with default values.

**Importing a published filter from the command line sets all its variables automatically:**
```
rdmp cmd CreateNewFilter AggregateConfiguration:33679 ExtractionFilter:8821
```
This single command creates the `AggregateFilter` (copying the WHERE SQL) **and** copies every
parameter the published filter declares, with its value. Verified: importing a filter with
`@cutoff` produced an `AggregateFilterParameter` `DECLARE @cutoff AS DATE;` `= '1950-01-01'`
with no extra step. The new filter also records `ClonedFromExtractionFilter_ID` = the source.

To change a value afterwards: `Set AggregateFilterParameter:<id> Value "..."`.
Some published filters also ship named value presets (`ExtractionFilterParameterSet`) you can
choose at import time.

> Where do the ExtractionFilter IDs come from? They live in the Catalogue. For building NEW
> cohorts the Builder needs a *catalogue manifest* listing, per catalogue, its published
> filters (id, name, WHERE SQL, parameters). That manifest is dumped the same way as cohorts.

## 5. How the dumped build maps back to these commands
`ExportCohortAsScript` walks an existing CIC and emits exactly this language:
- containers/operations → `SetContainerOperation` / `AddCohortSubContainer`
- cohort sets → `AddCatalogueToCohortIdentificationSetContainer`
- a filter with `ClonedFromExtractionFilter_ID` → `CreateNewFilter ... ExtractionFilter:<id>`
  (so parameters round-trip); a hand-written filter → `CreateNewFilter ... "Name" "WHERE"`
- all parameters (global + per-filter) → a deduped `# parameters` block (DECLARE + value + comment)

So a dumped `build.script.yaml` *is* a from-scratch recipe, and the training pair is
`requirement.md` (what you want) → `build.script.yaml` (the commands that build it).

## 6. Worked example (one INTERSECT set with two published filters)
```yaml
Commands:
  - CreateNewCohortIdentificationConfiguration "Tayside adults, resident & alive"
  - SetContainerOperation CohortAggregateContainer:$root INTERSECT
  - AddCatalogueToCohortIdentificationSetContainer CohortAggregateContainer:$root Catalogue:"Demography CHI Residency (retrospective)"
  # the catalogue's published filters bring @indexDate / @studyIndexStart automatically:
  - CreateNewFilter AggregateConfiguration:$set ExtractionFilter:<TaysideId>
  - CreateNewFilter AggregateConfiguration:$set ExtractionFilter:<Aged18-200Id>
  - CreateNewFilter AggregateConfiguration:$set ExtractionFilter:<ResidentAliveId>
```
(`$root`/`$set` are placeholders for the runtime IDs — resolve them as in section 1.)
```
