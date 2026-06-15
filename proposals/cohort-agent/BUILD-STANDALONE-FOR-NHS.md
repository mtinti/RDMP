# Runbook: build the self-contained RDMP 9.2.3 standalone and upload to OneDrive for NHS testing

This is the repeatable procedure for shipping the cohort-scripting commands
(`ExportCohortAsScript` / `BuildCohortFromScript`) to NHS as a **self-contained `rdmp.exe`**
(no .NET install needed), and uploading the zip to Michele's Dundee OneDrive so it can be pulled
on the NHS Windows box.

> **Why a standalone and not just the plugin?** The plugin (`.rdmp`) is tiny but needs an existing
> RDMP install on the target. The standalone bundles the whole CLI + runtime, so it runs anywhere.
> Both are built the same way; this doc covers the standalone (the one we hand to NHS).

---

## 0. Key facts (read once)

| Thing | Value |
|---|---|
| Main repo checkout | `/Users/mtinti/git_projects/RDMP` |
| **Command source of truth** (latest, all fixes) | branch **`feature/cohort-script-commands`**, path `Rdmp.Core/CommandExecution/AtomicCommands/CohortScript/` (2 files: `ExecuteCommandExportCohortAsScript.cs`, `ExecuteCommandBuildCohortFromScript.cs`) |
| **Build worktree (9.2.3)** | `/Users/mtinti/git_projects/rdmp-923` — a `git worktree` detached at tag **`v9.2.3`** (commit `89ebee265`) |
| Why 9.2.3, not develop | NHS runs the **released 9.2.3**. `develop` is `9.2.4-Unreleased` → platform-DB schema mismatch. **Never ship a develop build to NHS.** |
| rclone remote | `onedrive:` (Michele's Dundee account `MTinti@dundee.ac.uk`) |
| Upload target folder | `onedrive:rdmp/cohort_scripting/` |
| Deliverable | `~/Desktop/rdmp-cohort-scripting-9.2.3-win-x64-standalone.zip` (~62 MB) |
| Requires | .NET 10 SDK, `rclone`, `zip` (all already installed on this Mac) |

The build worktree may already exist. If it does not:
```bash
git -C /Users/mtinti/git_projects/RDMP worktree add /Users/mtinti/git_projects/rdmp-923 v9.2.3
```

---

## 1. One-shot build + upload script

Paste this whole block. It pulls the latest command source straight from the PR branch (so it does
not matter what is checked out), bakes it into the 9.2.3 worktree, publishes, zips, uploads, **and
verifies the upload byte-for-byte.**

```bash
set -e
MAIN=/Users/mtinti/git_projects/RDMP
WT=/Users/mtinti/git_projects/rdmp-923
BR=feature/cohort-script-commands
DST="$WT/Rdmp.Core/CommandExecution/AtomicCommands/CohortScript"
ZIP=~/Desktop/rdmp-cohort-scripting-9.2.3-win-x64-standalone.zip

# 1a. sync the latest command code from the PR branch into the 9.2.3 worktree (branch-independent)
mkdir -p "$DST"
for f in ExecuteCommandExportCohortAsScript.cs ExecuteCommandBuildCohortFromScript.cs; do
  git -C "$MAIN" show "$BR:Rdmp.Core/CommandExecution/AtomicCommands/CohortScript/$f" > "$DST/$f"
done

# 1b. publish self-contained single-file win-x64 (takes ~1-2 min)
cd "$WT"
OUT="$WT/publish-standalone-923"; rm -rf "$OUT"
dotnet publish Tools/rdmp/rdmp.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:WarningsNotAsErrors='"NU1902;NU1903;NU1904"' -o "$OUT"

# 1c. drop non-windows native libs, package
cd "$OUT"
rm -rf runtimes/linux-* runtimes/osx-*
rm -f "$ZIP"
zip -r -q "$ZIP" . -x '*.DS_Store'

# 1d. upload and VERIFY (do NOT trust a silent success - always compare bytes)
rclone copy "$ZIP" "onedrive:rdmp/cohort_scripting/"
LOCAL=$(stat -f%z "$ZIP")
REMOTE=$(rclone size "onedrive:rdmp/cohort_scripting" --json 2>/dev/null | grep -o '"bytes":[0-9]*' | grep -o '[0-9]*')
[ "$LOCAL" = "$REMOTE" ] && echo "UPLOAD VERIFIED ($LOCAL bytes)" || echo "MISMATCH local=$LOCAL remote=$REMOTE - RE-RUN rclone copy"
```

> **Run the `rclone copy` in the FOREGROUND.** A backgrounded upload has been observed to exit 0 while
> the remote silently keeps the old file. The byte-compare at the end is mandatory; if it prints
> MISMATCH, just re-run `rclone copy "$ZIP" "onedrive:rdmp/cohort_scripting/"`.

---

## 2. Verify the build before handing it over (optional but recommended)

```bash
cd "$WT/publish-standalone-923"
# the exe is win-x64 so it can't run on the Mac; instead confirm the same code loads into the
# 9.2.3 CLI build (dll) and that all commands are discovered:
dotnet "$WT/Tools/rdmp/bin/Release/net10.0/rdmp.dll" cmd ListSupportedCommands 2>&1 \
  | grep -E '^(ExportCohortAsScript|BuildCohortFromScript)$'
```
On the NHS box the real smoke test is: unzip, then `rdmp.exe cmd ListSupportedCommands` (should list
the commands), then run an export with `--skip-patching` (read-only).

---

## 3. rclone OneDrive token (when the upload step fails auth)

The `onedrive:` access token lasts ~1 hour but rclone auto-refreshes it from the stored refresh
token, so normally you do nothing. If rclone reports `invalid_grant` / `token expired`, the refresh
token itself has lapsed (weeks of disuse). Re-auth **headlessly** (Michele is usually remote, so the
browser must be on *their* device, not this Mac):

1. On a machine with a browser + rclone, run: `rclone authorize "onedrive"`
2. Authorize in the browser; it prints a token JSON.
3. Paste that JSON back; inject it here without a browser:
   ```bash
   rclone config update onedrive token '<PASTE THE JSON>'
   rclone lsd onedrive:        # confirm it works
   ```

(Google Drive was the original ask but its token also needs browser re-auth; OneDrive was the
pragmatic choice because the remote was already configured. Same `rclone authorize "drive"` flow
applies if a `gdrive` remote is ever set up.)

---

## 4. Troubleshooting

- **`Execution Timeout Expired` at startup** (stack shows `CatalogueChildProvider` / `BasicActivateItems`
  ctor, *before* the command runs): RDMP bulk-loads the whole platform catalogue at startup with a
  **hardcoded 30 s** command timeout (`DatabaseCommandHelper.GlobalTimeout`, no config knob in 9.2.3).
  On a busy NHS server (overnight maintenance, blocking locks) that read can exceed 30 s. **First fix:
  just retry, ideally outside maintenance hours.** Belt-and-braces: in the standalone only, set
  `DatabaseCommandHelper.GlobalTimeout = 300;` early in `Tools/rdmp/Program.cs` `Main` and rebuild —
  raises every query to 5 min. (Do not put this in the upstream PR; it is a deployment tweak.)
- **Duplicate-command errors / stale behaviour:** an old baked-in copy of a command left in
  `$WT/Rdmp.Core/.../AtomicCommands/` (flat, not in `CohortScript/`) shadows the new one. Remove any
  stray `ExecuteCommand*CohortAsScript.cs` / `*BuildCohortFromScript.cs` sitting directly in
  `AtomicCommands/` before building.
- **Upload "succeeded" but NHS has the old file:** the silent-background-upload trap. Always run the
  byte-compare in step 1d; re-run `rclone copy` if it mismatches.
- **`Databases.yaml` in the zip** is the neutral LocalDB default. NHS edits it to point at their
  platform `Catalogue` + `DataExport` DBs. Nothing patient-level is ever in the deliverable.

---

## 5. Plugin variant (if NHS already has RDMP 9.2.3 installed)

Same source, but build the in-tree plugin project instead and zip just its DLL + nuspec:
```bash
cd "$WT/proposals/cohort-agent/plugin"   # plugin .csproj refs the worktree's Rdmp.Core
dotnet build RdmpCohortExport.csproj -c Release -p:WarningsNotAsErrors='"NU1902;NU1903;NU1904"'
# then zip bin/Release/**/RdmpCohortExport.dll + RdmpCohortExport.nuspec into RdmpCohortExport-9.2.3.rdmp
```
The plugin command files use namespace `Rdmp.Core.CommandExecution.AtomicCommands` (the standalone's
`CohortScript/` files use `...CohortScript`); keep them in sync by `sed`-swapping the namespace line
when copying from the PR branch. Drop the `.rdmp` next to `rdmp.exe`; it loads at startup.
