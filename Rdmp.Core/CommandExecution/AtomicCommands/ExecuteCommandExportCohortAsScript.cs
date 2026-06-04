// Exports a Cohort Identification Configuration (CIC) as a data-free training triple:
//   <out>/<cic>/requirement.md   <out>/<cic>/build.script.yaml   <out>/<cic>/query.sql
//
// This is the verification copy of the cohort-export plugin command, placed in Rdmp.Core so
// the CLI's MEF discovery exposes it as `rdmp cmd ExportCohortAsScript` for round-trip testing.
// The shippable plugin (proposals/cohort-agent/plugin) carries the identical traversal logic.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
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
        var lines = new List<string>
        {
            $"# Decompiled from CohortIdentificationConfiguration ID {_cic.ID}",
            "Commands:",
            $"  - CreateNewCohortIdentificationConfiguration \"{_cic.Name}\"",
        };

        var root = _cic.RootCohortAggregateContainer;

        // SQL parameters (e.g. @indexDate) declared globally or carried by the cohort's filters.
        // RDMP hoists these to the top of the generated SQL; capture them so the script is complete.
        EmitParameters(root, lines);

        if (root != null)
            EmitContainer(root, lines);
        else
            lines.Add("  # (this CIC has no root container)");

        return string.Join("\n", lines) + "\n";
    }

    private void EmitParameters(CohortAggregateContainer root, List<string> lines)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var collected = new List<ISqlParameter>();

        void Collect(IEnumerable<ISqlParameter> ps)
        {
            foreach (var p in ps ?? Array.Empty<ISqlParameter>())
                if (!string.IsNullOrWhiteSpace(p?.ParameterName) && seen.Add(p.ParameterName))
                    collected.Add(p);
        }

        Collect(_cic.GetAllParameters());        // global parameters
        if (root != null)
            foreach (var f in AllFilters(root))
                Collect(f.GetAllParameters());    // parameters carried by each filter

        if (collected.Count == 0)
            return;

        lines.Add("  # --- SQL parameters used by this cohort (declared globally / by filters) ---");
        foreach (var p in collected)
        {
            var comment = string.IsNullOrWhiteSpace(p.Comment) ? "" : $"   /* {OneLine(p.Comment)} */";
            lines.Add($"  #   {OneLine(p.ParameterSQL)}   SET {p.ParameterName} = {OneLine(p.Value)}{comment}");
        }
    }

    private static IEnumerable<IFilter> AllFilters(CohortAggregateContainer container)
    {
        foreach (var agg in container.GetAggregateConfigurations())
            if (agg.RootFilterContainer is { } fc)
                foreach (var f in FiltersIn(fc))
                    yield return f;
        foreach (var sub in container.GetSubContainers())
            foreach (var f in AllFilters(sub))
                yield return f;
    }

    private static IEnumerable<IFilter> FiltersIn(IContainer fc)
    {
        foreach (var f in fc.GetFilters())
            yield return f;
        foreach (var sub in fc.GetSubContainers())
            foreach (var f in FiltersIn(sub))
                yield return f;
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
                    lines.Add($"  - AddCohortSubContainer CohortAggregateContainer:{cref}");
                    EmitContainer(sub, lines);
                    break;
            }
    }

    private void EmitAggregate(AggregateConfiguration agg, string containerRef, List<string> lines)
    {
        var cata = agg.Catalogue;
        var cataRef = cata != null ? Quote(cata.Name) : "<unknown-catalogue>";
        lines.Add(
            $"  - AddCatalogueToCohortIdentificationSetContainer CohortAggregateContainer:{containerRef} Catalogue:{cataRef}   # creates {AggregateRef(agg)}");

        foreach (var dim in agg.AggregateDimensions)
            lines.Add($"  # dimension: {dim.GetRuntimeName()}   (auto-set when catalogue added)");

        if (agg.RootFilterContainer is { } fc)
            EmitFilters(fc, AggregateRef(agg), lines);
    }

    private void EmitFilters(IContainer fc, string hostRef, List<string> lines)
    {
        foreach (var filter in fc.GetFilters())
            lines.Add(
                $"  - CreateNewFilter AggregateConfiguration:{hostRef} \"{filter.Name}\" \"{OneLine(filter.WhereSQL)}\"");

        var subs = fc.GetSubContainers();
        if (subs.Length > 0)
            lines.Add($"  # nested filter group(s) under operation {fc.Operation}: AddNewFilterContainer + SetContainerOperation");
        foreach (var sub in subs)
            EmitFilters(sub, hostRef, lines);
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
