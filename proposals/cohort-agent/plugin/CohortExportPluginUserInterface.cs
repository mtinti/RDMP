// Registers the export command on the right-click menu of a CohortIdentificationConfiguration.
// RDMP auto-discovers PluginUserInterface subclasses in loaded plugin assemblies.

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
            yield return new ExecuteCommandExportCohortAsScript(BasicActivator, cic);

        if (o is Catalogue cata)
            yield return new ExecuteCommandExportCatalogueManifest(BasicActivator, cata);
    }
}
