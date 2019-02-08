// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Rpc;
using Microsoft.Azure.WebJobs.Script.WebHost.Extensions;
using Microsoft.Azure.WebJobs.Script.WebHost.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Management
{
    public class FunctionsSyncManager : IFunctionsSyncManager
    {
        private const string HubName = "HubName";
        private const string TaskHubName = "taskHubName";
        private const string Connection = "connection";
        private const string DurableTaskStorageConnectionName = "azureStorageConnectionStringName";
        private const string DurableTask = "durableTask";

        private readonly IOptionsMonitor<ScriptApplicationHostOptions> _applicationHostOptions;
        private readonly ILogger _logger;
        private readonly IEnumerable<WorkerConfig> _workerConfigs;
        private readonly HttpClient _httpClient;
        private readonly ISecretManagerProvider _secretManagerProvider;

        public FunctionsSyncManager(IOptionsMonitor<ScriptApplicationHostOptions> applicationHostOptions, IOptions<LanguageWorkerOptions> languageWorkerOptions, ILoggerFactory loggerFactory, HttpClient httpClient, ISecretManagerProvider secretManagerProvider)
        {
            _applicationHostOptions = applicationHostOptions;
            _logger = loggerFactory?.CreateLogger(ScriptConstants.LogCategoryHostGeneral);
            _workerConfigs = languageWorkerOptions.Value.WorkerConfigs;
            _httpClient = httpClient;
            _secretManagerProvider = secretManagerProvider;
        }

        public async Task<(bool success, string error)> TrySyncTriggersAsync()
        {
            JObject result = await GetSyncTriggersPayload();
            string content = JsonConvert.SerializeObject(result);

            return await SetTriggersAsync(content);
        }

        public async Task<JObject> GetSyncTriggersPayload()
        {
            var hostOptions = _applicationHostOptions.CurrentValue.ToHostOptions();
            var functionsMetadata = WebFunctionsManager.GetFunctionsMetadata(hostOptions, _workerConfigs, _logger, includeProxies: true);

            // Add trigger information used by the ScaleController
            JObject result = new JObject();
            var triggers = await GetFunctionTriggers(functionsMetadata, hostOptions);
            result.Add("triggers", new JArray(triggers));

            // Add functions details to the payload
            // TODO: format correct base url
            JObject functions = new JObject();
            string baseUrl = "http://localhost";
            string routePrefix = await WebFunctionsManager.GetRoutePrefix(hostOptions.RootScriptPath);
            var functionDetails = await functionsMetadata.Select(p => p.ToFunctionMetadataResponse(hostOptions, routePrefix, baseUrl)).WhenAll();
            result.Add("functions", new JArray(functionDetails.Select(p => JObject.FromObject(p))));

            // Add functions secrets to the payload
            // TODO: encrypt the secrets
            // TODO: what about keyvault or other external secrets? can we cache these?
            JObject secrets = new JObject();
            var hostSecretsInfo = await _secretManagerProvider.Current.GetHostSecretsAsync();
            var hostSecrets = new JObject();
            hostSecrets.Add("master", hostSecretsInfo.MasterKey);
            hostSecrets.Add("function", JObject.FromObject(hostSecretsInfo.FunctionKeys));
            hostSecrets.Add("system", JObject.FromObject(hostSecretsInfo.SystemKeys));
            secrets.Add("host", hostSecrets);
            result.Add("secrets", secrets);

            var functionSecrets = new JArray();
            var httpFunctions = functionsMetadata.Where(p => !p.IsProxy && p.InputBindings.Any(q => q.IsTrigger && string.Compare(q.Type, "httptrigger", StringComparison.OrdinalIgnoreCase) == 0)).Select(p => p.Name);
            foreach (var functionName in httpFunctions)
            {
                var currSecrets = await _secretManagerProvider.Current.GetFunctionSecretsAsync(functionName);
                var currElement = new JObject()
                {
                    { "name", functionName },
                    { "secrets", JObject.FromObject(currSecrets) }
                };
                functionSecrets.Add(currElement);
            }
            secrets.Add("function", functionSecrets);

            return result;
        }

        internal async Task<IEnumerable<JObject>> GetFunctionTriggers(IEnumerable<FunctionMetadata> functionsMetadata, ScriptJobHostOptions hostOptions)
        {
            var durableTaskConfig = await ReadDurableTaskConfig();
            var triggers = (await functionsMetadata
                .Where(f => !f.IsProxy)
                .Select(f => f.ToFunctionTrigger(hostOptions))
                .WhenAll())
                .Where(t => t != null)
                .Select(t =>
                {
                    // if we have a durableTask hub name and the function trigger is either orchestrationTrigger OR activityTrigger,
                    // add a property "taskHubName" with durable task hub name.
                    if (durableTaskConfig.Any()
                        && (t["type"]?.ToString().Equals("orchestrationTrigger", StringComparison.OrdinalIgnoreCase) == true
                            || t["type"]?.ToString().Equals("activityTrigger", StringComparison.OrdinalIgnoreCase) == true))
                    {
                        if (durableTaskConfig.ContainsKey(HubName))
                        {
                            t[TaskHubName] = durableTaskConfig[HubName];
                        }

                        if (durableTaskConfig.ContainsKey(Connection))
                        {
                            t[Connection] = durableTaskConfig[Connection];
                        }
                    }
                    return t;
                });

            if (FileUtility.FileExists(Path.Combine(hostOptions.RootScriptPath, ScriptConstants.ProxyMetadataFileName)))
            {
                // This is because we still need to scale function apps that are proxies only
                triggers = triggers.Append(JObject.FromObject(new { type = "routingTrigger" }));
            }

            return triggers;
        }

        private async Task<Dictionary<string, string>> ReadDurableTaskConfig()
        {
            var hostOptions = _applicationHostOptions.CurrentValue.ToHostOptions();
            string hostJsonPath = Path.Combine(hostOptions.RootScriptPath, ScriptConstants.HostMetadataFileName);
            var config = new Dictionary<string, string>();
            if (FileUtility.FileExists(hostJsonPath))
            {
                var hostJson = JObject.Parse(await FileUtility.ReadAsync(hostJsonPath));
                JToken durableTaskValue;

                // we will allow case insensitivity given it is likely user hand edited
                // see https://github.com/Azure/azure-functions-durable-extension/issues/111
                //
                // We're looking for {VALUE}
                // {
                //     "durableTask": {
                //         "hubName": "{VALUE}",
                //         "azureStorageConnectionStringName": "{VALUE}"
                //     }
                // }
                if (hostJson.TryGetValue(DurableTask, StringComparison.OrdinalIgnoreCase, out durableTaskValue) && durableTaskValue != null)
                {
                    try
                    {
                        var kvp = (JObject)durableTaskValue;
                        if (kvp.TryGetValue(HubName, StringComparison.OrdinalIgnoreCase, out JToken nameValue) && nameValue != null)
                        {
                            config.Add(HubName, nameValue.ToString());
                        }

                        if (kvp.TryGetValue(DurableTaskStorageConnectionName, StringComparison.OrdinalIgnoreCase, out nameValue) && nameValue != null)
                        {
                            config.Add(Connection, nameValue.ToString());
                        }
                    }
                    catch (Exception)
                    {
                        throw new InvalidDataException("Invalid host.json configuration for 'durableTask'.");
                    }
                }
            }

            return config;
        }

        internal static HttpRequestMessage BuildSetTriggersRequest()
        {
            var protocol = "https";
            // On private stamps with no ssl certificate use http instead.
            if (Environment.GetEnvironmentVariable(EnvironmentSettingNames.SkipSslValidation) == "1")
            {
                protocol = "http";
            }

            var url = $"{protocol}://{Environment.GetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteHostName)}/operations/settriggers";

            return new HttpRequestMessage(HttpMethod.Post, url);
        }

        // This function will call POST https://{app}.azurewebsites.net/operation/settriggers with the content
        // of triggers. It'll verify app owner ship using a SWT token valid for 5 minutes. It should be plenty.
        private async Task<(bool, string)> SetTriggersAsync(string content)
        {
            var token = SimpleWebTokenHelper.CreateToken(DateTime.UtcNow.AddMinutes(5));

            using (var request = BuildSetTriggersRequest())
            {
                // This has to start with Mozilla because the frontEnd checks for it.
                request.Headers.Add("User-Agent", "Mozilla/5.0");
                request.Headers.Add("x-ms-site-restricted-token", token);
                request.Content = new StringContent(content, Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode
                    ? (true, string.Empty)
                    : (false, $"Sync triggers failed with: {response.StatusCode}");
            }
        }
    }
}
