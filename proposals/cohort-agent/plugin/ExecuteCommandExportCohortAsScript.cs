// Exports a Cohort Identification Configuration (CIC) as a data-free training triple:
//   <out>/<cic>/requirement.md   <out>/<cic>/build.script.yaml   <out>/<cic>/query.sql
//
// This is the verification copy of the cohort-export plugin command, placed in Rdmp.Core so
// the CLI's MEF discovery exposes it as `rdmp cmd ExportCohortAsScript` for round-trip testing.
// The shippable plugin (proposals/cohort-agent/plugin) carries the identical traversal logic.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.DataExport.Data;
using Rdmp.Core.QueryBuilding;

namespace Rdmp.Core.CommandExecution.AtomicCommands;

public class ExecuteCommandExportCohortAsScript : BasicCommandExecution
{
    private readonly CohortIdentificationConfiguration _cic;
    private readonly DirectoryInfo _outDir;

    public ExecuteCommandExportCohortAsScript(IBasicActivateItems activator,
        [DemandsInitialization("The cohort to export")]
        CohortIdentificationConfiguration cic,
        [DemandsInitialization("Folder to write the export into")]
        DirectoryInfo toDir = null) : base(activator)
    {
        _cic = cic;
        _outDir = toDir ?? new DirectoryInfo(Environment.CurrentDirectory);

        if (_cic == null)
            SetImpossible("No CohortIdentificationConfiguration was supplied");
    }

    public override void Execute()
    {
        base.Execute();

        var dir = new DirectoryInfo(Path.Combine(_outDir.FullName, Sanitise(_cic.Name)));
        dir.Create();

        // requirement.md is an intentionally-empty placeholder. The natural-language
        // requirement is added by hand later (extracted from the request form) - it is NOT
        // taken from CIC.Description, which may be unrelated. Never overwrite a requirement
        // that has already been filled in, so re-exporting is safe.
        var reqPath = Path.Combine(dir.FullName, "requirement.md");
        if (!File.Exists(reqPath))
            File.WriteAllText(reqPath, $"<!-- Paste the natural-language requirement for '{_cic.Name}' here. -->\n");

        File.WriteAllText(Path.Combine(dir.FullName, "build.script.yaml"), BuildScript());

        // query.sql: the SQL as RDMP would run it - this uses the cohort's QueryCache if one
        // is configured (so it references cache tables). SQL generation is best-effort.
        File.WriteAllText(Path.Combine(dir.FullName, "query.sql"), BuildSql(useCache: true));

        // query.uncached.sql: the full query against the raw tables, cache bypassed. Only emitted
        // when a cache is configured (otherwise query.sql is already the un-cached query).
        if (_cic.QueryCachingServer_ID.HasValue)
            File.WriteAllText(Path.Combine(dir.FullName, "query.uncached.sql"), BuildSql(useCache: false));

        BasicActivator.Show($"Exported '{_cic.Name}' to {dir.FullName}");
    }

    private string BuildSql(bool useCache)
    {
        try
        {
            var builder = new CohortQueryBuilder(_cic, null);
            if (!useCache && _cic.QueryCachingServer_ID.HasValue)
                builder.CacheServer = null; // force the query to run against the raw tables
            return builder.SQL ?? "";
        }
        catch (Exception e)
        {
            return $"-- SQL generation failed: {e.Message}";
        }
    }

    private string BuildScript()
    {
        var root = _cic.RootCohortAggregateContainer;

        // ' => $handle' declares the handle bound to the object a creating command produces, so
        // the runner (BuildCohortFromScript) can re-bind it to the real id it gets at build time.
        var lines = new List<string>
        {
            $"# Decompiled from CohortIdentificationConfiguration ID {_cic.ID}",
            "Commands:",
            root != null
                ? $"  - CreateNewCohortIdentificationConfiguration \"{_cic.Name}\" => {ContainerRef(root)}"
                : $"  - CreateNewCohortIdentificationConfiguration \"{_cic.Name}\"",
        };

        // If this cohort is tied to a Project (required to use project-specific catalogues),
        // record it so the rebuilt cohort joins the same Project BEFORE its catalogues are added.
        var assoc = BasicActivator.RepositoryLocator.DataExportRepository
            .GetAllObjectsWhere<ProjectCohortIdentificationConfigurationAssociation>(
                "CohortIdentificationConfiguration_ID", _cic.ID).FirstOrDefault();
        if (assoc != null)
            lines.Add($"  - AssociateWithProject Project:{assoc.Project_ID}");

        // Patient-index tables (joinables) must exist BEFORE the cohort sets that join to them.
        // The runner creates each as $pit<oldJoinableId> and rewrites the ix<oldId> filter alias.
        foreach (var j in _cic.GetAllJoinables())
        {
            var pitAgg = j.AggregateConfiguration;
            var dims = pitAgg.AggregateDimensions
                .Where(d => d.ExtractionInformation is not { IsExtractionIdentifier: true })
                .Select(d => d.GetRuntimeName());
            // Aggregate:<oldId> lets the runner bind the rebuilt PIT aggregate to $a<oldId> so the
            // dimension-SQL overrides below (which restore e.g. the qualified chi) can target it.
            lines.Add(
                $"  - CreatePatientIndexTable Catalogue:{Quote(pitAgg.Catalogue.Name)} Aggregate:{pitAgg.ID} Dimensions:\"{string.Join(",", dims)}\" => $pit{j.ID}");
            EmitDimensionOverrides(pitAgg, AggregateRef(pitAgg), lines);
        }

        // Cohort-level (global) parameters. Per-filter parameters are emitted inline as Set
        // commands right after each filter (see EmitFilters).
        EmitGlobalParameters(lines);

        if (root != null)
            EmitContainer(root, lines);
        else
            lines.Add("  # (this CIC has no root container)");

        return string.Join("\n", lines) + "\n";
    }

    private void EmitGlobalParameters(List<string> lines)
    {
        // Only cohort-level (global) parameters. Filter parameters are emitted inline as Set
        // commands after each filter, so they are not duplicated here.
        var globals = _cic.GetAllParameters();
        if (globals.Length == 0)
            return;

        lines.Add("  # --- global (cohort-level) SQL parameters ---");
        foreach (var p in globals)
        {
            var comment = string.IsNullOrWhiteSpace(p.Comment) ? "" : $"   /* {OneLine(p.Comment)} */";
            lines.Add($"  #   {OneLine(p.ParameterSQL)}   SET {p.ParameterName} = {OneLine(p.Value)}{comment}");
        }
    }

    // Containers and aggregates are referenced by stable handles ($c<id> / $a<id>) rather than
    // by name: names are not guaranteed unique or meaningful (an unnamed sub-container reports
    // its operation as its name). The trailing comment keeps the script human-readable.
    private static string ContainerRef(CohortAggregateContainer c) => $"$c{c.ID}";
    private static string AggregateRef(AggregateConfiguration a) => $"$a{a.ID}";

    private void EmitContainer(CohortAggregateContainer container, List<string> lines)
    {
        var cref = ContainerRef(container);
        lines.Add($"  - SetContainerOperation CohortAggregateContainer:{cref} {container.Operation}   # {container.Name}");

        foreach (var content in container.GetOrderedContents())
            switch (content)
            {
                case AggregateConfiguration agg:
                    EmitAggregate(agg, cref, lines);
                    break;
                case CohortAggregateContainer sub:
                    lines.Add($"  - AddCohortSubContainer CohortAggregateContainer:{cref} => {ContainerRef(sub)}");
                    EmitContainer(sub, lines);
                    break;
            }
    }

    private void EmitAggregate(AggregateConfiguration agg, string containerRef, List<string> lines)
    {
        var cata = agg.Catalogue;
        var cataRef = cata != null ? Quote(cata.Name) : "<unknown-catalogue>";
        lines.Add(
            $"  - AddCatalogueToCohortIdentificationSetContainer CohortAggregateContainer:{containerRef} Catalogue:{cataRef} => {AggregateRef(agg)}");

        foreach (var dim in agg.AggregateDimensions)
            lines.Add($"  # dimension: {dim.GetRuntimeName()}   (auto-set when catalogue added)");

        // Restore any dimension whose SelectSQL was customised away from the catalogue default
        // (e.g. the extraction identifier qualified to [db]..[tbl].[col] so a PIT join isn't ambiguous).
        EmitDimensionOverrides(agg, AggregateRef(agg), lines);

        // join-uses: this cohort set joins to a patient-index table ($pit<id>) created earlier.
        foreach (var use in agg.PatientIndexJoinablesUsed)
            lines.Add(
                $"  - UsePatientIndexTable {AggregateRef(agg)} $pit{use.JoinableCohortAggregateConfiguration_ID} {use.JoinType}");

        // aggregate-level parameters (e.g. @window) - distinct from filter parameters.
        // Query directly (agg.Parameters also filters on repository-type, which can miss).
        // Emitted as a directive the runner creates directly (AnyTableSqlParameter on the aggregate).
        foreach (var ap in BasicActivator.RepositoryLocator.CatalogueRepository
                     .GetAllObjects<AnyTableSqlParameter>()
                     .Where(p => p.ReferencedObjectType == nameof(AggregateConfiguration) && p.ReferencedObjectID == agg.ID))
            lines.Add(
                $"  - AddAggregateParameter {AggregateRef(agg)} \"{ap.ParameterName}\" \"{OneLine(ap.ParameterSQL)}\" \"{OneLine(ap.Value)}\"");

        if (agg.RootFilterContainer is { } fc)
            EmitFilters(fc, AggregateRef(agg), lines);
    }

    // Emits a SetDimensionSql directive for every dimension whose SelectSQL was customised away
    // from its catalogue ExtractionInformation default. Real (qualified) catalogues usually match,
    // so nothing is emitted; NewObject/test catalogues that were hand-qualified emit the override.
    private void EmitDimensionOverrides(AggregateConfiguration agg, string aggRef, List<string> lines)
    {
        foreach (var dim in agg.AggregateDimensions)
        {
            var eiSql = dim.ExtractionInformation?.SelectSQL;
            if (!string.IsNullOrWhiteSpace(dim.SelectSQL) && dim.SelectSQL != eiSql)
                lines.Add($"  - SetDimensionSql {aggRef} \"{dim.GetRuntimeName()}\" \"{OneLine(dim.SelectSQL)}\"");
        }
    }

    private void EmitFilters(IContainer rootFc, string aggRef, List<string> lines)
    {
        // Build the aggregate's root filter container with the right AND/OR operation, then fill it.
        // (Directives the runner handles directly; the CLI AddNewFilterContainer misbehaves headless.)
        var key = $"fc{((AggregateFilterContainer)rootFc).ID}";
        lines.Add($"  - EnsureFilterContainer {aggRef} {rootFc.Operation} => ${key}");
        EmitContainerFilters(rootFc, key, lines);
    }

    // fcKey (no leading $) is the handle of the container the filters/sub-containers go INTO.
    private void EmitContainerFilters(IContainer fc, string fcKey, List<string> lines)
    {
        foreach (var filter in fc.GetFilters())
        {
            var pbinds = string.Join(" ",
                filter.GetAllParameters().OfType<AggregateFilterParameter>().Select(p => $"$p{p.ID}"));
            var binds = string.IsNullOrEmpty(pbinds) ? "" : $" => {pbinds}";

            if (filter is AggregateFilter { ClonedFromExtractionFilter_ID: { } efid })
                lines.Add(
                    $"  - CreateNewFilter AggregateFilterContainer:${fcKey} ExtractionFilter:{efid}{binds}   # \"{filter.Name}\": {OneLine(filter.WhereSQL)}");
            else
                lines.Add(
                    $"  - CreateNewFilter AggregateFilterContainer:${fcKey} \"{filter.Name}\" \"{OneLine(filter.WhereSQL)}\"{binds}");

            foreach (var p in filter.GetAllParameters())
                if (p is AggregateFilterParameter afp)
                    lines.Add(
                        $"  - Set AggregateFilterParameter:$p{afp.ID} Value \"{OneLine(afp.Value)}\"   # {afp.ParameterName}  ({OneLine(afp.ParameterSQL)})");
        }

        foreach (var sub in fc.GetSubContainers())
        {
            var subKey = $"fc{((AggregateFilterContainer)sub).ID}";
            lines.Add($"  - AddFilterSubContainer ${fcKey} {sub.Operation} => ${subKey}");
            EmitContainerFilters(sub, subKey, lines);
        }
    }

    private static string Quote(string name) => $"\"{name}\"";
    private static string OneLine(string sql) => (sql ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

    private static string Sanitise(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        return sb.ToString();
    }
}
