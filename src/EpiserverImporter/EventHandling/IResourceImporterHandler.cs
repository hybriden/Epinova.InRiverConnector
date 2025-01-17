﻿using System.Collections.Generic;
using Epinova.InRiverConnector.Interfaces;

namespace Epinova.InRiverConnector.EpiserverImporter.EventHandling
{
    public interface IResourceImporterHandler
    {
        /// <summary>
        /// Called after the Resource has been imported into Commerce
        /// </summary>
        /// <param name="resources"></param>
        void PostImport(List<InRiverImportResource> resources);

        /// <summary>
        /// Called before the Resources is imported into Commerce
        /// </summary>
        /// <remarks>If any implementation throws an exception, the resources will not be imported</remarks>
        /// <param name="resources"></param>
        void PreImport(List<InRiverImportResource> resources);
    }
}