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

        // 1) requirement.md - the natural-language requirement lives in CIC.Description
        File.WriteAllText(Path.Combine(dir.FullName, "requirement.md"),
            $"# {_cic.Name}\n\n{_cic.Description ?? "(no description set on the CIC)"}\n");

        // 2) build.script.yaml - reconstruct the equivalent rdmp cmd script from the tree
        File.WriteAllText(Path.Combine(dir.FullName, "build.script.yaml"), BuildScript());

        // 3) query.sql - the SQL RDMP generates for this cohort
        var builder = new CohortQueryBuilder(_cic, null);
        File.WriteAllText(Path.Combine(dir.FullName, "query.sql"), builder.SQL ?? "");

        BasicActivator.Show($"Exported '{_cic.Name}' to {dir.FullName}");
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
        if (root != null)
            EmitContainer(root, lines);
        else
            lines.Add("  # (this CIC has no root container)");

        return string.Join("\n", lines) + "\n";
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
