﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Rpc;
using Microsoft.Azure.WebJobs.Script.WebHost;
using Microsoft.Azure.WebJobs.Script.WebHost.Management;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Managment
{
    public class WebFunctionsManagerTests : IDisposable
    {
        private readonly string _testRootScriptPath;
        private readonly string _testHostConfigFilePath;
        private readonly ScriptApplicationHostOptions _hostOptions;

        public WebFunctionsManagerTests()
        {
            _testRootScriptPath = Path.GetTempPath();
            _testHostConfigFilePath = Path.Combine(_testRootScriptPath, ScriptConstants.HostMetadataFileName);
            FileUtility.DeleteFileSafe(_testHostConfigFilePath);

            _hostOptions = new ScriptApplicationHostOptions
            {
                ScriptPath = @"x:\root",
                IsSelfHost = false,
                LogPath = @"x:\tmp\log",
                SecretsPath = @"x:\secrets",
                TestDataPath = @"x:\test"
            };
        }

        [Fact]
        public async Task VerifySyncTriggersContent()
        {
            var vars = new Dictionary<string, string>
            {
                { EnvironmentSettingNames.WebSiteAuthEncryptionKey, TestHelpers.GenerateKeyHexString() },
                { EnvironmentSettingNames.AzureWebsiteHostName, "appName.azurewebsites.net" }
            };
            using (var env = new TestScopedEnvironmentVariable(vars))
            {
                // Setup
                var fileSystem = CreateFileSystem(_hostOptions.ScriptPath);
                var loggerFactory = MockNullLogerFactory.CreateLoggerFactory();
                var contentBuilder = new StringBuilder();
                var httpClient = CreateHttpClient(contentBuilder);
                var factory = new TestOptionsFactory<ScriptApplicationHostOptions>(_hostOptions);
                var tokenSource = new TestChangeTokenSource();
                var changeTokens = new[] { tokenSource };
                var optionsMonitor = new OptionsMonitor<ScriptApplicationHostOptions>(factory, changeTokens, factory);
                var secretManagerProviderMock = new Mock<ISecretManagerProvider>(MockBehavior.Strict);
                var secretManagerMock = new Mock<ISecretManager>(MockBehavior.Strict);
                secretManagerProviderMock.SetupGet(p => p.Current).Returns(secretManagerMock.Object);
                var hostSecretsInfo = new HostSecretsInfo();
                hostSecretsInfo.MasterKey = "aaa";
                hostSecretsInfo.FunctionKeys = new Dictionary<string, string>
                {
                    { "TestHostFunctionKey1", "aaa" },
                    { "TestHostFunctionKey2", "bbb" }
                };
                hostSecretsInfo.SystemKeys = new Dictionary<string, string>
                {
                    { "TestSystemKey1", "aaa" },
                    { "TestSystemKey2", "bbb" }
                };
                secretManagerMock.Setup(p => p.GetHostSecretsAsync()).ReturnsAsync(hostSecretsInfo);
                Dictionary<string, string> functionSecretsResponse = new Dictionary<string, string>()
                {
                    { "TestFunctionKey1", "aaa" },
                    { "TestFunctionKey2", "bbb" }
                };
                secretManagerMock.Setup(p => p.GetFunctionSecretsAsync("function1", false)).ReturnsAsync(functionSecretsResponse);

                var functionsSyncManager = new FunctionsSyncManager(optionsMonitor, new OptionsWrapper<LanguageWorkerOptions>(CreateLanguageWorkerConfigSettings()), loggerFactory, httpClient, secretManagerProviderMock.Object);

                FileUtility.Instance = fileSystem;

                // Act
                (var success, var error) = await functionsSyncManager.TrySyncTriggersAsync();
                var result = JObject.Parse(contentBuilder.ToString());

                // Assert
                Assert.True(success, "SyncTriggers should return success true");
                Assert.True(string.IsNullOrEmpty(error), "Error should be null or empty");

                // verify triggers
                const string expectedSyncTriggersPayload = "[{\"authLevel\":\"anonymous\",\"type\":\"httpTrigger\",\"direction\":\"in\",\"name\":\"req\",\"functionName\":\"function1\"}," +
                "{\"name\":\"myQueueItem\",\"type\":\"orchestrationTrigger\",\"direction\":\"in\",\"queueName\":\"myqueue-items\",\"connection\":\"DurableStorage\",\"functionName\":\"function2\",\"taskHubName\":\"TestHubValue\"}," +
                "{\"name\":\"myQueueItem\",\"type\":\"activityTrigger\",\"direction\":\"in\",\"queueName\":\"myqueue-items\",\"connection\":\"DurableStorage\",\"functionName\":\"function3\",\"taskHubName\":\"TestHubValue\"}]";
                var triggers = result["triggers"];
                Assert.Equal(expectedSyncTriggersPayload, triggers.ToString(Formatting.None));

                // verify functions
                var functions = (JArray)result["functions"];
                Assert.Equal(3, functions.Count);

                // verify secrets
                var secrets = (JObject)result["secrets"];
                var hostSecrets = (JObject)secrets["host"];
                Assert.Equal("aaa", (string)hostSecrets["master"]);
                var hostFunctionSecrets = (JObject)hostSecrets["function"];
                Assert.Equal("aaa", (string)hostFunctionSecrets["TestHostFunctionKey1"]);
                Assert.Equal("bbb", (string)hostFunctionSecrets["TestHostFunctionKey2"]);
                var systemSecrets = (JObject)hostSecrets["system"];
                Assert.Equal("aaa", (string)systemSecrets["TestSystemKey1"]);
                Assert.Equal("bbb", (string)systemSecrets["TestSystemKey2"]);

                var functionSecrets = (JArray)secrets["function"];
                Assert.Equal(1, functionSecrets.Count);
                var function1Secrets = (JObject)functionSecrets[0];
                Assert.Equal("function1", function1Secrets["name"]);
                Assert.Equal("aaa", (string)function1Secrets["secrets"]["TestFunctionKey1"]);
                Assert.Equal("bbb", (string)function1Secrets["secrets"]["TestFunctionKey2"]);
            }
        }

        [Theory]
        [InlineData(1, "http://sitename/operations/settriggers")]
        [InlineData(0, "https://sitename/operations/settriggers")]
        public void Disables_Ssl_If_SkipSslValidation_Enabled(int skipSslValidation, string syncTriggersUri)
        {
            var vars = new Dictionary<string, string>
            {
                { EnvironmentSettingNames.SkipSslValidation, skipSslValidation.ToString() },
                { EnvironmentSettingNames.AzureWebsiteHostName, "sitename" },
            };

            using (var env = new TestScopedEnvironmentVariable(vars))
            {
                var httpRequest = FunctionsSyncManager.BuildSetTriggersRequest();
                Assert.Equal(syncTriggersUri, httpRequest.RequestUri.AbsoluteUri);
                Assert.Equal(HttpMethod.Post, httpRequest.Method);
            }
        }

        [Fact]
        public void ReadFunctionsMetadataSucceeds()
        {
            string functionsPath = Path.Combine(Environment.CurrentDirectory, @"..\..\..\..\..\sample");
            // Setup
            var fileSystem = CreateFileSystem(_hostOptions.ScriptPath);
            var loggerFactory = MockNullLogerFactory.CreateLoggerFactory();
            var contentBuilder = new StringBuilder();
            var httpClient = CreateHttpClient(contentBuilder);
            var factory = new TestOptionsFactory<ScriptApplicationHostOptions>(_hostOptions);
            var tokenSource = new TestChangeTokenSource();
            var changeTokens = new[] { tokenSource };
            var optionsMonitor = new OptionsMonitor<ScriptApplicationHostOptions>(factory, changeTokens, factory);
            var secretManagerProviderMock = new Mock<ISecretManagerProvider>(MockBehavior.Strict);
            var secretManagerMock = new Mock<ISecretManager>(MockBehavior.Strict);
            secretManagerProviderMock.SetupGet(p => p.Current).Returns(secretManagerMock.Object);
            var hostSecretsInfo = new HostSecretsInfo();
            secretManagerMock.Setup(p => p.GetHostSecretsAsync()).ReturnsAsync(hostSecretsInfo);
            Dictionary<string, string> functionSecrets = new Dictionary<string, string>();
            secretManagerMock.Setup(p => p.GetFunctionSecretsAsync("httptrigger", false)).ReturnsAsync(functionSecrets);

            var functionsSyncManager = new FunctionsSyncManager(optionsMonitor, new OptionsWrapper<LanguageWorkerOptions>(CreateLanguageWorkerConfigSettings()), loggerFactory, httpClient, secretManagerProviderMock.Object);
            var webManager = new WebFunctionsManager(optionsMonitor, new OptionsWrapper<LanguageWorkerOptions>(CreateLanguageWorkerConfigSettings()), loggerFactory, httpClient, secretManagerProviderMock.Object, functionsSyncManager);

            FileUtility.Instance = fileSystem;
            IEnumerable<FunctionMetadata> metadata = webManager.GetFunctionsMetadata();
            var jsFunctions = metadata.Where(funcMetadata => funcMetadata.Language == LanguageWorkerConstants.NodeLanguageWorkerName).ToList();
            var unknownFunctions = metadata.Where(funcMetadata => string.IsNullOrEmpty(funcMetadata.Language)).ToList();

            Assert.Equal(2, jsFunctions.Count());
            Assert.Equal(1, unknownFunctions.Count());
        }

        [Theory]
        [InlineData(null, "api")]
        [InlineData("", "api")]
        [InlineData("this { not json", "api")]
        [InlineData("{}", "api")]
        [InlineData("{ extensions: {} }", "api")]
        [InlineData("{ extensions: { http: {} }", "api")]
        [InlineData("{ extensions: { http: { routePrefix: 'test' }, foo: {} } }", "test")]
        public async Task GetRoutePrefix_Succeeds(string content, string expected)
        {
            if (content != null)
            {
                File.WriteAllText(_testHostConfigFilePath, content);
            }

            string prefix = await WebFunctionsManager.GetRoutePrefix(_testRootScriptPath);
            Assert.Equal(expected, prefix);
        }

        private static HttpClient CreateHttpClient(StringBuilder writeContent)
        {
            return new HttpClient(new MockHttpHandler(writeContent));
        }

        private static LanguageWorkerOptions CreateLanguageWorkerConfigSettings()
        {
            return new LanguageWorkerOptions
            {
                WorkerConfigs = TestHelpers.GetTestWorkerConfigs()
            };
        }

        private static IFileSystem CreateFileSystem(string rootPath)
        {
            var fullFileSystem = new FileSystem();
            var fileSystem = new Mock<IFileSystem>();
            var fileBase = new Mock<FileBase>();
            var dirBase = new Mock<DirectoryBase>();

            fileSystem.SetupGet(f => f.Path).Returns(fullFileSystem.Path);
            fileSystem.SetupGet(f => f.File).Returns(fileBase.Object);
            fileBase.Setup(f => f.Exists(Path.Combine(rootPath, "host.json"))).Returns(true);

            var hostJson = new MemoryStream(Encoding.UTF8.GetBytes(@"{ ""durableTask"": { ""HubName"": ""TestHubValue"", ""azureStorageConnectionStringName"": ""DurableStorage"" }}"));
            hostJson.Position = 0;
            fileBase.Setup(f => f.Open(Path.Combine(rootPath, @"host.json"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(hostJson);

            fileSystem.SetupGet(f => f.Directory).Returns(dirBase.Object);

            dirBase.Setup(d => d.EnumerateDirectories(rootPath))
                .Returns(new[]
                {
                    @"x:\root\function1",
                    @"x:\root\function2",
                    @"x:\root\function3"
                });

            var function1 = @"{
  ""scriptFile"": ""main.py"",
  ""disabled"": false,
  ""bindings"": [
    {
      ""authLevel"": ""anonymous"",
      ""type"": ""httpTrigger"",
      ""direction"": ""in"",
      ""name"": ""req""
    },
    {
      ""type"": ""http"",
      ""direction"": ""out"",
      ""name"": ""$return""
    }
  ]
}";
            var function2 = @"{
  ""disabled"": false,
  ""scriptFile"": ""main.js"",
  ""bindings"": [
    {
      ""name"": ""myQueueItem"",
      ""type"": ""orchestrationTrigger"",
      ""direction"": ""in"",
      ""queueName"": ""myqueue-items"",
      ""connection"": """"
    }
  ]
}";

            var function3 = @"{
  ""disabled"": false,
  ""scriptFile"": ""main.js"",
  ""bindings"": [
    {
      ""name"": ""myQueueItem"",
      ""type"": ""activityTrigger"",
      ""direction"": ""in"",
      ""queueName"": ""myqueue-items"",
      ""connection"": """"
    }
  ]
}";
            var function1Stream = new MemoryStream(Encoding.UTF8.GetBytes(function1));
            function1Stream.Position = 0;
            var function2Stream = new MemoryStream(Encoding.UTF8.GetBytes(function2));
            function2Stream.Position = 0;
            var function3Stream = new MemoryStream(Encoding.UTF8.GetBytes(function3));
            function3Stream.Position = 0;
            fileBase.Setup(f => f.Exists(Path.Combine(rootPath, @"function1\function.json"))).Returns(true);
            fileBase.Setup(f => f.Exists(Path.Combine(rootPath, @"function1\main.py"))).Returns(true);
            fileBase.Setup(f => f.ReadAllText(Path.Combine(rootPath, @"function1\function.json"))).Returns(function1);
            fileBase.Setup(f => f.Open(Path.Combine(rootPath, @"function1\function.json"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(function1Stream);

            fileBase.Setup(f => f.Exists(Path.Combine(rootPath, @"function2\function.json"))).Returns(true);
            fileBase.Setup(f => f.Exists(Path.Combine(rootPath, @"function2\main.js"))).Returns(true);
            fileBase.Setup(f => f.ReadAllText(Path.Combine(rootPath, @"function2\function.json"))).Returns(function2);
            fileBase.Setup(f => f.Open(Path.Combine(rootPath, @"function2\function.json"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(function2Stream);

            fileBase.Setup(f => f.Exists(Path.Combine(rootPath, @"function3\function.json"))).Returns(true);
            fileBase.Setup(f => f.Exists(Path.Combine(rootPath, @"function3\main.js"))).Returns(true);
            fileBase.Setup(f => f.ReadAllText(Path.Combine(rootPath, @"function3\function.json"))).Returns(function3);
            fileBase.Setup(f => f.Open(Path.Combine(rootPath, @"function3\function.json"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(function3Stream);

            return fileSystem.Object;
        }

        public void Dispose()
        {
            // Clean up mock IFileSystem
            FileUtility.Instance = null;
            Environment.SetEnvironmentVariable(EnvironmentSettingNames.WebSiteAuthEncryptionKey, string.Empty);
            Environment.SetEnvironmentVariable("WEBSITE_SITE_NAME", string.Empty);
            FileUtility.DeleteFileSafe(_testHostConfigFilePath);
        }

        private class MockHttpHandler : HttpClientHandler
        {
            private StringBuilder _content;

            public MockHttpHandler(StringBuilder content)
            {
                _content = content;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _content.Append(await request.Content.ReadAsStringAsync());
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            }
        }
    }
}
