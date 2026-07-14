# Plan - fully generic "ExportCohortBuildBreakDownByGroups" (rename + generic lookup + SHARE preset)

Responds to JFriel's review comment "is it worth making this more generic i.e.
ExportCohortBuildBreakDownByGroups or similar" by going the whole way: generic name, generic lookup
columns, nothing Scotland-specific in the engine, with the SHARE configuration shipped as a named
preset in the plugin.

Decisions (confirmed 2026-07-14): preset baked into the plugin (resolved by name at runtime);
full rename (plugin id, classes, CSV labels); lookup columns passed as ColumnInfo objects.

## 1. Renames

| Old | New |
|---|---|
| `ExecuteCommandExportCohortBuildHealthBoardBreakdown` | `ExecuteCommandExportCohortBuildBreakDownByGroups` (CLI verb `ExportCohortBuildBreakDownByGroups`) |
| `CohortBuildHealthBoardBreakdownReport` | `CohortBuildBreakdownByGroupsReport` |
| `RegionLookup` | `GroupLookup` |
| `CohortBuildBreakdownNode` / `CohortBuildBreakdownBuckets` | unchanged (already generic) |
| plugin id / assembly `RdmpCohortBuildHealthBoardBreakdown` | `RdmpCohortBuildBreakdownByGroups` (csproj AssemblyName + nuspec id + .rdmp file name) |
| package folder `RdmpCohortBuildHealthBoardBreakdown/` | `RdmpCohortBuildBreakdownByGroups/` (on the release branch; PR #2368 diff becomes a folder rename) |
| CSV bottom row `% of demography` | `% of reference population` |
| default output file suffix `-build-breakdown.csv` | unchanged |

Terminology in code/docs: "region" -> "group code"; "board" -> "group"; the demography catalogue ->
"the reference catalogue" (it supplies the patient id + the group column and defines the reference
population for the second % row).

## 2. Generic lookup columns

`GroupLookup.LoadFrom(DiscoveredTable, keyCol, labelCol, groupingCol?, timeout)`:
- **key column** - the code as it appears in the reference catalogue's group column (was `Region`)
- **label column** - display name used as the CSV column header (was `HB_Name`); NULL label falls back
  to the code
- **grouping column** (optional, may be null) - only orders the output columns (was
  `SafeHaven_Region`); NULL allowed per row

No column name is hardcoded anywhere in the engine.

## 3. Command signature (4 required objects; tables/catalogue DERIVED - confirmed 2026-07-14)

```
ExecuteCommandExportCohortBuildBreakDownByGroups(
    IBasicActivateItems activator,
    CohortIdentificationConfiguration cic,
    ColumnInfo groupColumn = null,          // e.g. SHARE_Demography's Region; its TableInfo implies the
                                            // reference table AND the patient id (the column flagged
                                            // IsExtractionIdentifier on that table)
    ColumnInfo lookupKeyColumn = null,      // e.g. z_hb_lookup.Region; its TableInfo IS the lookup table
    ColumnInfo lookupLabelColumn = null,    // e.g. z_hb_lookup.HB_Name
    ColumnInfo lookupGroupingColumn = null, // optional (z_hb_lookup.SafeHaven_Region)
    FileInfo toFile = null,
    int timeout = 5000)
```

Column 1 implies catalogue/table 1; columns 2-3 imply table 2 (one table = one catalogue in this
deployment, so derivation is unambiguous). Validation: lookup key/label/grouping must share one
TableInfo; the reference table must have EXACTLY ONE IsExtractionIdentifier column (zero = no join key,
several = ambiguous identifier domain -> loud stop rather than silently wrong counts). Existing guards
unchanged (cache server required, co-location).

## 4. Preset (baked into the plugin, not the engine)

New small file in the plugin: `SharePreset.cs` - a named preset = a set of NAMES resolved at runtime:

```
catalogue "SHARE_Demography" -> its column "Region"
table     "z_hb_lookup"      -> columns "Region", "HB_Name", "SafeHaven_Region"
```

Resolution order inside Execute (per missing input):
1. explicit argument (CLI or advanced GUI path)
2. preset: find object BY NAME (case-insensitive); exactly one match -> use it, else skip preset
3. GUI: prompt; CLI/headless: fail with a clear message naming what could not be resolved

GUI hook exposes TWO menu items on a CIC:
- "Export Build Breakdown By Groups (SHARE preset)" - constructs the command with whatever the preset
  resolves (prompts only for gaps)
- "Export Build Breakdown By Groups (choose inputs)" - constructs with nulls and a flag that skips the
  preset rung, so everything is prompted

Positioning for the review: the generic engine keeps NO defaults (JFriel's ask); the preset is a
deployment convenience living in the plugin layer, in one visibly separable file.

## 5. Tests

- Update the docker fixture to pass the lookup ColumnInfos (the z_hb_lookup TableInfo import already
  creates them) and the renamed command; assertions unchanged except the `% of reference population`
  label.
- No-DB tests: GroupLookup with arbitrary column names (not Region/HB_Name); label-falls-back-to-code;
  optional grouping null.
- Preset test: name-resolution finds the fixture's SHARE_Demography/z_hb_lookup by name; skips cleanly
  when absent.
- Rename ripple: SetOperationSql/CleanName tests follow the class rename.

## 6. Propagation

1. Core rename + tests on `feature/cohort-healthboard-breakdown` (git mv to preserve history).
2. Plugin: rename csproj/nuspec/UI hook, add SharePreset.cs, rebuild against the 9.2.3 worktree,
   repackage as `RdmpCohortBuildBreakdownByGroups.rdmp`.
3. Release branch: `git mv RdmpCohortBuildHealthBoardBreakdown RdmpCohortBuildBreakdownByGroups`,
   refresh src + .rdmp + README/INSTALL; PR #2368 title updated to match.
4. Proposals branch: refresh source + build-plugin copies + this plan.
5. OneDrive: upload the new artifact to onedrive:rdmp/healthboard_build_breakdown/ KEEPING the old
   RdmpCohortBuildHealthBoardBreakdown.rdmp alongside it (decided 2026-07-14).
6. Reviewer replies: #10 becomes "Done - renamed and fully generic"; #5 reworded to mention the preset
   as a plugin-layer convenience.

## 7. Out of scope

- The FINAL-LIST plugin (RdmpHealthBoardBreakdown) keeps its name and hardcoded lookup - separate
  deliverable, not part of PR #2368.
- ExtendedProperty-persisted presets (option discussed, not chosen).

Effort: ~1 day including tests and propagation.
