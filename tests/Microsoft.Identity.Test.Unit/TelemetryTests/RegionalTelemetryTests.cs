﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using Microsoft.Identity.Test.Unit.Throttling;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.OAuth2.Throttling;
using Microsoft.Identity.Client.TelemetryCore;
using Microsoft.Identity.Test.Common.Core.Helpers;
using Microsoft.Identity.Test.Common.Core.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Identity.Client.TelemetryCore.Internal.Events.ApiEvent;

namespace Microsoft.Identity.Test.Unit.TelemetryTests
{
    [TestClass]
    public class RegionalTelemetryTests : TestBase
    {
        private MockHttpAndServiceBundle _harness;
        private ConfidentialClientApplication _app;

        [TestInitialize]
        public override void TestInitialize()
        {
            base.TestInitialize();

            _harness = CreateTestHarness();
            _app = ConfidentialClientApplicationBuilder.Create(TestConstants.ClientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, TestConstants.TenantId)
                .WithClientSecret(TestConstants.ClientSecret)
                .WithHttpManager(_harness.HttpManager)
                .WithExperimentalFeatures(true)
                .BuildConcrete();
        }

        [TestCleanup]
        public override void TestCleanup()
        {
            Environment.SetEnvironmentVariable(TestConstants.RegionName, null);
            _harness?.Dispose();
            base.TestCleanup();
        }

        /// <summary>
        /// 1.  Acquire Token For Client with Region successfully
        ///        Current_request = 2 | ATC_ID, 0 | centralus, 1, 0,
        ///        Last_request = 2 | 0 | | |
        /// 
        /// 2. Acquire Token for client with Region -> HTTP error 503 (Service Unavailable)
        ///
        ///        Current_request = 2 | ATC_ID, 1 | centralus, 3, 0,
        ///        Last_request = 2 | 0 | | |
        ///
        /// 3. Acquire Token For Client with Region -> successful
        ///
        /// Sent to the server - 
        ///        Current_request = 2 | ATC_ID, 1 | centralus, 3, 0,
        ///        Last_request = 2 | 0 |  ATC_ID, corr_step_2  | ServiceUnavailable | centralus, 3
        /// </summary>
        [TestMethod]
        public async Task TelemetryAcceptanceTestAsync()
        {
            Environment.SetEnvironmentVariable(TestConstants.RegionName, TestConstants.Region);

            Trace.WriteLine("Step 1. Acquire Token For Client with region successful");
            var result = await RunAcquireTokenForClientAsync(AcquireTokenForClientOutcome.Success).ConfigureAwait(false);
            AssertCurrentTelemetry(result.HttpRequest, ApiIds.AcquireTokenForClient, forceRefresh: false, "1");
            AssertPreviousTelemetry(result.HttpRequest, expectedSilentCount: 0); // Previous_request = 2|0|||

            Trace.WriteLine("Step 2. Acquire Token For Client -> HTTP 5xx error (i.e. AAD is down)");
            result = await RunAcquireTokenForClientAsync(AcquireTokenForClientOutcome.AADUnavailableError).ConfigureAwait(false);
            Guid step2CorrelationId = result.Correlationid;

            // we can assert telemetry here, as it will be sent to AAD. However, AAD is down, so it will not record it.
            AssertCurrentTelemetry(result.HttpRequest, ApiIds.AcquireTokenForClient, forceRefresh: true, "3");
            AssertPreviousTelemetry(
                result.HttpRequest,
                expectedSilentCount: 0);

            // the 5xx error puts MSAL in a throttling state, so "wait" until this clears
            _harness.ServiceBundle.ThrottlingManager.SimulateTimePassing(
                HttpStatusProvider.s_throttleDuration.Add(TimeSpan.FromSeconds(1)));

            Trace.WriteLine("Step 3. Acquire Token For Client -> Success");
            result = await RunAcquireTokenForClientAsync(AcquireTokenForClientOutcome.Success, true).ConfigureAwait(false);

            AssertCurrentTelemetry(result.HttpRequest, ApiIds.AcquireTokenForClient, forceRefresh: true, "3");
            AssertPreviousTelemetry(
                result.HttpRequest,
                expectedSilentCount: 0,
                expectedFailedApiIds: new[] { ApiIds.AcquireTokenForClient },
                expectedCorrelationIds: new[] { step2CorrelationId },
                expectedErrors: new[] { "service_not_available" },
                expectedRegions: new[] { "centralus" },
                expectedRegionSources: new[] { "3" });
        }

        /// <summary>
        /// Acquire token for client with serialized token cache successfully
        ///    Current_request = 2 | ATC_ID, 0 | centralus, 1, 1
        ///    Last_request = 2 | 0 | | |
        /// </summary>
        [TestMethod]
        public async Task TelemetrySerializedTokenCacheTestAsync()
        {
            Environment.SetEnvironmentVariable(TestConstants.RegionName, TestConstants.Region);

            var inMemoryTokenCache = new InMemoryTokenCache();
            inMemoryTokenCache.Bind(_app.AppTokenCache);

            Trace.WriteLine("Acquire token for client with token serialization.");
            var result = await RunAcquireTokenForClientAsync(AcquireTokenForClientOutcome.Success).ConfigureAwait(false);
            AssertCurrentTelemetry(result.HttpRequest,
                ApiIds.AcquireTokenForClient,
                forceRefresh: false,
                "1",
                isCacheSerialized: true);
            AssertPreviousTelemetry(result.HttpRequest, expectedSilentCount: 0);
        }

        /// <summary>
        /// Acquire token for client with regionToUse when auto region discovery fails
        ///    Current_request = 2 | ATC_ID, 0 | centralus, 1, 1, centralus,
        ///    Last_request = 2 | 0 | | |
        /// </summary>
        [TestMethod]
        public async Task TelemetryUserProvidedRegionAutoDiscoveryFailsTestsAsync()
        {
            Trace.WriteLine("Acquire token for client with region provided by user and region detection fails.");
            _harness.HttpManager.AddMockHandlerContentNotFound(HttpMethod.Get, TestConstants.ImdsUrl);
            var result = await RunAcquireTokenForClientAsync(AcquireTokenForClientOutcome.UserProvidedRegion).ConfigureAwait(false);
            AssertCurrentTelemetry(result.HttpRequest,
                ApiIds.AcquireTokenForClient,
                forceRefresh: false,
                "4",
                isCacheSerialized: false,
                userProvidedRegion: TestConstants.Region);
            AssertPreviousTelemetry(result.HttpRequest, expectedSilentCount: 0);
        }

        /// <summary>
        /// Acquire token for client with regionToUse when auto region discovery passes with region same as regionToUse
        ///    Current_request = 2 | ATC_ID, 0 | centralus, 1, 1, centralus, 1
        ///    Last_request = 2 | 0 | | |
        /// </summary>
        [TestMethod]
        public async Task TelemetryUserProvidedRegionAutoDiscoverRegionSameTestsAsync()
        {
            Environment.SetEnvironmentVariable(TestConstants.RegionName, TestConstants.Region);

            Trace.WriteLine("Acquire token for client with region provided by user and region detected is same as regionToUse.");
            var result = await RunAcquireTokenForClientAsync(AcquireTokenForClientOutcome.UserProvidedRegion).ConfigureAwait(false);
            AssertCurrentTelemetry(result.HttpRequest,
                ApiIds.AcquireTokenForClient,
                forceRefresh: false,
                "1",
                isCacheSerialized: false,
                userProvidedRegion: TestConstants.Region,
                isvalidUserProvidedRegion: "1");
            AssertPreviousTelemetry(result.HttpRequest, expectedSilentCount: 0);
        }

        /// <summary>
        /// Acquire token for client with regionToUse when auto region discovery passes with region different from regionToUse
        ///    Current_request = 2 | ATC_ID, 0 | centralus, 1, 1, invalid, 0
        ///    Last_request = 2 | 0 | | |
        /// </summary>
        [TestMethod]
        public async Task TelemetryUserProvidedRegionAutoDiscoverRegionDifferentTestsAsync()
        {
            Environment.SetEnvironmentVariable(TestConstants.RegionName, TestConstants.Region);

            Trace.WriteLine("Acquire token for client with region provided by user and region detected is different from regionToUse.");
            var result = await RunAcquireTokenForClientAsync(AcquireTokenForClientOutcome.UserProvidedInvalidRegion).ConfigureAwait(false);
            AssertCurrentTelemetry(result.HttpRequest,
                ApiIds.AcquireTokenForClient,
                forceRefresh: false,
                "1",
                isCacheSerialized: false,
                userProvidedRegion: "invalid",
                isvalidUserProvidedRegion: "0");
            AssertPreviousTelemetry(result.HttpRequest, expectedSilentCount: 0);
        }

        private enum AcquireTokenForClientOutcome
        {
            Success,
            UserProvidedRegion,
            UserProvidedInvalidRegion,
            AADUnavailableError
        }

        private async Task<(HttpRequestMessage HttpRequest, Guid Correlationid)> RunAcquireTokenForClientAsync(
            AcquireTokenForClientOutcome outcome, bool forceRefresh = false)
        {
            MockHttpMessageHandler tokenRequestHandler = null;
            Guid correlationId = default;

            switch (outcome)
            {
                case AcquireTokenForClientOutcome.Success:

                    tokenRequestHandler = _harness.HttpManager.AddSuccessTokenResponseMockHandlerForPost(authority: TestConstants.AuthorityRegional);
                    var authResult = await _app
                        .AcquireTokenForClient(TestConstants.s_scope)
                        .WithPreferredAzureRegion(true)
                        .WithForceRefresh(forceRefresh)
                        .ExecuteAsync()
                        .ConfigureAwait(false);
                    correlationId = authResult.CorrelationId;
                    break;

                case AcquireTokenForClientOutcome.UserProvidedRegion:

                    tokenRequestHandler = _harness.HttpManager.AddSuccessTokenResponseMockHandlerForPost(authority: TestConstants.AuthorityRegional);
                    authResult = await _app
                        .AcquireTokenForClient(TestConstants.s_scope)
                        .WithPreferredAzureRegion(true, TestConstants.Region)
                        .WithForceRefresh(forceRefresh)
                        .ExecuteAsync()
                        .ConfigureAwait(false);
                    correlationId = authResult.CorrelationId;
                    break;

                case AcquireTokenForClientOutcome.UserProvidedInvalidRegion:

                    tokenRequestHandler = _harness.HttpManager.AddSuccessTokenResponseMockHandlerForPost(authority: TestConstants.AuthorityRegional);
                    authResult = await _app
                        .AcquireTokenForClient(TestConstants.s_scope)
                        .WithPreferredAzureRegion(true, "invalid")
                        .WithForceRefresh(forceRefresh)
                        .ExecuteAsync()
                        .ConfigureAwait(false);
                    correlationId = authResult.CorrelationId;
                    break;

                case AcquireTokenForClientOutcome.AADUnavailableError:
                    correlationId = Guid.NewGuid();

                    tokenRequestHandler = new MockHttpMessageHandler()
                    {
                        ExpectedMethod = HttpMethod.Post,
                        ResponseMessage = MockHelpers.CreateFailureMessage(
                           System.Net.HttpStatusCode.GatewayTimeout, "gateway timeout")
                    };
                    var tokenRequestHandler2 = new MockHttpMessageHandler()
                    {
                        ExpectedMethod = HttpMethod.Post,
                        ResponseMessage = MockHelpers.CreateFailureMessage(
                         System.Net.HttpStatusCode.GatewayTimeout, "gateway timeout")
                    };

                    // 2 of these are needed because MSAL has a "retry once" policy for 5xx errors
                    _harness.HttpManager.AddMockHandler(tokenRequestHandler2);
                    _harness.HttpManager.AddMockHandler(tokenRequestHandler);

                    var serviceEx = await AssertException.TaskThrowsAsync<MsalServiceException>(() =>
                        _app
                        .AcquireTokenForClient(TestConstants.s_scope)
                        .WithPreferredAzureRegion(true)
                        .WithForceRefresh(true)
                        .WithCorrelationId(correlationId)
                        .ExecuteAsync())
                        .ConfigureAwait(false);

                    break;

                default:
                    throw new NotImplementedException();
            }

            Assert.AreEqual(0, _harness.HttpManager.QueueSize);

            return (tokenRequestHandler?.ActualRequestMessage, correlationId);
        }

        private static void AssertCurrentTelemetry(
            HttpRequestMessage requestMessage,
            ApiIds apiId,
            bool forceRefresh,
            string regionSource,
            bool isCacheSerialized = false,
            string userProvidedRegion = "",
            string isvalidUserProvidedRegion = "")
        {
            string actualCurrentTelemetry = requestMessage.Headers.GetValues(
                TelemetryConstants.XClientCurrentTelemetry).Single();

            var actualTelemetryParts = actualCurrentTelemetry.Split('|');
            Assert.AreEqual(3, actualTelemetryParts.Length);

            Assert.AreEqual(TelemetryConstants.HttpTelemetrySchemaVersion2, actualTelemetryParts[0]); // version

            Assert.AreEqual(
                ((int)apiId).ToString(CultureInfo.InvariantCulture),
                actualTelemetryParts[1].Split(',')[0]); // current_api_id

            Assert.IsTrue(actualTelemetryParts[1].EndsWith(forceRefresh ? "1" : "0")); // force_refresh flag

            string[] platformConfig = actualTelemetryParts[2].Split(',');
            Assert.AreEqual(isCacheSerialized ? "1" : "0", platformConfig[2]);
            Assert.AreEqual(TestConstants.Region, platformConfig[0]);
            Assert.AreEqual(regionSource, platformConfig[1]);
            Assert.AreEqual(userProvidedRegion, platformConfig[3]);
            Assert.AreEqual(isvalidUserProvidedRegion, platformConfig[4]);
        }

        private static void AssertPreviousTelemetry(
           HttpRequestMessage requestMessage,
           int expectedSilentCount,
           ApiIds[] expectedFailedApiIds = null,
           Guid[] expectedCorrelationIds = null,
           string[] expectedErrors = null,
           string[] expectedRegions = null,
           string[] expectedRegionSources = null)
        {
            expectedFailedApiIds = expectedFailedApiIds ?? new ApiIds[0];
            expectedCorrelationIds = expectedCorrelationIds ?? new Guid[0];
            expectedErrors = expectedErrors ?? new string[0];
            expectedRegions = expectedRegions ?? new string[0];
            expectedRegionSources = expectedRegionSources ?? new string[0];

            var actualHeader = ParseLastRequestHeader(requestMessage);

            Assert.AreEqual(expectedSilentCount, actualHeader.SilentCount);
            CoreAssert.AreEqual(actualHeader.FailedApis.Length, actualHeader.CorrelationIds.Length, actualHeader.Errors.Length);

            CollectionAssert.AreEqual(
                expectedFailedApiIds.Select(apiId => ((int)apiId).ToString(CultureInfo.InvariantCulture)).ToArray(),
                actualHeader.FailedApis);

            CollectionAssert.AreEqual(
                expectedCorrelationIds.Select(g => g.ToString()).ToArray(),
                actualHeader.CorrelationIds);

            CollectionAssert.AreEqual(
                expectedErrors,
                actualHeader.Errors);

            CollectionAssert.AreEqual(
                expectedRegions,
                actualHeader.Regions);

            CollectionAssert.AreEqual(
                expectedRegionSources,
                actualHeader.RegionSources);
        }

        private static (int SilentCount, string[] FailedApis, string[] CorrelationIds, string[] Errors, string[] Regions, string[] RegionSources) ParseLastRequestHeader(HttpRequestMessage requestMessage)
        {
            // schema_version | silent_succesful_count | failed_requests | errors | platform_fields
            // where a failed_request is "api_id, correlation_id"
            string lastTelemetryHeader = requestMessage.Headers.GetValues(
               TelemetryConstants.XClientLastTelemetry).Single();
            var lastRequestParts = lastTelemetryHeader.Split('|');

            Assert.AreEqual(5, lastRequestParts.Length); //  2 | 1 | | |
            Assert.AreEqual(TelemetryConstants.HttpTelemetrySchemaVersion2, lastRequestParts[0]); // version

            int actualSuccessfullSilentCount = int.Parse(lastRequestParts[1], CultureInfo.InvariantCulture);

            string[] actualFailedApiIds = lastRequestParts[2]
                .Split(',')
                .Where((item, index) => index % 2 == 0)
                .Where(it => !string.IsNullOrEmpty(it))
                .ToArray();
            string[] correlationIds = lastRequestParts[2]
                .Split(',')
                .Where((item, index) => index % 2 != 0)
                .Where(it => !string.IsNullOrEmpty(it))
                .ToArray();

            string[] actualErrors = lastRequestParts[3]
                .Split(',')
                .Where(it => !string.IsNullOrEmpty(it))
                .ToArray();

            string[] regions = lastRequestParts[4]
                .Split(',')
                .Where((item, index) => index % 2 == 0)
                .Where(it => !string.IsNullOrEmpty(it))
                .ToArray();
            string[] regionSources = lastRequestParts[4]
                .Split(',')
                .Where((item, index) => index % 2 != 0)
                .Where(it => !string.IsNullOrEmpty(it))
                .ToArray();

            return (actualSuccessfullSilentCount, actualFailedApiIds, correlationIds, actualErrors, regions, regionSources);
        }
    }
}
