// Surfaces the three commands in the RDMP desktop GUI (right-click menus). The same command
// classes are auto-discovered for the CLI (`rdmp cmd <Name>`), so one definition serves both.

using System.Collections.Generic;
using Rdmp.Core;
using Rdmp.Core.CommandExecution;
using Rdmp.Core.CommandExecution.AtomicCommands;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.Cohort;

namespace RdmpCohortExport;

public class CohortExportPluginUserInterface : PluginUserInterface
{
    public CohortExportPluginUserInterface(IBasicActivateItems itemActivator) : base(itemActivator)
    {
    }

    public override IEnumerable<IAtomicCommand> GetAdditionalRightClickMenuItems(object o)
    {
        if (o is CohortIdentificationConfiguration cic)
        {
            yield return new ExecuteCommandExportCohortAsScript(BasicActivator, cic);
            // file + name unset -> the command prompts for them in the GUI
            yield return new ExecuteCommandBuildCohortFromScript(BasicActivator, null);
        }

        if (o is Catalogue cata)
            yield return new ExecuteCommandExportCatalogueManifest(BasicActivator, cata);
    }
}
