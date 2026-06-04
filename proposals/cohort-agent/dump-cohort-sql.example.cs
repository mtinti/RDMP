// Example glue tool: dump the SQL that RDMP generates for a CohortIdentificationConfiguration.
//
// This is the piece the *Verifier* agent needs: the CIC SQL as TEXT (not query results).
// `rdmp cmd ViewData` deliberately EXECUTES the cohort and writes CSV, so it is the wrong tool.
// Here we call CohortQueryBuilder directly, exactly like ViewCohortIdentificationConfigurationSqlCollection.GetSql().
//
// Build as a tiny console project that references Rdmp.Core, e.g.:
//   dotnet run --project tools/DumpCohortSql -- <cicId> [--dir ./meta | --yaml ./Databases.yaml]
//
// Two repository backends are shown:
//   * YamlRepository    — file-backed metadata folder (portable, no SQL Server)
//   * LinkedRepository  — the full Catalogue + DataExport SQL Server platform DBs (your chosen path)

using System;
using System.IO;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.QueryBuilding;
using Rdmp.Core.Repositories;
using Rdmp.Core.Startup;

var cicId = int.Parse(args[0]);

IRDMPPlatformRepositoryServiceLocator locator;

if (args.Length >= 3 && args[1] == "--dir")
{
    // File-backed metadata folder
    locator = new RepositoryProvider(new YamlRepository(new DirectoryInfo(args[2])));
}
else
{
    // Full SQL Server platform DBs. Pass the two connection strings however you like;
    // the CLI loads them from Databases.yaml via ConnectionStringsYamlFile — mirror that.
    var catalogue = Environment.GetEnvironmentVariable("RDMP_CATALOGUE_CONNSTR");
    var dataExport = Environment.GetEnvironmentVariable("RDMP_DATAEXPORT_CONNSTR");
    locator = new LinkedRepositoryProvider(catalogue, dataExport);
}

// Make sure platform startup has run (patching can be skipped for read-only use).
new Startup(locator) { SkipPatching = true }.DoStartup(new ThrowImmediatelyCheckNotifier());

var cic = locator.CatalogueRepository.GetObjectByID<CohortIdentificationConfiguration>(cicId);

var builder = new CohortQueryBuilder(cic, null);
builder.RegenerateSQL();

// Print to stdout so the harness can capture it; redirect to a file if you prefer.
Console.Write(builder.SQL);
