# RDMP cohort-building command reference

Verified against the built `rdmp` CLI (`cmd ListSupportedCommands`, 181 commands total —
see `commands_full_list.txt`) and the command constructors in
`Rdmp.Core/CommandExecution/AtomicCommands/`. These are the REAL commands and argument
orders for building a Cohort Identification Configuration (CIC) from the CLI.

## Invocation & object references
- Form: `rdmp cmd <CommandName> <arg0> <arg1> ... [--dir <repo>]`
- Reference an existing object as `Type:ID` (e.g. `Catalogue:5`) or `Type:Name`
  (e.g. `Catalogue:Biochemistry`). Names with spaces go in quotes.
- In a script (`rdmp cmd -f script.yaml`, `UseScope: true`), objects created earlier in
  the same script are tracked and can be referenced by name.
- Enum arguments are given by name (e.g. `UNION`).

## CIC structure recap
```
CohortIdentificationConfiguration
└── root CohortAggregateContainer        (SetOperation: UNION | INTERSECT | EXCEPT)
    ├── AggregateConfiguration  ("cohort set", built from one Catalogue; its identifier
    │     └── (optional) AggregateFilterContainer (AND/OR) -> AggregateFilter(s)   column is the dimension)
    └── nested CohortAggregateContainer(s)
```

## The commands (exact arg order)

| Command | Args (after the implicit activator) | Notes |
|---------|--------------------------------------|-------|
| `CreateNewCohortIdentificationConfiguration` | `"<name>"` | Also auto-creates the root container (defaults to UNION). |
| `SetContainerOperation` | `CohortAggregateContainer:<ref>` `UNION\|INTERSECT\|EXCEPT` | Sets the set logic of a container. |
| `AddCohortSubContainer` | `CohortAggregateContainer:<parentRef>` | Adds a nested container (for mixing AND/OR/EXCEPT logic). |
| `AddCatalogueToCohortIdentificationSetContainer` | `CohortAggregateContainer:<ref>` `Catalogue:<ref>` `[ExtractionInformation:<ref>]` | **Container first, then catalogue.** Creates a cohort-set AggregateConfiguration and auto-sets the patient-identifier dimension. 3rd arg only needed if the catalogue has >1 IsExtractionIdentifier column. |
| `AddAggregateConfigurationToCohortIdentificationSetContainer` | `AggregateConfiguration:<ref>` `CohortAggregateContainer:<ref>` | Add an existing aggregate as a set. |
| `AddCatalogueToCohortIdentificationAsPatientIndexTable` | `Catalogue:<ref>` `CohortIdentificationConfiguration:<ref>` | Use for "patient index" / joinable lookup tables. |
| `SetAggregateDimension` | `AggregateConfiguration:<ref>` `ExtractionInformation:<ref>` | Usually NOT needed — the dimension is auto-set when the catalogue is added. Use only to override. |
| `CreateNewFilter` | `AggregateConfiguration:<ref>` `"<FilterName>"` `"<WHERE SQL>"` | Adds a WHERE filter; auto-creates the aggregate's root AND filter container. First arg may also be a filter `CohortAggregateContainer`/`AggregateFilterContainer`. |
| `AddNewFilterContainer` | `AggregateConfiguration:<ref>` (or `AggregateFilterContainer:<ref>`) | Add an AND/OR sub-group of filters; set its operation, then add filters into it. |

## SetOperation values
`UNION` (anyone in any subset) · `INTERSECT` (only people in every subset) · `EXCEPT`
(people in the first subset minus the rest). For "A AND B EXCLUDING C": root = EXCEPT,
first child = an INTERSECT sub-container holding A and B, second child = C.

## Worked example (free-text WHERE filters)
```yaml
Commands:
  - CreateNewCohortIdentificationConfiguration "Diabetics on Diazepam (alive)"
  # root = EXCEPT: (inclusion) minus (has died)
  - SetContainerOperation CohortAggregateContainer:"Root Container" EXCEPT
  - AddCohortSubContainer CohortAggregateContainer:"Root Container"           # inclusion sub-container
  - SetContainerOperation CohortAggregateContainer:"Inclusion Criteria" INTERSECT
  - AddCatalogueToCohortIdentificationSetContainer CohortAggregateContainer:"Inclusion Criteria" Catalogue:Admissions
  - CreateNewFilter AggregateConfiguration:Admissions "DiabetesAfter2010" "main_condition_icd10 LIKE 'E1[0-4]%' AND admission_date >= '2010-01-01'"
  - AddCatalogueToCohortIdentificationSetContainer CohortAggregateContainer:"Inclusion Criteria" Catalogue:Prescribing
  - CreateNewFilter AggregateConfiguration:Prescribing "Diazepam" "drug_name = 'Diazepam'"
  - AddCatalogueToCohortIdentificationSetContainer CohortAggregateContainer:"Root Container" Catalogue:Demography
  - CreateNewFilter AggregateConfiguration:Demography "HasDied" "date_of_death IS NOT NULL"
```

## ⚠️ Known scripting gap to resolve when wiring RealRdmp
The root container and the auto-created cohort-set aggregates get runtime IDs/auto names.
Referencing them reliably mid-script (by `"Root Container"`, by the catalogue name, or by
ID captured from prior output) is the main thing to validate against the real CLI. RDMP's
`NewObjectPool` (script `UseScope: true`) is designed to help here; confirm the exact
reference form that resolves, and adjust this reference + the Builder prompt accordingly.
The names `"Root Container"`, `"Inclusion Criteria"`, `"Exclusion Criteria"` are RDMP's
defaults (see `ExecuteCommandCreateNewCohortIdentificationConfiguration`).
