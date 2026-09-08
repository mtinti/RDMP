# Transfer plan - RdmpCohortBuildBreakdownByGroups into HicServices/HICPlugin

Status: PLAN, reviewed and updated 2026-09-08 (see Review addendum, section 9). Nothing has been
forked, ported or opened yet. Target confirmed by Michele: https://github.com/HicServices/HICPlugin

## 1. Background

The plugin currently lives as a self-contained folder on PR HicServices/RDMP#2368 (draft). JFriel's
review asked for it to live as a plugin rather than in core RDMP, and offered to discuss a HIC home.
Michele has since been granted HicServices org membership (invite via m.tinti@dundee.ac.uk, bound to
the `mtinti` GitHub account). The agreed destination is the existing HICPlugin repository, which is
literally described as "a Plugin for RDMP containing a collection of HIC related and Scotland
specific" functionality - exactly this plugin's category.

## 2. Verified facts (recon, 2026-07-26)

Access:
- `mtinti` is an ACTIVE member of the HicServices org.
- HICPlugin permissions for `mtinti`: pull + triage. NO direct push. Contribution route is therefore
  fork -> branch -> PR, with triage rights to manage the PR conversation.

HICPlugin repository conventions:
- One solution (`HICPlugin.sln`) containing SEVERAL plugin projects side by side: `HICPlugin`,
  `DrsPlugin`, `GoDartsPlugin`(+UI), `JiraPlugin`, `HIC.Demography`, `HICPluginInteractive`,
  `SCIStorePlugin` etc, plus ONE test project `HICPluginTests`.
- RDMP is a GIT SUBMODULE at `RDMP/`, pinned to branch `develop`. Plugin projects reference
  `..\RDMP\Rdmp.Core\Rdmp.Core.csproj` directly (ProjectReference, not the NuGet package).
- `HICPluginTests` targets `net10.0-windows` and references `..\RDMP\Tests.Common\Tests.Common.csproj`
  (the same DatabaseTests harness our fixture already uses) plus every plugin project.
- Projects link `..\SharedAssemblyInfo.cs`.
- CI (`.github/workflows/dotnet-core.yml`, windows-latest): builds the solution, spins up
  LocalDB + MySQL, runs `rdmp install`, runs `dotnet test`, generates a nuspec whose
  `HIC.RDMP.Plugin` dependency version is read from the RDMP submodule, packages ONE combined
  `HIC.Rdmp.HicPlugin.Plugin.<version>.rdmp` (7z: nuspec + published outputs), load-verifies it via
  `rdmp cmd listsupportedcommands`, and on tags uploads the `.rdmp` + `.nupkg` as release assets.
- Latest release: v6.1.17 (May 2026).
- The RDMP submodule is PINNED at commit 48e9c59 (2026-05-19), which declares version 9.2.2,
  predates catalogue patch 093 and predates the SixLabors license enforcement (see section 9).
- HICPlugin has NO changelog file and NO PR template (`Packages.md` documents third-party packages,
  not releases).
- RDMP's own releases BUNDLE the HICPlugin `.rdmp` inside the CLI/client zips (verified in the
  v9.3.0 assets: HIC.Rdmp.HicPlugin.Plugin.6.1.17.rdmp ships in the box).

## 3. Consequences to accept before starting

1. **House style replaces our standalone build.** Our csproj currently references the released
   `HIC.RDMP.Plugin 9.2.3` NuGet package so it builds from any checkout. In HICPlugin the convention
   is a ProjectReference to the RDMP submodule (develop). We conform: new csproj modeled on
   `HICPlugin/HICPlugin.csproj`. The four engine source files move UNCHANGED.
2. **NHS transition caveat - softer than first thought.** The submodule tracks `develop` but is
   PINNED at a 9.2.2-era commit. A release cut at the CURRENT pin declares dependency 9.2 - which
   NHS's 9.2.3 CAN load. If the maintainers bump the submodule before releasing (likely,
   eventually), the release targets 9.3+ instead. Either way the interim is covered: OneDrive holds
   version-matched standalone artifacts for 9.2.x and 9.3.x with WHICH-VERSION.txt. The transfer is
   the long-term home, not an immediate replacement of the NHS deliverable.
3. **One combined .rdmp.** Our commands will ship inside `HIC.Rdmp.HicPlugin.Plugin.<v>.rdmp`
   together with the other HIC plugins, on their 6.x version line, rebuilt by THEIR CI on every
   release - the recompile treadmill becomes HIC-owned (the original motivation). Better still:
   RDMP releases bundle the HICPlugin `.rdmp` inside the CLI/client zips, so once merged and
   released our plugin ships INSIDE RDMP itself - no separate install for new deployments.
4. **Review restarts with JFriel on his turf.** The PR to HICPlugin is a fresh review context; the
   body will link RDMP#2368 as the prior review history.

## 4. Design decisions (proposed - please confirm)

| # | Decision | Proposal |
|---|---|---|
| D1 | New project vs merge into `HICPlugin` project | **New project `RdmpCohortBuildBreakdownByGroups/`** in the solution. Matches the repo's multi-project pattern (DrsPlugin, JiraPlugin...), keeps our namespace and file identity, keeps the diff reviewable. |
| D2 | Namespace | Keep `RdmpCohortBuildBreakdownByGroups` (repo pattern is one namespace per project). |
| D3 | What moves | The 6 plugin sources (command, report, models, GroupLookup, SharePreset, UI hook) + the test file. The built `.rdmp`, README/INSTALL and this proposals folder do NOT move (their CI builds the artifact; docs get a page in their `Documentation/` + changelog entry per house style). |
| D4 | Tests | Our test file goes into `HICPluginTests` (it already references Tests.Common + gets a ProjectReference to the new project). The PostgreSql fixture case will skip in their CI (no pg service) and stays runnable locally - noted in the PR body. |
| D5 | UI hook TFM | Our `PluginUserInterface` hook is plain Rdmp.Core (no WinForms), so it stays in the net10.0 project - no `...Interactive`/windows project needed. |

## 5. Phases and concrete steps

### Phase 1 - fork, clone, branch (~30 min)
1. `gh repo fork HicServices/HICPlugin --clone=false` (fork under `mtinti`).
2. `git clone --recurse-submodules git@github.com:mtinti/HICPlugin.git ~/git_projects/HICPlugin`
   (submodule RDMP will clone the full RDMP repo, ~minutes).
3. `git switch -c feature/cohort-build-breakdown-by-groups`.
4. Verify baseline: `dotnet build HICPlugin.sln` compiles on the Mac (their projects target
   net10.0 / net10.0-windows; RDMP already builds cross-platform with EnableWindowsTargeting).
   If the windows-TFM projects block a mac build, verify per-project builds instead and lean on CI.
5. Compile OUR six sources against the submodule pin (9.2.2-era core). Expected clean - the same
   code builds against the released 9.2.3 package and current develop (9.3.0) - but this check
   catches it on day one. If it fails (unlikely), raise the submodule bump with JFriel; do NOT
   bump the pin inside our PR.

### Phase 2 - port (~half day)
1. Create `RdmpCohortBuildBreakdownByGroups/RdmpCohortBuildBreakdownByGroups.csproj`:
   net10.0, ProjectReference `..\RDMP\Rdmp.Core\Rdmp.Core.csproj`, link `..\SharedAssemblyInfo.cs`
   (copy the shape of `HICPlugin/HICPlugin.csproj`, minus its resources).
2. Copy the 6 source files unchanged from
   `RDMP repo release branch: RdmpCohortBuildBreakdownByGroups/src/*.cs`.
3. Add the project to `HICPlugin.sln`.
4. Wire it into packaging: inspect how the CI's publish step ("p" folder) picks up plugin projects
   (likely `Plugin/main/main.csproj` references every plugin project - add ours the same way).
5. Tests: copy `CohortBuildBreakdownByGroupsTests.cs` into `HICPluginTests/`, add the
   ProjectReference to the new project, and rename the test file's namespace to the tests
   project's convention (namespace/usings only - the engine sources still move unchanged; the
   "no code changes" rule in section 7 refers to the six plugin sources). Keep the fixture's
   `[TestCase(PostgreSql)]` (skips where unconfigured).
6. Docs: short page in `Documentation/` (what the command does, the four ColumnInfo inputs, the
   SHARE preset, the lookup-table expectations, single-valued precondition). RESOLVED: HICPlugin
   has no changelog file, so there is no changelog step - the PR body carries the summary.

### Phase 3 - verify locally (~half day)
1. Build the solution; build the new project in Release (fine at the current pin - the SixLabors
   license enforcement arrived on develop AFTER it; if the pin is ever bumped past dep-bump #2390,
   build Debug locally instead - CI holds the license key).
2. IMPORTANT - schema drift: our docker TEST_ platform databases are now patched to CURRENT
   develop (patch 093, re-patched 2026-09-08), which is NEWER than the submodule pin - the pinned
   Startup would refuse them. So give the submodule its own databases: in the submodule's
   `Tests.Common/TestDatabases.txt` set `Prefix: TESTHP_` (plus the docker SQL Server credentials)
   and create them with the SUBMODULE's tool:
   `dotnet RDMP/Tools/rdmp/bin/Release/net10.0/rdmp.dll install "localhost,1433" TESTHP_ -u sa -p '...' -d`.
   Then run: `dotnet test HICPluginTests --filter FullyQualifiedName~CohortBuildBreakdownByGroups`.
   Optionally enable the PostgreSql line (containers running) for the pg case.
3. Do NOT commit the TestDatabases change (mirror of our existing rule).
4. Optional: replicate the CI packaging steps locally and load-verify the combined `.rdmp` against
   the RDMP submodule CLI.

### Phase 4 - PR to HicServices/HICPlugin (only on Michele's explicit go)
1. Push the branch to the `mtinti` fork of HICPlugin.
2. Open the PR: title "Add RdmpCohortBuildBreakdownByGroups (cohort build breakdown by groups)".
   Body: what it does, the four inputs, the SHARE preset, test coverage (SQL Server in their CI,
   PostgreSQL locally), link to HicServices/RDMP#2368 as the prior review with all threads
   addressed, and the NHS 9.2.3 note. Follow their PR conventions if a template exists.
3. JFriel reviews; Michele has triage to manage the conversation.

### Phase 5 - closure (after merge)
1. Close HicServices/RDMP#2368 with a comment linking the HICPlugin PR/merge ("moved to its agreed
   home; review history preserved here"). Do NOT delete the fork release branch (preserves the PR
   diff and the review anchors).
2. Close the redundant fork PR mtinti/RDMP#1 the same way.
3. OneDrive `INSTALL.md`: add "long-term home: HICPlugin releases (RDMP >= <version of first
   HICPlugin release containing it>); RDMP 9.2.3 users continue with this standalone artifact".
4. Keep the standalone 9.2.3 `.rdmp` + OneDrive folder as the NHS channel until NHS upgrades.
5. Update project memory + this proposals folder with the outcome.

## 6. Risks and small unknowns (resolved during Phase 2/3, none blocking)

- Packaging wiring: exactly how the combined `.rdmp` enumerates plugin projects (inspect
  `Plugin/main/main.csproj` + the CI 7z step; add ours identically). Still the one real unknown.
- Changelog convention: RESOLVED - no changelog file exists; nothing to do.
- Mac build of `net10.0-windows` test project: expected OK to build; if `dotnet test` will not run
  the windows TFM on macOS, local verification uses a temporary net10.0 test shim or relies on
  their Windows CI (which is the merge gate anyway).
- Their CI runs MySQL cases; our fixture has none - fine. The pg case skips in their CI - noted.
- Submodule pin staleness: the pin (2026-05-19, 9.2.2-era) is OLDER than both v9.3.0 and our
  develop merge-base. Our code builds against released 9.2.3 AND current develop, so the pin
  sitting between them is expected clean (verified day one by Phase 1 step 5). We never bump the
  pin ourselves.
- SharePreset behaviour is unchanged - it resolves by name at runtime and prompts when not found,
  so bundling inside HICPlugin does not affect non-SHARE users.

## 7. What deliberately does NOT happen in this transfer

- No changes to the code itself (the port is file-copies + csproj/solution wiring).
- No new PR is opened without Michele's explicit approval (Phase 4 gate).
- RDMP#2368 stays open until the HICPlugin PR is merged (no burning bridges mid-transfer).
- The OneDrive NHS artifact keeps working throughout.

## 8. Effort estimate

Phase 0.5 (develop-sync pre-flight): DONE 2026-09-08 - see section 9.
Phase 1: 0.5 h - Phase 2: ~4 h - Phase 3: ~3 h - Phase 4: 1 h - Phase 5: 1 h.
Total: about 1.5 working days end to end, excluding JFriel's review latency.

## 9. Review addendum (2026-09-08)

Done since the plan was written:
- Phase 0.5 develop-sync pre-flight COMPLETE: origin/develop (55 commits) merged into
  `feature/cohort-healthboard-breakdown` with no conflicts; 14/14 tests pass on current develop
  (= 9.3.0) on SQL Server + PostgreSQL; pushed as 555873edc.
- RDMP 9.3.0 was RELEASED (2026-08-13). OneDrive now carries version-matched standalone artifacts:
  `RdmpCohortBuildBreakdownByGroups.rdmp` (9.2.x) and
  `RdmpCohortBuildBreakdownByGroups-rdmp9.3.0.rdmp` (9.3.x), plus WHICH-VERSION.txt.

New facts folded into the plan above:
- Submodule pin = 48e9c59 (2026-05-19, declares 9.2.2; predates patch 093 and the SixLabors
  license change) -> Phase 1 gains a pin-compile check; Phase 3 gains the TESTHP_ separate-database
  procedure; consequence 2 (NHS) is SOFTER (a release at the current pin is 9.2-compatible).
- HICPlugin has no changelog or PR template -> changelog steps removed.
- RDMP releases bundle HICPlugin's .rdmp in the box -> post-merge our plugin ships inside RDMP.

Two develop-side gotchas discovered by the pre-flight (recorded for anyone re-running local tests):
- Catalogue patch 093 means TEST_ platform databases needed
  `rdmp install "localhost,1433" TEST_ -u sa -p '...' -d` (drop-and-recreate) after the merge.
- SixLabors.ImageSharp.Drawing 3.1.0 (develop dep-bump #2390) enforces its license in RELEASE
  builds only; local Release builds of Tools/rdmp fail without SIXLABORS_LICENSE_KEY - build the
  tool in Debug locally (tests are Debug and unaffected; RDMP's CI holds the key).
