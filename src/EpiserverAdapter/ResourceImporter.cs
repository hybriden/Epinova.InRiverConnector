﻿using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;
using Epinova.InRiverConnector.EpiserverAdapter.Communication;
using Epinova.InRiverConnector.EpiserverAdapter.Poco;
using Epinova.InRiverConnector.Interfaces;
using inRiver.Integration.Logging;
using inRiver.Remoting.Log;

namespace Epinova.InRiverConnector.EpiserverAdapter
{
    public class ResourceImporter
    {
        private readonly IConfiguration _config;
        private readonly HttpClientInvoker _httpClient;

        public ResourceImporter(IConfiguration config, HttpClientInvoker httpClient)
        {
            _config = config;
            _httpClient = httpClient;
        }
        
        public bool ImportResources(string resourceXmlFilePath, string baseResourcePath)
        {
            IntegrationLogger.Write(LogLevel.Information, $"Starting Resource Import. Manifest: {resourceXmlFilePath} BaseResourcePath: {baseResourcePath}");
            var serializer = new XmlSerializer(typeof(Resources));
            Resources resources;
            using (var reader = XmlReader.Create(resourceXmlFilePath))
            {
                resources = (Resources)serializer.Deserialize(reader);
            }

            var resourcesForImport = new List<InRiverImportResource>();
            foreach (var resource in resources.ResourceFiles.Resource)
            {
                var newRes = new InRiverImportResource
                {
                    Action = resource.action
                };

                if (resource.ParentEntries != null && resource.ParentEntries.EntryCode != null)
                {
                    foreach (var entryCode in resource.ParentEntries.EntryCode)
                    {
                        if (string.IsNullOrEmpty(entryCode.Value))
                            continue;

                        newRes.Codes.Add(entryCode.Value);
                        newRes.EntryCodes.Add(new Interfaces.EntryCode
                        {
                            Code = entryCode.Value,
                            IsMainPicture = entryCode.IsMainPicture
                        });
                    }
                }

                if (resource.action != ImporterActions.Deleted)
                {
                    newRes.MetaFields = GenerateMetaFields(resource);

                    // path is ".\some file.ext"
                    if (resource.Paths != null && resource.Paths.Path != null)
                    {
                        string filePath = resource.Paths.Path.Value.Remove(0, 1);
                        filePath = filePath.Replace("/", "\\");
                        newRes.Path = baseResourcePath + filePath;
                    }
                }

                newRes.ResourceId = resource.id;
                resourcesForImport.Add(newRes);
            }

            if (resourcesForImport.Count == 0)
            {
                IntegrationLogger.Write(LogLevel.Debug, "No resources to import, no action taken.");
                return true;
            }

            return PostToEpiserver(resourcesForImport);
        }

        private List<ResourceMetaField> GenerateMetaFields(Resource resource)
        {
            List<ResourceMetaField> metaFields = new List<ResourceMetaField>();
            if (resource.ResourceFields == null)
                return metaFields;

            foreach (var metaField in resource.ResourceFields.MetaField)
            {
                var resourceMetaField = new ResourceMetaField { Id = metaField.Name.Value };
                var values = new List<Value>();

                foreach (var data in metaField.Data)
                {
                    Value value = new Value { Languagecode = data.language };
                    if (data.Item != null && data.Item.Count > 0)
                    {
                        foreach (var item in data.Item)
                        {
                            value.Data += item.value + ";";
                        }
                            
                        var lastIndexOf = value.Data.LastIndexOf(';');
                        if (lastIndexOf != -1)
                        {
                            value.Data = value.Data.Remove(lastIndexOf);
                        }
                    }
                    else
                    {
                        value.Data = data.value;    
                    }
                        
                    values.Add(value);
                }

                resourceMetaField.Values = values;

                metaFields.Add(resourceMetaField);
            }

            return metaFields;
        }

        private bool PostToEpiserver(List<InRiverImportResource> resourcesForImport)
        {
            var batchSize = 200;
            for (var i = 0; i < resourcesForImport.Count; i += batchSize)
            {
                IntegrationLogger.Write(LogLevel.Debug, $"Sending resources {i}-{i+batchSize} out of {resourcesForImport.Count} resources to Episerver");

                var resourcesToPost = resourcesForImport.Skip(i).Take(batchSize);

                var response = _httpClient.PostAsJsonAsync(_config.Endpoints.ImportResources, resourcesToPost);
                response.Wait();
                response.Result.EnsureSuccessStatusCode();
            }

            return true;
        }
    }
}
