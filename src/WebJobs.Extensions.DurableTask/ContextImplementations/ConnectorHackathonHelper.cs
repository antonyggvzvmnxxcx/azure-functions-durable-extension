// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.WebJobs.Extensions.DurableTask
{
    internal static class ConnectorHackathonHelper
    {
        private const string Location = "westus";

        private static TemplateMatcher templateMatcher = new TemplateMatcher(TemplateParser.Parse("subscriptions/{subscriptionid}/resourceGroups/{resourceGroup}/providers/Microsoft.Web/connections/{connector}/{**rest}"), new RouteValueDictionary());

        internal static async Task ProvisionApiConnectionIfNecessary(IDurableOrchestrationContext orchestrationContext, DurableHttpRequest req)
        {
            var routeValues = new RouteValueDictionary();
            if (!templateMatcher.TryMatch(req.Uri.AbsolutePath, routeValues))
            {
                return;
            }

            RequestDetails requestDetails = new RequestDetails()
            {
                SubscriptionId = (string)routeValues["subscriptionid"],
                ResourceGroup = (string)routeValues["resourceGroup"],
                Connector = (string)routeValues["connector"],
                TokenSource = req.TokenSource,
            };

            bool connectorExists = await DoesConnectorAlreadyExistAsync(orchestrationContext, requestDetails);
            if (!connectorExists)
            {
                JObject requiredParameters = await GetRequiredParametersForConnector(orchestrationContext, requestDetails);
                var parameters = new Dictionary<string, string>();
                if (requiredParameters.Value<JObject>("connectionParameters").Count != 0)
                {
                    var status = new JObject();
                    status["description"] = $"Waiting for parameters to construct the connector {requestDetails.Connector}";
                    status["parameters"] = requiredParameters;
                    orchestrationContext.SetCustomStatus(status);
                    var recievedParameters = await orchestrationContext.WaitForExternalEvent<List<KeyValuePair<string, string>>>("SendParameters");

                    // TODO verify the parameters and retry if incorrect
                    foreach (var parameter in recievedParameters)
                    {
                        parameters.Add(parameter.Key, parameter.Value);
                    }
                }

                var createResponse = await CreateConnectorAsync(orchestrationContext, requestDetails, parameters);

                if ((createResponse.Value<JArray>("value")?.Count ?? 0) != 0)
                {
                    var status = new JObject();
                    status["description"] = $"Waiting for new connector {requestDetails.Connector} to be granted consent";
                    status["consentLinks"] = createResponse["value"];
                    await orchestrationContext.WaitForExternalEvent("FinishedConsenting");
                }
            }
        }

        private static async Task<bool> DoesConnectorAlreadyExistAsync(IDurableOrchestrationContext orchestrationContext, RequestDetails reqDetails)
        {
            DurableHttpRequest req = new DurableHttpRequest(HttpMethod.Get, new Uri($"https://management.azure.com/subscriptions/{reqDetails.SubscriptionId}/providers/Microsoft.Web/connections/{reqDetails.Connector}?api-version=2016-06-01"))
            {
                TokenSource = reqDetails.TokenSource,
            };
            DurableHttpResponse response = await orchestrationContext.CallHttpAsync(req);
            return response.StatusCode == HttpStatusCode.OK;
        }

        private static async Task<JObject> GetRequiredParametersForConnector(IDurableOrchestrationContext orchestrationContext, RequestDetails reqDetails)
        {
            DurableHttpRequest req = new DurableHttpRequest(HttpMethod.Get, new Uri($"https://management.azure.com/subscriptions/{reqDetails.SubscriptionId}/providers/Microsoft.Web/locations/{Location}/managedApis/{reqDetails.Connector}?api-version=2016-06-01"))
            {
                TokenSource = reqDetails.TokenSource,
            };
            DurableHttpResponse response = await orchestrationContext.CallHttpAsync(req);
            return JObject.Parse(response.Content).Value<JObject>("properties");
        }

        private static async Task<JObject> CreateConnectorAsync(IDurableOrchestrationContext orchestrationContext, RequestDetails reqDetails, Dictionary<string, string> parameters)
        {
            JObject body = new JObject();
            body["properties"] = new JObject();
            body["location"] = Location;
            body["properties"]["api"] = new JObject();
            body["properties"]["api"]["id"] = $"/subscriptions/{reqDetails.SubscriptionId}/providers/Microsoft.Web/locations/{Location}/managedApis/{reqDetails.Connector}";
            body["properties"]["api"]["parameterValues"] = new JObject();
            foreach (var parameter in parameters)
            {
                body["properties"]["api"]["parameterValues"][parameter.Key] = parameter.Value;
            }

            DurableHttpRequest req = new DurableHttpRequest(HttpMethod.Post, new Uri($"https://management.azure.com/subscriptions/{reqDetails.SubscriptionId}/providers/Microsoft.Web/connections/{reqDetails.Connector}?api-version=2018-07-01-preview"))
            {
                Content = body.ToString(),
                TokenSource = reqDetails.TokenSource,
            };
            DurableHttpResponse response = await orchestrationContext.CallHttpAsync(req);
            return JObject.Parse(response.Content);
        }

        private class RequestDetails
        {
            public string SubscriptionId { get; set; }

            public string ResourceGroup { get; set; }

            public string Connector { get; set; }

            public ITokenSource TokenSource { get; set; }
        }
    }
}
