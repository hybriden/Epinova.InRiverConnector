﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Epinova.InRiverConnector.EpiserverAdapter.Communication;
using Epinova.InRiverConnector.EpiserverAdapter.EpiXml;
using Epinova.InRiverConnector.EpiserverAdapter.Helpers;
using Epinova.InRiverConnector.EpiserverAdapter.Utilities;
using Epinova.InRiverConnector.Interfaces.Enums;
using inRiver.Integration.Logging;
using inRiver.Remoting;
using inRiver.Remoting.Connect;
using inRiver.Remoting.Log;
using inRiver.Remoting.Objects;

namespace Epinova.InRiverConnector.EpiserverAdapter
{
    public class ChannelPublisher
    {
        private readonly IConfiguration _config;
        private readonly EpiDocumentFactory _epiDocumentFactory;
        private readonly EpiElementFactory _epiElementFactory;
        private readonly ResourceElementFactory _resourceElementFactory;
        private readonly EpiApi _epiApi;
        private readonly EpiMappingHelper _mappingHelper;
        private readonly DeleteUtility _deleteUtility;
        private readonly DocumentFileHelper _documentFileHelper;
        private readonly PimFieldAdapter _pimFieldAdapter;
        private readonly IEntityService _entityService;

        public ChannelPublisher(IConfiguration config, 
                                EpiDocumentFactory epiDocumentFactory, 
                                EpiElementFactory epiElementFactory,
                                ResourceElementFactory resourceElementFactory,
                                EpiApi epiApi,
                                EpiMappingHelper mappingHelper,
                                DeleteUtility deleteUtility,
                                DocumentFileHelper documentFileHelper,
                                PimFieldAdapter pimFieldAdapter,
                                IEntityService entityService)
        {
            _config = config;
            _epiDocumentFactory = epiDocumentFactory;
            _epiElementFactory = epiElementFactory;
            _resourceElementFactory = resourceElementFactory;
            _epiApi = epiApi;
            _mappingHelper = mappingHelper;
            _deleteUtility = deleteUtility;
            _documentFileHelper = documentFileHelper;
            _pimFieldAdapter = pimFieldAdapter;
            _entityService = entityService;
        }

        public ConnectorEvent Publish(Entity channel)
        {
            var publishEvent = ConnectorEventHelper.InitiateEvent(_config, ConnectorEventType.Publish, $"Publish started for channel: {channel.DisplayName.Data}", 0);
            ConnectorEventHelper.UpdateEvent(publishEvent, "Fetching all channel entities...", 1);

            var channelStructureEntities = _entityService.GetAllStructureEntitiesInChannel(_config.ExportEnabledEntityTypes);
            
            ConnectorEventHelper.UpdateEvent(publishEvent, "Fetched all channel entities. Generating catalog.xml...", 10);

            var epiElements = _epiDocumentFactory.GetEPiElements(channelStructureEntities);
            var metaClasses = _epiElementFactory.GetMetaClassesFromFieldSets();
            var associationTypes = _epiDocumentFactory.GetAssociationTypes();

            var catalogDocument = _epiDocumentFactory.CreateImportDocument(channel, metaClasses, associationTypes, epiElements);

            LogCatalogProperties(epiElements);

            var resourceEntities = RemoteManager.ChannelService.GetAllChannelStructureEntitiesForTypeFromPath(channel.Id.ToString(), "Resource");

            PubilshToEpiserver(publishEvent, catalogDocument, resourceEntities, channel);
            
            return publishEvent;
        }

        internal void PublishEntities(Entity channel, ConnectorEvent connectorEvent, List<StructureEntity> structureEntities)
        {
            ConnectorEventHelper.UpdateEvent(connectorEvent, "Generating catalog.xml...", 11);
            var epiElements = _epiDocumentFactory.GetEPiElements(structureEntities);
            
            var catalogDocument = _epiDocumentFactory.CreateImportDocument(channel, null, null, epiElements);

            LogCatalogProperties(epiElements);

            PubilshToEpiserver(connectorEvent, catalogDocument, structureEntities, channel);
        }

        private void PubilshToEpiserver(ConnectorEvent connectorEvent, 
                                        XDocument catalogDocument, 
                                        List<StructureEntity> structureEntitiesToGetResourcesFor,
                                        Entity channelEntity)
        {
            ConnectorEventHelper.UpdateEvent(connectorEvent, "Done generating catalog.xml. Generating Resource.xml and saving files to disk...", 26);

            var folderNameTimestampComponent = DateTime.Now.ToString(Constants.PublicationFolderNameTimeComponent);

            var resourcesBasePath = Path.Combine(_config.ResourcesRootPath, folderNameTimestampComponent);
            var resourceDocument = _resourceElementFactory.GetResourcesNodeForChannelEntities(structureEntitiesToGetResourcesFor, resourcesBasePath);
            var resourceDocumentPath = _documentFileHelper.SaveDocument(resourceDocument, resourcesBasePath);
            
            var savedCatalogDocument = _documentFileHelper.SaveCatalogDocument(channelEntity, catalogDocument, folderNameTimestampComponent);

            ConnectorEventHelper.UpdateEvent(connectorEvent, "Done generating/saving Resource.xml, sending Catalog.xml to EPiServer...", 50);

            _epiApi.ImportCatalog(savedCatalogDocument);
            
            ConnectorEventHelper.UpdateEvent(connectorEvent, "Done sending Catalog.xml to EPiServer", 75);

            _epiApi.NotifyEpiserverPostImport(savedCatalogDocument);

            ConnectorEventHelper.UpdateEvent(connectorEvent, "Sending Resources to EPiServer...", 76);

            _epiApi.ImportResources(resourceDocumentPath, resourcesBasePath);

            ConnectorEventHelper.UpdateEvent(connectorEvent, "Done sending Resources to EPiServer...", 99);

            _epiApi.NotifyEpiserverPostImport(resourceDocumentPath);
            var channelName = _mappingHelper.GetNameForEntity(channelEntity, 100);

            _epiApi.ImportUpdateCompleted(channelName, ImportUpdateCompletedEventType.Publish, true);
        }

        private static void LogCatalogProperties(CatalogElementContainer epiElements)
        {
            IntegrationLogger.Write(LogLevel.Information, $"Catalog saved with the following: " +
                                                          $"Nodes: {epiElements.Nodes.Count}. " +
                                                          $"Entries: {epiElements.Entries.Count}. " +
                                                          $"Relations: {epiElements.Relations.Count}. " +
                                                          $"Associations: {epiElements.Associations.Count}. ");
        }

        public ConnectorEvent ChannelEntityAdded(Entity channel, int entityId)
        {
            var connectorEvent = ConnectorEventHelper.InitiateEvent(_config, ConnectorEventType.ChannelEntityAdded, $"Received entity added for entity {entityId} in channel {channel.DisplayName}", 0);

            var structureEntities = new List<StructureEntity>();
            var addedStructureEntities = _entityService.GetStructureEntitiesForEntityInChannel(_config.ChannelId, entityId);

            foreach (var addedEntity in addedStructureEntities)
            {
                var parentEntity = _entityService.GetParentStructureEntity(_config.ChannelId, addedEntity.ParentId, addedEntity.EntityId, addedStructureEntities);
                structureEntities.Add(parentEntity);
            }

            structureEntities.AddRange(addedStructureEntities);

            var targetEntityPath = _entityService.GetTargetEntityPath(entityId, addedStructureEntities);
            var childLinks = _entityService.GetChildrenEntitiesInChannel(entityId, targetEntityPath);
            
            foreach (var linkStructureEntity in childLinks)
            {
                var childLinkedEntities = _entityService.GetChildrenEntitiesInChannel(linkStructureEntity.EntityId, linkStructureEntity.Path);
                structureEntities.AddRange(childLinkedEntities);
            }

            structureEntities.AddRange(childLinks);

            PublishEntities(channel, connectorEvent, structureEntities);
          
            string channelName = _mappingHelper.GetNameForEntity(channel, 100);
            _epiApi.ImportUpdateCompleted(channelName, ImportUpdateCompletedEventType.EntityAdded, true);
            return connectorEvent;
        }

        public ConnectorEvent ChannelEntityUpdated(Entity channel, int entityId, string data)
        {
            var connectorEvent = ConnectorEventHelper.InitiateEvent(_config, ConnectorEventType.ChannelEntityUpdated, $"Received entity update for entity {entityId} in channel {channel.DisplayName}", 0);

            var updatedEntity = RemoteManager.DataService.GetEntity(entityId, LoadLevel.DataAndLinks);

            if (updatedEntity.EntityType.IsLinkEntityType)
                return connectorEvent;

            string folderDateTime = DateTime.Now.ToString(Constants.PublicationFolderNameTimeComponent);

            bool resourceIncluded = false;
            
            var structureEntities = _entityService.GetStructureEntitiesForEntityInChannel(_config.ChannelId, entityId);

            if (updatedEntity.EntityType.Id.Equals("Resource"))
            {
                resourceIncluded = HandleResourceUpdate(updatedEntity, folderDateTime);
            }
            else
            {
                IntegrationLogger.Write(LogLevel.Debug, $"Updated entity found. Type: {updatedEntity.EntityType.Id}, id: {updatedEntity.Id}");

                if (updatedEntity.EntityType.Id.Equals("Item") && data != null && data.Split(',').Contains("SKUs"))
                {
                    HandleSkuUpdate(entityId, channel, connectorEvent, structureEntities, out resourceIncluded);
                }
                else if (updatedEntity.EntityType.Id.Equals("ChannelNode"))
                {
                    HandleChannelNodeUpdate(channel, structureEntities, connectorEvent);
                    return connectorEvent;
                }

                XDocument doc = _epiDocumentFactory.CreateUpdateDocument(channel, updatedEntity);
               
                string catalogDocumentName = _documentFileHelper.SaveCatalogDocument(channel, doc, folderDateTime);

                IntegrationLogger.Write(LogLevel.Debug, "Starting automatic import!");

                _epiApi.ImportCatalog(catalogDocumentName);
                _epiApi.NotifyEpiserverPostImport(catalogDocumentName);
            }

            string channelName = _mappingHelper.GetNameForEntity(channel, 100);
            _epiApi.ImportUpdateCompleted(channelName, ImportUpdateCompletedEventType.EntityUpdated, resourceIncluded);
            return connectorEvent;
        }

        public ConnectorEvent ChannelEntityDeleted(Entity channel, Entity deletedEntity)
        {
            var channelName = _mappingHelper.GetNameForEntity(channel, 100);
            var connectorEvent = ConnectorEventHelper.InitiateEvent(_config, 
                                            ConnectorEventType.ChannelEntityDeleted, 
                                            $"Received entity deleted for entity {deletedEntity.Id} in channel {channelName}.", 0);

            _deleteUtility.Delete(channel, deletedEntity);
            
            _epiApi.DeleteCompleted(channelName, DeleteCompletedEventType.EntitiyDeleted);

            return connectorEvent;
        }

        public ConnectorEvent ChannelLinkAdded(Entity channel, int sourceEntityId, int targetEntityId, string linkTypeId, int? linkEntityId)
        {
            var connectorEvent = ConnectorEventHelper.InitiateEvent(_config, ConnectorEventType.ChannelLinkAdded,
                    $"Received link added for sourceEntityId {sourceEntityId} and targetEntityId {targetEntityId} in channel {channel.DisplayName}", 0);

            ConnectorEventHelper.UpdateEvent(connectorEvent, "Fetching channel entities...", 1);

            var existingEntitiesInChannel = _entityService.GetStructureEntitiesForEntityInChannel(_config.ChannelId, targetEntityId);

            List<StructureEntity> structureEntities = new List<StructureEntity>();

            foreach (StructureEntity existingEntity in existingEntitiesInChannel)
            {
                List<string> parentIds = existingEntity.Path.Split('/').ToList();
                parentIds.Reverse();
                parentIds.RemoveAt(0);

                for (int i = 0; i < parentIds.Count - 1; i++)
                {
                    int entityId = int.Parse(parentIds[i]);
                    int parentId = int.Parse(parentIds[i + 1]);

                    structureEntities.AddRange(RemoteManager.ChannelService.GetAllStructureEntitiesForEntityWithParentInChannel(channel.Id, entityId, parentId));
                }
            }

            foreach (StructureEntity existingEntity in existingEntitiesInChannel)
            {
                string targetEntityPath = _entityService.GetTargetEntityPath(existingEntity.EntityId, existingEntitiesInChannel, existingEntity.ParentId);
                structureEntities.AddRange(RemoteManager.ChannelService.GetAllChannelStructureEntitiesFromPath(targetEntityPath));
            }

            // Remove duplicates
            structureEntities = structureEntities.GroupBy(x => x.EntityId).Select(x => x.First()).ToList();

            //Adding existing Entities. If it occurs more than one time in channel. We can not remove duplicates.
            structureEntities.AddRange(existingEntitiesInChannel);

            ConnectorEventHelper.UpdateEvent(connectorEvent, "Done fetching channel entities", 10);

            PublishEntities(channel, connectorEvent, structureEntities);

            string channelName = _mappingHelper.GetNameForEntity(channel, 100);
            _epiApi.ImportUpdateCompleted(channelName, ImportUpdateCompletedEventType.LinkAdded, true);

            return connectorEvent;
        }

        public ConnectorEvent ChannelLinkDeleted(Entity channel, int sourceEntityId, int targetEntityId, string linkTypeId, int? linkEntityId)
        {
            var connectorEvent = ConnectorEventHelper.InitiateEvent(_config, ConnectorEventType.ChannelLinkDeleted,
                $"Received link deleted for sourceEntityId {sourceEntityId} and targetEntityId {targetEntityId} in channel {channel.DisplayName.Data}", 0);

            Entity removalTarget = RemoteManager.DataService.GetEntity(targetEntityId, LoadLevel.DataAndLinks);
            Entity removalSource = RemoteManager.DataService.GetEntity(sourceEntityId, LoadLevel.DataAndLinks);

            if (removalTarget.EntityType.Id == "Resource")
            {
                _deleteUtility.DeleteResourceLink(removalTarget, removalSource);
            }
            else
            {
                
                _deleteUtility.DeleteLink(removalSource, removalTarget, linkTypeId);
            }

            string channelName = _mappingHelper.GetNameForEntity(channel, 100);
            _epiApi.DeleteCompleted(channelName, DeleteCompletedEventType.LinkDeleted);

            return connectorEvent;
        }

        public ConnectorEvent ChannelLinkUpdated(Entity channel, int sourceEntityId, int targetEntityId, string linkTypeId, int? linkEntityId)
        {
            var connectorEvent = ConnectorEventHelper.InitiateEvent(_config, ConnectorEventType.ChannelLinkAdded,
                $"Received link update for sourceEntityId {sourceEntityId} and targetEntityId {targetEntityId} in channel {channel.DisplayName}.", 0);

            ConnectorEventHelper.UpdateEvent(connectorEvent, "Fetching channel entities...", 1);

            var targetEntityStructure = _entityService.GetEntityInChannelWithParent(_config.ChannelId, targetEntityId, sourceEntityId);
            var parentStructureEntity = _entityService.GetParentStructureEntity(_config.ChannelId, sourceEntityId, targetEntityId, targetEntityStructure);

            if (parentStructureEntity == null)
                throw new Exception($"Can't find parent structure entity {sourceEntityId} with target entity id {targetEntityId}");

            var structureEntities = new List<StructureEntity>
            {
                parentStructureEntity
            };

            var entities = _entityService.GetChildrenEntitiesInChannel(parentStructureEntity.EntityId, parentStructureEntity.Path);
            structureEntities.AddRange(entities);

            ConnectorEventHelper.UpdateEvent(connectorEvent, "Done fetching channel entities", 10);

            PublishEntities(channel, connectorEvent, structureEntities);

            var channelName = _mappingHelper.GetNameForEntity(channel, 100);
            _epiApi.ImportUpdateCompleted(channelName, ImportUpdateCompletedEventType.LinkUpdated, true);

            return connectorEvent;
        }


        private void HandleSkuUpdate(int entityId, 
                                      Entity channelEntity,
                                      ConnectorEvent connectorEvent, 
                                      List<StructureEntity> structureEntities, 
                                      out bool resourceIncluded)
        {
            resourceIncluded = false;
            Field currentField = RemoteManager.DataService.GetField(entityId, "SKUs");

            List<Field> fieldHistory = RemoteManager.DataService.GetFieldHistory(entityId, "SKUs");

            Field previousField = fieldHistory.FirstOrDefault(f => f.Revision == currentField.Revision - 1);

            string oldXml = string.Empty;
            if (previousField != null && previousField.Data != null)
            {
                oldXml = (string)previousField.Data;
            }

            string newXml = string.Empty;
            if (currentField.Data != null)
            {
                newXml = (string)currentField.Data;
            }

            List<XElement> skusToDelete, skusToAdd;
            PimFieldAdapter.CompareAndParseSkuXmls(oldXml, newXml, out skusToAdd, out skusToDelete);

            foreach (XElement skuToDelete in skusToDelete)
            {
                var skuId = skuToDelete.Attribute("id").Value;
                _epiApi.DeleteSku(skuId);
            }

            if (skusToAdd.Count > 0)
            {
                PublishEntities(channelEntity, connectorEvent, structureEntities);
                resourceIncluded = true;
            }
        }

        private void HandleChannelNodeUpdate(Entity channel, List<StructureEntity> structureEntities, ConnectorEvent entityUpdatedConnectorEvent)
        {
            PublishEntities(channel, entityUpdatedConnectorEvent, structureEntities);
            _epiApi.ImportUpdateCompleted(_pimFieldAdapter.GetDisplayName(channel, 100), ImportUpdateCompletedEventType.EntityUpdated, true);
        }

        private bool HandleResourceUpdate(Entity updatedEntity, string folderDateTime)
        {
            var resourceIncluded = false;
            var resourceDocument = _resourceElementFactory.HandleResourceUpdate(updatedEntity, folderDateTime);
            _documentFileHelper.SaveDocument( resourceDocument, folderDateTime);
            
            IntegrationLogger.Write(LogLevel.Debug, "Resources saved, Starting automatic resource import!");

            var baseFilePath = Path.Combine(_config.ResourcesRootPath, folderDateTime);
            var resourceXmlPath = Path.Combine(baseFilePath, "Resources.xml");

            _epiApi.ImportResources(resourceXmlPath, baseFilePath);

            _epiApi.NotifyEpiserverPostImport(resourceXmlPath);
            resourceIncluded = true;

            return resourceIncluded;
        }
    }
}