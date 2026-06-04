// Exports a "catalogue manifest" - the menu of building blocks a cohort Builder needs:
// for each Catalogue, its extractable columns, its patient-identifier column(s), and its
// PUBLISHED filters (ExtractionFilter) with id, name, WHERE SQL and parameters.
//
// This grounds the Builder so it can pick filters by MEANING (and reference them by id),
// and lets a Verifier check that a chosen ExtractionFilter actually matches the requirement.
//
//   rdmp cmd ExportCatalogueManifest                       (all catalogues -> ./catalogue-manifest.yaml)
//   rdmp cmd ExportCatalogueManifest Catalogue:18210 .\m.yaml
//
// Verification copy in Rdmp.Core so the CLI's MEF discovery exposes it; identical logic ships
// in the plugin sources (proposals/cohort-agent/plugin).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;
using YamlDotNet.Serialization;

namespace RdmpCohortExport;

public class ExecuteCommandExportCatalogueManifest : BasicCommandExecution
{
    private readonly Catalogue[] _catalogues;
    private readonly FileInfo _toFile;

    public ExecuteCommandExportCatalogueManifest(IBasicActivateItems activator,
        [DemandsInitialization("A single catalogue to export, or null for all catalogues")]
        Catalogue catalogue = null,
        [DemandsInitialization("File to write the manifest to (default ./catalogue-manifest.yaml)")]
        FileInfo toFile = null) : base(activator)
    {
        _catalogues = catalogue != null
            ? new[] { catalogue }
            : activator.RepositoryLocator.CatalogueRepository.GetAllObjects<Catalogue>();
        _toFile = toFile ?? new FileInfo(Path.Combine(Environment.CurrentDirectory, "catalogue-manifest.yaml"));
    }

    public override void Execute()
    {
        base.Execute();

        var catalogues = new List<object>();
        foreach (var c in _catalogues.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var eis = c.GetAllExtractionInformation();

            var filters = new List<object>();
            foreach (var f in c.GetAllFilters().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                var ps = f.GetAllParameters().Select(p => (object)new Dictionary<string, object>
                {
                    ["name"] = p.ParameterName,
                    ["declare"] = OneLine(p.ParameterSQL),
                    ["value"] = OneLine(p.Value),
                    ["comment"] = OneLine(p.Comment)
                }).ToList();

                filters.Add(new Dictionary<string, object>
                {
                    ["id"] = f.ID,
                    ["name"] = f.Name,
                    ["where"] = OneLine(f.WhereSQL),
                    ["parameters"] = ps
                });
            }

            catalogues.Add(new Dictionary<string, object>
            {
                ["id"] = c.ID,
                ["name"] = c.Name,
                ["identifier_columns"] = eis.Where(e => e.IsExtractionIdentifier)
                    .Select(e => e.GetRuntimeName()).ToList(),
                ["columns"] = eis.Select(e => e.GetRuntimeName()).ToList(),
                ["filters"] = filters
            });
        }

        var yaml = new SerializerBuilder().Build()
            .Serialize(new Dictionary<string, object> { ["catalogues"] = catalogues });

        _toFile.Directory?.Create();
        File.WriteAllText(_toFile.FullName, yaml);

        BasicActivator.Show($"Exported {_catalogues.Length} catalogue(s) to {_toFile.FullName}");
    }

    private static string OneLine(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
}
