# Cohort-building agent system (NL requirement → RDMP cohort → SQL → feedback)

> Status: proposal / first design. Author: agent-assisted, for Michele Tinti.
> Decisions locked in: **full metadata DB** export to docker SQL Server · build via
> **`rdmp cmd` scripts** · orchestrate with a **Claude API harness**.

## 1. Goal

Two cooperating LLM agents that turn a natural-language cohort requirement into a real
RDMP **Cohort Identification Configuration (CIC)**, as accurately as possible on the first pass:

- **Builder** — reads the NL requirement (+ the available catalogue metadata) and emits a
  sequence of `rdmp cmd` commands that construct the CIC.
- **Verifier** — reads the SQL that RDMP generates from the built CIC, compares it to the
  requirement, and feeds a structured diff/critique back to the Builder.

The loop repeats until the Verifier is satisfied (or a max-iteration budget is hit).

## 2. Why this is tractable in RDMP

A CIC is a deterministic, fully-serializable object graph stored in the RDMP **Catalogue**
platform database:

```
CohortIdentificationConfiguration
└── RootCohortAggregateContainer        (set operation: UNION / INTERSECT / EXCEPT)
    ├── AggregateConfiguration          ("cohort set" — one per inclusion/exclusion list)
    │   ├── AggregateDimension(s)       (the patient identifier column, e.g. chi)
    │   └── AggregateFilterContainer     (AND/OR)
    │       └── AggregateFilter(s)       (WHERE clauses, with parameters)
    └── (nested sub-containers, patient-index tables / "joinables")
```

RDMP turns that graph into SQL **purely as text generation**:

```
Rdmp.Core/QueryBuilding/CohortQueryBuilder.cs   →   new CohortQueryBuilder(cic, null).SQL
```

(see `Rdmp.Core/DataViewing/ViewCohortIdentificationConfigurationSqlCollection.GetSql()`).

So the agents can operate on two equivalent representations of the same thing — the **build**
(the `cmd` script / object graph) and the **SQL** — without ever touching patient data.
SQL generation only needs the *metadata* (table names, column names, extraction filters).

## 3. Running outside the NHS environment

You already have the hard part built (`mac-test-env/`): SQL Server 2022 in docker +
`rdmp install` to create the four platform DBs. The chosen "full metadata DB" approach
reuses exactly that.

```
NHS RDMP                          Outside (your Mac / a VM)
─────────                         ─────────────────────────
TEST_Catalogue   ──┐   bacpac /   docker SQL Server 2022  (mac-test-env/docker-compose.yml)
TEST_DataExport  ──┼─ scripted ─► restore → Catalogue + DataExport platform DBs
(metadata only)    │   export                │
                   └────────────────────────►│  rdmp CLI talks to it via Databases.yaml
                                              │  (or --dir for a YamlRepository copy)
```

### Export options, in order of preference
1. **SQL Server backup/restore of the metadata DBs** (Catalogue + DataExport). Highest
   fidelity, mirrors NHS object IDs exactly. Restore into the docker instance.
   ⚠️ **Governance:** these DBs contain table/column names, filter SQL, descriptions, and
   data-access connection strings (encrypted). Strip/replace `ExternalDatabaseServer`
   connection details and any free-text that could be sensitive before export.
2. **`YamlRepository` snapshot.** RDMP can mirror the whole platform metadata to a folder of
   `.yaml` files (`Rdmp.Core/Repositories/YamlRepository.cs`). Run the CLI with `--dir ./meta`
   instead of a connection-strings file. Git-friendly, diffable, trivially portable, no SQL
   Server needed for the metadata layer. Good for versioning the example cohorts.
3. (Not chosen) metadata-only re-creation / triples-only.

> The CIC SQL references your real data tables by name, but you never need to *run* it to
> compare against the requirement. If you later want to validate that the SQL actually
> executes, point the Catalogues at a synthetic/dummy database with the same schema.

## 4. The two agents

### 4.1 Builder
**Input:** NL requirement + a compact "catalogue manifest" (the tables, columns and
pre-defined `ExtractionFilter`s available to build with) + few-shot examples from your
existing triples.

**Output:** an `rdmp cmd` script (a YAML file of commands, run with `rdmp cmd -f script.yaml`;
see `Rdmp.Core/CommandLine/Options/RdmpScript.cs`). Core commands available:

| Step | Command |
|------|---------|
| Create the CIC | `CreateNewCohortIdentificationConfiguration "<name>"` |
| Set root operation | `SetContainerOperation CohortAggregateContainer:<id> UNION\|INTERSECT\|EXCEPT` |
| Add a sub-container | `AddCohortSubContainer CohortAggregateContainer:<id>` |
| Add a cohort set (from a Catalogue) | `AddCatalogueToCohortIdentificationSetContainer Catalogue:<x> CohortAggregateContainer:<id>` |
| Add a patient-index table | `AddCatalogueToCohortIdentificationAsPatientIndexTable ...` |
| Set the identifier dimension | `SetAggregateDimension ...` |
| Add a filter container | `AddNewFilterContainer ...` |
| Add a WHERE filter | `CreateNewFilter ...` (or import from an existing `ExtractionFilter`) |

Run `dotnet rdmp.dll cmd ListSupportedCommands` against your build to get the authoritative,
version-correct list and argument signatures — feed that list into the Builder's system prompt.

### 4.2 Verifier
**Input:** the requirement + the SQL generated from the built CIC.

**To get the SQL** (the one bit of glue needed — `ViewData --toFile` *executes* the query and
writes CSV, which is **not** what we want): add a tiny command/tool that dumps the generated
SQL text. Two ways:

- **(Recommended) ~15-line standalone tool** that references `Rdmp.Core`, loads the repo,
  fetches the CIC by ID and prints `new CohortQueryBuilder(cic, null).SQL`. See
  `dump-cohort-sql.example.cs` in this folder.
- Or add an `ExecuteCommandViewCohortSql` atomic command (writes `builder.SQL` to a file) so
  it's reachable as `rdmp cmd ViewCohortSql CohortIdentificationConfiguration:<id> ./out.sql`.

**Output:** structured feedback — `{verdict: pass|revise, mismatches: [...], suggested_fixes: [...]}` —
plus, when you have the *expected* SQL from a labelled example, a normalised diff (canonicalise
whitespace/casing/alias names before comparing, since RDMP-generated SQL has stable but verbose
formatting).

## 5. Orchestration (Claude API harness)

A small Python orchestrator (recommended; could be .NET) using the Anthropic SDK **with prompt
caching** on the big static context (the command list + catalogue manifest + few-shot examples):

```
for example in eval_set:                      # your (NL, CIC, SQL) triples
    ctx = cache(command_list + catalogue_manifest + few_shots)
    script = Builder(ctx, example.requirement)
    run: rdmp cmd -f script  (--dir ./meta  OR  via Databases.yaml)
    sql  = dump_cohort_sql(cic_id)
    fb   = Verifier(example.requirement, sql)
    while fb.verdict == "revise" and iters < MAX:
        script = Builder(ctx, requirement, prior_script, fb)
        ... rebuild, re-dump, re-verify ...
    score(sql, example.expected_sql)          # held-out metric
```

- **Few-shot from your existing triples**: each becomes `(requirement → known-good cmd script → SQL)`.
- **Eval harness**: hold out some triples; score Builder output by SQL equivalence to the
  labelled SQL (after canonicalisation), not by exact-string match.
- **Each run is isolated**: create the CIC in a scratch namespace, then delete (`rdmp cmd Delete CohortIdentificationConfiguration:<id>`) or work in a fresh `--dir` per run for clean state.

## 6. Build / data flow

```
                         ┌─────────────────────────── Claude API harness ───────────────────────────┐
 NL requirement ───────► │  Builder agent ──► rdmp cmd script ──► [ rdmp CLI ] ──► CIC in metadata DB │
                         │       ▲                                                      │            │
   example triples ──────┤  feedback (JSON diff)                              dump-cohort-sql        │
   (few-shot + eval)     │       │                                                      ▼            │
                         │  Verifier agent ◄────────────────── generated SQL  ◄─────────┘            │
                         └──────────────────────────────────────────────────────────────────────────┘
```

## 7. First milestones

1. **Stand up metadata outside NHS.** Restore the (sanitised) Catalogue + DataExport DBs into
   the docker instance from `mac-test-env/`, or snapshot to a `YamlRepository` folder. Verify
   `rdmp cmd ListSupportedCommands` and that you can hand-build one CIC and read its SQL.
2. **Add the SQL-dump glue** (`dump-cohort-sql` tool or `ViewCohortSql` command).
3. **Curate the triples** into `requirement.md` / `build.script.yaml` / `expected.sql` per example.
4. **Builder v0** — single shot, no loop. Measure SQL match on held-out triples.
5. **Add the Verifier + feedback loop.** Measure lift over v0.
6. **Iterate prompts / few-shot selection** to maximise first-pass accuracy.

## 8. Open items needing your input
- Confirm governance sign-off for exporting the metadata DBs, and what must be scrubbed
  (connection strings, free-text descriptions).
- How many labelled example triples exist, and in what form today?
- Identifier column conventions (e.g. is it always `chi`?) — the Builder needs to know the
  patient-identifier dimension for each catalogue.
