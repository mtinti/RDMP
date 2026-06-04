// RDMP plugin command: "decompile" a Cohort Identification Configuration (CIC) into the
// data-free training triple used by the cohort-agent prototype:
//   <out>/<cic>/requirement.md      (from CIC.Description)
//   <out>/<cic>/build.script.yaml   (equivalent rdmp cmd script, reconstructed from the tree)
//   <out>/<cic>/query.sql           (CohortQueryBuilder generated SQL)
//
// Runs INSIDE the RDMP environment (UI right-click on a CIC, or CLI). Only the three small
// text files it writes ever need to leave - no database/data export.
//
// CLI:  rdmp cmd ExportCohortAsScript CohortIdentificationConfiguration:5 ./export
// UI:   right-click a CohortIdentificationConfiguration (see CohortExportPluginUserInterface)
//
// STATUS: first pass, authored against the RDMP API but NOT yet compiled/run (no metadata
// available outside NHS). Build + smoke-test inside NHS and adjust as noted in README.md.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.QueryBuilding;

namespace RdmpCohortExport;

public class ExecuteCommandExportCohortAsScript : BasicCommandExecution
{
    private readonly CohortIdentificationConfiguration _cic;
    private readonly DirectoryInfo _outDir;

    // The activator + a CIC: RDMP's CLI resolves `CohortIdentificationConfiguration:5`
    // and the UI passes the right-clicked object into this constructor.
    public ExecuteCommandExportCohortAsScript(IBasicActivateItems activator,
        CohortIdentificationConfiguration cic,
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

        // 1) requirement.md - an intentionally-empty placeholder. The natural-language
        //    requirement is pasted in by hand later (from the request form); it is NOT taken
        //    from CIC.Description (which may be unrelated). Never overwrite an already-filled
        //    requirement, so re-exporting only refreshes the script + SQL.
        var reqPath = Path.Combine(dir.FullName, "requirement.md");
        if (!File.Exists(reqPath))
            File.WriteAllText(reqPath, $"<!-- Paste the natural-language requirement for '{_cic.Name}' here. -->\n");

        // 2) build.script.yaml - reconstruct the equivalent rdmp cmd script from the tree
        File.WriteAllText(Path.Combine(dir.FullName, "build.script.yaml"), BuildScript());

        // 3) query.sql - the SQL RDMP generates (uses the cohort's QueryCache if configured)
        File.WriteAllText(Path.Combine(dir.FullName, "query.sql"), BuildSql(useCache: true));

        // 3b) query.uncached.sql - full query against the raw tables (cache bypassed). Only
        //     written when a cache is configured; otherwise query.sql is already un-cached.
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
    // VERIFIED: round-trips a built cohort (see proposals/cohort-agent/plugin/README.md).
    private static string ContainerRef(CohortAggregateContainer c) => $"$c{c.ID}";
    private static string AggregateRef(AggregateConfiguration a) => $"$a{a.ID}";

    // Walks a CohortAggregateContainer: its set operation, its cohort-set aggregates and
    // any nested sub-containers, in display order.
    private void EmitContainer(CohortAggregateContainer container, List<string> lines)
    {
        var cref = ContainerRef(container);
        lines.Add($"  - SetContainerOperation CohortAggregateContainer:{cref} {container.Operation}   # {container.Name}");

        foreach (var content in container.GetOrderedContents())
        {
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
    }

    // A "cohort set": one Catalogue added to the container, its identifier dimension, its filters.
    private void EmitAggregate(AggregateConfiguration agg, string containerRef, List<string> lines)
    {
        var cata = agg.Catalogue;
        var cataRef = cata != null ? Quote(cata.Name) : "<unknown-catalogue>";
        lines.Add(
            $"  - AddCatalogueToCohortIdentificationSetContainer CohortAggregateContainer:{containerRef} Catalogue:{cataRef}   # creates {AggregateRef(agg)}");

        // The identifier dimension is auto-set when the catalogue is added; record it for clarity.
        foreach (var dim in agg.AggregateDimensions)
            lines.Add($"  # dimension: {dim.GetRuntimeName()}   (auto-set; SetAggregateDimension only if overriding)");

        if (agg.RootFilterContainer is { } fc)
            EmitFilters(fc, AggregateRef(agg), lines);
    }

    // Walks an AggregateFilterContainer (AND/OR) and its filters/sub-containers.
    // hostRef is the aggregate (or filter sub-container) the filters attach to.
    private void EmitFilters(IContainer fc, string hostRef, List<string> lines)
    {
        foreach (var filter in fc.GetFilters())
            lines.Add(
                $"  - CreateNewFilter AggregateConfiguration:{hostRef} \"{filter.Name}\" \"{OneLine(filter.WhereSQL)}\"");

        var subs = fc.GetSubContainers();
        if (subs.Length > 0)
            lines.Add(
                $"  # nested filter group(s) under operation {fc.Operation}: build via AddNewFilterContainer then SetContainerOperation");
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
