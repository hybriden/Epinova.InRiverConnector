﻿using System;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Controllers;

namespace Epinova.InRiverConnector.EpiserverImporter
{
    public class SecuredApiController : ApiController
    {
        private const string ApiKeyName = "apikey";

        private static string ApiKeyValue => ConfigurationManager.AppSettings["InRiverConnector.APIKey"];

        public override Task<HttpResponseMessage> ExecuteAsync(HttpControllerContext controllerContext, CancellationToken cancellationToken)
        {
            if (!ValidateApiKey(controllerContext.Request))
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Forbidden);
                var tsc = new TaskCompletionSource<HttpResponseMessage>();
                tsc.SetResult(resp);
                return tsc.Task;
            }

            return base.ExecuteAsync(controllerContext, cancellationToken);
        }

        protected virtual bool ValidateApiKey(HttpRequestMessage request)
        {
            if (String.IsNullOrEmpty(ApiKeyValue))
                return false;

            return request.Headers.GetValues(ApiKeyName).FirstOrDefault() == ApiKeyValue;
        }
    }
}