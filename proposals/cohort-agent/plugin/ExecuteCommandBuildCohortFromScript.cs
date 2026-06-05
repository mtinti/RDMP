// Rebuilds a cohort from a build.script.yaml produced by ExecuteCommandExportCohortAsScript.
// Replays each command in-process, binding the $c/$a/$p handles to the real ids RDMP assigns
// as each object is created (via NewObjectPool), deletes the auto Inclusion/Exclusion
// containers, and restores child Order (each Add inserts at the top, which would reverse it).
//
//   rdmp cmd BuildCohortFromScript .\out\MyCohort\build.script.yaml "MyCohort (rebuilt)"
//
// This is the inverse of ExportCohortAsScript. Verified to reproduce identical membership.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Rdmp.Core.CommandLine.Interactive.Picking;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.DataExport.Data;
using Rdmp.Core.MapsDirectlyToDatabaseTable;
using Rdmp.Core.Repositories;
using YamlDotNet.Serialization;

namespace Rdmp.Core.CommandExecution.AtomicCommands;

public class ExecuteCommandBuildCohortFromScript : BasicCommandExecution
{
    private readonly FileInfo _scriptFile;
    private readonly string _newName;
    private readonly Dictionary<string, int> _handles = new(); // "$c25" -> real id
    private CohortIdentificationConfiguration _currentCic;

    public ExecuteCommandBuildCohortFromScript(IBasicActivateItems activator,
        [DemandsInitialization("The build.script.yaml produced by ExportCohortAsScript")]
        FileInfo scriptFile,
        [DemandsInitialization("Name for the rebuilt cohort (must be unique)")]
        string newCohortName = null) : base(activator)
    {
        _scriptFile = scriptFile;
        _newName = newCohortName;
        if (scriptFile != null && !scriptFile.Exists)
            SetImpossible("Script file does not exist"); // null is allowed - the GUI prompts
    }

    private class ScriptDto { public string[] Commands { get; set; } }

    public override void Execute()
    {
        base.Execute();

        // From the GUI the file/name may be unset - prompt for them.
        var file = _scriptFile ?? BasicActivator.SelectFile("Select build.script.yaml to rebuild", "Cohort script", "*.yaml");
        if (file == null) return;
        var newName = _newName;
        if (string.IsNullOrWhiteSpace(newName) &&
            (!BasicActivator.TypeText("Rebuild Cohort", "Name for the rebuilt cohort", 200, null, out newName, false)
             || string.IsNullOrWhiteSpace(newName)))
            return;

        var script = new Deserializer().Deserialize<ScriptDto>(File.ReadAllText(file.FullName));
        if (script?.Commands == null || script.Commands.Length == 0)
            throw new Exception("Script contained no Commands");

        var repo = BasicActivator.RepositoryLocator.CatalogueRepository;
        var invoker = new CommandInvoker(BasicActivator);
        var byName = new Dictionary<string, Type>(StringComparer.InvariantCultureIgnoreCase);
        foreach (var t in invoker.GetSupportedCommands())
            byName.TryAdd(BasicCommandExecution.GetCommandName(t.Name), t);

        // (parentContainerId, isAggregate, childId) in the order children were added
        var adds = new List<(int parent, bool isAgg, int child)>();

        using (NewObjectPool.StartSession())
        {
            foreach (var raw in script.Commands)
            {
                var line = raw;
                string binds = null;
                var i = line.IndexOf(" => ", StringComparison.Ordinal);
                if (i >= 0) { binds = line[(i + 4)..].Trim(); line = line[..i]; }

                if (line.StartsWith("CreateNewCohortIdentificationConfiguration", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(newName))
                    line = $"CreateNewCohortIdentificationConfiguration \"{newName}\"";

                line = Substitute(line);

                // runner directive: associate the new CIC with a Project (so project-specific
                // catalogues can be added). Handled directly, not via a command.
                if (line.StartsWith("AssociateWithProject", StringComparison.OrdinalIgnoreCase))
                {
                    var pid = int.Parse(line[(line.IndexOf("Project:", StringComparison.Ordinal) + 8)..].Trim());
                    var dx = BasicActivator.RepositoryLocator.DataExportRepository;
                    _ = new ProjectCohortIdentificationConfigurationAssociation(dx, dx.GetObjectByID<Project>(pid), _currentCic);
                    continue;
                }

                var tokens = Tokenize(line);
                if (!byName.TryGetValue(tokens[0], out var type))
                    throw new Exception($"Unknown command '{tokens[0]}'");

                invoker.ExecuteCommand(type, new CommandLineObjectPicker(tokens.Skip(1).ToArray(), BasicActivator));

                BindAndTrack(tokens[0], line, binds, repo, adds);
            }

            RestoreOrder(repo, adds);
        }

        var cic = repo.GetAllObjectsWhere<CohortIdentificationConfiguration>("Name", newName).FirstOrDefault();
        BasicActivator.Show($"Rebuilt cohort '{newName}'" + (cic != null ? $" (ID {cic.ID})" : ""));
        if (cic != null) Publish(cic);
    }

    private void BindAndTrack(string cmd, string line, string binds, ICatalogueRepository repo,
        List<(int, bool, int)> adds)
    {
        switch (cmd)
        {
            case "CreateNewCohortIdentificationConfiguration":
                var cic = (CohortIdentificationConfiguration)NewObjectPool.Latest(
                    repo.GetAllObjects<CohortIdentificationConfiguration>());
                _currentCic = cic;
                var root = cic.RootCohortAggregateContainer;
                if (binds != null && root != null) _handles[binds] = root.ID;
                // remove the auto Inclusion/Exclusion containers (the script recreates what it needs)
                foreach (var sub in root.GetSubContainers())
                    if (sub.Name is "Inclusion Criteria" or "Exclusion Criteria")
                        sub.DeleteInDatabase();
                break;

            case "AddCohortSubContainer":
                var newC = (CohortAggregateContainer)NewObjectPool.Latest(
                    repo.GetAllObjects<CohortAggregateContainer>());
                if (binds != null) _handles[binds] = newC.ID;
                adds.Add((ParentContainerId(line), false, newC.ID));
                break;

            case "AddCatalogueToCohortIdentificationSetContainer":
                var newA = (AggregateConfiguration)NewObjectPool.Latest(
                    repo.GetAllObjects<AggregateConfiguration>());
                if (binds != null) _handles[binds] = newA.ID;
                adds.Add((ParentContainerId(line), true, newA.ID));
                break;

            case "CreateNewFilter":
                var filter = (AggregateFilter)NewObjectPool.Latest(repo.GetAllObjects<AggregateFilter>());
                var ps = filter.GetAllParameters().OfType<AggregateFilterParameter>().ToArray();
                var handles = (binds ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (var k = 0; k < handles.Length && k < ps.Length; k++)
                    _handles[handles[k]] = ps[k].ID;
                break;
        }
    }

    private static void RestoreOrder(ICatalogueRepository repo, List<(int parent, bool isAgg, int child)> adds)
    {
        foreach (var grp in adds.GroupBy(a => a.parent))
        {
            var n = 0;
            foreach (var (_, isAgg, child) in grp)
            {
                if (isAgg)
                    repo.CohortContainerManager.SetOrder(repo.GetObjectByID<AggregateConfiguration>(child), n);
                else
                {
                    var c = repo.GetObjectByID<CohortAggregateContainer>(child);
                    c.Order = n;
                    c.SaveToDatabase();
                }
                n++;
            }
        }
    }

    private string Substitute(string line)
    {
        foreach (var kv in _handles.OrderByDescending(k => k.Key.Length))
            line = line.Replace(kv.Key, kv.Value.ToString());
        return line;
    }

    private static int ParentContainerId(string line)
    {
        // first CohortAggregateContainer:<id> in the (already-substituted) line
        const string key = "CohortAggregateContainer:";
        var at = line.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return -1;
        var s = at + key.Length;
        var e = s;
        while (e < line.Length && char.IsDigit(line[e])) e++;
        return int.TryParse(line[s..e], out var id) ? id : -1;
    }

    // split on spaces, honouring double quotes anywhere in a token; quote chars are dropped
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;
        var has = false;
        foreach (var ch in line)
        {
            if (ch == '"') { inQuote = !inQuote; has = true; }
            else if (ch == ' ' && !inQuote)
            {
                if (has) { tokens.Add(sb.ToString()); sb.Clear(); has = false; }
            }
            else { sb.Append(ch); has = true; }
        }
        if (has) tokens.Add(sb.ToString());
        return tokens;
    }
}
