// Copyright (c) The University of Dundee 2024-2024
// This file is part of the Research Data Management Platform (RDMP).
// RDMP is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
// RDMP is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
// You should have received a copy of the GNU General Public License along with RDMP. If not, see <https://www.gnu.org/licenses/>.

using System.IO;
using System.Linq;
using System.Threading;
using Rdmp.Core.CohortCreation;
using Rdmp.Core.CohortCreation.Execution;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Cohort;

namespace Rdmp.Core.CommandExecution.AtomicCommands;

/// <summary>
/// Runs a cohort identification configuration and writes the per-set and per-container row counts
/// (including the cumulative totals shown in the Cohort Builder) to a CSV file. Each row records the
/// container the count was computed from. See <see cref="CohortCountReport"/>.
/// </summary>
public class ExecuteCommandExportCohortCounts : BasicCommandExecution
{
    private readonly CohortIdentificationConfiguration _cic;
    private readonly int _timeout;
    private readonly bool _includeCumulativeTotals;
    private FileInfo _toFile;

    public ExecuteCommandExportCohortCounts(IBasicActivateItems activator,
        [DemandsInitialization("The cohort identification configuration to run and report counts for")]
        CohortIdentificationConfiguration cic,
        [DemandsInitialization("CSV file to write. Defaults to <cic name>-counts.csv in the current directory")]
        FileInfo toFile = null,
        [DemandsInitialization("Per-query command timeout in seconds", DefaultValue = 5000)]
        int timeout = 5000,
        [DemandsInitialization("Whether to compute the cumulative running totals per container", DefaultValue = true)]
        bool includeCumulativeTotals = true) : base(activator)
    {
        _cic = cic;
        _toFile = toFile;
        _timeout = timeout;
        _includeCumulativeTotals = includeCumulativeTotals;

        if (_cic == null)
            SetImpossible("No CohortIdentificationConfiguration was supplied");
        else if (_cic.RootCohortAggregateContainer_ID == null)
            SetImpossible($"'{_cic}' has no root container to run");
    }

    public override void Execute()
    {
        base.Execute();

        _toFile ??= BasicActivator.IsInteractive
            ? BasicActivator.SelectFile("Path to write cohort counts to", "Cohort counts", "*.csv")
            : new FileInfo(Path.Combine(System.Environment.CurrentDirectory, $"{Sanitise(_cic.Name)}-counts.csv"));

        if (_toFile == null)
            return;

        var compiler = new CohortCompiler(BasicActivator, _cic)
        {
            IncludeCumulativeTotals = _includeCumulativeTotals
        };

        var runner = new CohortCompilerRunner(compiler, _timeout) { RunSubcontainers = true };
        runner.Run(new CancellationToken());

        CohortCountReport.WriteCsv(_toFile.FullName, compiler.Tasks.Keys);

        var crashed = compiler.Tasks.Keys.Count(t => t.State == CompilationState.Crashed);
        var summary = crashed > 0
            ? $"Exported counts to {_toFile.FullName} ({crashed} task(s) crashed - see the State/CrashMessage columns)"
            : $"Exported counts to {_toFile.FullName}";

        BasicActivator.Show(summary);
    }

    private static string Sanitise(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
