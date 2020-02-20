// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed;
using BuildXL.Cache.ContentStore.Distributed.NuCache;
using BuildXL.Cache.ContentStore.Distributed.NuCache.EventStreaming;
using BuildXL.Cache.ContentStore.Distributed.Redis;
using BuildXL.Cache.ContentStore.Distributed.Sessions;
using BuildXL.Cache.ContentStore.Distributed.Stores;
using BuildXL.Cache.ContentStore.Extensions;
using BuildXL.Cache.ContentStore.FileSystem;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Secrets;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.InterfacesTest.Results;
using BuildXL.Cache.ContentStore.Service;
using BuildXL.Cache.ContentStore.Stores;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Cache.Host.Configuration;
using BuildXL.Cache.Host.Service;
using BuildXL.Cache.Host.Service.Internal;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Tracing;
using ContentStoreTest.Distributed.ContentLocation;
using ContentStoreTest.Distributed.Redis;
using ContentStoreTest.Test;
using FluentAssertions;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Test.BuildXL.TestUtilities.Xunit;
using Xunit;
using Xunit.Abstractions;
using AbsolutePath = BuildXL.Cache.ContentStore.Interfaces.FileSystem.AbsolutePath;
using OperationContext = BuildXL.Cache.ContentStore.Tracing.Internal.OperationContext;

namespace ContentStoreTest.Distributed.Sessions
{
    [Trait("Category", "Integration")]
    [Trait("Category", "LongRunningTest")]
    [Collection("Redis-based tests")]
    public partial class LocalLocationStoreDistributedContentTests : DistributedContentTests
    {
        private readonly LocalRedisFixture _redis;
        protected const HashType ContentHashType = HashType.Vso0;
        protected const int ContentByteCount = 100;
        protected const int SafeToLazilyUpdateMachineCountThreshold = 3;
        protected const int ReplicaCreditInMinutes = 3;
        private const int InfiniteHeartbeatMinutes = 10_000;

        protected bool _enableSecondaryRedis = false;
        protected bool _poolSecondaryRedisDatabase = true;
        protected bool _registerAdditionalLocationPerMachine = false;

        private readonly ConcurrentDictionary<(Guid, int), LocalRedisProcessDatabase> _localDatabases = new ConcurrentDictionary<(Guid, int), LocalRedisProcessDatabase>();

        private readonly Dictionary<int, RedisContentLocationStoreConfiguration> _configurations
            = new Dictionary<int, RedisContentLocationStoreConfiguration>();

        private Func<AbsolutePath, int, RedisContentLocationStoreConfiguration> CreateContentLocationStoreConfiguration { get; set; }
        private LocalRedisProcessDatabase _primaryGlobalStoreDatabase;
        private LocalRedisProcessDatabase _secondaryGlobalStoreDatabase;

        /// <nodoc />
        public LocalLocationStoreDistributedContentTests(LocalRedisFixture redis, ITestOutputHelper output)
            : base(output)
        {
            _redis = redis;
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            foreach (var database in _localDatabases.Values)
            {
                database.Dispose();
            }

            if (!_poolSecondaryRedisDatabase && !_secondaryGlobalStoreDatabase.Closed)
            {
                _secondaryGlobalStoreDatabase.Dispose(close: true);
            }

            base.Dispose(disposing);
        }

        /// <nodoc />
        protected bool EnableProactiveCopy { get; set; } = false;
        protected bool PushProactiveCopies { get; set; } = false;
        protected bool EnableProactiveReplication { get; set; } = false;
        protected bool ProactiveCopyOnPuts { get; set; } = true;
        protected bool ProactiveCopyOnPins { get; set; } = false;
        protected bool ProactiveCopyUsePreferredLocations { get; set; } = false;

        protected bool UseRealEventHubAndStorage { get; set; } = false;

        private Action<DistributedContentSettings> _overrideDistributed = null;
        private Action<RedisContentLocationStoreConfiguration> _overrideRedis = null;

        private MemoryContentLocationEventStoreConfiguration MemoryEventStoreConfiguration { get; } = new MemoryContentLocationEventStoreConfiguration();

        protected TestHost Host { get; } = new TestHost();

        private class TestHostOverrides : DistributedCacheServiceHostOverrides
        {
            private readonly LocalLocationStoreDistributedContentTests _tests;
            private readonly int _storeIndex;

            public TestHostOverrides(LocalLocationStoreDistributedContentTests tests, int storeIndex)
            {
                _tests = tests;
                _storeIndex = storeIndex;
            }

            public override IClock Clock => _tests.TestClock;

            public override void Override(RedisContentLocationStoreConfiguration configuration)
            {
                configuration.InlinePostInitialization = true;

                // Set recompute time to zero to force recomputation on every heartbeat
                configuration.RecomputeInactiveMachinesExpiry = TimeSpan.Zero;

                if (!_tests.UseRealEventHubAndStorage)
                {
                    configuration.CentralStore = new LocalDiskCentralStoreConfiguration(_tests.TestRootDirectoryPath / "centralStore", "checkpoints-key");

                    // Propagate epoch from normal configuration to in-memory configuration
                    _tests.MemoryEventStoreConfiguration.Epoch = configuration.EventStore.Epoch;
                    configuration.EventStore = _tests.MemoryEventStoreConfiguration;
                }

                _tests._overrideRedis?.Invoke(configuration);

                _tests._configurations[_storeIndex] = configuration;
            }

            public override void Override(DistributedContentStoreSettings settings)
            {
                settings.InlinePutBlobs = true;
                settings.InlineProactiveCopies = true;
                settings.InlineProactiveReplication = true;
                settings.SetPostInitializationCompletionAfterStartup = true;
            }
        }

        protected class TestHost : IDistributedCacheServiceHost
        {
            private readonly Dictionary<string, string> _secrets = new Dictionary<string, string>();

            public string StoreSecret(string key, string value)
            {
                _secrets[key] = value;
                return key;
            }

            public string GetSecretStoreValue(string key)
            {
                return _secrets[key];
            }

            public Task<Dictionary<string, Secret>> RetrieveSecretsAsync(List<RetrieveSecretsRequest> requests, CancellationToken token)
            {
                return Task.FromResult(requests.ToDictionary(r => r.Name, r => (Secret)new PlainTextSecret(_secrets[r.Name])));
            }

            public void OnStartedService() { }
            public Task OnStartingServiceAsync() => Task.CompletedTask;
            public void OnTeardownCompleted() { }
        }

        protected override (IContentStore store, IStartupShutdown server) CreateStore(
            Context context,
            IAbsolutePathFileCopier fileCopier,
            DisposableDirectory testDirectory,
            int index,
            bool enableDistributedEviction,
            int? replicaCreditInMinutes,
            bool enableRepairHandling,
            uint grpcPort,
            object additionalArgs)
        {
            var rootPath = testDirectory.Path / "Root";
            var configurationModel = new ConfigurationModel(Config);
            var pathTransformer = new TestPathTransformer();

            int dbIndex = 0;
            var localDatabase = GetDatabase(context, ref dbIndex);
            var localMachineDatabase = GetDatabase(context, ref dbIndex);
            _primaryGlobalStoreDatabase = GetDatabase(context, ref dbIndex);
            if (_enableSecondaryRedis)
            {
                _secondaryGlobalStoreDatabase = GetDatabase(context, ref dbIndex, _poolSecondaryRedisDatabase);
            }

            var settings = new DistributedContentSettings()
            {
                ConnectionSecretNamesMap = new Dictionary<string, RedisContentSecretNames>()
                {
                    {
                        ".*",
                        new RedisContentSecretNames(
                            redisContentSecretName: Host.StoreSecret("ContentRedis", localDatabase.ConnectionString),
                            redisMachineLocationsSecretName: Host.StoreSecret("MachineRedis", localDatabase.ConnectionString))
                    }
                },
                IsDistributedContentEnabled = true,
                KeySpacePrefix = "TestPrefix",

                // By default, only first store is master eligible
                IsMasterEligible = index == 0,

                GlobalRedisSecretName = Host.StoreSecret("PrimaryRedis", _primaryGlobalStoreDatabase.ConnectionString),
                SecondaryGlobalRedisSecretName = _enableSecondaryRedis ? Host.StoreSecret("SecondaryRedis", _secondaryGlobalStoreDatabase.ConnectionString) : null,

                // Specify event hub and storage secrets even thoug they are not used in tests to satisfy DistributedContentStoreFactory
                EventHubSecretName = Host.StoreSecret("EventHub_Unspecified", "Unused"),
                AzureStorageSecretName = Host.StoreSecret("Storage_Unspecified", "Unused"),

                IsContentLocationDatabaseEnabled = true,
                ContentLocationReadMode = nameof(ContentLocationMode.Redis),
                ContentLocationWriteMode = nameof(ContentLocationMode.Both),
                UseDistributedCentralStorage = true,
                ContentHashBumpTimeMinutes = 60,
                MachineExpiryMinutes = 10,
                IsDistributedEvictionEnabled = true,

                SafeToLazilyUpdateMachineCountThreshold = SafeToLazilyUpdateMachineCountThreshold,

                RestoreCheckpointIntervalMinutes = 1,
                CreateCheckpointIntervalMinutes = 1,
                HeartbeatIntervalMinutes = InfiniteHeartbeatMinutes,

                RetryIntervalForCopiesMs = DistributedContentSessionTests.DefaultRetryIntervalsForTest.Select(t => (int)t.TotalMilliseconds).ToArray(),

                RedisBatchPageSize = 1,
                CheckLocalFiles = true,

                // Tests disable reconciliation by default
                Unsafe_DisableReconciliation = true,

                IsPinBetterEnabled = false,
                ContentAvailabilityGuarantee = ContentAvailabilityGuarantee.ToString(),
                PinCacheReplicaCreditRetentionMinutes = 30,

                // Low risk and high risk tolerance for machine or file loss to prevent pin better from kicking in
                MachineRisk = 0.0000001,
                FileRisk = 0.0000001,
                PinRisk = 0.9999,
                IsPinCachingEnabled = false,

                ProactiveCopyMode = EnableProactiveCopy ? nameof(ProactiveCopyMode.OutsideRing) : nameof(ProactiveCopyMode.Disabled),
                PushProactiveCopies = PushProactiveCopies,
                EnableProactiveReplication = EnableProactiveReplication,
                ProactiveCopyRejectOldContent = true,
                ProactiveCopyOnPut = ProactiveCopyOnPuts,
                ProactiveCopyOnPin = ProactiveCopyOnPins,
                ProactiveCopyUsePreferredLocations = ProactiveCopyUsePreferredLocations,
                IsRepairHandlingEnabled = enableRepairHandling,
            };

            _overrideDistributed?.Invoke(settings);

            var localCasSettings = new LocalCasSettings()
            {
                UseScenarioIsolation = false,
                CasClientSettings = new LocalCasClientSettings()
                {
                    UseCasService = true,
                    DefaultCacheName = "Default",
                },
                PreferredCacheDrive = Path.GetPathRoot(rootPath.Path),
                CacheSettings = new Dictionary<string, NamedCacheSettings>()
                {
                    {
                        "Default",
                        new NamedCacheSettings()
                        {
                            CacheRootPath = rootPath.Path,
                            CacheSizeQuotaString = "50MB"
                        }
                    }
                },
                ServiceSettings = new LocalCasServiceSettings()
                {
                    GrpcPort = grpcPort,
                    GrpcPortFileName = Guid.NewGuid().ToString(),
                    ScenarioName = Guid.NewGuid().ToString(),
                }
            };

            if (_registerAdditionalLocationPerMachine)
            {
                localCasSettings.CacheSettings["FakeTestCas"] = new NamedCacheSettings()
                {
                    CacheRootPath = @"\\Fake\Test\Cas\" + index,
                    CacheSizeQuotaString = "1MB"
                };
            }

            var configuration = new DistributedCacheServiceConfiguration(localCasSettings, settings);

            var arguments = new DistributedCacheServiceArguments(
                Logger,
                fileCopier,
                pathTransformer,
                (IContentCommunicationManager)fileCopier,
                Host,
                new HostInfo("TestStamp", "TestRing", capabilities: new string[0]),
                Token,
                dataRootPath: rootPath.Path,
                configuration: configuration,
                keyspace: RedisContentLocationStoreFactory.DefaultKeySpace
            );

            arguments.Overrides = new TestHostOverrides(this, index);

            if (UseGrpcServer)
            {
                var server = (ILocalContentServer<IContentStore>)new CacheServerFactory(arguments).Create();
                var store = ((MultiplexedContentStore)server.StoresByName["Default"]).PreferredContentStore;
                return (store, server);
            }
            else
            {
                var factory = new DistributedContentStoreFactory(arguments);
                var store = factory.CreateContentStore(factory.OrderedResolvedCacheSettings[0]);
                store.DisposeContentStoreFactory = true;
                return (store, null);
            }
        }

        protected bool ConfigureWithRealEventHubAndStorage(Action<DistributedContentSettings> overrideDistributed = null, Action<RedisContentLocationStoreConfiguration> overrideRedis = null)
        {
            if (!ReadConfiguration(out var storageAccountKey, out var storageAccountName, out var eventHubConnectionString, out var eventHubName))
            {
                return false;
            }

            UseRealEventHubAndStorage = true;

            ConfigureWithOneMaster(s =>
                {
                    overrideDistributed?.Invoke(s);
                    s.EventHubSecretName = Host.StoreSecret(eventHubName, eventHubConnectionString);
                    s.AzureStorageSecretName = Host.StoreSecret(storageAccountName, storageAccountKey);
                },
                overrideRedis);
            return true;
        }

        protected void ConfigureWithOneMaster(Action<DistributedContentSettings> overrideDistributed = null, Action<RedisContentLocationStoreConfiguration> overrideRedis = null)
        {
            _overrideDistributed = s =>
            {
                s.IsPinBetterEnabled = true;
                s.ContentLocationReadMode = nameof(ContentLocationMode.LocalLocationStore);
                s.ContentLocationWriteMode = nameof(ContentLocationMode.LocalLocationStore);
                s.ContentAvailabilityGuarantee = nameof(ContentAvailabilityGuarantee.RedundantFileRecordsOrCheckFileExistence);
                overrideDistributed?.Invoke(s);
            };
            _overrideRedis = overrideRedis;
        }

        private bool ReadConfiguration(out string storageAccountKey, out string storageAccountName, out string eventHubConnectionString, out string eventHubName)
        {
            storageAccountKey = Environment.GetEnvironmentVariable("TestEventHub_StorageAccountKey");
            storageAccountName = Environment.GetEnvironmentVariable("TestEventHub_StorageAccountName");
            eventHubConnectionString = Environment.GetEnvironmentVariable("TestEventHub_EventHubConnectionString");
            eventHubName = Environment.GetEnvironmentVariable("TestEventHub_EventHubName");

            if (storageAccountKey == null)
            {
                Output.WriteLine("Please specify 'TestEventHub_StorageAccountKey' to run this test");
                return false;
            }

            if (storageAccountName == null)
            {
                Output.WriteLine("Please specify 'TestEventHub_StorageAccountName' to run this test");
                return false;
            }

            if (eventHubConnectionString == null)
            {
                Output.WriteLine("Please specify 'TestEventHub_EventHubConnectionString' to run this test");
                return false;
            }

            if (eventHubName == null)
            {
                Output.WriteLine("Please specify 'TestEventHub_EventHubName' to run this test");
                return false;
            }

            Output.WriteLine("The test is configured correctly.");
            return true;
        }

        [Fact]
        public async Task RunOutOfBandAsyncStartsNewTaskIfTheCurrentOneIsCompleted()
        {
            var context = new OperationContext(new Context(Logger));
            var tracer = new Tracer("tracer");
            var locker = new object();
            Task<BoolResult> task = null;

            var operation = context.CreateOperation(tracer,
                async () =>
                {
                    await Task.Delay(1);
                    return BoolResult.Success;
                });

            Task<BoolResult> result = LocalLocationStore.RunOutOfBandAsync(inline: false, ref task, locker, operation, out var factoryWasCalled);

            result.IsCompleted.Should().BeTrue("The task should be completed synchronously.");
            task.Should().NotBeNull();
            factoryWasCalled.Should().BeTrue();

            (await task).ShouldBeSuccess();

            result = LocalLocationStore.RunOutOfBandAsync(inline: false, ref task, locker, operation, out _);
            result.IsCompleted.Should().BeTrue("The task should be completed synchronously.");
            task.Should().NotBeNull();
        }

        [Fact]
        public void RunOutOfBandAsyncWithInlineTest()
        {
            var context = new OperationContext(new Context(Logger));
            var tracer = new Tracer("tracer");
            var locker = new object();
            Task<BoolResult> task = null;

            var operation = context.CreateOperation(tracer,
                async () =>
                {
                    await Task.Delay(1);
                    return BoolResult.Success;
                });

            Task<BoolResult> result = LocalLocationStore.RunOutOfBandAsync(inline: true, ref task, locker, operation, out _);

            result.IsCompleted.Should().BeFalse("The task should not be completed synchronously.");
            task.Should().BeNull("Task is not set when inline is true");
        }

        [Fact(Skip = "Flaky test")]
        public async Task SkipRestoreCheckpointTest()
        {
            // Ensure master lease is long enough that role doesn't switch between machines
            var masterLeaseExpiryTime = TimeSpan.FromMinutes(60);
            ConfigureWithOneMaster(
                s =>
                {
                    s.RestoreCheckpointAgeThresholdMinutes = 60;
                },
                r =>
                {
                    r.Checkpoint.MasterLeaseExpiryTime = masterLeaseExpiryTime;
                });

            await RunTestAsync(
                new Context(Logger),
                2,
                iterations: 3,
                testFunc: async (TestContext context) =>
                {
                    var sessions = context.Sessions;

                    var masterStore = context.GetLocalLocationStore(context.GetMasterIndex());
                    var workerStore = context.GetLocalLocationStore(context.GetFirstWorkerIndex());

                    var workerSession = sessions[context.GetFirstWorkerIndex()];

                    // Insert random file in session 0
                    var putResult0 = await workerSession.PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();

                    await masterStore.CreateCheckpointAsync(context).ShouldBeSuccess();
                    TestClock.UtcNow += TimeSpan.FromMinutes(10);

                    // Iteration 0: No checkpoint to restore
                    // Iteration 1: Restore checkpoint created during iteration 0
                    // Iteration 2: Skip Restore checkpoint created during iteration 1
                    if (context.Iteration == 2)
                    {
                        // Should skip the restore checkpoint for startup
                        workerStore.Counters[ContentLocationStoreCounters.RestoreCheckpointsSkipped].Value.Should().Be(1);
                    }
                    else
                    {
                        if (context.Iteration == 1)
                        {
                            workerStore.Counters[ContentLocationStoreCounters.RestoreCheckpointsSucceeded].Value.Should().Be(1);
                        }

                        workerStore.Counters[ContentLocationStoreCounters.RestoreCheckpointsSkipped].Value.Should().Be(0);
                    }
                });
        }

        [Fact]
        public async Task DeleteAsyncDistributedTest()
        {
            int machineCount = 3;
            var loggingContext = new Context(Logger);
            var servers = new LocalContentServer[machineCount];
            ConfigureWithOneMaster();

            await RunTestAsync(
                loggingContext,
                machineCount,
                async context =>
                {
                    var stores = context.Stores;
                    var sessions = context.Sessions;
                    var masterIndex = context.GetMasterIndex();
                    var masterSession = sessions[masterIndex];
                    var masterLocationStore = context.GetLocationStore(masterIndex);

                    var content = ThreadSafeRandom.GetBytes((int)ContentByteCount);
                    var hashInfo = HashInfoLookup.Find(ContentHashType);
                    var contentHash = hashInfo.CreateContentHasher().GetContentHash(content);
                    var path = context.Directories[0].CreateRandomFileName();
                    FileSystem.WriteAllBytes(path, content);

                    // Put file into master session
                    var putResult = await masterSession.PutFileAsync(context, ContentHashType, path, FileRealizationMode.Any, Token).ShouldBeSuccess();

                    // Put file into each worker session
                    foreach (var workerId in context.EnumerateWorkersIndices())
                    {
                        var workerSession = context.Sessions[workerId];
                        var workerResult = await workerSession.PutFileAsync(context, ContentHashType, path, FileRealizationMode.Any, Token)
                            .ShouldBeSuccess();
                    }

                    // Create checkpoint on master, and restore checkpoint on workers
                    await UploadCheckpointOnMasterAndRestoreOnWorkers(context);

                    var masterResult = await masterLocationStore.GetBulkAsync(context, new List<ContentHash>() { putResult.ContentHash }, Token, UrgencyHint.Nominal, GetBulkOrigin.Local).ShouldBeSuccess();
                    masterResult.ContentHashesInfo[0].Locations.Count.Should().Be(machineCount);

                    // Call distributed delete of the content from worker session
                    var deleteResult = await stores[context.GetFirstWorkerIndex()].DeleteAsync(context, putResult.ContentHash, new DeleteContentOptions() { DeleteLocalOnly = false });

                    // Verify no records of machine having this content from master session
                    masterResult = await masterLocationStore.GetBulkAsync(context, new List<ContentHash>() { putResult.ContentHash }, Token, UrgencyHint.Nominal, GetBulkOrigin.Local).ShouldBeSuccess();
                    masterResult.ContentHashesInfo[0].Locations.Count.Should().Be(0);
                });
        }

        [Fact]
        public async Task ProactiveCopyDistributedTest()
        {
            EnableProactiveCopy = true;

            // Use the same context in two sessions when checking for file existence
            var loggingContext = new Context(Logger);

            var contentHashes = new List<ContentHash>();

            int machineCount = 2;
            ConfigureWithOneMaster();

            await RunTestAsync(
                loggingContext,
                machineCount,
                async context =>
                {
                    var masterStore = context.GetMaster();
                    var defaultFileSize = (Config.MaxSizeQuota.Hard / 4) + 1;

                    var sessions = context.EnumerateWorkersIndices().Select(i => context.GetDistributedSession(i)).ToArray();

                    // Insert random file #1 into worker #1
                    var putResult1 = await sessions[0].PutRandomAsync(context, HashType.Vso0, false, defaultFileSize, Token).ShouldBeSuccess();
                    var hash1 = putResult1.ContentHash;

                    var getBulkResult1 = await masterStore.GetBulkAsync(context, hash1, GetBulkOrigin.Global).ShouldBeSuccess();

                    // Proactive copy should have replicated the content.
                    getBulkResult1.ContentHashesInfo[0].Locations.Count.Should().Be(2);
                },
                implicitPin: ImplicitPin.None);
        }

        [Fact]
        public async Task PushedProactiveCopyDistributedTest()
        {
            EnableProactiveCopy = true;
            PushProactiveCopies = true;
            ProactiveCopyOnPuts = true;

            // Use the same context in two sessions when checking for file existence
            var loggingContext = new Context(Logger);

            var contentHashes = new List<ContentHash>();

            int machineCount = 3;
            ConfigureWithOneMaster();

            await RunTestAsync(
                loggingContext,
                machineCount,
                async context =>
                {
                    var masterStore = context.GetMaster();
                    var defaultFileSize = (Config.MaxSizeQuota.Hard / 4) + 1;

                    var sessions = context.EnumerateWorkersIndices().Select(i => context.GetDistributedSession(i)).ToArray();

                    // Insert random file #1 into worker #1
                    var putResult1 = await sessions[0].PutRandomAsync(context, HashType.Vso0, false, defaultFileSize, Token).ShouldBeSuccess();
                    var hash1 = putResult1.ContentHash;

                    var getBulkResult1 = await masterStore.GetBulkAsync(context, hash1, GetBulkOrigin.Global).ShouldBeSuccess();

                    // Proactive copy should have replicated the content.
                    getBulkResult1.ContentHashesInfo[0].Locations.Count.Should().Be(2);
                },
                implicitPin: ImplicitPin.None);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ProactiveReplicationTest(bool usePreferredLocations)
        {
            EnableProactiveReplication = true;
            EnableProactiveCopy = true;
            PushProactiveCopies = true;
            ProactiveCopyOnPuts = false;
            ProactiveCopyUsePreferredLocations = usePreferredLocations;
            var storeCount = 2;

            ConfigureWithOneMaster(dcs =>
            {
                dcs.RestoreCheckpointAgeThresholdMinutes = 0;
            });

            PutResult putResult = default;

            await RunTestAsync(
                new Context(Logger),
                storeCount,
                iterations: 2,
                testFunc: async context =>
                {
                    var sessions = context.Sessions;
                    var master = context.GetMasterIndex();

                    var ls = Enumerable.Range(0, storeCount).Select(n => context.GetLocationStore(n)).ToArray();
                    var lls = Enumerable.Range(0, storeCount).Select(n => context.GetLocalLocationStore(n)).ToArray();

                    if (context.Iteration == 0)
                    {
                        putResult = await sessions[1].PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();

                        // Content should be available in only one session, with proactive put set to false.
                        var masterResult = await ls[master].GetBulkAsync(context, new[] { putResult.ContentHash }, Token, UrgencyHint.Nominal, GetBulkOrigin.Local).ShouldBeSuccess();
                        masterResult.ContentHashesInfo[0].Locations.Count.Should().Be(1);
                        await ls[master].LocalLocationStore.CreateCheckpointAsync(context).ShouldBeSuccess();

                        TestClock.UtcNow += TimeSpan.FromMinutes(5);
                    }
                    else if (context.Iteration == 1)
                    {
                        var proactiveStore = (DistributedContentStore<AbsolutePath>)context.GetDistributedStore(1);
                        var proactiveSession = (await proactiveStore.ProactiveCopySession.Value).ThrowIfFailure();
                        var counters = proactiveSession.GetCounters().ToDictionaryIntegral();
                        counters["ProactiveCopy_OutsideRingFromPreferredLocations.Count"].Should().Be(usePreferredLocations ? 1 : 0);

                        // Content should be available in two sessions, due to proactive replication in second iteration.
                        var masterResult = await ls[master].GetBulkAsync(context, new[] { putResult.ContentHash }, Token, UrgencyHint.Nominal, GetBulkOrigin.Global).ShouldBeSuccess();
                        masterResult.ContentHashesInfo[0].Locations.Count.Should().Be(2);
                    }
                });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ProactivePutTest(bool usePreferredLocations)
        {
            EnableProactiveCopy = true;
            PushProactiveCopies = true;
            ProactiveCopyOnPuts = true;
            ProactiveCopyUsePreferredLocations = usePreferredLocations;

            ConfigureWithOneMaster(dcs =>
            {
                dcs.RestoreCheckpointAgeThresholdMinutes = 0;
            });

            PutResult putResult = default;

            await RunTestAsync(
                new Context(Logger),
                storeCount: 3,
                iterations: 2,
                testFunc: async context =>
                {
                    var sessions = context.Sessions;
                    var master = context.GetMasterIndex();

                    var ls = Enumerable.Range(0, 3).Select(n => context.GetLocationStore(n)).ToArray();
                    var lls = Enumerable.Range(0, 3).Select(n => context.GetLocalLocationStore(n)).ToArray();

                    if (context.Iteration == 0)
                    {
                        // Put into master to ensure it has something to checkpoint
                        await sessions[master].PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();
                        await ls[master].LocalLocationStore.CreateCheckpointAsync(context).ShouldBeSuccess();
                    }
                    if (context.Iteration == 1)
                    {
                        putResult = await sessions[1].PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();

                        // Content should be available in two sessions, because of proactive put.
                        var masterResult = await ls[master].GetBulkAsync(context, new[] { putResult.ContentHash }, Token, UrgencyHint.Nominal, GetBulkOrigin.Local).ShouldBeSuccess();
                        masterResult.ContentHashesInfo[0].Locations.Count.Should().Be(2);

                        var proactiveSession = context.GetDistributedSession(1);
                        var counters = proactiveSession.GetCounters().ToDictionaryIntegral();
                        counters["ProactiveCopy_OutsideRingFromPreferredLocations.Count"].Should().Be(usePreferredLocations ? 1 : 0);
                    }
                });
        }

        [Fact]
        public async Task ProactiveCopyEvictionRejectionTest()
        {
            EnableProactiveReplication = false;
            EnableProactiveCopy = true;
            PushProactiveCopies = true;
            ProactiveCopyOnPuts = false;
            UseGrpcServer = true;

            ConfigureWithOneMaster(dcs => dcs.TouchFrequencyMinutes = 1);

            var largeFileSize = Config.MaxSizeQuota.Hard / 2 + 1;

            await RunTestAsync(
                new Context(Logger),
                storeCount: 2,
                iterations: 1,
                implicitPin: ImplicitPin.None,
                testFunc: async context =>
                {
                    var session0 = context.GetDistributedSession(0);
                    var store0 = (DistributedContentStore<AbsolutePath>)context.GetDistributedStore(0);

                    var session1 = context.GetDistributedSession(1);
                    var store1 = (DistributedContentStore<AbsolutePath>)context.GetDistributedStore(1);

                    var putResult0 = await session0.PutRandomAsync(context, HashType.MD5, provideHash: false, size: largeFileSize, CancellationToken.None);
                    var oldHash = putResult0.ContentHash;

                    TestClock.Increment();

                    // Put a large file.
                    var putResult = await session1.PutRandomAsync(context, HashType.MD5, provideHash: false, size: largeFileSize, CancellationToken.None);

                    await UploadCheckpointOnMasterAndRestoreOnWorkers(context);

                    // Put another large file, which should trigger eviction.
                    // Last eviction should be newer than last access time of the old hash.
                    var putResult2 = await session1.PutRandomAsync(context, HashType.MD5, provideHash: false, size: largeFileSize, CancellationToken.None);

                    store1.CounterCollection[DistributedContentStore<AbsolutePath>.Counters.RejectedPushCopyCount_OlderThanEvicted].Value.Should().Be(0);
                    await session0.ProactiveCopyIfNeededAsync(context, oldHash, tryBuildRing: false, ProactiveCopyReason.Replication).ShouldBeSuccessAsync();
                    store1.CounterCollection[DistributedContentStore<AbsolutePath>.Counters.RejectedPushCopyCount_OlderThanEvicted].Value.Should().Be(1);

                    TestClock.UtcNow += TimeSpan.FromMinutes(2); // Need to increase to make checkpoints happen.

                    // Bump last access time.
                    await session0.PinAsync(context, oldHash, CancellationToken.None).ShouldBeSuccess();

                    await UploadCheckpointOnMasterAndRestoreOnWorkers(context);

                    // Copy should not be rejected.
                    store1.CounterCollection[DistributedContentStore<AbsolutePath>.Counters.RejectedPushCopyCount_OlderThanEvicted].Value.Should().Be(1);
                    await session0.ProactiveCopyIfNeededAsync(context, oldHash, tryBuildRing: false, ProactiveCopyReason.Replication).ShouldBeSuccessAsync();
                    store1.CounterCollection[DistributedContentStore<AbsolutePath>.Counters.RejectedPushCopyCount_OlderThanEvicted].Value.Should().Be(1);
                });
        }

        private LocalRedisProcessDatabase GetDatabase(Context context, ref int index, bool useDatabasePool = true)
        {
            if (!useDatabasePool)
            {
                return LocalRedisProcessDatabase.CreateAndStartEmpty(_redis, TestGlobal.Logger, SystemClock.Instance);
            }

            index++;

            if (!_localDatabases.TryGetValue((context.Id, index), out var localDatabase))
            {
                localDatabase = LocalRedisProcessDatabase.CreateAndStartEmpty(_redis, TestGlobal.Logger, SystemClock.Instance);
                _localDatabases.TryAdd((context.Id, index), localDatabase);
            }

            return localDatabase;
        }

        [Fact]
        public async Task PinCacheTests()
        {
            var startTime = TestClock.UtcNow;
            TimeSpan pinCacheTimeToLive = TimeSpan.FromMinutes(30);

            _overrideDistributed = s =>
            {
                // Enable pin better to ensure pin configuration is passed to distributed store,
                // but defaults use low risk and high risk tolerance for machine 
                // or file loss to prevent pin better from kicking in.
                s.IsPinBetterEnabled = true;
                s.ContentAvailabilityGuarantee = nameof(ContentAvailabilityGuarantee.FileRecordsExist);
                s.PinCacheReplicaCreditRetentionMinutes = (int)pinCacheTimeToLive.TotalMinutes;
                s.IsPinCachingEnabled = true;
            };

            await RunTestAsync(
                new Context(Logger),
                3,
                async context =>
                {
                    var sessions = context.Sessions;
                    var session0 = context.GetDistributedSession(0);

                    var redisStore0 = context.GetRedisStore(session0);

                    // Insert random file in session 0
                    var putResult0 = await sessions[0].PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();

                    // Pinning the file on another machine should succeed
                    await sessions[1].PinAsync(context, putResult0.ContentHash, Token).ShouldBeSuccess();

                    // Remove the location from backing content location store so that in the absence of pin caching the
                    // result of pin should be false.
                    var getBulkResult = await redisStore0.GetBulkAsync(context, new[] { putResult0.ContentHash }, Token, UrgencyHint.Nominal).ShouldBeSuccess();
                    Assert.True(getBulkResult.ContentHashesInfo[0].Locations.Count == 1);

                    await redisStore0.TrimBulkAsync(
                        context,
                        getBulkResult.ContentHashesInfo.Select(c => c.ContentHash).ToList(),
                        Token,
                        UrgencyHint.Nominal).ShouldBeSuccess();

                    // Verify no locations for the content
                    var postTrimGetBulkResult = await redisStore0.GetBulkAsync(context, new[] { putResult0.ContentHash }, Token, UrgencyHint.Nominal).ShouldBeSuccess();
                    Assert.True((postTrimGetBulkResult.ContentHashesInfo[0].Locations?.Count ?? 0) == 0);

                    // Simulate calling pin within pin cache TTL
                    TestClock.UtcNow = startTime + TimeSpan.FromMinutes(pinCacheTimeToLive.TotalMinutes * .99);

                    // Now try to pin/pin bulk again (within pin cache TTL)
                    await sessions[1].PinAsync(context.Context, putResult0.ContentHash, Token).ShouldBeSuccess();

                    var pinBulkResult1withinTtl = await sessions[1].PinAsync(context.Context, new[] { putResult0.ContentHash }, Token);
                    Assert.True((await pinBulkResult1withinTtl.Single()).Item.Succeeded);

                    // Simulate calling pin within pin cache TTL
                    TestClock.UtcNow = startTime + TimeSpan.FromMinutes(pinCacheTimeToLive.TotalMinutes * 1.01);

                    var pinResult1afterTtl = await sessions[1].PinAsync(context.Context, putResult0.ContentHash, Token);
                    Assert.False(pinResult1afterTtl.Succeeded);

                    var pinBulkResult1afterTtl = await sessions[1].PinAsync(context.Context, new[] { putResult0.ContentHash }, Token);
                    Assert.False((await pinBulkResult1afterTtl.Single()).Item.Succeeded);
                });
        }

        [Fact]
        public async Task LocalLocationStoreRedundantReconcileTest()
        {
            ConfigureWithOneMaster();

            await RunTestAsync(
                new Context(Logger),
                2,
                async context =>
                {
                    var worker = context.GetFirstWorker();

                    worker.LocalLocationStore.IsReconcileUpToDate(worker.LocalMachineId).Should().BeFalse();

                    await worker.ReconcileAsync(context).ThrowIfFailure();

                    var result = await worker.ReconcileAsync(context).ThrowIfFailure();
                    result.Value.totalLocalContentCount.Should().Be(-1, "Amount of local content should be unknown because reconcile is skipped");

                    worker.LocalLocationStore.IsReconcileUpToDate(worker.LocalMachineId).Should().BeTrue();

                    TestClock.UtcNow += LocalLocationStoreConfiguration.DefaultLocationEntryExpiry.Multiply(0.5);

                    worker.LocalLocationStore.IsReconcileUpToDate(worker.LocalMachineId).Should().BeTrue();

                    TestClock.UtcNow += LocalLocationStoreConfiguration.DefaultLocationEntryExpiry.Multiply(0.5);

                    worker.LocalLocationStore.IsReconcileUpToDate(worker.LocalMachineId).Should().BeFalse();

                    worker.LocalLocationStore.MarkReconciled(worker.LocalMachineId);

                    worker.LocalLocationStore.IsReconcileUpToDate(worker.LocalMachineId).Should().BeTrue();

                    worker.LocalLocationStore.MarkReconciled(worker.LocalMachineId, reconciled: false);

                    worker.LocalLocationStore.IsReconcileUpToDate(worker.LocalMachineId).Should().BeFalse();
                });
        }

        [Fact]
        public async Task LocalLocationStoreDistributedEvictionTest()
        {
            // Use the same context in two sessions when checking for file existence
            var loggingContext = new Context(Logger);

            var contentHashes = new List<ContentHash>();

            int machineCount = 5;
            ConfigureWithOneMaster();

            await RunTestAsync(
                loggingContext,
                machineCount,
                async context =>
                {
                    var session = context.Sessions[context.GetMasterIndex()];
                    var masterStore = context.GetMaster();

                    var defaultFileSize = (Config.MaxSizeQuota.Hard / 4) + 1;

                    // Insert random file #0 into session
                    var putResult = await session.PutRandomAsync(context, HashType.Vso0, false, defaultFileSize, Token).ShouldBeSuccess();
                    contentHashes.Add(putResult.ContentHash);

                    // Ensure first piece of content older than other content by at least the replica credit
                    TestClock.UtcNow += TimeSpan.FromMinutes(ReplicaCreditInMinutes);

                    // Put random large file #1 into session.
                    putResult = await session.PutRandomAsync(context, HashType.Vso0, false, defaultFileSize, Token).ShouldBeSuccess();
                    contentHashes.Add(putResult.ContentHash);

                    // Put random large file #2 into session.
                    putResult = await session.PutRandomAsync(context, HashType.Vso0, false, defaultFileSize, Token).ShouldBeSuccess();
                    contentHashes.Add(putResult.ContentHash);

                    // Add replicas on all workers
                    foreach (var workerId in context.EnumerateWorkersIndices())
                    {
                        var workerSession = context.Sessions[workerId];

                        // Open stream to ensure content is brought to machine
                        using (await workerSession.OpenStreamAsync(context, contentHashes[2], Token).ShouldBeSuccess().SelectResult(o => o.Stream))
                        {
                        }
                    }

                    var locationsResult = await masterStore.GetBulkAsync(
                        context,
                        contentHashes,
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Local).ShouldBeSuccess();

                    // Random file #2 should be found in all machines.
                    locationsResult.ContentHashesInfo.Count.Should().Be(3);
                    locationsResult.ContentHashesInfo[0].Locations.Count.Should().Be(1);
                    locationsResult.ContentHashesInfo[1].Locations.Count.Should().Be(1);
                    locationsResult.ContentHashesInfo[2].Locations.Count.Should().Be(machineCount);

                    // Put random large file #3 into session that will evict file #2.
                    putResult = await session.PutRandomAsync(context, HashType.Vso0, false, defaultFileSize, Token).ShouldBeSuccess();
                    contentHashes.Add(putResult.ContentHash);

                    await context.SyncAsync(context.GetMasterIndex());

                    locationsResult = await masterStore.GetBulkAsync(
                        context,
                        contentHashes,
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Local).ShouldBeSuccess();

                    // Random file #2 should have been evicted from master.
                    locationsResult.ContentHashesInfo.Count.Should().Be(4);
                    locationsResult.ContentHashesInfo[0].Locations.Should().NotBeEmpty();
                    locationsResult.ContentHashesInfo[1].Locations.Should().NotBeEmpty();
                    locationsResult.ContentHashesInfo[2].Locations.Count.Should().Be(machineCount - 1, "Master should have evicted newer content because effective age due to replicas was older than other content");
                    locationsResult.ContentHashesInfo[3].Locations.Should().NotBeEmpty();
                },
                implicitPin: ImplicitPin.None,
                enableDistributedEviction: true);
        }

        [Fact]
        public async Task RegisterLocalLocationToGlobalRedisTest()
        {
            ConfigureWithOneMaster();

            await RunTestAsync(
                new Context(Logger),
                3,
                async context =>
                {
                    var store0 = context.GetLocationStore(0);
                    var store1 = context.GetLocationStore(1);

                    var hash = ContentHash.Random();

                    // Add to store 0
                    await store0.RegisterLocalLocationAsync(context, new[] { new ContentHashWithSize(hash, 120) }, Token, UrgencyHint.Nominal, touch: true).ShouldBeSuccess();

                    // Result should be available from store 1 as a global result
                    var globalResult = await store1.GetBulkAsync(context, new[] { hash }, Token, UrgencyHint.Nominal, GetBulkOrigin.Global).ShouldBeSuccess();
                    globalResult.ContentHashesInfo[0].Locations.Should().NotBeNullOrEmpty();

                    var redisStore0 = (RedisGlobalStore)store0.LocalLocationStore.GlobalStore;

                    int registerContentCount = 5;
                    int registerMachineCount = 300;
                    HashSet<MachineId> ids = new HashSet<MachineId>();
                    List<MachineLocation> locations = new List<MachineLocation>();
                    List<ContentHashWithSize> content = Enumerable.Range(0, 40).Select(i => RandomContentWithSize()).ToList();

                    content.Add(new ContentHashWithSize(ContentHash.Random(), -1));

                    var contentLocationIdLists = new ConcurrentDictionary<ContentHash, HashSet<MachineId>>();

                    for (int i = 0; i < registerMachineCount; i++)
                    {
                        var location = new MachineLocation((TestRootDirectoryPath / "redis" / i.ToString()).ToString());
                        locations.Add(location);
                        var mapping = await redisStore0.RegisterMachineAsync(context, location);
                        var id = mapping.Id;
                        ids.Should().NotContain(id);
                        ids.Add(id);

                        List<ContentHashWithSize> machineContent = Enumerable.Range(0, registerContentCount)
                            .Select(_ => content[ThreadSafeRandom.Generator.Next(content.Count)]).ToList();

                        await redisStore0.RegisterLocationAsync(context, id, machineContent).ShouldBeSuccess();

                        foreach (var item in machineContent)
                        {
                            var locationIds = contentLocationIdLists.GetOrAdd(item.Hash, new HashSet<MachineId>());
                            locationIds.Add(id);
                        }

                        var getBulkResult = await redisStore0.GetBulkAsync(context, machineContent.SelectList(c => c.Hash)).ShouldBeSuccess();
                        IReadOnlyList<ContentLocationEntry> entries = getBulkResult.Value;

                        entries.Count.Should().Be(machineContent.Count);
                        for (int j = 0; j < entries.Count; j++)
                        {
                            var entry = entries[j];
                            var hashAndSize = machineContent[j];
                            entry.ContentSize.Should().Be(hashAndSize.Size);
                            entry.Locations[id].Should().BeTrue();
                        }
                    }

                    foreach (var page in content.GetPages(10))
                    {
                        var globalGetBulkResult = await store1.GetBulkAsync(context, page.SelectList(c => c.Hash), Token, UrgencyHint.Nominal, GetBulkOrigin.Global).ShouldBeSuccess();

                        var redisGetBulkResult = await redisStore0.GetBulkAsync(context, page.SelectList(c => c.Hash)).ShouldBeSuccess();

                        var infos = globalGetBulkResult.ContentHashesInfo;
                        var entries = redisGetBulkResult.Value;

                        for (int i = 0; i < page.Count; i++)
                        {
                            ContentHashWithSizeAndLocations info = infos[i];
                            ContentLocationEntry entry = entries[i];

                            context.Context.Debug($"Hash: {info.ContentHash}, Size: {info.Size}, LocCount: {info.Locations?.Count}");

                            info.ContentHash.Should().Be(page[i].Hash);
                            info.Size.Should().Be(page[i].Size);
                            entry.ContentSize.Should().Be(page[i].Size);

                            if (contentLocationIdLists.ContainsKey(info.ContentHash))
                            {
                                var locationIdList = contentLocationIdLists[info.ContentHash];
                                entry.Locations.Should().BeEquivalentTo(locationIdList);
                                entry.Locations.Should().HaveSameCount(locationIdList);
                                info.Locations.Should().HaveSameCount(locationIdList);

                            }
                            else
                            {
                                info.Locations.Should().BeNullOrEmpty();
                            }
                        }
                    }
                });
        }

        private ContentHashWithSize RandomContentWithSize()
        {
            var maxValue = 1L << ThreadSafeRandom.Generator.Next(1, 63);
            var factor = ThreadSafeRandom.Generator.NextDouble();
            long size = (long)(factor * maxValue);

            return new ContentHashWithSize(ContentHash.Random(), size);
        }

        [Fact]
        public async Task LazyAddForHighlyReplicatedContentTest()
        {
            ConfigureWithOneMaster();

            await RunTestAsync(
                new Context(Logger),
                SafeToLazilyUpdateMachineCountThreshold + 1,
                async context =>
                {
                    var master = context.GetMaster();

                    var hash = ContentHash.Random();
                    var hashes = new[] { new ContentHashWithSize(hash, 120) };

                    foreach (var workerStore in context.EnumerateWorkers())
                    {
                        // Add to store
                        await workerStore.RegisterLocalLocationAsync(context, hashes, Token, UrgencyHint.Nominal, touch: true).ShouldBeSuccess();
                        workerStore.LocalLocationStore.Counters[ContentLocationStoreCounters.LocationAddQueued].Value.Should().Be(0);
                        workerStore.LocalLocationStore.GlobalStore.Counters[GlobalStoreCounters.RegisterLocalLocation].Value.Should().Be(1);
                    }

                    await master.RegisterLocalLocationAsync(context, hashes, Token, UrgencyHint.Nominal, touch: true).ShouldBeSuccess();

                    master.LocalLocationStore.Counters[ContentLocationStoreCounters.LocationAddQueued].Value.Should().Be(1,
                        "When number of replicas is over limit location adds should be set through event stream but not eagerly sent to redis");

                    master.LocalLocationStore.GlobalStore.Counters[GlobalStoreCounters.RegisterLocalLocation].Value.Should().Be(0);
                });
        }

        [Fact]
        public async Task TestEvictionBelowMinimumAge()
        {
            ConfigureWithOneMaster(s => s.Unsafe_DisableReconciliation = false);

            await RunTestAsync(
                new Context(Logger),
                storeCount: 1,
                async context =>
                {
                    var master = context.GetMaster();
                    var hashes = new ContentHashWithLastAccessTimeAndReplicaCount[]
                                 {
                                     new ContentHashWithLastAccessTimeAndReplicaCount(ContentHash.Random(), TestClock.UtcNow)
                                 };
                    var lruHashes = master.GetHashesInEvictionOrder(context, hashes).ToList();
                    master.LocalLocationStore.Counters[ContentLocationStoreCounters.EvictionMinAge].Value.Should().Be(expected: 0);

                    _configurations[0].EvictionMinAge = TimeSpan.FromHours(1);
                    lruHashes = master.GetHashesInEvictionOrder(context, hashes).ToList();
                    master.LocalLocationStore.Counters[ContentLocationStoreCounters.EvictionMinAge].Value.Should().Be(expected: 1);

                    await Task.Yield();
                });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task TestGetHashesInEvictionOrder(bool reverse)
        {
            _overrideDistributed = s => s.Unsafe_DisableReconciliation = false;
            ConfigureWithOneMaster();

            await RunTestAsync(
                new Context(Logger),
                2,
                async context =>
                {
                    var master = context.GetMaster();

                    int count = 10000;
                    var hashes = Enumerable.Range(0, count).Select(i => (delay: count - i, hash: ContentHash.Random()))
                        .Select(
                            c => new ContentHashWithLastAccessTimeAndReplicaCount(
                                c.hash,
                                DateTime.Now + TimeSpan.FromSeconds(2 * c.delay)))
                        .ToList();

                    if (reverse)
                    {
                        hashes = hashes.OrderBy(h => h.LastAccessTime).ToList();
                    }

                    var orderedHashes = master.GetHashesInEvictionOrder(context, hashes, reverse).ToList();

                    var visitedHashes = new HashSet<ContentHash>();
                    TimeSpan? lastAge = null;
                    var ascendingAges = 0;
                    var descendingAges = 0;
                    // All the hashes should be unique
                    foreach (var hash in orderedHashes)
                    {
                        if (lastAge != null)
                        {
                            if (lastAge < hash.EffectiveAge)
                            {
                                ascendingAges++;
                            }
                            else
                            {
                                descendingAges++;
                            }
                        }

                        lastAge = hash.EffectiveAge;
                        visitedHashes.Add(hash.ContentHash).Should().BeTrue();
                    }

                    // GetLruPages returns not a fully ordered entries. Instead, it sporadically shufles some of them.
                    // This makes impossible to assert here that the result is fully sorted.
                    // Because of this, we allow for some error.
                    var threshold = (int)(count * 0.99);
                    if (reverse)
                    {
                        ascendingAges.Should().BeGreaterThan(threshold);
                    }
                    else
                    {
                        descendingAges.Should().BeGreaterThan(threshold);
                    }

                    await Task.Yield();
                });
        }


        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ReconciliationTest(bool slowReconciliation)
        {
            var removeCount = 100;
            var addCount = 10;
            _enableSecondaryRedis = false;

            var reconciliationMaxCycleSize = 50;

            ConfigureWithOneMaster(s =>
            {
                s.Unsafe_DisableReconciliation = false;
                if (slowReconciliation)
                {
                    s.ReconciliationMaxCycleSize = reconciliationMaxCycleSize;
                    s.ReconciliationCycleFrequencyMinutes = 1;
                }
            },
            r =>
            {
                r.AllowSkipReconciliation = false;

                if (slowReconciliation)
                {
                    // Verify that configuration propagated and change it to 1ms rather than 1 minute
                    // for the sake of test speed
                    r.ReconciliationCycleFrequency.Should().Be(TimeSpan.FromMinutes(1));
                    r.ReconciliationCycleFrequency = TimeSpan.FromMilliseconds(1);
                }
            });

            ThreadSafeRandom.SetSeed(1);

            var addedHashes = new List<ContentHashWithSize>();
            var retainedHashes = new List<ContentHashWithSize>();
            var removedHashes = Enumerable.Range(0, removeCount).Select(i => new ContentHashWithSize(ContentHash.Random(), 120)).OrderBy(h => h.Hash).ToList();

            await RunTestAsync(
                new Context(Logger),
                2,
                testFunc: async context =>
                {
                    var master = context.GetMaster();
                    var worker = context.GetFirstWorker();
                    var workerId = worker.LocalMachineId;

                    var workerSession = context.Sessions[context.GetFirstWorkerIndex()];

                    for (int i = 0; i < addCount; i++)
                    {
                        var putResult = await workerSession.PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();
                        addedHashes.Add(new ContentHashWithSize(putResult.ContentHash, putResult.ContentSize));
                    }

                    for (int i = 0; i < 10; i++)
                    {
                        var putResult = await workerSession.PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();
                        retainedHashes.Add(new ContentHashWithSize(putResult.ContentHash, putResult.ContentSize));
                    }

                    foreach (var removedHash in removedHashes)
                    {
                        // Add hashes to master db that are not present on the worker so during reconciliation remove events will be sent to master for these hashes
                        master.LocalLocationStore.Database.LocationAdded(context, removedHash.Hash, workerId, removedHash.Size);
                        HasLocation(master.LocalLocationStore.Database, context, removedHash.Hash, workerId, removedHash.Size).Should()
                            .BeTrue();
                    }

                    foreach (var addedHash in addedHashes)
                    {
                        // Remove hashes from master db that ARE present on the worker so during reconciliation add events will be sent to master for these hashes
                        master.LocalLocationStore.Database.LocationRemoved(context, addedHash.Hash, workerId);
                        HasLocation(master.LocalLocationStore.Database, context, addedHash.Hash, workerId, addedHash.Size).Should()
                            .BeFalse();
                    }

                    await UploadCheckpointOnMasterAndRestoreOnWorkers(context, reconcile: true);

                    if (slowReconciliation)
                    {
                        var expectedCycles = ((removeCount + addCount) / reconciliationMaxCycleSize) + 2;
                        worker.LocalLocationStore.Counters[ContentLocationStoreCounters.ReconciliationCycles].Value.Should().Be(expectedCycles);
                    }

                    int removedIndex = 0;
                    foreach (var removedHash in removedHashes)
                    {
                        HasLocation(master.LocalLocationStore.Database, context, removedHash.Hash, workerId, removedHash.Size).Should()
                            .BeFalse($"Index={removedIndex}, Hash={removedHash}");
                        removedIndex++;
                    }

                    foreach (var addedHash in addedHashes.Concat(retainedHashes))
                    {
                        HasLocation(master.LocalLocationStore.Database, context, addedHash.Hash, workerId, addedHash.Size).Should()
                            .BeTrue(addedHash.ToString());
                    }
                });
        }

        private static bool HasLocation(ContentLocationDatabase db, BuildXL.Cache.ContentStore.Tracing.Internal.OperationContext context, ContentHash hash, MachineId machine, long size)
        {
            if (!db.TryGetEntry(context, hash, out var entry))
            {
                return false;
            }

            entry.ContentSize.Should().Be(size);

            return entry.Locations[machine.Index];
        }

        [Fact]
        public async Task CopyFileWithCancellation()
        {
            ConfigureWithOneMaster();
            await RunTestAsync(new Context(Logger), 3, async context =>
            {
                var sessions = context.Sessions;

                // Insert random file in session 0
                var randomBytes1 = ThreadSafeRandom.GetBytes(0x40);
                var worker = context.GetFirstWorkerIndex();
                var putResult1 = await sessions[worker].PutStreamAsync(context, HashType.Vso0, new MemoryStream(randomBytes1), Token).ShouldBeSuccess();

                // Ensure both files are downloaded to session 2
                var cts = new CancellationTokenSource();
                cts.Cancel();
                var master = context.GetMasterIndex();
                OpenStreamResult openResult = await sessions[master].OpenStreamAsync(context, putResult1.ContentHash, cts.Token);
                openResult.ShouldBeCancelled();
            });
        }

        [Fact]
        public async Task SkipRedundantTouchAndAddTest()
        {
            ConfigureWithOneMaster();

            await RunTestAsync(
                new Context(Logger),
                3,
                async context =>
                {
                    var workerStore = context.GetFirstWorker();

                    var hash = ContentHash.Random();
                    var hashes = new[] { new ContentHashWithSize(hash, 120) };
                    // Add to store
                    await workerStore.RegisterLocalLocationAsync(context, hashes, Token, UrgencyHint.Nominal, touch: true).ShouldBeSuccess();

                    // Redundant add should not be sent
                    await workerStore.RegisterLocalLocationAsync(context, hashes, Token, UrgencyHint.Nominal, touch: true).ShouldBeSuccess();

                    workerStore.LocalLocationStore.EventStore.Counters[ContentLocationEventStoreCounters.PublishAddLocations].Value.Should().Be(1);
                    workerStore.LocalLocationStore.EventStore.Counters[ContentLocationEventStoreCounters.PublishTouchLocations].Value.Should().Be(0);

                    await workerStore.TouchBulkAsync(context, hashes, Token, UrgencyHint.Nominal).ShouldBeSuccess();

                    // Touch after register local should not touch the content since it will be viewed as recently touched
                    workerStore.LocalLocationStore.EventStore.Counters[ContentLocationEventStoreCounters.PublishTouchLocations].Value.Should().Be(0);

                    TestClock.UtcNow += TimeSpan.FromDays(1);

                    await workerStore.TouchBulkAsync(context, hashes, Token, UrgencyHint.Nominal).ShouldBeSuccess();

                    // Touch after touch frequency should touch the content again
                    workerStore.LocalLocationStore.EventStore.Counters[ContentLocationEventStoreCounters.PublishTouchLocations].Value.Should().Be(1);

                    // After time interval the redundant add should be sent again (this operates as a touch of sorts)
                    await workerStore.RegisterLocalLocationAsync(context, hashes, Token, UrgencyHint.Nominal, touch: true).ShouldBeSuccess();
                    workerStore.LocalLocationStore.EventStore.Counters[ContentLocationEventStoreCounters.PublishAddLocations].Value.Should().Be(2);
                });
        }

        [Theory]
        [InlineData(MachineReputation.Bad)]
        [InlineData(MachineReputation.Missing)]
        [InlineData(MachineReputation.Timeout)]
        public async Task ReputationTrackerTests(MachineReputation badReputation)
        {
            await RunTestAsync(
                new Context(Logger),
                3,
                async context =>
                {
                    var sessions = context.Sessions;
                    var session0 = context.GetDistributedSession(0);

                    var redisStore0 = context.GetRedisStore(session0);

                    string content = "MyContent";
                    // Inserting the content into session 0
                    var putResult0 = await sessions[0].PutContentAsync(context, content).ShouldBeSuccess();

                    // Inserting the content into sessions 1 and 2
                    await sessions[1].PutContentAsync(context, content).ShouldBeSuccess();
                    await sessions[2].PutContentAsync(context, content).ShouldBeSuccess();

                    var getBulkResult = await redisStore0.GetBulkAsync(context, new[] { putResult0.ContentHash }, Token, UrgencyHint.Nominal).ShouldBeSuccess();
                    Assert.Equal(3, getBulkResult.ContentHashesInfo[0].Locations.Count);

                    var firstLocation = getBulkResult.ContentHashesInfo[0].Locations[0];
                    var reputation = redisStore0.MachineReputationTracker.GetReputation(firstLocation);
                    Assert.Equal(MachineReputation.Good, reputation);

                    // Changing the reputation
                    redisStore0.MachineReputationTracker.ReportReputation(firstLocation, badReputation);
                    reputation = redisStore0.MachineReputationTracker.GetReputation(firstLocation);
                    Assert.Equal(badReputation, reputation);

                    getBulkResult = await redisStore0.GetBulkAsync(context, new[] { putResult0.ContentHash }, Token, UrgencyHint.Nominal).ShouldBeSuccess();
                    Assert.Equal(3, getBulkResult.ContentHashesInfo[0].Locations.Count);

                    // Location of the machine with bad reputation should be the last one in the list.
                    Assert.Equal(firstLocation, getBulkResult.ContentHashesInfo[0].Locations[2]);

                    // Causing reputation to expire
                    TestClock.UtcNow += TimeSpan.FromHours(1);

                    reputation = redisStore0.MachineReputationTracker.GetReputation(firstLocation);
                    Assert.Equal(MachineReputation.Good, reputation);
                });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public virtual async Task MultiLevelContentLocationStoreDatabasePinTests(bool usePinBulk)
        {
            ConfigureWithOneMaster();
            int storeCount = 3;

            await RunTestAsync(
                new Context(Logger),
                storeCount,
                async context =>
                {
                    var sessions = context.Sessions;

                    var workerStore = context.GetFirstWorker();
                    var firstWorkerIndex = context.GetFirstWorkerIndex();

                    var masterStore = context.GetMaster();

                    // Insert random file in a worker session
                    var putResult0 = await sessions[firstWorkerIndex].PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();

                    // Content SHOULD NOT be registered locally since it has not been queried
                    var localGetBulkResult1a = await workerStore.GetBulkAsync(context, new[] { putResult0.ContentHash }, Token, UrgencyHint.Nominal, GetBulkOrigin.Local).ShouldBeSuccess();
                    localGetBulkResult1a.ContentHashesInfo[0].Locations.Should().BeNullOrEmpty();

                    for (int sessionIndex = 0; sessionIndex < storeCount; sessionIndex++)
                    {
                        // Pin the content in the session which should succeed
                        await PinContentForSession(putResult0.ContentHash, sessionIndex).ShouldBeSuccess();
                    }

                    await workerStore.TrimBulkAsync(
                        context,
                        new[] { putResult0.ContentHash },
                        Token,
                        UrgencyHint.Nominal).ShouldBeSuccess();

                    // Verify no locations for the content on master local db after receiving trim event
                    var postTrimGetBulkResult = await masterStore.GetBulkAsync(context, new[] { putResult0.ContentHash }, Token, UrgencyHint.Nominal, GetBulkOrigin.Local).ShouldBeSuccess();
                    postTrimGetBulkResult.ContentHashesInfo[0].Locations.Should().BeNullOrEmpty();

                    async Task<PinResult> PinContentForSession(ContentHash hash, int sessionIndex)
                    {
                        if (usePinBulk)
                        {
                            var result = await sessions[sessionIndex].PinAsync(context, new[] { hash }, Token);
                            return (await result.First()).Item;
                        }

                        return await sessions[sessionIndex].PinAsync(context, hash, Token);
                    }
                });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task MultiLevelContentLocationStoreDatabasePinFailOnEvictedContentTests(bool usePinBulk)
        {
            ConfigureWithOneMaster(s =>
            {
                // Disable test pin better logic which currently succeeds if there is one replica registered. This will cause the pin
                // logic to fall back to verifying when the number of replicas is below 3
                s.IsPinBetterEnabled = false;
            });

            int storeCount = 3;

            await RunTestAsync(
                new Context(Logger),
                storeCount,
                async context =>
                {
                    var sessions = context.Sessions;

                    var workerStore = context.GetFirstWorker();
                    var masterStore = context.GetMaster();

                    var hash = ContentHash.Random();

                    // Add to worker store
                    await workerStore.RegisterLocalLocationAsync(context, new[] { new ContentHashWithSize(hash, 120) }, Token, UrgencyHint.Nominal, touch: true).ShouldBeSuccess();

                    TestClock.UtcNow += TimeSpan.FromMinutes(2);
                    await masterStore.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();

                    for (int sessionIndex = 0; sessionIndex < storeCount; sessionIndex++)
                    {
                        // Heartbeat to ensure machine receives checkpoint
                        await context.GetLocationStore(sessionIndex).LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();

                        // Pin the content in the session which should fail with content not found
                        await PinContentForSession(sessionIndex).ShouldBeContentNotFound();
                    }

                    async Task<PinResult> PinContentForSession(int sessionIndex)
                    {
                        if (usePinBulk)
                        {
                            var result = await sessions[sessionIndex].PinAsync(context, new[] { hash }, Token);
                            return (await result.First()).Item;
                        }

                        return await sessions[sessionIndex].PinAsync(context, hash, Token);
                    }
                });
        }

        [Fact]
        public async Task MultiLevelContentLocationStoreOpenStreamTests()
        {
            ConfigureWithOneMaster();

            await RunTestAsync(
                new Context(Logger),
                3,
                async context =>
                {
                    var sessions = context.Sessions;
                    var master = context.GetMaster();
                    var worker = context.GetFirstWorker();

                    var contentHash = await PutContentInSession0_PopulateSession1LocalDb_RemoveContentFromGlobalStore(sessions, context, worker, master);
                    var openStreamResult = await sessions[1].OpenStreamAsync(
                        context,
                        contentHash,
                        Token).ShouldBeSuccess();

#pragma warning disable AsyncFixer02
                    openStreamResult.Stream.Dispose();
#pragma warning restore AsyncFixer02
                });
        }

        [Fact]
        public async Task MultiLevelContentLocationStorePlaceFileTests()
        {
            ConfigureWithOneMaster();

            await RunTestAsync(
                new Context(Logger),
                3,
                async context =>
                {
                    var sessions = context.Sessions;
                    var master = context.GetMaster();
                    var worker = context.GetFirstWorker();

                    var contentHash = await PutContentInSession0_PopulateSession1LocalDb_RemoveContentFromGlobalStore(sessions, context, worker, master);
                    await sessions[1].PlaceFileAsync(
                        context,
                        contentHash,
                        context.Directories[0].Path / "randomfile",
                        FileAccessMode.Write,
                        FileReplacementMode.ReplaceExisting,
                        FileRealizationMode.Copy,
                        Token).ShouldBeSuccess();
                });
        }

        [Fact]
        public async Task MultiLevelContentLocationStorePlaceFileFallbackToGlobalTest()
        {
            ConfigureWithOneMaster();

            await RunTestAsync(
                new Context(Logger),
                3,
                async context =>
                {
                    var sessions = context.Sessions;
                    var store0 = context.GetLocationStore(context.GetMasterIndex());
                    var store1 = context.GetLocationStore(context.EnumerateWorkersIndices().ElementAt(0));
                    var store2 = context.GetLocationStore(context.EnumerateWorkersIndices().ElementAt(1));

                    var content = ThreadSafeRandom.GetBytes((int)ContentByteCount);
                    var hashInfo = HashInfoLookup.Find(ContentHashType);
                    var contentHash = hashInfo.CreateContentHasher().GetContentHash(content);

                    // Register missing location with store 1
                    await store1.RegisterLocalLocationAsync(
                        context,
                        new[] { new ContentHashWithSize(contentHash, content.Length) },
                        Token,
                        UrgencyHint.Nominal,
                        touch: true).ShouldBeSuccess();

                    // Heartbeat to distribute checkpoints
                    await UploadCheckpointOnMasterAndRestoreOnWorkers(context);

                    var localResult = await store2.GetBulkAsync(context, contentHash, GetBulkOrigin.Local).ShouldBeSuccess();
                    localResult.ContentHashesInfo[0].Locations.Count.Should().Be(1);

                    var globalResult = await store2.GetBulkAsync(context, contentHash, GetBulkOrigin.Global).ShouldBeSuccess();
                    globalResult.ContentHashesInfo[0].Locations.Count.Should().Be(1);

                    // Put content into session 0
                    var putResult0 = await sessions[0].PutStreamAsync(context, ContentHashType, new MemoryStream(content), Token).ShouldBeSuccess();

                    // State should be:
                    //  Local: Store1
                    //  Global: Store1, Store0
                    localResult = await store2.GetBulkAsync(context, contentHash, GetBulkOrigin.Local).ShouldBeSuccess();
                    localResult.ContentHashesInfo[0].Locations.Count.Should().Be(1);

                    globalResult = await store2.GetBulkAsync(context, contentHash, GetBulkOrigin.Global).ShouldBeSuccess();
                    globalResult.ContentHashesInfo[0].Locations.Count.Should().Be(2);

                    // Place on session 2
                    await sessions[2].PlaceFileAsync(
                        context,
                        contentHash,
                        context.Directories[0].Path / "randomfile",
                        FileAccessMode.Write,
                        FileReplacementMode.ReplaceExisting,
                        FileRealizationMode.Copy,
                        Token).ShouldBeSuccess();
                });
        }

        [Fact]
        public async Task LocalDatabaseReplicationWithLocalDiskCentralStoreTest()
        {
            ConfigureWithOneMaster();

            await RunTestAsync(
                new Context(Logger),
                3,
                async context =>
                {
                    var sessions = context.Sessions;

                    var worker = context.GetFirstWorker();
                    var master = context.GetMaster();

                    // Insert random file in session 0
                    var putResult0 = await sessions[0].PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();

                    // Content should be available in session 0
                    var masterLocalResult = await master.GetBulkAsync(
                        context,
                        new[] { putResult0.ContentHash },
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Local).ShouldBeSuccess();
                    masterLocalResult.ContentHashesInfo[0].Locations.Should().NotBeNullOrEmpty();

                    // Making sure that the data exists in the first session but not in the second
                    var workerLocalResult = await worker.GetBulkAsync(
                        context,
                        new[] { putResult0.ContentHash },
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Local).ShouldBeSuccess();
                    workerLocalResult.ContentHashesInfo[0].Locations.Should().BeNullOrEmpty();

                    TestClock.UtcNow += TimeSpan.FromMinutes(20);

                    await UploadCheckpointOnMasterAndRestoreOnWorkers(context);

                    // Now the data should be in the second session.
                    var workerLocalResult1 = await worker.GetBulkAsync(
                        context,
                        new[] { putResult0.ContentHash },
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Local).ShouldBeSuccess();
                    workerLocalResult1.ContentHashesInfo[0].Locations.Should().NotBeNullOrEmpty();

                    // Ensure content is pulled from peers since distributed central storage is enabled
                    worker.LocalLocationStore.DistributedCentralStorage.Counters[CentralStorageCounters.TryGetFileFromPeerSucceeded].Value.Should().BeGreaterThan(0);
                    worker.LocalLocationStore.DistributedCentralStorage.Counters[CentralStorageCounters.TryGetFileFromFallback].Value.Should().Be(0);
                });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LocalDatabaseReplicationWithMasterSelectionTest(bool useIncrementalCheckpointing)
        {
            var masterLeaseExpiryTime = TimeSpan.FromMinutes(3);

            ConfigureWithOneMaster(
                s =>
                {
                    // Set all machines to master eligible to enable master election 
                    s.IsMasterEligible = true;
                    s.UseIncrementalCheckpointing = true;
                    s.RestoreCheckpointAgeThresholdMinutes = 0;
                },
                r =>
                {
                    r.Checkpoint.MasterLeaseExpiryTime = masterLeaseExpiryTime;
                });

            await RunTestAsync(
                new Context(Logger),
                2,
                async context =>
                {
                    var sessions = context.Sessions;

                    var ls0 = context.GetLocationStore(0);
                    var ls1 = context.GetLocationStore(1);

                    var lls0 = context.GetLocalLocationStore(0);
                    var lls1 = context.GetLocalLocationStore(1);

                    // Machines must acquire role on startup
                    Assert.True(lls0.CurrentRole != null);
                    Assert.True(lls1.CurrentRole != null);

                    // One of the machines must acquire the master role
                    Assert.True(lls0.CurrentRole == Role.Master || lls1.CurrentRole == Role.Master);

                    // One of the machines should be a worker (i.e. only one master is allowed)
                    Assert.True(lls0.CurrentRole == Role.Worker || lls1.CurrentRole == Role.Worker);

                    var masterRedisStore = lls0.CurrentRole == Role.Master ? ls0 : ls1;
                    var workerRedisStore = lls0.CurrentRole == Role.Master ? ls1 : ls0;

                    static long diff<TEnum>(CounterCollection<TEnum> c1, CounterCollection<TEnum> c2, TEnum name)
                        where TEnum : struct => c1[name].Value - c2[name].Value;

                    for (int i = 0; i < 5; i++)
                    {
                        var masterCounters = masterRedisStore.LocalLocationStore.Counters.Snapshot();
                        var workerCounters = workerRedisStore.LocalLocationStore.Counters.Snapshot();

                        // Insert random file in session 0
                        var putResult0 = await sessions[0].PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();

                        // Content should be available in session 0
                        var masterResult = await masterRedisStore.GetBulkAsync(
                            context,
                            new[] { putResult0.ContentHash },
                            Token,
                            UrgencyHint.Nominal,
                            GetBulkOrigin.Local).ShouldBeSuccess();
                        masterResult.ContentHashesInfo[0].Locations.Should().NotBeEmpty();

                        // Making sure that the data exists in the master session but not in the worker
                        var workerResult = await workerRedisStore.GetBulkAsync(
                            context,
                            new[] { putResult0.ContentHash },
                            Token,
                            UrgencyHint.Nominal,
                            GetBulkOrigin.Local).ShouldBeSuccess();
                        workerResult.ContentHashesInfo[0].Locations.Should().BeNullOrEmpty();

                        TestClock.UtcNow += TimeSpan.FromMinutes(2);
                        TestClock.UtcNow += TimeSpan.FromMinutes(masterLeaseExpiryTime.TotalMinutes / 2);

                        // Save checkpoint by heartbeating master
                        await masterRedisStore.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();

                        // Verify file was uploaded
                        // Verify file was skipped (if not first iteration)

                        // Restore checkpoint by  heartbeating worker
                        await workerRedisStore.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();

                        if (useIncrementalCheckpointing)
                        {
                            // Files should be uploaded by master and downloaded by worker
                            diff(masterRedisStore.LocalLocationStore.Counters, masterCounters, ContentLocationStoreCounters.IncrementalCheckpointFilesUploaded).Should().BePositive();
                            diff(workerRedisStore.LocalLocationStore.Counters, workerCounters, ContentLocationStoreCounters.IncrementalCheckpointFilesDownloaded).Should().BePositive();

                            if (i != 0)
                            {
                                // Prior files should be skipped on subsequent iterations
                                diff(masterRedisStore.LocalLocationStore.Counters, masterCounters, ContentLocationStoreCounters.IncrementalCheckpointFilesUploadSkipped).Should().BePositive();
                                diff(workerRedisStore.LocalLocationStore.Counters, workerCounters, ContentLocationStoreCounters.IncrementalCheckpointFilesDownloadSkipped).Should().BePositive();
                            }
                        }

                        // Master should retain its role since the lease expiry time has not elapsed
                        Assert.Equal(Role.Master, masterRedisStore.LocalLocationStore.CurrentRole);
                        Assert.Equal(Role.Worker, workerRedisStore.LocalLocationStore.CurrentRole);

                        // Now the data should be in the worker session.
                        workerResult = await workerRedisStore.GetBulkAsync(
                            context,
                            new[] { putResult0.ContentHash },
                            Token,
                            UrgencyHint.Nominal,
                            GetBulkOrigin.Local).ShouldBeSuccess();
                        workerResult.ContentHashesInfo[0].Locations.Should().NotBeNullOrEmpty();
                    }

                    // Roles should be retained if heartbeat happen within lease expiry window
                    TestClock.UtcNow += TimeSpan.FromMinutes(masterLeaseExpiryTime.TotalMinutes / 2);
                    await workerRedisStore.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();
                    await masterRedisStore.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();
                    Assert.Equal(Role.Worker, workerRedisStore.LocalLocationStore.CurrentRole);
                    Assert.Equal(Role.Master, masterRedisStore.LocalLocationStore.CurrentRole);

                    // Increment the time to ensure master lease expires
                    // then heartbeat worker first to ensure it steals the lease
                    // Master heartbeat trigger it to become a worker since the other
                    // machine will
                    TestClock.UtcNow += masterLeaseExpiryTime;
                    TestClock.UtcNow += TimeSpan.FromMinutes(masterLeaseExpiryTime.TotalMinutes * 2);
                    await workerRedisStore.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();
                    await masterRedisStore.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();

                    // Worker should steal master role since it h
                    // Worker should steal master role since it has expired
                    Assert.Equal(Role.Master, workerRedisStore.LocalLocationStore.CurrentRole);
                    Assert.Equal(Role.Worker, masterRedisStore.LocalLocationStore.CurrentRole);

                    // Test releasing role
                    await workerRedisStore.LocalLocationStore.ReleaseRoleIfNecessaryAsync(context);
                    Assert.Equal(null, workerRedisStore.LocalLocationStore.CurrentRole);

                    // Master redis store should now be able to reacquire master role
                    await masterRedisStore.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();
                    Assert.Equal(Role.Master, masterRedisStore.LocalLocationStore.CurrentRole);
                });
        }

        [Fact]
        public async Task IncrementalCheckpointingResetWithEpochChangeTest()
        {
            // Test Description:
            // In loop:
            // Set epoch to new value
            // Create checkpoint with data (files should not be reused from prior iteration)

            var masterLeaseExpiryTime = TimeSpan.FromMinutes(3);
            int iteration = 0;

            ConfigureWithOneMaster(
                s =>
                {
                    s.UseIncrementalCheckpointing = true;
                    s.RestoreCheckpointAgeThresholdMinutes = 0;
                    s.EventHubEpoch = $"Epoch:{iteration}";
                },
                r =>
                {
                    r.Checkpoint.MasterLeaseExpiryTime = masterLeaseExpiryTime;
                });

            static long diff<TEnum>(CounterCollection<TEnum> c1, CounterCollection<TEnum> c2, TEnum name)
                where TEnum : struct => c1[name].Value - c2[name].Value;

            await RunTestAsync(
                new Context(Logger),
                iterations: 5,
                storeCount: 2,
                testFunc: async context =>
                {
                    // +1 because this value is not consumed until the next iteration
                    iteration = context.Iteration + 1;

                    var sessions = context.Sessions;

                    var ls0 = context.GetLocationStore(0);
                    var ls1 = context.GetLocationStore(1);

                    var lls0 = context.GetLocalLocationStore(0);
                    var lls1 = context.GetLocalLocationStore(1);

                    // Machines must acquire role on startup
                    Assert.True(lls0.CurrentRole != null);
                    Assert.True(lls1.CurrentRole != null);

                    // One of the machines must acquire the master role
                    Assert.True(lls0.CurrentRole == Role.Master || lls1.CurrentRole == Role.Master);

                    // One of the machines should be a worker (i.e. only one master is allowed)
                    Assert.True(lls0.CurrentRole == Role.Worker || lls1.CurrentRole == Role.Worker);

                    var masterRedisStore = lls0.CurrentRole == Role.Master ? ls0 : ls1;
                    var workerRedisStore = lls0.CurrentRole == Role.Master ? ls1 : ls0;

                    var masterCounters = masterRedisStore.LocalLocationStore.Counters.Snapshot();
                    var workerCounters = workerRedisStore.LocalLocationStore.Counters.Snapshot();

                    // Insert random file in session 0
                    var putResult0 = await sessions[0].PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();

                    // Content should be available in session 0
                    var masterResult = await masterRedisStore.GetBulkAsync(
                        context,
                        new[] { putResult0.ContentHash },
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Local).ShouldBeSuccess();
                    masterResult.ContentHashesInfo[0].Locations.Should().NotBeEmpty();

                    // Making sure that the data exists in the master session but not in the worker
                    var workerResult = await workerRedisStore.GetBulkAsync(
                        context,
                        new[] { putResult0.ContentHash },
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Local).ShouldBeSuccess();
                    workerResult.ContentHashesInfo[0].Locations.Should().BeNullOrEmpty();

                    TestClock.UtcNow += TimeSpan.FromMinutes(2);
                    TestClock.UtcNow += TimeSpan.FromMinutes(masterLeaseExpiryTime.TotalMinutes / 2);

                    // Save checkpoint by heartbeating master
                    await masterRedisStore.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();

                    // Restore checkpoint by  heartbeating worker
                    await workerRedisStore.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();

                    // Files should be uploaded by master and downloaded by worker
                    diff(masterRedisStore.LocalLocationStore.Counters, masterCounters, ContentLocationStoreCounters.IncrementalCheckpointFilesUploaded).Should().BePositive();
                    diff(workerRedisStore.LocalLocationStore.Counters, workerCounters, ContentLocationStoreCounters.IncrementalCheckpointFilesDownloaded).Should().BePositive();

                    if (context.Iteration != 0)
                    {
                        // No files should be reused since the epoch is changing
                        diff(masterRedisStore.LocalLocationStore.Counters, masterCounters, ContentLocationStoreCounters.IncrementalCheckpointFilesUploadSkipped).Should().Be(0);
                        diff(workerRedisStore.LocalLocationStore.Counters, workerCounters, ContentLocationStoreCounters.IncrementalCheckpointFilesDownloadSkipped).Should().Be(0);
                    }
                });
        }

        [Fact]
        public async Task EventStreamContentLocationStoreBasicTests()
        {
            ConfigureWithOneMaster();

            await RunTestAsync(
                new Context(Logger),
                3,
                async context =>
                {
                    var sessions = context.Sessions;

                    var worker = context.GetFirstWorker();
                    var master = context.GetMaster();

                    // Insert random file in session 0
                    var putResult0 = await sessions[0].PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();

                    var workerResult = await worker.GetBulkAsync(
                        context,
                        new[] { putResult0.ContentHash },
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Local).ShouldBeSuccess();
                    workerResult.ContentHashesInfo[0].Locations.Should().BeNullOrEmpty("Worker should not have the content.");

                    var masterResult = await master.GetBulkAsync(
                        context,
                        new[] { putResult0.ContentHash },
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Local).ShouldBeSuccess();
                    masterResult.ContentHashesInfo[0].Locations.Count.Should().Be(1, "Master should receive an event and add the content to local store");

                    masterResult = await master.GetBulkAsync(
                        context,
                        new[] { putResult0.ContentHash },
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Global).ShouldBeSuccess();
                    masterResult.ContentHashesInfo[0].Locations.Count.Should().Be(1, "Master should be able to get the content from the global store");

                    await UploadCheckpointOnMasterAndRestoreOnWorkers(context);

                    workerResult = await worker.GetBulkAsync(
                        context,
                        new[] { putResult0.ContentHash },
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Local).ShouldBeSuccess();
                    workerResult.ContentHashesInfo[0].Locations.Should().NotBeNullOrEmpty("Worker should get the content in local database after sync");

                    // Remove the location from backing content location store so that in the absence of pin caching the
                    // result of pin should be false.
                    await master.TrimBulkAsync(
                        context,
                        masterResult.ContentHashesInfo.Select(c => c.ContentHash).ToList(),
                        Token,
                        UrgencyHint.Nominal).ShouldBeSuccess();

                    await UploadCheckpointOnMasterAndRestoreOnWorkers(context);

                    // Verify no locations for the content
                    workerResult = await worker.GetBulkAsync(
                        context,
                        new[] { putResult0.ContentHash },
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Local).ShouldBeSuccess();
                    workerResult.ContentHashesInfo[0].Locations.Should().BeNullOrEmpty();

                    // Verify no locations for the content
                    masterResult = await master.GetBulkAsync(
                        context,
                        new[] { putResult0.ContentHash },
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Global).ShouldBeSuccess();
                    masterResult.ContentHashesInfo[0].Locations.Should().NotBeNullOrEmpty("With LLS only mode, content is not eagerly removed from Redis.");

                    masterResult = await master.GetBulkAsync(
                        context,
                        new[] { putResult0.ContentHash },
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Local).ShouldBeSuccess();
                    masterResult.ContentHashesInfo[0].Locations.Should().BeNullOrEmpty("The result should not be available in LLS.");
                });
        }

        [Fact]
        public async Task TestRegisterActions()
        {
            // This test validates that events (like add location/remove location) are properly generated
            // based on the local location store's internal state and configuration.
            // For instance, some events are skipped because they were added recently, and some events should be eager
            // and the central store should be updated.
            ConfigureWithOneMaster();

            await RunTestAsync(
                new Context(Logger),
                3,
                async context =>
                {
                    var sessions = context.Sessions;

                    var workersSession = sessions[context.GetFirstWorkerIndex()];
                    var worker = context.GetFirstWorker();

                    // Insert random file to a worker.
                    var worker1Lls = worker.LocalLocationStore;

                    worker1Lls.Counters[ContentLocationStoreCounters.LocationAddEager].Value.Should().Be(0);
                    var putResult0 = await workersSession.PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();
                    worker1Lls.Counters[ContentLocationStoreCounters.LocationAddEager].Value.Should().Be(1);

                    var hashWithSize = new ContentHashWithSize(putResult0.ContentHash, putResult0.ContentSize);

                    worker1Lls.Counters[ContentLocationStoreCounters.RedundantRecentLocationAddSkipped].Value.Should().Be(0);
                    await worker.RegisterLocalLocationAsync(context, new[] { hashWithSize }, touch: true).ThrowIfFailure();
                    // Still should be one, because we just recently added the content.
                    worker1Lls.Counters[ContentLocationStoreCounters.LocationAddEager].Value.Should().Be(1);
                    worker1Lls.Counters[ContentLocationStoreCounters.RedundantRecentLocationAddSkipped].Value.Should().Be(1);

                    // Force the roundtrip to get the locations on the worker.
                    await UploadCheckpointOnMasterAndRestoreOnWorkers(context);

                    TestClock.UtcNow += TimeSpan.FromHours(1.5);
                    await worker.GetBulkLocalAsync(context, putResult0.ContentHash).ShouldBeSuccess();
                    TestClock.UtcNow += TimeSpan.FromHours(1.5);

                    // It was 3 hours since the content was added and 1.5h since the last touch.
                    worker1Lls.Counters[ContentLocationStoreCounters.LazyTouchEventOnly].Value.Should().Be(0);
                    await worker.RegisterLocalLocationAsync(context, new[] { hashWithSize }, touch: true).ThrowIfFailure();
                    worker1Lls.Counters[ContentLocationStoreCounters.LazyTouchEventOnly].Value.Should().Be(1);

                    await worker.TrimBulkAsync(context, new[] { hashWithSize.Hash }, Token, UrgencyHint.Nominal).ThrowIfFailure();

                    // We just removed the content, now, if we'll add it back, we should notify the global store eagerly.
                    worker1Lls.Counters[ContentLocationStoreCounters.LocationAddRecentRemoveEager].Value.Should().Be(0);
                    await worker.RegisterLocalLocationAsync(context, new[] { hashWithSize }, touch: true).ThrowIfFailure();
                    worker1Lls.Counters[ContentLocationStoreCounters.LocationAddRecentRemoveEager].Value.Should().Be(1);
                });
        }

        private static void CopyDirectory(string sourceRoot, string destinationRoot, bool overwriteExistingFiles = false)
        {
            sourceRoot = sourceRoot.TrimEnd('\\');
            destinationRoot = destinationRoot.TrimEnd('\\');

            var allFiles = Directory
                .GetFiles(sourceRoot, "*.*", SearchOption.AllDirectories);
            foreach (var file in allFiles)
            {
                var destinationFileName = Path.Combine(destinationRoot, file.Substring(sourceRoot.Length + 1));
                if (File.Exists(destinationFileName) && !overwriteExistingFiles)
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationFileName));
                File.Copy(file, destinationFileName);
                File.SetAttributes(destinationFileName, File.GetAttributes(destinationFileName) & ~FileAttributes.ReadOnly);
            }
        }

        [Fact(Skip = "Diagnostic purposes only")]
        public async Task TestDistributedEviction()
        {
            var testDbPath = new AbsolutePath(@"ADD PATH TO LLS DB HERE");
            //_testDatabasePath = TestRootDirectoryPath / "tempdb";
            //CopyDirectory(testDbPath.Path, _testDatabasePath.Path);

            var contentDirectoryPath = new AbsolutePath(@"ADD PATH TO CONTENT DIRECTORY HERE");
            ConfigureWithOneMaster();

            await RunTestAsync(
                new Context(Logger),
                1,
                async context =>
                {
                    var sessions = context.Sessions;

                    var master = context.GetMaster();

                    var root = TestRootDirectoryPath / "memdir";
                    var tempDbDir = TestRootDirectoryPath / "tempdb";


                    FileSystem.CreateDirectory(root);
                    var dir = new MemoryContentDirectory(new PassThroughFileSystem(), root);

                    File.Copy(contentDirectoryPath.Path, dir.FilePath.Path, overwrite: true);
                    await dir.StartupAsync(context).ThrowIfFailure();

                    master.LocalMachineId = new MachineId(144);

                    var lruContent = await dir.GetLruOrderedCacheContentWithTimeAsync();

                    var tracer = context.Context;

                    tracer.Debug($"LRU content count = {lruContent.Count}");
                    long lastTime = 0;
                    HashSet<ContentHash> hashes = new HashSet<ContentHash>();
                    foreach (var item in master.GetHashesInEvictionOrder(context, lruContent))
                    {
                        tracer.Debug($"{item}");
                        tracer.Debug($"LTO: {item.EffectiveAge.Ticks - lastTime}, LOTO: {item.EffectiveAge.Ticks - lastTime}, IsDupe: {!hashes.Add(item.ContentHash)}");

                        lastTime = item.Age.Ticks;
                    }

                    await Task.Yield();
                });
        }

        [Fact]
        public async Task DualRedundancyGlobalRedisTest()
        {
            // Disable cluster state storage in DB to ensure it doesn't interfere with testing
            // Redis cluster state resiliency
            _enableSecondaryRedis = true;
            ConfigureWithOneMaster(s => s.StoreClusterStateInDatabase = false);
            int machineCount = 3;

            await RunTestAsync(
                new Context(Logger),
                machineCount,
                async context =>
                {
                    var sessions = context.Sessions;

                    var masterSession = sessions[context.GetMasterIndex()];
                    var workerSession = sessions[context.GetFirstWorkerIndex()];
                    var master = context.GetMaster();
                    var worker = context.GetFirstWorker();
                    var masterGlobalStore = ((RedisGlobalStore)master.LocalLocationStore.GlobalStore);

                    // Heartbeat the master to ensure cluster state is mirrored to secondary
                    TestClock.UtcNow += _configurations[0].ClusterStateMirrorInterval + TimeSpan.FromSeconds(1);
                    await master.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();

                    var keys = _primaryGlobalStoreDatabase.Keys.ToList();

                    // Delete cluster state from primary
                    (await _primaryGlobalStoreDatabase.KeyDeleteAsync(masterGlobalStore.FullyQualifiedClusterStateKey)).Should().BeTrue();

                    var masterClusterState = master.LocalLocationStore.ClusterState;

                    var clusterState = ClusterState.CreateForTest();
                    await worker.LocalLocationStore.GlobalStore.UpdateClusterStateAsync(context, clusterState).ShouldBeSuccess();

                    clusterState.MaxMachineId.Should().Be(machineCount);

                    for (int machineIndex = 1; machineIndex <= clusterState.MaxMachineId; machineIndex++)
                    {
                        var machineId = new MachineId(machineIndex);
                        clusterState.TryResolve(machineId, out var machineLocation).Should().BeTrue();
                        masterClusterState.TryResolve(machineId, out var masterResolvedMachineLocation).Should().BeTrue();
                        machineLocation.Should().BeEquivalentTo(masterResolvedMachineLocation);
                    }

                    // Registering new machine should assign a new id which is greater than current ids (i.e. register machine operation
                    // should operate against secondary key which should have full set of data)
                    var newMachineId1 = await masterGlobalStore.RegisterMachineAsync(context, new MachineLocation(@"\\TestLocations\1"));
                    newMachineId1.Id.Index.Should().Be(clusterState.MaxMachineId + 1);

                    // Heartbeat the master to ensure cluster state is restored to primary
                    TestClock.UtcNow += _configurations[0].ClusterStateMirrorInterval + TimeSpan.FromSeconds(1);
                    await master.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();

                    // Delete cluster state from secondary (now primary should be only remaining copy)
                    (await _secondaryGlobalStoreDatabase.KeyDeleteAsync(masterGlobalStore.FullyQualifiedClusterStateKey)).Should().BeTrue();

                    // Try to register machine again should give same machine id
                    var newMachineId1AfterDelete = await masterGlobalStore.RegisterMachineAsync(context, new MachineLocation(@"\\TestLocations\1"));
                    newMachineId1AfterDelete.Id.Index.Should().Be(newMachineId1.Id.Index);

                    // Registering another machine should assign an id 1 more than the last machine id despite the cluster state deletion
                    var newMachineId2 = await masterGlobalStore.RegisterMachineAsync(context, new MachineLocation(@"\\TestLocations\2"));
                    newMachineId2.Id.Index.Should().Be(newMachineId1.Id.Index + 1);

                    // Ensure resiliency to removal from both primary and secondary
                    await verifyContentResiliency(_primaryGlobalStoreDatabase, _secondaryGlobalStoreDatabase);
                    await verifyContentResiliency(_secondaryGlobalStoreDatabase, _primaryGlobalStoreDatabase);

                    async Task verifyContentResiliency(LocalRedisProcessDatabase redis1, LocalRedisProcessDatabase redis2)
                    {
                        // Insert random file in session 0
                        var putResult = await masterSession.PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();

                        var globalGetBulkResult = await worker.GetBulkAsync(
                            context,
                            new[] { putResult.ContentHash },
                            Token,
                            UrgencyHint.Nominal,
                            GetBulkOrigin.Global).ShouldBeSuccess();
                        globalGetBulkResult.ContentHashesInfo[0].Locations.Count.Should().Be(1, "Content should be registered with the global store");

                        // Delete key from primary database
                        (await redis1.DeleteStringKeys(s => s.Contains(RedisGlobalStore.GetRedisKey(putResult.ContentHash)))).Should().BeGreaterThan(0);

                        globalGetBulkResult = await worker.GetBulkAsync(
                            context,
                            new[] { putResult.ContentHash },
                            Token,
                            UrgencyHint.Nominal,
                            GetBulkOrigin.Global).ShouldBeSuccess();

                        globalGetBulkResult.ContentHashesInfo[0].Locations.Count.Should().Be(1, "Content should be registered with the global store since locations are backed up in other store");

                        // Delete key from secondary database
                        (await redis2.DeleteStringKeys(s => s.Contains(RedisGlobalStore.GetRedisKey(putResult.ContentHash)))).Should().BeGreaterThan(0);

                        globalGetBulkResult = await worker.GetBulkAsync(
                            context,
                            new[] { putResult.ContentHash },
                            Token,
                            UrgencyHint.Nominal,
                            GetBulkOrigin.Global).ShouldBeSuccess();
                        globalGetBulkResult.ContentHashesInfo[0].Locations.Should().BeNullOrEmpty("Content should be missing from global store after removal from both redis databases");
                    }
                });
        }

        [Fact]
        public async Task CancelRaidedRedisTest()
        {
            _enableSecondaryRedis = true;
            _poolSecondaryRedisDatabase = false;
            ConfigureWithOneMaster();
            int machineCount = 1;

            await RunTestAsync(
                new Context(Logger),
                machineCount,
                async context =>
                {
                    var sessions = context.Sessions;

                    var masterSession = sessions[context.GetMasterIndex()];
                    var master = context.GetMaster();
                    var masterGlobalStore = ((RedisGlobalStore)master.LocalLocationStore.GlobalStore);

                    var putResult = await masterSession.PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();
                    var globalGetBulkResult = await master.GetBulkAsync(
                        context,
                        new[] { putResult.ContentHash },
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Global).ShouldBeSuccess();
                    globalGetBulkResult.ContentHashesInfo[0].Locations.Count.Should().Be(1, "Content should be registered with the global store");

                    //Turn off the second redis instance, and set a retry window
                    //The second instance should always fail and resort to timing out in the retry window limit
                    _configurations[0].RetryWindow = TimeSpan.FromSeconds(1);
                    _secondaryGlobalStoreDatabase.Dispose(close: true);

                    masterGlobalStore.RaidedRedis.Counters[RaidedRedisDatabaseCounters.CancelRedisInstance].Value.Should().Be(0);
                    globalGetBulkResult = await master.GetBulkAsync(
                        context,
                        new[] { putResult.ContentHash },
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Global).ShouldBeSuccess();

                    masterGlobalStore.RaidedRedis.Counters[RaidedRedisDatabaseCounters.CancelRedisInstance].Value.Should().Be(1);
                });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ClusterStateIsPersistedLocally(bool registerAdditionalLocation)
        {
            _registerAdditionalLocationPerMachine = registerAdditionalLocation;

            ConfigureWithOneMaster();
            int machineCount = 3;

            await RunTestAsync(
                new Context(Logger),
                machineCount,
                async context =>
                {
                    var sessions = context.Sessions;

                    var masterSession = sessions[context.GetMasterIndex()];
                    var workerSession = sessions[context.GetFirstWorkerIndex()];
                    var master = context.GetMaster();
                    var worker = context.GetFirstWorker();

                    // Heartbeat the master to ensure cluster state is written to local db
                    TestClock.UtcNow += _configurations[0].ClusterStateMirrorInterval + TimeSpan.FromSeconds(1);
                    await master.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();

                    var masterClusterState = master.LocalLocationStore.ClusterState;

                    var clusterState = ClusterState.CreateForTest();

                    // Try populating cluster state from local db
                    master.LocalLocationStore.Database.UpdateClusterState(context, clusterState, write: false);

                    var expectedMachineCount = registerAdditionalLocation ? machineCount * 2 : machineCount;
                    clusterState.MaxMachineId.Should().Be(expectedMachineCount);

                    for (int machineIndex = 1; machineIndex <= clusterState.MaxMachineId; machineIndex++)
                    {
                        var machineId = new MachineId(machineIndex);
                        clusterState.TryResolve(machineId, out var machineLocation).Should().BeTrue();
                        masterClusterState.TryResolve(machineId, out var masterResolvedMachineLocation).Should().BeTrue();
                        machineLocation.Should().BeEquivalentTo(masterResolvedMachineLocation);
                    }
                });
        }

        [Fact]
        public async Task GarbageCollectionTests()
        {
            ConfigureWithOneMaster();

            await RunTestAsync(
                new Context(Logger),
                3,
                async context =>
                {
                    var sessions = context.Sessions;

                    var workerSession = sessions[context.GetFirstWorkerIndex()];
                    var master = context.GetMaster();

                    // Insert random file in session 0
                    var putResult0 = await workerSession.PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();

                    var masterResult = await master.GetBulkAsync(
                        context,
                        new[] { putResult0.ContentHash },
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Local).ShouldBeSuccess();
                    masterResult.ContentHashesInfo[0].Locations.Count.Should().Be(1, "Master should receive an event and add the content to local store");

                    // Add time so worker machine is inactive
                    TestClock.UtcNow += TimeSpan.FromMinutes(20);
                    await master.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();

                    masterResult = await master.GetBulkAsync(
                        context,
                        new[] { putResult0.ContentHash },
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Local).ShouldBeSuccess();
                    masterResult.ContentHashesInfo[0].Locations.Should().BeNullOrEmpty("After heartbeat, worker location should be filtered due to inactivity");

                    master.LocalLocationStore.Database.Counters[ContentLocationDatabaseCounters.TotalNumberOfCleanedEntries].Value.Should().Be(0, "No entries should be cleaned before GC is called");
                    master.LocalLocationStore.Database.Counters[ContentLocationDatabaseCounters.TotalNumberOfCollectedEntries].Value.Should().Be(0, "No entries should be cleaned before GC is called");

                    master.LocalLocationStore.Database.GarbageCollect(context);

                    master.LocalLocationStore.Database.Counters[ContentLocationDatabaseCounters.TotalNumberOfCollectedEntries].Value.Should().Be(1, "After GC, the entry with only a location from the expired machine should be collected");
                });
        }

        [Fact]
        public async Task SelfEvictionTests()
        {
            ConfigureWithOneMaster();

            await RunTestAsync(
                new Context(Logger),
                3,
                async context =>
                {
                    var sessions = context.Sessions;

                    var worker0 = context.GetFirstWorker();
                    var worker1 = context.EnumerateWorkers().ElementAt(1);
                    var workerSession = sessions[context.GetFirstWorkerIndex()];
                    var workerContentStore = (IRepairStore)context.GetDistributedStore(context.GetFirstWorkerIndex());
                    var master = context.GetMaster();

                    // Insert random file in session 0
                    var putResult0 = await workerSession.PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();

                    worker0.LocalLocationStore.Counters[ContentLocationStoreCounters.LocationAddRecentInactiveEager].Value.Should().Be(0);

                    var masterResult = await master.GetBulkAsync(
                        context,
                        new[] { putResult0.ContentHash },
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Local).ShouldBeSuccess();
                    masterResult.ContentHashesInfo[0].Locations.Count.Should().Be(1, "Master should receive an event and add the content to local store");

                    // Add time so machine recomputes inactive machines
                    TestClock.UtcNow += TimeSpan.FromSeconds(1);

                    // Call heartbeat first to ensure last heartbeat time is up to date but then call remove from tracker to ensure marked unavailable
                    await worker0.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();
                    await workerContentStore.RemoveFromTrackerAsync(context).ShouldBeSuccess();

                    // Heartbeat the master to ensure set of inactive machines is updated
                    await master.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();

                    masterResult = await master.GetBulkAsync(
                        context,
                        new[] { putResult0.ContentHash },
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Local).ShouldBeSuccess();
                    masterResult.ContentHashesInfo[0].Locations.Should().BeNullOrEmpty("After heartbeat, worker location should be filtered due to inactivity");

                    master.LocalLocationStore.Database.Counters[ContentLocationDatabaseCounters.TotalNumberOfCleanedEntries].Value.Should().Be(0, "No entries should be cleaned before GC is called");
                    master.LocalLocationStore.Database.Counters[ContentLocationDatabaseCounters.TotalNumberOfCollectedEntries].Value.Should().Be(0, "No entries should be cleaned before GC is called");

                    master.LocalLocationStore.Database.ForceCacheFlush(context);

                    master.LocalLocationStore.Database.GarbageCollect(context);

                    master.LocalLocationStore.Database.Counters[ContentLocationDatabaseCounters.TotalNumberOfCollectedEntries].Value.Should().Be(1, "After GC, the entry with only a location from the expired machine should be collected");

                    // Heartbeat worker to switch back to active state
                    await worker0.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();

                    // Insert random file in session 0
                    var putResult1 = await workerSession.PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();

                    worker0.LocalLocationStore.Counters[ContentLocationStoreCounters.LocationAddRecentInactiveEager].Value.Should().Be(1, "Putting content after inactivity should eagerly go to global store.");

                    var worker1GlobalResult = await worker1.GetBulkAsync(
                        context,
                        new[] { putResult1.ContentHash },
                        Token,
                        UrgencyHint.Nominal,
                        GetBulkOrigin.Global).ShouldBeSuccess();
                    worker1GlobalResult.ContentHashesInfo[0].Locations.Should()
                        .NotBeNullOrEmpty("Putting content on worker 0 after inactivity should eagerly go to global store.");

                },
                enableRepairHandling: true);
        }

        [Fact]
        public async Task EventStreamContentLocationStoreEventHubBasicTests()
        {
            if (!ConfigureWithRealEventHubAndStorage())
            {
                // Test is misconfigured.
                Output.WriteLine("The test is skipped.");
                return;
            }

            // TODO: How to wait for events?
            const int EventPropagationDelayMs = 5000;

            await RunTestAsync(
                new Context(Logger),
                3,
                async context =>
                {
                    // Here is the user scenario that the test verifies:
                    // Setup:
                    //   - Session0: EH (master) + RocksDb
                    //   - Session1: EH (worker) + RocksDb
                    //   - Session2: EH (worker) + RocksDb
                    //
                    // 1. Put a location into Worker1
                    // 2. Get a local location from Master0. Location should exists in a local database, because master synchronizes events eagerly.
                    // 3. Get a local location from Worker2. Location SHOULD NOT exists in a local database, because worker does not receive events eagerly.
                    // 4. Remove the location from Worker1
                    // 5. Get a local location from Master0 (should not exists)
                    // 6. Get a local location from Worker2 (should still exists).
                    var sessions = context.Sessions;

                    var master0 = context.GetMaster();
                    var worker1Session = sessions[context.GetFirstWorkerIndex()];
                    var worker1 = context.EnumerateWorkers().ElementAt(0);
                    var worker2 = context.EnumerateWorkers().ElementAt(1);

                    // Only session0 is a master. So we need to put a location into a worker session and check that master received a sync event.
                    var putResult0 = await worker1Session.PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();

                    await Task.Delay(EventPropagationDelayMs);

                    // Content SHOULD be registered locally for master.
                    var localGetBulkResultMaster = await master0.GetBulkAsync(context, putResult0.ContentHash, GetBulkOrigin.Local).ShouldBeSuccess();
                    localGetBulkResultMaster.ContentHashesInfo[0].Locations.Should().NotBeNullOrEmpty();

                    // Content SHOULD NOT be registered locally for the second worker, because it does not receive events eagerly.
                    var localGetBulkResultWorker2 = await worker2.GetBulkAsync(context, putResult0.ContentHash, GetBulkOrigin.Local).ShouldBeSuccess();
                    localGetBulkResultWorker2.ContentHashesInfo[0].Locations.Should().BeNullOrEmpty();

                    // Content SHOULD be available globally via the second worker
                    var globalGetBulkResult1 = await worker2.GetBulkAsync(context, putResult0.ContentHash, GetBulkOrigin.Global).ShouldBeSuccess();
                    globalGetBulkResult1.ContentHashesInfo[0].Locations.Should().NotBeNullOrEmpty();

                    // Remove the location from backing content location store so that in the absence of pin caching the
                    // result of pin should be false.
                    await worker1.TrimBulkAsync(
                        context,
                        globalGetBulkResult1.ContentHashesInfo.Select(c => c.ContentHash).ToList(),
                        Token,
                        UrgencyHint.Nominal).ShouldBeSuccess();

                    await Task.Delay(EventPropagationDelayMs);

                    // Verify no locations for the content
                    var postLocalTrimGetBulkResult0a = await master0.GetBulkAsync(context, putResult0.ContentHash, GetBulkOrigin.Local).ShouldBeSuccess();
                    postLocalTrimGetBulkResult0a.ContentHashesInfo[0].Locations.Should().BeNullOrEmpty();
                });
        }

        // TODO: add a test case to cover different epochs
        // TODO: run tests against event hub automatically

        [Fact]
        public async Task EventStreamContentLocationStoreEventHubWithBlobStorageBasedCentralStore()
        {
            var checkpointsKey = Guid.NewGuid().ToString();

            if (!ConfigureWithRealEventHubAndStorage())
            {
                // Test is misconfigured.
                Output.WriteLine("The test is skipped.");
                return;
            }

            // TODO: How to wait for events?
            const int EventPropagationDelayMs = 5000;

            await RunTestAsync(
                new Context(Logger),
                4,
                async context =>
                {
                    // Here is the user scenario that the test verifies:
                    // Setup:
                    //   - Session0: EH (master) + RocksDb
                    //   - Session1: EH (master) + RocksDb
                    //   - Session2: EH (worker) + RocksDb
                    //   - Session3: EH (worker) + RocksDb
                    //
                    // 1. Put a location into Worker1
                    // 2. Get a local location from Master0. Location should exist in a local database, because master synchronizes events eagerly.
                    // 3. Get a local location from Worker2. Location SHOULD NOT exist in a local database, because worker does not receive events eagerly.
                    // 4. Force checkpoint creation, by triggering heartbeat on Master0
                    // 5. Get checkpoint on Worker2, by triggering heartbeat on Worker2
                    // 6. Get a local location from Worker2. LOcation should exist in local database, because database was updated with new checkpoint
                    var sessions = context.Sessions;

                    var master0 = context.GetLocationStore(0);
                    var master1 = context.GetLocationStore(1);
                    var worker2 = context.GetLocationStore(2);

                    // Only session0 is a master. So we need to put a location into a worker session and check that master received a sync event.
                    var putResult0 = await sessions[1].PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();

                    await Task.Delay(EventPropagationDelayMs);

                    // Content SHOULD be registered locally for master.
                    var localGetBulkResultMaster = await master0.GetBulkAsync(context, putResult0.ContentHash, GetBulkOrigin.Local).ShouldBeSuccess();
                    localGetBulkResultMaster.ContentHashesInfo[0].Locations.Should().NotBeNullOrEmpty();

                    // Content SHOULD NOT be registered locally for the second worker, because it does not receive events eagerly.
                    var localGetBulkResultWorker2 = await worker2.GetBulkAsync(context, putResult0.ContentHash, GetBulkOrigin.Local).ShouldBeSuccess();
                    localGetBulkResultWorker2.ContentHashesInfo[0].Locations.Should().BeNullOrEmpty();

                    // Content SHOULD be available globally via the second worker
                    var globalGetBulkResult1 = await worker2.GetBulkAsync(context, putResult0.ContentHash, GetBulkOrigin.Global).ShouldBeSuccess();
                    globalGetBulkResult1.ContentHashesInfo[0].Locations.Should().NotBeNullOrEmpty();

                    TestClock.UtcNow += TimeSpan.FromMinutes(2);
                    await master0.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();
                    await master1.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();
                    await worker2.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();

                    // Waiting for some time to make the difference between entry insertion time and the touch time that updates it.
                    TestClock.UtcNow += TimeSpan.FromMinutes(2);

                    // Content SHOULD be available local via the WORKER 2 after downloading checkpoint (touches content)
                    var localGetBulkResultWorker2b = await worker2.GetBulkAsync(context, putResult0.ContentHash, GetBulkOrigin.Local).ShouldBeSuccess();
                    localGetBulkResultWorker2b.ContentHashesInfo[0].Locations.Should().NotBeNullOrEmpty();

                    // Waiting for events to be propagated from the worker to the master
                    await Task.Delay(EventPropagationDelayMs);

                    // TODO[LLS]: change it or remove completely. (bug 1365340)
                    // Waiting for another 2 minutes before triggering the GC
                    //TestClock.UtcNow += TimeSpan.FromMinutes(2);
                    //((RocksDbContentLocationDatabase)master1.Database).GarbageCollect(context);

                    //// 4 minutes already passed after the entry insertion. It means that the entry should be collected unless touch updates the entry
                    //// Master1 still should have an entry in a local database
                    //localGetBulkResultMaster1 = await master1.GetBulkAsync(context, putResult0.ContentHash, GetBulkOrigin.Local).ShouldBeSuccess();
                    //Assert.True(localGetBulkResultMaster1.ContentHashesInfo[0].Locations.Count == 1);

                    //// Waiting for another 2 minutes forcing the entry to fall out of the local database
                    //TestClock.UtcNow += TimeSpan.FromMinutes(2);
                    //((RocksDbContentLocationDatabase)master1.Database).GarbageCollect(context);

                    //localGetBulkResultMaster1 = await master1.GetBulkAsync(context, putResult0.ContentHash, GetBulkOrigin.Local).ShouldBeSuccess();
                    //Assert.True(localGetBulkResultMaster1.ContentHashesInfo[0].Locations.NullOrEmpty());
                });
        }

        private static async Task<ContentHash> PutContentInSession0_PopulateSession1LocalDb_RemoveContentFromGlobalStore(
            IList<IContentSession> sessions,
            Context context,
            TransitioningContentLocationStore worker,
            TransitioningContentLocationStore master)
        {
            // Insert random file in session 0
            var putResult0 = await sessions[0].PutRandomAsync(context, ContentHashType, false, ContentByteCount, Token).ShouldBeSuccess();

            // Content SHOULD NOT be registered locally since it has not been queried
            var localGetBulkResult1a = await worker.GetBulkAsync(
                context,
                putResult0.ContentHash,
                GetBulkOrigin.Local).ShouldBeSuccess();
            localGetBulkResult1a.ContentHashesInfo[0].Locations.Should().BeNullOrEmpty();

            var globalGetBulkResult1 = await worker.GetBulkAsync(
                context,
                new[] { putResult0.ContentHash },
                Token,
                UrgencyHint.Nominal,
                GetBulkOrigin.Global).ShouldBeSuccess();
            globalGetBulkResult1.ContentHashesInfo[0].Locations.Should().NotBeNullOrEmpty();

            // Content SHOULD be registered locally since it HAS been queried as a result of GetBulk with GetBulkOrigin.Global
            var localGetBulkResult1b = await master.GetBulkAsync(
                context,
                putResult0.ContentHash,
                GetBulkOrigin.Local).ShouldBeSuccess();
            localGetBulkResult1b.ContentHashesInfo[0].Locations.Should().NotBeNullOrEmpty();

            // Remove the location from backing content location store so that in the absence of pin caching the
            // result of pin should be false.
            await worker.TrimBulkAsync(
                context,
                globalGetBulkResult1.ContentHashesInfo.Select(c => c.ContentHash).ToList(),
                Token,
                UrgencyHint.Nominal).ShouldBeSuccess();

            // Verify no locations for the content
            var postTrimGetBulkResult = await master.GetBulkAsync(
                context, putResult0.ContentHash,
                GetBulkOrigin.Global).ShouldBeSuccess();
            postTrimGetBulkResult.ContentHashesInfo[0].Locations.Should().NotBeNullOrEmpty("TrimBulkAsync does not clean global store.");
            return putResult0.ContentHash;
        }

        private async Task UploadCheckpointOnMasterAndRestoreOnWorkers(TestContext context, bool reconcile = false)
        {
            // Update time to trigger checkpoint upload and restore on master and workers respectively
            TestClock.UtcNow += TimeSpan.FromMinutes(2);

            var masterStore = context.GetMaster();

            // Heartbeat master first to upload checkpoint
            await masterStore.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();

            if (reconcile)
            {
                await masterStore.ReconcileAsync(context, force: true).ShouldBeSuccess();
            }

            // Next heartbeat workers to restore checkpoint
            foreach (var workerStore in context.EnumerateWorkers())
            {
                await workerStore.LocalLocationStore.HeartbeatAsync(context).ShouldBeSuccess();

                if (reconcile)
                {
                    await workerStore.ReconcileAsync(context, force: true).ShouldBeSuccess();
                }
            }
        }

        [Fact(Skip = "For manual testing only. Requires storage account credentials")]
        public async Task BlobCentralStorageCredentialsUpdate()
        {
            var testBasePath = FileSystem.GetTempPath();
            var containerName = "checkpoints";
            var checkpointsKey = "checkpoints-eventhub";
            if (!ReadConfiguration(out var storageAccountKey, out var storageAccountName, out _, out _))
            {
                Output.WriteLine("The test is skipped due to misconfiguration.");
                return;
            }

            var credentials = new StorageCredentials(storageAccountName, storageAccountKey);
            var account = new CloudStorageAccount(credentials, storageAccountName, endpointSuffix: null, useHttps: true);

            var sasToken = account.GetSharedAccessSignature(new SharedAccessAccountPolicy
            {
                SharedAccessExpiryTime = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5),
                Permissions = SharedAccessAccountPermissions.None,
                Services = SharedAccessAccountServices.Blob,
                ResourceTypes = SharedAccessAccountResourceTypes.Object,
                Protocols = SharedAccessProtocol.HttpsOnly
            });
            var blobStoreCredentials = new StorageCredentials(sasToken);

            var blobCentralStoreConfiguration = new BlobCentralStoreConfiguration(
                new AzureBlobStorageCredentials(blobStoreCredentials, storageAccountName, endpointSuffix: null),
                containerName,
                checkpointsKey);
            var blobCentralStore = new BlobCentralStorage(blobCentralStoreConfiguration);

            var operationContext = new BuildXL.Cache.ContentStore.Tracing.Internal.OperationContext(new Context(Logger));

            // Attempt a get of an inexistent file. It should fail due to permissions.
            var forbiddenReadResult = await blobCentralStore.TryGetFileAsync(operationContext,
                "fail",
                AbsolutePath.CreateRandomFileName(testBasePath));
            forbiddenReadResult.ShouldBeError("(403) Forbidden");

            // Update the token, this would usually be done by the secret store.
            var sasTokenWithReadPermission = account.GetSharedAccessSignature(new SharedAccessAccountPolicy
            {
                SharedAccessExpiryTime = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5),
                Permissions = SharedAccessAccountPermissions.Read | SharedAccessAccountPermissions.List,
                Services = SharedAccessAccountServices.Blob,
                ResourceTypes = SharedAccessAccountResourceTypes.Object,
                Protocols = SharedAccessProtocol.HttpsOnly
            });
            blobStoreCredentials.UpdateSASToken(sasTokenWithReadPermission);

            // Attempt a get of an inexistent file. It should fail due to it not existing.
            var allowedReadResult = await blobCentralStore.TryGetFileAsync(operationContext,
                "fail",
                AbsolutePath.CreateRandomFileName(testBasePath));
            allowedReadResult.ShouldBeError(@"Checkpoint blob 'checkpoints\fail' does not exist in shard #0.");
        }

        [Fact(Skip = "For manual usage only")]
        public async Task MultiThreadedStressTestRocksDbContentLocationDatabaseOnNewEntries()
        {
            bool useIncrementalCheckpointing = true;
            int numberOfMachines = 100;
            int addsPerMachine = 25000;
            int maximumBatchSize = 1000;
            int warmupBatches = 10000;

            var masterLeaseExpiryTime = TimeSpan.FromMinutes(3);

            ConfigureWithOneMaster(
                s =>
                {
                    s.UseIncrementalCheckpointing = useIncrementalCheckpointing;
                    s.RestoreCheckpointAgeThresholdMinutes = 60;
                },
                r =>
                {
                    r.Checkpoint.MasterLeaseExpiryTime = masterLeaseExpiryTime;
                });

            var events = GenerateAddEvents(numberOfMachines, addsPerMachine, maximumBatchSize);

            await RunTestAsync(
                new Context(Logger),
                2,
                async context =>
                {
                    var master = context.GetMaster();
                    var sessions = context.Sessions;
                    Warmup(context, maximumBatchSize, warmupBatches);
                    context.GetMaster().LocalLocationStore.Database.ForceCacheFlush(context);
                    PrintCacheStatistics(context);

                    {
                        var stopWatch = new Stopwatch();
                        Output.WriteLine("[Benchmark] Starting in 5s (use this when analyzing with dotTrace)");
                        await Task.Delay(5000);

                        // Benchmark
                        stopWatch.Restart();
                        Parallel.ForEach(Enumerable.Range(0, numberOfMachines), machineId =>
                        {
                            var eventHub = MemoryEventStoreConfiguration.Hub;

                            foreach (var ev in events[machineId])
                            {
                                context.SendEventToMaster(ev);
                            }
                        });
                        context.GetMaster().LocalLocationStore.Database.ForceCacheFlush(context);
                        stopWatch.Stop();

                        var ts = stopWatch.Elapsed;
                        var elapsedTime = string.Format("{0:00}:{1:00}:{2:00}.{3:00}",
                            ts.Hours, ts.Minutes, ts.Seconds,
                            ts.Milliseconds / 10);
                        Output.WriteLine("[Benchmark] Total Time: " + ts);
                    }

                    PrintCacheStatistics(context);
                    await Task.Delay(5000);
                });
        }

        private void Warmup(TestContext context, int maximumBatchSize, int warmupBatches)
        {
            Output.WriteLine("[Warmup] Starting");
            var warmupRng = new Random(Environment.TickCount);

            var stopWatch = new Stopwatch();
            stopWatch.Start();
            foreach (var _ in Enumerable.Range(0, warmupBatches))
            {
                var machineId = new MachineId(warmupRng.Next());
                var batch = Enumerable.Range(0, maximumBatchSize).Select(x => new ShortHash(ContentHash.Random())).ToList();
                context.SendEventToMaster(new RemoveContentLocationEventData(machineId, batch));
            }
            stopWatch.Stop();

            var ts = stopWatch.Elapsed;
            var elapsedTime = string.Format("{0:00}:{1:00}:{2:00}.{3:00}",
                ts.Hours, ts.Minutes, ts.Seconds,
                ts.Milliseconds / 10);
            Output.WriteLine("[Warmup] Total Time: " + ts);
        }

        private static List<List<ContentLocationEventData>> GenerateAddEvents(int numberOfMachines, int addsPerMachine, int maximumBatchSize)
        {
            var randomSeed = Environment.TickCount;
            var events = new List<List<ContentLocationEventData>>(numberOfMachines);
            events.AddRange(Enumerable.Range(0, numberOfMachines).Select(x => (List<ContentLocationEventData>)null));

            Parallel.ForEach(Enumerable.Range(0, numberOfMachines), machineId =>
            {
                var machineIdObject = new MachineId(machineId);
                var rng = new Random(Interlocked.Increment(ref randomSeed));

                var addedContent = Enumerable.Range(0, addsPerMachine).Select(_ => ContentHash.Random()).ToList();

                var machineEvents = new List<ContentLocationEventData>();
                for (var pendingHashes = addedContent.Count; pendingHashes > 0;)
                {
                    // Add the hashes in random batches
                    var batchSize = rng.Next(1, Math.Min(maximumBatchSize, pendingHashes));
                    var batch = addedContent.GetRange(addedContent.Count - pendingHashes, batchSize).Select(hash => new ShortHashWithSize(new ShortHash(hash), 200)).ToList();
                    machineEvents.Add(new AddContentLocationEventData(machineIdObject, batch));
                    pendingHashes -= batchSize;
                }
                events[machineId] = machineEvents;
            });

            return events;
        }

        [Fact(Skip = "For manual usage only")]
        public async Task MultiThreadedStressTestRocksDbContentLocationDatabaseOnMixedAddAndDelete()
        {
            bool useIncrementalCheckpointing = true;
            int numberOfMachines = 100;
            int deletesPerMachine = 25000;
            int maximumBatchSize = 2000;
            int warmupBatches = 10000;

            var centralStoreConfiguration = new LocalDiskCentralStoreConfiguration(TestRootDirectoryPath / "centralstore", Guid.NewGuid().ToString());
            var masterLeaseExpiryTime = TimeSpan.FromMinutes(3);

            ConfigureWithOneMaster(
                s =>
                {
                    s.UseIncrementalCheckpointing = useIncrementalCheckpointing;
                    s.RestoreCheckpointAgeThresholdMinutes = 60;
                },
                r =>
                {
                    r.Checkpoint.MasterLeaseExpiryTime = masterLeaseExpiryTime;
                });

            var events = GenerateMixedAddAndDeleteEvents(numberOfMachines, deletesPerMachine, maximumBatchSize);

            await RunTestAsync(
                new Context(Logger),
                2,
                async context =>
                {
                    var sessions = context.Sessions;
                    var master = context.GetMaster();
                    Warmup(context, maximumBatchSize, warmupBatches);
                    context.GetMaster().LocalLocationStore.Database.ForceCacheFlush(context);
                    PrintCacheStatistics(context);

                    {
                        var stopWatch = new Stopwatch();
                        Output.WriteLine("[Benchmark] Starting in 5s (use this when analyzing with dotTrace)");
                        await Task.Delay(5000);

                        // Benchmark
                        stopWatch.Restart();
                        Parallel.ForEach(Enumerable.Range(0, numberOfMachines), machineId =>
                        {
                            var eventHub = MemoryEventStoreConfiguration.Hub;

                            foreach (var ev in events[machineId])
                            {
                                context.SendEventToMaster(ev);
                            }
                        });
                        context.GetMaster().LocalLocationStore.Database.ForceCacheFlush(context);
                        stopWatch.Stop();

                        var ts = stopWatch.Elapsed;
                        var elapsedTime = string.Format("{0:00}:{1:00}:{2:00}.{3:00}",
                            ts.Hours, ts.Minutes, ts.Seconds,
                            ts.Milliseconds / 10);
                        Output.WriteLine("[Benchmark] Total Time: " + ts);

                        PrintCacheStatistics(context);
                    }

                    await Task.Delay(5000);
                });
        }

        private class FstComparer<T> : IComparer<(int, T)>
        {
            public int Compare((int, T) x, (int, T) y)
            {
                return x.Item1.CompareTo(y.Item1);
            }
        }

        private static List<List<ContentLocationEventData>> GenerateMixedAddAndDeleteEvents(int numberOfMachines, int deletesPerMachine, int maximumBatchSize)
        {
            var randomSeed = Environment.TickCount;

            var events = new List<List<ContentLocationEventData>>(numberOfMachines);
            events.AddRange(Enumerable.Range(0, numberOfMachines).Select(x => (List<ContentLocationEventData>)null));

            Parallel.ForEach(Enumerable.Range(0, numberOfMachines), machineId =>
            {
                var machineIdObject = new MachineId(machineId);
                var rng = new Random(Interlocked.Increment(ref randomSeed));

                // We want deletes to be performed in any arbitrary order, so the first in the pair is a random integer
                // This distribution is obviously not uniform at the end, but it doesn't matter, all we want is for
                // add -> delete pairs not to be contiguous.
                var addedPool = new BuildXL.Cache.ContentStore.Utils.PriorityQueue<(int, ShortHash)>(deletesPerMachine, new FstComparer<ShortHash>());

                var machineEvents = new List<ContentLocationEventData>();
                var totalAddsPerfomed = 0;
                // We can only delete after we have added, so we only reach the condition at the end
                for (var totalDeletesPerformed = 0; totalDeletesPerformed < deletesPerMachine;)
                {
                    bool addEnabled = totalAddsPerfomed < deletesPerMachine;
                    // We can only delete when it is causally consistent to do so
                    bool deleteEnabled = totalDeletesPerformed < deletesPerMachine && addedPool.Count > 0;
                    bool performDelete = deleteEnabled && rng.Next(0, 10) > 8 || !addEnabled;

                    if (performDelete)
                    {
                        var batchSize = Math.Min(deletesPerMachine - totalDeletesPerformed, addedPool.Count);
                        batchSize = rng.Next(1, batchSize);
                        batchSize = Math.Min(batchSize, maximumBatchSize);

                        var batch = new List<ShortHash>(batchSize);
                        foreach (var _ in Enumerable.Range(0, batchSize))
                        {
                            var shortHash = addedPool.Top.Item2;
                            addedPool.Pop();
                            batch.Add(shortHash);
                        }

                        machineEvents.Add(new RemoveContentLocationEventData(machineIdObject, batch));
                        totalDeletesPerformed += batch.Count;
                    }
                    else
                    {
                        var batchSize = Math.Min(deletesPerMachine - totalAddsPerfomed, maximumBatchSize);
                        batchSize = rng.Next(1, batchSize);

                        var batch = new List<ShortHashWithSize>(batchSize);
                        foreach (var x in Enumerable.Range(0, batchSize))
                        {
                            var shortHash = new ShortHash(ContentHash.Random());
                            batch.Add(new ShortHashWithSize(shortHash, 200));
                            addedPool.Push((rng.Next(), shortHash));
                        }

                        machineEvents.Add(new AddContentLocationEventData(machineIdObject, batch));
                        totalAddsPerfomed += batch.Count;
                    }
                }

                events[machineId] = machineEvents;
            });

            return events;
        }

        [Fact(Skip = "For manual usage only")]
        public async Task MultiThreadedStressTestRocksDbContentLocationDatabaseOnUniqueAddsWithCacheHit()
        {
            bool useIncrementalCheckpointing = true;
            int warmupBatches = 10000;
            int numberOfMachines = 100;
            int operationsPerMachine = 25000;
            float cacheHitRatio = 0.5f;
            int maximumBatchSize = 1000;

            var centralStoreConfiguration = new LocalDiskCentralStoreConfiguration(TestRootDirectoryPath / "centralstore", Guid.NewGuid().ToString());
            var masterLeaseExpiryTime = TimeSpan.FromMinutes(3);

            ConfigureWithOneMaster(
                s =>
                {
                    s.IsMasterEligible = true;
                    s.UseIncrementalCheckpointing = useIncrementalCheckpointing;
                    s.RestoreCheckpointAgeThresholdMinutes = 60;
                },
                r =>
                {
                    r.Checkpoint.MasterLeaseExpiryTime = masterLeaseExpiryTime;
                });

            var events = GenerateUniquenessWorkload(numberOfMachines, cacheHitRatio, maximumBatchSize, operationsPerMachine, randomSeedOverride: 42);

            await RunTestAsync(
                new Context(Logger),
                2,
                async context =>
                {
                    var sessions = context.Sessions;
                    var master = context.GetMaster();
                    Warmup(context, maximumBatchSize, warmupBatches);
                    context.GetMaster().LocalLocationStore.Database.ForceCacheFlush(context);
                    PrintCacheStatistics(context);

                    {
                        var stopWatch = new Stopwatch();
                        Output.WriteLine("[Benchmark] Starting in 5s (use this when analyzing with dotTrace)");
                        await Task.Delay(5000);

                        // Benchmark
                        stopWatch.Restart();
                        Parallel.ForEach(Enumerable.Range(0, numberOfMachines), machineId =>
                        {
                            var eventHub = MemoryEventStoreConfiguration.Hub;

                            foreach (var ev in events[machineId])
                            {
                                context.SendEventToMaster(ev);
                            }
                        });
                        context.GetMaster().LocalLocationStore.Database.ForceCacheFlush(context);
                        stopWatch.Stop();

                        var ts = stopWatch.Elapsed;
                        var elapsedTime = string.Format("{0:00}:{1:00}:{2:00}.{3:00}",
                            ts.Hours, ts.Minutes, ts.Seconds,
                            ts.Milliseconds / 10);
                        Output.WriteLine("[Benchmark] Total Time: " + ts);

                        PrintCacheStatistics(context);
                    }

                    await Task.Delay(5000);
                });
        }

        private void PrintCacheStatistics(TestContext context)
        {
            var db = context.GetMaster().LocalLocationStore.Database;
            var counters = db.Counters;

            if (db.IsInMemoryCacheEnabled)
            {
                Output.WriteLine("CACHE ENABLED");
            }
            else
            {
                Output.WriteLine("CACHE DISABLED");
            }

            Output.WriteLine("[Statistics] NumberOfStoreOperations: " + counters[ContentLocationDatabaseCounters.NumberOfStoreOperations].ToString());
            Output.WriteLine("[Statistics] NumberOfGetOperations: " + counters[ContentLocationDatabaseCounters.NumberOfGetOperations].ToString());
            Output.WriteLine("[Statistics] TotalNumberOfCacheHit: " + counters[ContentLocationDatabaseCounters.TotalNumberOfCacheHit].ToString());
            Output.WriteLine("[Statistics] TotalNumberOfCacheMiss: " + counters[ContentLocationDatabaseCounters.TotalNumberOfCacheMiss].ToString());
            var totalCacheRequests = counters[ContentLocationDatabaseCounters.TotalNumberOfCacheHit].Value + counters[ContentLocationDatabaseCounters.TotalNumberOfCacheMiss].Value;
            if (totalCacheRequests > 0)
            {
                double cacheHitRate = ((double)counters[ContentLocationDatabaseCounters.TotalNumberOfCacheHit].Value) / ((double)totalCacheRequests);
                Output.WriteLine("[Statistics] Cache Hit Rate: " + cacheHitRate.ToString());
            }

            Output.WriteLine("[Statistics] NumberOfPersistedEntries: " + counters[ContentLocationDatabaseCounters.NumberOfPersistedEntries].ToString());
            Output.WriteLine("[Statistics] TotalNumberOfCacheFlushes: " + counters[ContentLocationDatabaseCounters.TotalNumberOfCacheFlushes].ToString());
            Output.WriteLine("[Statistics] NumberOfCacheFlushesTriggeredByUpdates: " + counters[ContentLocationDatabaseCounters.NumberOfCacheFlushesTriggeredByUpdates].ToString());
            Output.WriteLine("[Statistics] NumberOfCacheFlushesTriggeredByTimer: " + counters[ContentLocationDatabaseCounters.NumberOfCacheFlushesTriggeredByTimer].ToString());
            Output.WriteLine("[Statistics] NumberOfCacheFlushesTriggeredByGarbageCollection: " + counters[ContentLocationDatabaseCounters.NumberOfCacheFlushesTriggeredByReconciliation].ToString());
            Output.WriteLine("[Statistics] NumberOfCacheFlushesTriggeredByCheckpoint: " + counters[ContentLocationDatabaseCounters.NumberOfCacheFlushesTriggeredByCheckpoint].ToString());

            Output.WriteLine("[Statistics] CacheFlush: " + counters[ContentLocationDatabaseCounters.CacheFlush].ToString());
        }

        private static List<List<ContentLocationEventData>> GenerateUniquenessWorkload(int numberOfMachines, float cacheHitRatio, int maximumBatchSize, int operationsPerMachine, int? randomSeedOverride = null)
        {
            var randomSeed = randomSeedOverride ?? Environment.TickCount;

            var events = new List<List<ContentLocationEventData>>(numberOfMachines);
            events.AddRange(Enumerable.Range(0, numberOfMachines).Select(x => (List<ContentLocationEventData>)null));

            var cacheHitHashPool = new ConcurrentBigSet<ShortHash>();
            Parallel.ForEach(Enumerable.Range(0, numberOfMachines), machineId =>
            {
                var machineIdObject = new MachineId(machineId);
                var rng = new Random(Interlocked.Increment(ref randomSeed));

                var machineEvents = new List<ContentLocationEventData>();
                for (var operations = 0; operations < operationsPerMachine;)
                {
                    // Done this way to ensure batches don't get progressively smaller and hog memory
                    var batchSize = rng.Next(1, maximumBatchSize);
                    batchSize = Math.Min(batchSize, operationsPerMachine - operations);

                    var hashes = new List<ShortHashWithSize>();
                    while (hashes.Count < batchSize)
                    {
                        var shouldHitCache = rng.NextDouble() < cacheHitRatio;

                        ShortHash hashToUse;
                        if (cacheHitHashPool.Count > 0 && shouldHitCache)
                        {
                            // Since this set is grow-only, this should always work
                            hashToUse = cacheHitHashPool[rng.Next(0, cacheHitHashPool.Count)];
                        }
                        else
                        {
                            do
                            {
                                hashToUse = new ShortHash(ContentHash.Random());
                            } while (cacheHitHashPool.Contains(hashToUse) || !cacheHitHashPool.Add(hashToUse));
                        }

                        hashes.Add(new ShortHashWithSize(hashToUse, 200));
                    }

                    machineEvents.Add(new AddContentLocationEventData(
                        machineIdObject,
                        hashes));

                    operations += batchSize;
                }

                events[machineId] = machineEvents;
            });

            return events;
        }
    }
}
