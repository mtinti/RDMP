# RdmpHealthBoardBreakdown plugin (RDMP 9.2.3)

Breaks a cohort's final inclusion list down by Scottish health board and node, by joining the
cohort's patient identifiers (CHI) to a demography catalogue's region column and counting distinct
patients per board.

Built against the **released RDMP 9.2.3**. Do not use on a different major.minor RDMP.

## Install

**Option A - GUI (recommended):** RDMP desktop -> Tables(Advanced) / Plugins node -> right-click
-> *Add Plugin* (or drag `RdmpHealthBoardBreakdown.rdmp` onto the Plugins node) -> restart RDMP.

**Option B - drop next to the executable:** copy `RdmpHealthBoardBreakdown.rdmp` into the same
folder as `rdmp.exe` / `ResearchDataManagementPlatform.exe`. It loads at startup.

Confirm it loaded (CLI): `rdmp.exe cmd ListSupportedCommands` should list
`ExportCohortHealthBoardBreakdown` and `ExportCicHealthBoardBreakdown`.

## Use

**GUI:** right-click a committed cohort *or* a Cohort Identification Configuration ->
*Export ... Health Board Breakdown* -> choose a CSV path.

**CLI:**
```
rdmp.exe cmd ExportCohortHealthBoardBreakdown ExtractableCohort:<id> out.csv "SHARE_Demography" "Region"
rdmp.exe cmd ExportCicHealthBoardBreakdown    CohortIdentificationConfiguration:<id> out.csv "SHARE_Demography" "Region"
```
Arguments after the cohort/CIC are optional (defaults: file `<name>-healthboard.csv`, catalogue
`SHARE_Demography`, column `Region`).

## Output

CSV columns: `Region,HBCode,HBName,Node,Count` - one row per health board, a subtotal per node, an
`Unknown` row for unmapped/NULL regions, and a grand total. `Count` is `COUNT(DISTINCT chi)`
(patients). A run summary reports how many cohort patients matched in the demography catalogue and
how many were not found (reconciliation).

## Assumptions

- The demography catalogue and the cohort identifiers are on the **same SQL Server**.
- The join identifier is **CHI** on both sides (the demography catalogue's IsExtractionIdentifier
  column).
- `Region` holds the single-letter health board cipher; the board/node mapping is built in.
- No small-number suppression is applied (raw counts) - apply disclosure control downstream.
