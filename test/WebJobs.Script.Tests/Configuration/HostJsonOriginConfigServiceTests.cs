// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Linq;
using Microsoft.Azure.WebJobs.Script.Configuration;
using Microsoft.Azure.WebJobs.Script.WebHost.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.WebJobs.Script.Tests;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Configuration
{
    public class HostJsonOriginConfigServiceTests
    {
        private const string TestDataMissingOriginConfig =
            @"{
                'version': '2.0',
            }";

        private const string TestDataSimpleOriginConfig =
            @"{
                'version': '2.0',
                'origin': 'serverlesslibrary',
            }";

        private const string TestDataNestedOriginConfig =
            @"{
                'version': '2.0',
                'origin': {
                    'name': 'serverlesslibrary',
                    'id': 'https://raw.githubusercontent.com/Azure-Samples/functions-node-sas-token/master/azuredeploy.json'
                }
            }";

        private readonly TestEnvironment _environment = new TestEnvironment();
        private readonly LoggerFactory _loggerFactory = new LoggerFactory();
        private readonly TestLoggerProvider _loggerProvider = new TestLoggerProvider();
        private readonly string _hostJsonFile;
        private readonly ScriptApplicationHostOptions _options;

        public HostJsonOriginConfigServiceTests()
        {
            string rootPath = Path.Combine(Environment.CurrentDirectory, "ScriptHostTests");

            if (!Directory.Exists(rootPath))
            {
                Directory.CreateDirectory(rootPath);
            }

            _options = new ScriptApplicationHostOptions
            {
                ScriptPath = rootPath
            };

            _hostJsonFile = Path.Combine(rootPath, "host.json");
            if (File.Exists(_hostJsonFile))
            {
                File.Delete(_hostJsonFile);
            }

            _loggerFactory.AddProvider(_loggerProvider);
        }

        [Theory]
        [InlineData(TestDataMissingOriginConfig)]
        [InlineData(TestDataSimpleOriginConfig)]
        [InlineData(TestDataNestedOriginConfig)]
        public void MissingOrValidOriginConfig_DoesNotThrowException(string hostJsonContent)
        {
            File.WriteAllText(_hostJsonFile, hostJsonContent);
            Assert.True(File.Exists(_hostJsonFile));
            var configuration = BuildHostJsonConfiguration();

            HostJsonOriginConfigService service = new HostJsonOriginConfigService(configuration, _loggerFactory);
            var ex = Record.ExceptionAsync(async () => await service.StartAsync(default).ConfigureAwait(false));
            Assert.Null(ex.Result);
        }

        [Theory]
        [InlineData(TestDataMissingOriginConfig)]
        public void MissingOriginConfig_DoesNotGenerateLog(string hostJsonContent)
        {
            File.WriteAllText(_hostJsonFile, hostJsonContent);
            Assert.True(File.Exists(_hostJsonFile));
            var configuration = BuildHostJsonConfiguration();

            HostJsonOriginConfigService service = new HostJsonOriginConfigService(configuration, _loggerFactory);
            var ex = Record.ExceptionAsync(async () => await service.StartAsync(default).ConfigureAwait(false));
            Assert.Null(ex.Result);

            var logMessages = _loggerProvider.GetAllLogMessages();
            var originLog = logMessages.FirstOrDefault(m => m.FormattedMessage.StartsWith("Origin found"));
            Assert.Null(originLog);
        }

        [Theory]
        [InlineData(TestDataSimpleOriginConfig)]
        [InlineData(TestDataNestedOriginConfig)]
        public void ValidOriginConfig_GeneratesLog(string hostJsonContent)
        {
            File.WriteAllText(_hostJsonFile, hostJsonContent);
            Assert.True(File.Exists(_hostJsonFile));
            var configuration = BuildHostJsonConfiguration();

            HostJsonOriginConfigService service = new HostJsonOriginConfigService(configuration, _loggerFactory);
            var ex = Record.ExceptionAsync(async () => await service.StartAsync(default).ConfigureAwait(false));
            Assert.Null(ex.Result);

            var logMessages = _loggerProvider.GetAllLogMessages();
            var originLog = logMessages.FirstOrDefault(m => m.FormattedMessage.StartsWith("Origin found"));
            Assert.NotNull(originLog);
        }

        [Theory]
        [InlineData(TestDataSimpleOriginConfig)]
        [InlineData(TestDataNestedOriginConfig)]
        public void ValidOriginConfig_GetJsonObject(string hostJsonContent)
        {
            File.WriteAllText(_hostJsonFile, hostJsonContent);
            Assert.True(File.Exists(_hostJsonFile));
            var configuration = BuildHostJsonConfiguration();

            string originPath = ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, "Origin");
            IConfigurationSection originSection = configuration.GetSection(originPath);

            var actualJsonObject = HostJsonOriginConfigService.GetJsonObject(originSection);
            Assert.NotNull(actualJsonObject);

            var expectedJsonObject = JObject.Parse(hostJsonContent)["origin"];
            Assert.True(JToken.DeepEquals(expectedJsonObject, actualJsonObject));
        }

        private IConfiguration BuildHostJsonConfiguration(IEnvironment environment = null)
        {
            environment = environment ?? new TestEnvironment();

            var configSource = new HostJsonFileConfigurationSource(_options, environment, _loggerFactory);

            var configurationBuilder = new ConfigurationBuilder()
                .Add(configSource);

            return configurationBuilder.Build();
        }
    }
}
