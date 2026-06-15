// Copyright (c) The University of Dundee 2024-2024
// This file is part of the Research Data Management Platform (RDMP).
// RDMP is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
// RDMP is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
// You should have received a copy of the GNU General Public License along with RDMP. If not, see <https://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Rdmp.Core.CohortCreation.Execution;
using Rdmp.Core.CohortCreation.Execution.Joinables;

namespace Rdmp.Core.CohortCreation;

/// <summary>
/// Turns the results held by a <see cref="CohortCompiler"/> after a cohort build (the per-set and
/// per-container row counts shown in the Cohort Builder) into a flat, file-friendly report.
///
/// <para>The cumulative count of a row is the running total RDMP computed for that container as its
/// set operations (UNION/INTERSECT/EXCEPT) were applied, which is why each row records the container
/// it was computed within.</para>
/// </summary>
public static class CohortCountReport
{
    /// <summary>
    /// One row of the report: a single cohort set, patient index table or container and the counts
    /// RDMP computed for it during the build.
    /// </summary>
    public sealed class CohortCountRecord
    {
        public int Order { get; init; }

        /// <summary>"Container", "Cohort Set", "Patient Index Table" or the task type name.</summary>
        public string Type { get; init; }

        /// <summary>Name of the set / container the counts were computed from.</summary>
        public string Name { get; init; }

        public int ChildId { get; init; }

        /// <summary>Name of the container this row sits inside (empty for the root / for a top level container).</summary>
        public string Container { get; init; }

        /// <summary>UNION / INTERSECT / EXCEPT for container rows; empty otherwise.</summary>
        public string SetOperation { get; init; }

        public string State { get; init; }

        /// <summary>Row count for this set/container on its own. Only meaningful when <see cref="State"/> is Finished.</summary>
        public int? FinalCount { get; init; }

        /// <summary>Running cumulative total within the container; null if not computed (e.g. cumulative totals were off, or this is the first set in its container).</summary>
        public int? CumulativeCount { get; init; }

        public double? ElapsedSeconds { get; init; }

        public bool IsEnabled { get; init; }

        public string CrashMessage { get; init; }
    }

    private static readonly string[] Header =
    {
        "Order", "Type", "Name", "ChildId", "Container", "SetOperation", "State",
        "FinalCount", "CumulativeCount", "ElapsedSeconds", "IsEnabled", "CrashMessage"
    };

    /// <summary>
    /// Projects the compiler's tasks into report records, ordered as they appear in the builder.
    /// </summary>
    public static IReadOnlyList<CohortCountRecord> Extract(IEnumerable<ICompileable> tasks)
    {
        return tasks
            .Where(t => t != null)
            .Select(ToRecord)
            .OrderBy(r => r.Order)
            .ThenBy(r => r.Name, System.StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static CohortCountRecord ToRecord(ICompileable task)
    {
        // ParentContainerIfAny lives on the concrete Compileable base, not the interface.
        var parentContainer = (task as Compileable)?.ParentContainerIfAny;

        var (type, setOperation) = task switch
        {
            AggregationContainerTask c => ("Container", c.Container.Operation.ToString()),
            AggregationTask => ("Cohort Set", ""),
            JoinableTask => ("Patient Index Table", ""),
            _ => (task.GetType().Name, "")
        };

        return new CohortCountRecord
        {
            Order = task.Order,
            Type = type,
            Name = task.Child?.ToString() ?? task.ToString(),
            ChildId = task.Child?.ID ?? 0,
            Container = parentContainer?.Name ?? "",
            SetOperation = setOperation,
            State = task.State.ToString(),
            // FinalRowCount is only meaningful once the task has finished; leave blank otherwise.
            FinalCount = task.State == CompilationState.Finished ? task.FinalRowCount : null,
            CumulativeCount = task.CumulativeRowCount,
            ElapsedSeconds = task.ElapsedTime?.TotalSeconds,
            IsEnabled = task.IsEnabled(),
            CrashMessage = task.CrashMessage?.Message ?? ""
        };
    }

    /// <summary>
    /// Serialises records to CSV (with a header row).
    /// </summary>
    public static string ToCsv(IEnumerable<CohortCountRecord> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Header.Select(Escape)));

        foreach (var r in records)
            sb.AppendLine(string.Join(",", new[]
            {
                r.Order.ToString(CultureInfo.InvariantCulture),
                r.Type,
                r.Name,
                r.ChildId.ToString(CultureInfo.InvariantCulture),
                r.Container,
                r.SetOperation,
                r.State,
                r.FinalCount?.ToString(CultureInfo.InvariantCulture) ?? "",
                r.CumulativeCount?.ToString(CultureInfo.InvariantCulture) ?? "",
                r.ElapsedSeconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? "",
                r.IsEnabled.ToString(),
                r.CrashMessage
            }.Select(Escape)));

        return sb.ToString();
    }

    /// <summary>
    /// Convenience: extract the compiler tasks and write the CSV report to <paramref name="path"/>.
    /// </summary>
    public static void WriteCsv(string path, IEnumerable<ICompileable> tasks) =>
        File.WriteAllText(path, ToCsv(Extract(tasks)));

    private static string Escape(string field)
    {
        field ??= "";
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            return $"\"{field.Replace("\"", "\"\"")}\"";
        return field;
    }
}
