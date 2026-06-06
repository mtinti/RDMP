# Cohort round-trip test fixtures (docker TEST_ DB)

Two fixtures on the mac-test-env docker SQL Server, with fake data so cohort membership is
hand-verifiable. Used to develop/verify the export + BuildCohortFromScript round-trip.

## 1. FT_Original — simple (CIC 8)  → returns {P06, P10}
- Data: `TEST_ScratchArea.dbo.FT_{Demography,Prescribing,Biochemistry}` (12 patients).
- Catalogues `FT_*`, 3 published filters (`BornBefore @dobCutoff`, `OnDrug @drug`, `HighVal @threshold`).
- Structure: root EXCEPT [ INTERSECT(BornBefore, Diazepam)  -  HighVal ].
- Covers: set ops, published-filter imports, filter params, child order. **Round-trip verified** (copy == original).

## 2. CX_Complex — full feature set (CIC 14)  → returns {P01, P04, P05, P08}
- Data: `TEST_ScratchArea.dbo.CX_{Demography,Admissions,Prescriptions}` (10 patients).
- Catalogues `CX_Admissions`, `CX_Prescriptions`.
- **Patient index table** (joinable id 1): aggregate on CX_Admissions exposing `chi + admission_date`
  (built by: add as cohort set -> add admission_date dimension -> ConvertAggregateConfigurationToPatientIndexTable).
- **Cohort = root INTERSECT:**
  - Set A (CX_Admissions) **nested filter**: `(main_diagnosis LIKE 'J%' OR LIKE 'I%') AND admission_date >= '2020-01-01'`.
  - Set B (CX_Prescriptions): **aggregate param** `@window=90`, **join-use** to the index table
    (`INNER`), filter `prescribed_date BETWEEN ix1.admission_date AND DATEADD(day,@window,ix1.admission_date)`.
    The join alias is `ix{joinableId}` = `ix1`.
- Covers (the features the export/runner still needs to handle): **patient-index tables + join-use +
  the `ix####` alias in filter SQL, nested AND/OR filter containers, aggregate-level parameters.**

### Gotchas learned building CX_Complex (vs a real RDMP catalogue)
- NewObject-made catalogues store the dimension/EI `SelectSQL` UNQUALIFIED (`chi`); fine without a
  join, but a PIT join makes `chi` **ambiguous**. Real imported catalogues store the fully-qualified
  `[db]..[tbl].[col]` — so qualify `ExtractionInformation.SelectSQL` / `AggregateDimension.SelectSQL`.
- No CLI command creates a **join-use** (`JoinableCohortAggregateConfiguration.AddUser`) — insert into
  `JoinableCohortAggregateConfigurationUse(JoinableCohortAggregateConfiguration_ID, AggregateConfiguration_ID, JoinType)`.
- `SetContainerOperation` on the **named** Root/Inclusion/Exclusion containers, and `AddNewFilterContainer`,
  prompt interactively / misbehave headless — set `CohortAggregateContainer.Operation` /
  `AggregateFilterContainer.Operation` (0=AND/UNION,1=OR/INTERSECT,2=EXCEPT) + the SubContainer link directly.
- The PIT command `AddCatalogueToCohortIdentificationAsPatientIndexTable` isn't CLI-invokable
  (needs a `CatalogueCombineable`); use `ConvertAggregateConfigurationToPatientIndexTable` instead.
