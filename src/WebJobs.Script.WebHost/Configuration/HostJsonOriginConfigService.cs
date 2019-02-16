// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Logging;
using Microsoft.Azure.WebJobs.Script.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using static System.Environment;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Configuration
{
    public class HostJsonOriginConfigService : IHostedService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger _logger;

        public HostJsonOriginConfigService(IConfiguration configuration, ILoggerFactory loggerFactory)
        {
            _configuration = configuration;
            _logger = loggerFactory.CreateLogger(LogCategories.Startup);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            string originPath = ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, "Origin");
            IConfigurationSection configSection = _configuration.GetSection(originPath);
            if (configSection.Exists())
            {
                _logger.LogInformation($"Origin found:{NewLine}{GetJsonObject(configSection)?.ToString()}");
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private static JToken GetJsonObject(IConfigurationSection configSection)
        {
            if (configSection.GetChildren().Any())
            {
                JObject rootObj = new JObject();
                foreach (var child in configSection.GetChildren())
                {
                    JToken childObj = GetJsonObject(child);
                    rootObj[child.Key] = childObj;
                }

                return rootObj;
            }
            else
            {
                return JToken.FromObject(configSection.Value);
            }
        }
    }
}
