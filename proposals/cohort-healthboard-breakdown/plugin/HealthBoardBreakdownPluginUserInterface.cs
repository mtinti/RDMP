// Surfaces the health board breakdown commands in the RDMP desktop GUI (right-click menus). The
// same command classes are auto-discovered for the CLI (`rdmp cmd <Name>`), so one definition
// serves both. A committed cohort gets the ExtractableCohort breakdown; a cohort identification
// configuration gets the live-build-query breakdown.

using System.Collections.Generic;
using Rdmp.Core;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.CommandExecution.AtomicCommands;
using Rdmp.Core.Curation.Data.Cohort;
using Rdmp.Core.DataExport.Data;

namespace RdmpHealthBoardBreakdown;

public class HealthBoardBreakdownPluginUserInterface : PluginUserInterface
{
    public HealthBoardBreakdownPluginUserInterface(IBasicActivateItems itemActivator) : base(itemActivator)
    {
    }

    public override IEnumerable<IAtomicCommand> GetAdditionalRightClickMenuItems(object o)
    {
        // file + demography args unset -> the command prompts / uses defaults in the GUI
        if (o is ExtractableCohort cohort)
            yield return new ExecuteCommandExportCohortHealthBoardBreakdown(BasicActivator, cohort);

        if (o is CohortIdentificationConfiguration cic)
            yield return new ExecuteCommandExportCicHealthBoardBreakdown(BasicActivator, cic);
    }
}
