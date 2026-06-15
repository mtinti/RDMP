// Copyright (c) The University of Dundee 2024-2024
// This file is part of the Research Data Management Platform (RDMP).
// RDMP is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
// RDMP is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
// You should have received a copy of the GNU General Public License along with RDMP. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using FAnsi;
using FAnsi.Discovery;
using NUnit.Framework;
using Rdmp.Core.CohortCreation;
using Rdmp.Core.CohortCreation.Execution;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Aggregation;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.MapsDirectlyToDatabaseTable;
using Rdmp.Core.ReusableLibraryCode.DataAccess;
using Tests.Common;

namespace Rdmp.Core.Tests.CohortCreation;

/// <summary>
/// Unit tests for the report formatting (no database required) plus one database-backed end-to-end
/// test that runs a real cohort and checks the exported counts.
/// </summary>
public class CohortCountReportTests
{
    [Test]
    public void ToCsv_HeaderColumnsAndOrdering()
    {
        var records = new[]
        {
            new CohortCountReport.CohortCountRecord
            {
                Order = 1, Type = "Cohort Set", Name = "Diabetics", ChildId = 7,
                Container = "Root", SetOperation = "", State = "Finished",
                FinalCount = 200, CumulativeCount = 150, ElapsedSeconds = 1.25, IsEnabled = true
            },
            new CohortCountReport.CohortCountRecord
            {
                Order = 0, Type = "Container", Name = "Root", ChildId = 3,
                Container = "", SetOperation = "INTERSECT", State = "Finished",
                FinalCount = 150, CumulativeCount = null, ElapsedSeconds = 2, IsEnabled = true
            }
        };

        var csv = CohortCountReport.ToCsv(records);
        var lines = csv.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        Assert.Multiple(() =>
        {
            // header present and in declared order
            Assert.That(lines[0], Is.EqualTo(
                "Order,Type,Name,ChildId,Container,SetOperation,State,FinalCount,CumulativeCount,ElapsedSeconds,IsEnabled,CrashMessage"));

            // the input is unsorted; ToCsv preserves the order given (Extract is what sorts) -
            // so row order here matches the array we passed in
            Assert.That(lines[1], Does.StartWith("1,Cohort Set,Diabetics,7,Root,,Finished,200,150,1.25,True,"));

            // null cumulative renders as an empty field, not "0"
            Assert.That(lines[2], Does.StartWith("0,Container,Root,3,,INTERSECT,Finished,150,,2,True,"));
        });
    }

    [Test]
    public void ToCsv_EscapesCommasQuotesAndNewlines()
    {
        var records = new[]
        {
            new CohortCountReport.CohortCountRecord
            {
                Order = 0, Type = "Cohort Set", Name = "Tayside, Fife", ChildId = 1,
                Container = "", SetOperation = "", State = "Crashed",
                CrashMessage = "boom \"quoted\"\nsecond line"
            }
        };

        var csv = CohortCountReport.ToCsv(records);

        Assert.Multiple(() =>
        {
            // name with a comma is wrapped in quotes
            Assert.That(csv, Does.Contain("\"Tayside, Fife\""));
            // embedded quotes are doubled and the whole field quoted
            Assert.That(csv, Does.Contain("\"boom \"\"quoted\"\"\nsecond line\""));
        });
    }

    [Test]
    public void Extract_OrdersByOrderThenName_AndHandlesNullChild()
    {
        var tasks = new ICompileable[]
        {
            new FakeCompileable { Order = 2, State = CompilationState.Finished, FinalRowCount = 10 },
            new FakeCompileable { Order = 1, State = CompilationState.NotScheduled, FinalRowCount = 99 }
        };

        var records = CohortCountReport.Extract(tasks);

        Assert.Multiple(() =>
        {
            Assert.That(records, Has.Count.EqualTo(2));
            Assert.That(records[0].Order, Is.EqualTo(1));

            // FinalCount is only emitted for Finished tasks; the NotScheduled one is null despite FinalRowCount=99
            Assert.That(records[0].FinalCount, Is.Null);
            Assert.That(records[1].FinalCount, Is.EqualTo(10));

            // null Child falls back to ToString() and id 0, container empty
            Assert.That(records[0].Name, Is.EqualTo(nameof(FakeCompileable)));
            Assert.That(records[0].ChildId, Is.EqualTo(0));
            Assert.That(records[0].Container, Is.Empty);
        });
    }

    /// <summary>Minimal in-memory <see cref="ICompileable"/> for testing the report without a build.</summary>
    private sealed class FakeCompileable : ICompileable
    {
        public int Order { get; set; }
        public IMapsDirectlyToDatabaseTable Child => null;
        public int Timeout { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public CancellationTokenSource CancellationTokenSource { get; set; }
        public CompilationState State { get; set; }
#pragma warning disable CS0067 // required by the interface but not raised by this fake
        public event EventHandler StateChanged;
#pragma warning restore CS0067
        public Exception CrashMessage { get; set; }
        public string Log { get; set; }
        public int FinalRowCount { get; set; }
        public int? CumulativeRowCount { get; set; }
        public IDataAccessPoint[] GetDataAccessPoints() => Array.Empty<IDataAccessPoint>();
        public Stopwatch Stopwatch { get; set; }
        public TimeSpan? ElapsedTime => TimeSpan.FromSeconds(1);
        public bool IsEnabled() => true;
        public string GetCachedQueryUseCount() => "0/0";
        public void SetKnownContainer(CohortAggregateContainer parent, bool isFirstInContainer) { }
        public override string ToString() => nameof(FakeCompileable);
    }
}

/// <summary>
/// End-to-end: build a real cohort, run it, and confirm the exported CSV carries the container
/// name and the per-set counts. Requires the test SQL Server (see mac-test-env).
/// </summary>
public class CohortCountReportDatabaseTests : DatabaseTests
{
    [Test]
    public void Export_RealCohort_ContainsContainerAndCounts()
    {
        var dt = new DataTable();
        dt.Columns.Add("PK");
        for (var i = 0; i < 1000; i++)
            dt.Rows.Add(i);

        var db = GetCleanedServer(DatabaseType.MicrosoftSQLServer);
        DiscoveredTable tbl = db.CreateTable("CohortCountReportTestsTable", dt);

        var cata = Import(tbl);
        var ei = cata.CatalogueItems[0].ExtractionInformation;
        ei.IsExtractionIdentifier = true;
        ei.SaveToDatabase();

        var agg = new AggregateConfiguration(CatalogueRepository, cata, "MyAgg") { CountSQL = null };
        agg.SaveToDatabase();
        _ = new AggregateDimension(CatalogueRepository, ei, agg);

        var cic = new CohortIdentificationConfiguration(CatalogueRepository, "MyCic");
        cic.CreateRootContainerIfNotExists();
        cic.RootCohortAggregateContainer.AddChild(agg, 0);
        cic.EnsureNamingConvention(agg);

        var compiler = new CohortCompiler(new ThrowImmediatelyActivator(RepositoryLocator, null), cic)
        {
            IncludeCumulativeTotals = true
        };
        var runner = new CohortCompilerRunner(compiler, 5000) { RunSubcontainers = true };
        runner.Run(new CancellationToken());

        var records = CohortCountReport.Extract(compiler.Tasks.Keys);

        var container = records.SingleOrDefault(r => r.Type == "Container");
        Assert.That(container, Is.Not.Null, "Expected a container row in the report");

        Assert.Multiple(() =>
        {
            Assert.That(container.Name, Is.EqualTo(cic.RootCohortAggregateContainer.Name));
            Assert.That(container.FinalCount, Is.EqualTo(dt.Rows.Count));
            Assert.That(records.Any(r => r.Type == "Cohort Set"), Is.True);
        });

        // round-trip through file and confirm it is non-empty CSV with the header
        var file = Path.GetTempFileName();
        try
        {
            CohortCountReport.WriteCsv(file, compiler.Tasks.Keys);
            var written = File.ReadAllText(file);
            Assert.That(written, Does.StartWith("Order,Type,Name"));
            Assert.That(written, Does.Contain(cic.RootCohortAggregateContainer.Name));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
