﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Subscriptions;
using Raven.Client.Exceptions.Documents.Subscriptions;
using Raven.Client.Extensions;
using Raven.Client.Http;
using Raven.Server.Commercial;
using Raven.Server.Documents.Subscriptions;
using Raven.Server.ServerWide.Commands.Subscriptions;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow.Json;
using Sparrow.Server;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Client.Subscriptions
{
    public class ConcurrentSubscriptionsTests : ReplicationTestBase
    {
        public ConcurrentSubscriptionsTests(ITestOutputHelper output) : base(output)
        {
        }

        private readonly TimeSpan _reasonableWaitTime = Debugger.IsAttached ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(60);

        [RavenFact(RavenTestCategory.Subscriptions)]
        public async Task ConcurrentSubscriptions()
        {
            using (var store = GetDocumentStore())
            {
                var id = store.Subscriptions.Create<User>();
                using (var subscription = store.Subscriptions.GetSubscriptionWorker(new SubscriptionWorkerOptions(id)
                {
                    TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                    Strategy = SubscriptionOpeningStrategy.Concurrent,
                    MaxDocsPerBatch = 2
                }))
                using (var secondSubscription = store.Subscriptions.GetSubscriptionWorker(new SubscriptionWorkerOptions(id)
                {
                    Strategy = SubscriptionOpeningStrategy.Concurrent,
                    TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                    MaxDocsPerBatch = 2
                }))
                {
                    using (var session = store.OpenSession())
                    {
                        session.Store(new User(), "user/1");
                        session.Store(new User(), "user/2");
                        session.Store(new User(), "user/3");
                        session.Store(new User(), "user/4");
                        session.Store(new User(), "user/5");
                        session.Store(new User(), "user/6");
                        session.SaveChanges();
                    }

                    var con1Docs = new List<string>();
                    var con2Docs = new List<string>();

                    var t = subscription.Run(x =>
                    {
                        foreach (var item in x.Items)
                        {
                            con1Docs.Add(item.Id);
                        }
                    });

                    var _ = secondSubscription.Run(x =>
                    {
                        foreach (var item in x.Items)
                        {
                            con2Docs.Add(item.Id);
                        }
                    });

                    await AssertWaitForTrueAsync(() => Task.FromResult(con1Docs.Count + con2Docs.Count == 6), 6000);
                    await AssertNoLeftovers(store, id);
                }
            }
        }

        [RavenFact(RavenTestCategory.Subscriptions)]
        public async Task ConcurrentSubscriptionsManyWorkers()
        {
            var workersAmount = 10;
            using (var store = GetDocumentStore())
            {
                var id = store.Subscriptions.Create<User>();

                var workerToDocsAmount = new Dictionary<SubscriptionWorker<User>, HashSet<string>>();

                for (int i = 0; i < workersAmount; i++)
                {
                    workerToDocsAmount.Add(store.Subscriptions.GetSubscriptionWorker<User>(new SubscriptionWorkerOptions(id)
                    {
                        TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                        Strategy = SubscriptionOpeningStrategy.Concurrent,
                        MaxDocsPerBatch = 1
                    }), new HashSet<string>());
                }

                using (var session = store.OpenSession())
                {
                    for (int i = 1; i <= 100; i++)
                    {
                        session.Store(new User(), $"user/{i}");
                    }
                    session.SaveChanges();
                }

                foreach (var (worker, docs) in workerToDocsAmount)
                {
                    var _ = worker.Run(x =>
                    {
                        foreach (var item in x.Items)
                        {
                            workerToDocsAmount[worker].Add(item.Id);
                        }
                    });
                }

                await AssertWaitForTrueAsync(() => Task.FromResult(workerToDocsAmount.Sum(x => x.Value.Count) == 100), 6000);
                await AssertNoLeftovers(store, id);
            }
        }

        [RavenFact(RavenTestCategory.Subscriptions)]
        public async Task MakeSureNoopAckDoesntDeleteItemsFromResend()
        {
            DebuggerAttachedTimeout.DisableLongTimespan = true;
            using (var store = GetDocumentStore())
            {
                var id = store.Subscriptions.Create<User>();
                using (var subscription = store.Subscriptions.GetSubscriptionWorker(new SubscriptionWorkerOptions(id)
                {
                    TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                    Strategy = SubscriptionOpeningStrategy.Concurrent,
                    MaxDocsPerBatch = 6
                }))
                {
                    var conn1 = new AsyncManualResetEvent();
                    var holdConn2 = new AsyncManualResetEvent();
                    var amre = new AsyncManualResetEvent();
                    var amre2 = new AsyncManualResetEvent();
                    var waitForConn2ToFetch = new AsyncManualResetEvent();
                    var noopSent = new AsyncManualResetEvent();

                    using (var session = store.OpenSession())
                    {
                        session.Store(new User(), "user/1");
                        session.Store(new User(), "user/2");
                        session.Store(new User(), "user/3");
                        session.Store(new User(), "user/4");
                        session.Store(new User(), "user/5");
                        session.Store(new User(), "user/6");
                        session.SaveChanges();
                    }

                    //run a connection and pause it
                    var t = subscription.Run(async x =>
                    {
                        amre2.Set();
                        await amre.WaitAsync();
                    });

                    await amre2.WaitAsync();
                    //update a document which is processed right now
                    using (var session = store.OpenSession())
                    {
                        session.Store(new User { Name = "Changed" }, "user/1");
                        session.Store(new User { Name = "Changed" }, "user/2");
                        session.SaveChanges();
                    }

                    //pause the connection in server before it collects the next batch (which will be resend)
                    var db = await Databases.GetDocumentDatabaseInstanceFor(store);
                    var conns = db.SubscriptionStorage.GetSubscriptionStateById(Convert.ToInt32(id)).GetConnections();
                    conns[0].ForTestingPurposesOnly().PauseConnection = conn1;

                    //create new doc for conn2 to have something to fetch
                    using (var session = store.OpenSession())
                    {
                        session.Store(new User(), "user/7");
                        session.SaveChanges();
                    }

                    //add connection 2 so subscription cv will be higher than user/1 user/2
                    using var subscription2 = store.Subscriptions.GetSubscriptionWorker(new SubscriptionWorkerOptions(id)
                    {
                        TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                        Strategy = SubscriptionOpeningStrategy.Concurrent,
                        MaxDocsPerBatch = 6
                    });
                    var t2 = subscription2.Run(async (x) =>
                    {
                        //will skip user/1 user/2 because they are processed by another
                        waitForConn2ToFetch.Set();
                        await holdConn2.WaitAsync();
                    });

                    await waitForConn2ToFetch.WaitAsync();

                    //let connection ack, but will pause in server from continuing
                    amre.Set();

                    var executor = store.GetRequestExecutor();

                    //wait for user/1 and user/2 to enter resend because they were processed by conn1 when updated updated
                    var enteredResend = await WaitForValueAsync(() =>
                    {
                        using var _ = executor.ContextPool.AllocateOperationContext(out var ctx);
                        var cmd = new GetSubscriptionResendListCommand(store.Database, id);
                        executor.Execute(cmd, ctx);
                        var res = cmd.Result;
                        return res.Results.Count(x => (x.Id == "user/1" || x.Id == "user/2") && x.Batch == -1) == 2;
                    }, true, 3000);

                    if (enteredResend == false)
                    {
                        ReleaseConnections();
                        Assert.False(true, "user/1 and user/2 did not enter the resend table");
                    }

                    //add new conn3 to trigger noop, it will fetch user/1 OR user/2 and then we pause it in server
                    using var subscription3 = store.Subscriptions.GetSubscriptionWorker(new SubscriptionWorkerOptions(id)
                    {
                        TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                        Strategy = SubscriptionOpeningStrategy.Concurrent,
                        MaxDocsPerBatch = 2
                    });

                    bool? noopDeleted = null;

                    //create new doc for conn3
                    using (var session = store.OpenSession())
                    {
                        session.Store(new User(), "user/8");
                        session.SaveChanges();
                    }

                    var first = true;

                    var _ = subscription3.Run((x) =>
                    {
                        //will fetch user/2 if resend table was not deleted by noop
                        //will fetch user/8 if resend was deleted
                        if (first)
                        {
                            if (x.Items.Exists(x => x.Id == "user/8"))
                            {
                                noopDeleted = true;
                            }
                            else if (x.Items.Exists(x => x.Id == "user/2" || x.Id == "user/1"))
                            {
                                noopDeleted = false;
                            }

                            first = false;
                        }

                        noopSent.Set();
                        return Task.CompletedTask;
                    });

                    await noopSent.WaitAsync();
                    ReleaseConnections();

                    Assert.NotNull(noopDeleted);
                    Assert.False(noopDeleted, "Noop deleted resend items with batchId -1");

                    void ReleaseConnections()
                    {
                        conns[0].ForTestingPurposesOnly().PauseConnection = null;
                        conn1.Set();
                        holdConn2.Set();
                    }
                }
            }
        }


        [RavenTheory(RavenTestCategory.Subscriptions)]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ResendAfterConnectionClosed(bool filter)
        {
            using (var store = GetDocumentStore())
            {
                var id = await (filter ? store.Subscriptions.CreateAsync<User>(user => user.Name != "John") : store.Subscriptions.CreateAsync<User>());
                await using (var subscription = store.Subscriptions.GetSubscriptionWorker(new SubscriptionWorkerOptions(id)
                {
                    TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                    Strategy = SubscriptionOpeningStrategy.Concurrent,
                    MaxDocsPerBatch = 2
                }))
                await using (var Subscription2 = store.Subscriptions.GetSubscriptionWorker(new SubscriptionWorkerOptions(id)
                {
                    Strategy = SubscriptionOpeningStrategy.Concurrent,
                    TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                    MaxDocsPerBatch = 2
                }))
                {
                    using (var session = store.OpenSession())
                    {
                        session.Store(new User(), "user/1");
                        session.Store(new User(), "user/2");
                        session.Store(new User(), "user/3");
                        session.Store(new User(), "user/4");
                        session.Store(new User(), "user/5");
                        session.Store(new User(), "user/6");
                        session.SaveChanges();
                    }

                    var con1Docs = new List<string>();
                    var con2Docs = new List<string>();

                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var mre = new ManualResetEvent(false);

                    var _ = Subscription2.Run(x =>
                    {
                        foreach (var item in x.Items)
                        {
                            con2Docs.Add(item.Id);
                        }

                        mre.Set();
                        tcs.Task.Wait();
                    });

                    mre.WaitOne();

                    var exception = string.Empty;
                    var t = subscription.Run(x =>
                    {
                        foreach (var item in x.Items)
                        {
                            if (string.IsNullOrEmpty(exception) && string.IsNullOrEmpty(item.ExceptionMessage) == false)
                            {
                                exception = item.ExceptionMessage;
                            }

                            con1Docs.Add(item.Id);
                        }
                    });

                    Assert.True(await WaitForValueAsync(() => Task.FromResult(con2Docs.Count == 2), true, 6000, 100), $"connection 2 has {con2Docs.Count} docs");
                    Assert.True(await WaitForValueAsync(() => Task.FromResult(con1Docs.Count == 4), true, 6000, 100), $"connection 1 has {con1Docs.Count} docs");

                    tcs.SetException(new InvalidOperationException());
                    await Subscription2.DisposeAsync(waitForSubscriptionTask: true);

                    Assert.True(await WaitForValueAsync(() => Task.FromResult(con1Docs.Count == 6), true, 6000, 100), $"connection 1 has {con1Docs.Count} docs");
                    Assert.True(string.IsNullOrEmpty(exception), $"string.IsNullOrEmpty(exception): " + exception);

                    await AssertNoLeftovers(store, id);
                }
            }
        }

        [RavenFact(RavenTestCategory.Subscriptions)]
        public async Task ResendWhenDocumentIsProcessedByAnotherConnection()
        {
            using (var store = GetDocumentStore())
            {
                var id = store.Subscriptions.Create<User>();
                await using (var subscription = store.Subscriptions.GetSubscriptionWorker<User>(new SubscriptionWorkerOptions(id)
                {
                    TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                    Strategy = SubscriptionOpeningStrategy.Concurrent,
                    MaxDocsPerBatch = 2
                }))
                await using (var subscription2 = store.Subscriptions.GetSubscriptionWorker<User>(new SubscriptionWorkerOptions(id)
                {
                    Strategy = SubscriptionOpeningStrategy.Concurrent,
                    TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                    MaxDocsPerBatch = 2
                }))
                {
                    using (var session = store.OpenSession())
                    {
                        var user = new User();
                        user.Name = "NotChanged";
                        session.Store(user, "user/1");
                        session.SaveChanges();
                    }

                    var con1Docs = new List<(string id, string name)>();
                    var con2Docs = new List<(string id, string name)>();

                    var delayConn2ack = new AsyncManualResetEvent();
                    var waitUntilConn2GetsUser1 = new AsyncManualResetEvent();
                    var waitBeforeConn1FinishesSecondBatch = new AsyncManualResetEvent();
                    var batchesProcessedByConn1 = 0;

                    var _ = subscription2.Run(async x =>
                    {
                        foreach (var item in x.Items)
                        {
                            con2Docs.Add((item.Id, item.Result.Name));

                            waitUntilConn2GetsUser1.Set();
                            await delayConn2ack.WaitAsync(_reasonableWaitTime);
                        }
                    });

                    await waitUntilConn2GetsUser1.WaitAsync(_reasonableWaitTime);

                    using (var session = store.OpenSession())
                    {
                        var user1 = session.Load<User>("user/1");
                        user1.Name = "Changed";
                        session.Store(new User(), "user/2");
                        session.Store(new User(), "user/3");

                        session.SaveChanges();
                    }

                    var waitUntilConn1FinishesBatch = new AsyncManualResetEvent();
                    var t = subscription.Run(async x =>
                    {
                        foreach (var item in x.Items)
                        {
                            con1Docs.Add((item.Id, item.Result.Name));
                        }

                        batchesProcessedByConn1++;
                        if (batchesProcessedByConn1 == 1)
                        {
                            waitUntilConn1FinishesBatch.Set();
                            await waitBeforeConn1FinishesSecondBatch.WaitAsync();
                        }
                    });
                    await waitUntilConn1FinishesBatch.WaitAsync(_reasonableWaitTime);
                    Assert.Contains(("user/2", null), con1Docs);
                    Assert.Contains(("user/3", null), con1Docs);
                    Assert.DoesNotContain(("user/1", "NotChanged"), con1Docs);
                    Assert.DoesNotContain(("user/1", "Changed"), con1Docs);

                    waitBeforeConn1FinishesSecondBatch.Set();
                    delayConn2ack.Set(); // let connection 2 finish with old user/1

                    Assert.Contains(("user/1", "NotChanged"), con2Docs);

                    Assert.True(await WaitForValueAsync(() => Task.FromResult(con1Docs.Contains(("user/1", "Changed")) || con2Docs.Contains(("user/1", "Changed"))), true, 6000, 100), $"connection 1 and 2 are missing new user/1");
                    Assert.False(await WaitForValueAsync(() => Task.FromResult(con1Docs.Contains(("user/1", "Changed")) && con2Docs.Contains(("user/1", "Changed"))), true, 6000, 100), $"connection 1 and 2 both got new user/1");

                    await AssertNoLeftovers(store, id);
                }
            }
        }

        [RavenFact(RavenTestCategory.Subscriptions)]
        public async Task RemoveDeletedDocumentFromResend()
        {
            using (var store = GetDocumentStore())
            {
                var id = store.Subscriptions.Create<User>();
                await using (var subscription = store.Subscriptions.GetSubscriptionWorker<User>(new SubscriptionWorkerOptions(id)
                {
                    TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                    Strategy = SubscriptionOpeningStrategy.Concurrent,
                    MaxDocsPerBatch = 2
                }))
                await using (var subscription2 = store.Subscriptions.GetSubscriptionWorker<User>(new SubscriptionWorkerOptions(id)
                {
                    Strategy = SubscriptionOpeningStrategy.Concurrent,
                    TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                    MaxDocsPerBatch = 2
                }))
                {
                    using (var session = store.OpenSession())
                    {
                        session.Store(new User(), "users/1");
                        session.Store(new User(), "users/2");
                        session.Store(new User(), "users/3");
                        session.Store(new User(), "users/4");
                        session.Store(new User(), "users/5");
                        session.Store(new User(), "users/6");
                        session.SaveChanges();
                    }

                    var con1Docs = new List<string>();
                    var con2Docs = new List<string>();

                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var mre = new ManualResetEvent(false);

                    var _ = subscription2.Run(x =>
                    {
                        foreach (var item in x.Items)
                        {
                            con2Docs.Add(item.Id);
                        }

                        mre.Set();
                        tcs.Task.Wait();
                    });

                    mre.WaitOne();

                    using (var session = store.OpenAsyncSession())
                    {
                        session.Delete("users/1");
                        await session.StoreAsync(new User(), "users/7");
                        await session.SaveChangesAsync();
                    }

                    var t = subscription.Run(x =>
                    {
                        foreach (var item in x.Items)
                        {
                            con1Docs.Add(item.Id);
                        }
                    });

                    Assert.True(await WaitForValueAsync(() => Task.FromResult(con2Docs.Count == 2), true, 6000, 100), $"connection 2 has {con2Docs.Count} docs");
                    Assert.True(await WaitForValueAsync(() => Task.FromResult(con1Docs.Count == 5), true, 6000, 100), $"connection 1 has {con1Docs.Count} docs");

                    Assert.Contains("users/7", con1Docs);
                    tcs.SetException(new InvalidOperationException());

                    await WaitForNoExceptionAsync(() => AssertNoLeftovers(store, id));
                }
            }
        }

        public async Task WaitForNoExceptionAsync(Func<Task> task, TimeSpan? timeToWait = null)
        {
            timeToWait ??= TimeSpan.FromSeconds(15);
            var sp = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    await task();
                    return;
                }
                catch
                {
                    if (sp.Elapsed > timeToWait)
                        throw;

                    await Task.Delay(25);
                }
            }
        }

        [RavenFact(RavenTestCategory.Subscriptions)]
        public async Task ResendChangedDocument()
        {
            using (var store = GetDocumentStore())
            {
                var id = store.Subscriptions.Create<User>();
                await using (var subscription = store.Subscriptions.GetSubscriptionWorker<User>(new SubscriptionWorkerOptions(id)
                {
                    TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                    Strategy = SubscriptionOpeningStrategy.Concurrent,
                    MaxDocsPerBatch = 2
                }))
                await using (var Subscription2 = store.Subscriptions.GetSubscriptionWorker<User>(new SubscriptionWorkerOptions(id)
                {
                    Strategy = SubscriptionOpeningStrategy.Concurrent,
                    TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                    MaxDocsPerBatch = 2
                }))
                {
                    using (var session = store.OpenSession())
                    {
                        session.Store(new User(), "users/1");
                        session.Store(new User(), "users/2");
                        session.Store(new User(), "users/3");
                        session.Store(new User(), "users/4");
                        session.Store(new User(), "users/5");
                        session.Store(new User(), "users/6");
                        session.SaveChanges();
                    }

                    var con1Docs = new List<string>();
                    var con2Docs = new List<string>();

                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var mre = new ManualResetEvent(false);

                    var _ = Subscription2.Run(x =>
                    {
                        foreach (var item in x.Items)
                        {
                            con2Docs.Add(item.Id);
                        }

                        mre.Set();
                        tcs.Task.Wait();
                    });

                    mre.WaitOne();

                    using (var session = store.OpenAsyncSession())
                    {
                        await session.StoreAsync(new User { Name = "Changed" }, "users/1");
                        await session.StoreAsync(new User(), "users/7");
                        await session.SaveChangesAsync();
                    }

                    var gotIt = false;
                    var t = subscription.Run(x =>
                    {
                        foreach (var item in x.Items)
                        {
                            con1Docs.Add(item.Id);
                            if (item.Result.Name == "Changed")
                            {
                                gotIt = true;
                            }
                        }
                    });

                    Assert.True(await WaitForValueAsync(() => Task.FromResult(con2Docs.Count == 2), true, 6000, 100), $"connection 2 has {con2Docs.Count} docs");
                    Assert.True(await WaitForValueAsync(() => Task.FromResult(con1Docs.Count == 5), true, 6000, 100), $"connection 1 has {con1Docs.Count} docs");

                    Assert.Contains("users/7", con1Docs);
                    tcs.SetException(new InvalidOperationException());

                    Assert.True(await WaitForValueAsync(() => Task.FromResult(gotIt), true), $"updated document didn't arrived");

                    await AssertNoLeftovers(store, id);
                }
            }
        }

        private async Task AssertNoLeftovers(IDocumentStore store, string id)
        {
            var db = await Databases.GetDocumentDatabaseInstanceFor(store);

            await AssertWaitForValueAsync(() =>
            {
                using (Server.ServerStore.Engine.ContextPool.AllocateOperationContext(out ClusterOperationContext ctx))
                using (ctx.OpenReadTransaction())
                {
                    return Task.FromResult(db.SubscriptionStorage.GetSubscriptionConnectionsState(ctx, id).GetNumberOfResendDocuments(SubscriptionType.Document));
                }
            }, 0);
        }

        [RavenFact(RavenTestCategory.Subscriptions)]
        public async Task ResendChangedDocument2()
        {
            using (var store = GetDocumentStore())
            {
                var id = store.Subscriptions.Create<User>();
                await using (var subscription = store.Subscriptions.GetSubscriptionWorker<User>(new SubscriptionWorkerOptions(id)
                {
                    TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                    Strategy = SubscriptionOpeningStrategy.Concurrent,
                    MaxDocsPerBatch = 2
                }))
                await using (var Subscription2 = store.Subscriptions.GetSubscriptionWorker<User>(new SubscriptionWorkerOptions(id)
                {
                    Strategy = SubscriptionOpeningStrategy.Concurrent,
                    TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                    MaxDocsPerBatch = 2
                }))
                {
                    using (var session = store.OpenSession())
                    {
                        session.Store(new User(), "users/1");
                        session.Store(new User(), "users/2");
                        session.Store(new User(), "users/3");
                        session.Store(new User(), "users/4");
                        session.Store(new User(), "users/5");
                        session.Store(new User(), "users/6");
                        session.SaveChanges();
                    }

                    var con1Docs = new List<string>();
                    var con2Docs = new List<string>();

                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var mre = new ManualResetEvent(false);

                    var _ = Subscription2.Run(x =>
                    {
                        foreach (var item in x.Items)
                        {
                            con2Docs.Add(item.Id);
                        }

                        mre.Set();
                        tcs.Task.Wait();
                    });

                    mre.WaitOne();

                    using (var session = store.OpenAsyncSession())
                    {
                        await session.StoreAsync(new User { Name = "Changed" }, "users/1");
                        await session.StoreAsync(new User { Name = "Changed" }, "users/2");
                        await session.StoreAsync(new User(), "users/7");
                        await session.SaveChangesAsync();
                    }

                    var gotIt = false;
                    var t = subscription.Run(x =>
                    {
                        foreach (var item in x.Items)
                        {
                            con1Docs.Add(item.Id);
                            if (item.Result.Name == "Changed")
                            {
                                gotIt = true;
                            }
                        }
                    });

                    Assert.True(await WaitForValueAsync(() => Task.FromResult(con2Docs.Count == 2), true, 6000, 100), $"connection 2 has {con2Docs.Count} docs");
                    Assert.True(await WaitForValueAsync(() => Task.FromResult(con1Docs.Count == 5), true, 6000, 100), $"connection 1 has {con1Docs.Count} docs");

                    Assert.Contains("users/7", con1Docs);
                    tcs.SetException(new InvalidOperationException());

                    Assert.True(await WaitForValueAsync(() => Task.FromResult(gotIt), true), $"updated document didn't arrived");
                    await AssertNoLeftovers(store, id);
                }
            }
        }

        [RavenFact(RavenTestCategory.Subscriptions)]
        public async Task ConcurrentSubscriptionMultipleNodes()
        {
            DebuggerAttachedTimeout.DisableLongTimespan = true;
            var cluster = await CreateRaftCluster(3, watcherCluster: true);

            using var store = GetDocumentStore(new Options
            {
                ReplicationFactor = 3,
                Server = cluster.Leader,
                ModifyDocumentStore = s => s.Conventions.LoadBalanceBehavior = LoadBalanceBehavior.UseSessionContext
            });

            var database = store.Database;

            var node1 = await cluster.Nodes[0].ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(database);
            var node2 = await cluster.Nodes[1].ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(database);
            var node3 = await cluster.Nodes[2].ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(database);

            var t1 = await BreakReplication(cluster.Nodes[0].ServerStore, database);
            var t2 = await BreakReplication(cluster.Nodes[1].ServerStore, database);
            var t3 = await BreakReplication(cluster.Nodes[2].ServerStore, database);

            using (var session = store.OpenAsyncSession())
            {
                session.Advanced.SessionInfo.SetContext("foo");
                await session.StoreAsync(new User(), "user/1");
                await session.StoreAsync(new User(), "user/2");
                await session.StoreAsync(new User(), "user/3");
                await session.SaveChangesAsync();
            }

            using (var session = store.OpenAsyncSession())
            {
                session.Advanced.SessionInfo.SetContext("bar");
                await session.StoreAsync(new User(), "user/4");
                await session.StoreAsync(new User(), "user/5");
                await session.StoreAsync(new User(), "user/6");
                await session.SaveChangesAsync();
            }

            t1.Mend();
            t2.Mend();
            t3.Mend();

            await WaitForDocumentInClusterAsync<User>(cluster.Nodes, database, "user/6", predicate: null, TimeSpan.FromSeconds(15));
            await WaitForDocumentInClusterAsync<User>(cluster.Nodes, database, "user/3", predicate: null, TimeSpan.FromSeconds(15));

            var id = store.Subscriptions.Create<User>(options: new SubscriptionCreationOptions
            {
                MentorNode = "A"
            });
            await using (var subscription = store.Subscriptions.GetSubscriptionWorker(new SubscriptionWorkerOptions(id)
            {
                TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                Strategy = SubscriptionOpeningStrategy.Concurrent,
                MaxDocsPerBatch = 2
            }))
            await using (var subscription2 = store.Subscriptions.GetSubscriptionWorker(new SubscriptionWorkerOptions(id)
            {
                Strategy = SubscriptionOpeningStrategy.Concurrent,
                TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                MaxDocsPerBatch = 2
            }))
            {
                var con1Docs = new List<string>();
                var con2Docs = new List<string>();
                var mre = new ManualResetEventSlim(false);
                var isDown = false;
                int lockServerDisposal = 0;
                subscription.AfterAcknowledgment += b =>
                {
                    if (b.NumberOfItemsInBatch > 0)
                    {
                        Interlocked.Increment(ref lockServerDisposal);
                        if (lockServerDisposal == 1)
                        {
                            if (isDown == false)
                            {
                                isDown = true;
                                mre.Set();
                            }
                        }
                        Interlocked.Decrement(ref lockServerDisposal);
                    }

                    return Task.CompletedTask;
                };
                subscription2.AfterAcknowledgment += b =>
                {
                    if (b.NumberOfItemsInBatch > 0)
                    {
                        Interlocked.Increment(ref lockServerDisposal);
                        if (lockServerDisposal == 1)
                        {
                            if (isDown == false)
                            {
                                isDown = true;
                                mre.Set();
                            }
                        }
                        Interlocked.Decrement(ref lockServerDisposal);
                    }

                    return Task.CompletedTask;
                };

                var t = subscription.Run(x =>
                {
                    foreach (var item in x.Items)
                    {
                        con1Docs.Add(item.Id);
                    }
                });

                var _ = subscription2.Run(x =>
                {
                    foreach (var item in x.Items)
                    {
                        con2Docs.Add(item.Id);
                    }
                });

                await AssertWaitForTrueAsync(() => Task.FromResult(con1Docs.Count + con2Docs.Count == 6), 6000, 100);
            }
        }

        [RavenFact(RavenTestCategory.Subscriptions)]
        public async Task DropSingleConnection()
        {
            using (var store = GetDocumentStore())
            {
                var id = store.Subscriptions.Create<User>();
                using (var worker = store.Subscriptions.GetSubscriptionWorker(new SubscriptionWorkerOptions(id)
                {
                    TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                    Strategy = SubscriptionOpeningStrategy.Concurrent,
                    MaxDocsPerBatch = 2
                }))
                using (var worker2 = store.Subscriptions.GetSubscriptionWorker(new SubscriptionWorkerOptions(id)
                {
                    Strategy = SubscriptionOpeningStrategy.Concurrent,
                    TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                    MaxDocsPerBatch = 2
                }))
                {
                    using (var session = store.OpenSession())
                    {
                        session.Store(new User(), "user/1");
                        session.Store(new User(), "user/2");
                        session.Store(new User(), "user/3");
                        session.Store(new User(), "user/4");
                        session.Store(new User(), "user/5");
                        session.Store(new User(), "user/6");
                        session.SaveChanges();
                    }

                    var mre1 = new AsyncManualResetEvent();
                    var mre2 = new AsyncManualResetEvent();

                    worker.OnEstablishedSubscriptionConnection += () =>
                    {
                        mre1.Set();
                    };
                    worker2.OnEstablishedSubscriptionConnection += () =>
                    {
                        mre2.Set();
                    };

                    var con1Docs = new List<string>();
                    var con2Docs = new List<string>();

                    var t = worker.Run(x =>
                    {
                        foreach (var item in x.Items)
                        {
                            con1Docs.Add(item.Id);
                        }
                    });

                    var _ = worker2.Run(x =>
                    {
                        foreach (var item in x.Items)
                        {
                            con2Docs.Add(item.Id);
                        }
                    });

                    await mre1.WaitAsync();
                    await mre2.WaitAsync();

                    store.Subscriptions.DropSubscriptionWorker(worker2);
                    var db = await Databases.GetDocumentDatabaseInstanceFor(store);
                    db.SubscriptionStorage.TryGetRunningSubscriptionConnectionsState(long.Parse(id), out var subscriptionConnectionsState);
                    Assert.Equal(1, subscriptionConnectionsState.GetConnections().Count);
                    await AssertWaitForTrueAsync(() => Task.FromResult(con1Docs.Count + con2Docs.Count == 6 || con1Docs.Count + con2Docs.Count == 8), 6000);
                    await AssertNoLeftovers(store, id);
                }
            }
        }

        [RavenTheory(RavenTestCategory.Subscriptions)]
        [InlineData(SubscriptionOpeningStrategy.TakeOver, SubscriptionOpeningStrategy.Concurrent)]
        [InlineData(SubscriptionOpeningStrategy.Concurrent, SubscriptionOpeningStrategy.TakeOver)]
        public async Task CannotConnectInDifferentMode(SubscriptionOpeningStrategy strategy1, SubscriptionOpeningStrategy strategy2)
        {
            using (var store = GetDocumentStore())
            {
                var id = store.Subscriptions.Create<User>();
                using var subscription1 = store.Subscriptions.GetSubscriptionWorker(new SubscriptionWorkerOptions(id)
                {
                    Strategy = strategy1,
                });
                using var subscription2 = store.Subscriptions.GetSubscriptionWorker(new SubscriptionWorkerOptions(id)
                {
                    Strategy = strategy2,
                });

                var mre1 = new ManualResetEventSlim();

                subscription1.OnEstablishedSubscriptionConnection += mre1.Set;

                var t = subscription1.Run(x =>
                {

                });

                Assert.True(mre1.Wait(TimeSpan.FromSeconds(15)));
                mre1.Reset();

                await Assert.ThrowsAsync<SubscriptionInUseException>(() => subscription2.Run((_) => { }).WaitAsync(TimeSpan.FromSeconds(5)));
                await store.Subscriptions.DropSubscriptionWorkerAsync(subscription1);
                await Assert.ThrowsAsync<SubscriptionClosedException>(() => t.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }

        [RavenTheory(RavenTestCategory.Subscriptions)]
        [InlineData(SubscriptionOpeningStrategy.TakeOver, SubscriptionOpeningStrategy.Concurrent)]
        [InlineData(SubscriptionOpeningStrategy.Concurrent, SubscriptionOpeningStrategy.TakeOver)]
        public async Task CanDropAndConnectInDifferentMode(SubscriptionOpeningStrategy strategy1, SubscriptionOpeningStrategy strategy2)
        {
            using (var store = GetDocumentStore())
            {
                var id = store.Subscriptions.Create<User>();
                using var subscription1 = store.Subscriptions.GetSubscriptionWorker(new SubscriptionWorkerOptions(id)
                {
                    Strategy = strategy1,
                });
                using var subscription2 = store.Subscriptions.GetSubscriptionWorker(new SubscriptionWorkerOptions(id)
                {
                    Strategy = strategy2,
                });

                var mre1 = new ManualResetEventSlim();
                var mre2 = new ManualResetEventSlim();

                subscription1.OnEstablishedSubscriptionConnection += mre1.Set;
                subscription2.OnEstablishedSubscriptionConnection += mre2.Set;

                var t = subscription1.Run(x =>
                {

                });

                Assert.True(mre1.Wait(TimeSpan.FromSeconds(15)));
                mre1.Reset();

                await store.Subscriptions.DropSubscriptionWorkerAsync(subscription1);
                await Assert.ThrowsAsync<SubscriptionClosedException>(() => t);

                t = subscription2.Run((_) => { });
                Assert.True(mre2.Wait(TimeSpan.FromSeconds(15)));
            }
        }

        [RavenFact(RavenTestCategory.Subscriptions | RavenTestCategory.Licensing)]
        public async Task ThrowOnInvalidLicense()
        {
            DoNotReuseServer();
            using (var store = GetDocumentStore())
            {
                Server.ServerStore.LicenseManager.LicenseStatus.Attributes[LicenseAttribute.ConcurrentSubscriptions] = false;

                var id = store.Subscriptions.Create<User>();
                using (var subscription = store.Subscriptions.GetSubscriptionWorker(new SubscriptionWorkerOptions(id)
                {
                    Strategy = SubscriptionOpeningStrategy.Concurrent,
                }))
                {
                    var t = subscription.Run(x =>
                    {

                    });

                    var ex = await Assert.ThrowsAsync<SubscriptionInvalidStateException>(() => t.WaitAndThrowOnTimeout(TimeSpan.FromSeconds(15)));
                    Assert.Contains("Your current license doesn't include the Concurrent Subscriptions feature", ex.ToString());
                }
            }
        }

        [RavenFact(RavenTestCategory.Subscriptions)]
        public async Task ShouldClearOldItemsFromResendListOnBatchProcessing()
        {
            using (var store = GetDocumentStore())
            {
                var collectionSize = 1000;
                using (var session = store.OpenSession())
                {

                    for (int i = 0; i < collectionSize; i++)
                    {
                        session.Store(new User() { Name = "E" }, $"user/{i}");
                    }

                    session.SaveChanges();
                }

                var mre = new AsyncManualResetEvent();
                var id = store.Subscriptions.Create(new SubscriptionCreationOptions()
                {
                    Query = "from Users where Name != 'R'"
                });

                var count = 0;
                using (var subscription = store.Subscriptions.GetSubscriptionWorker<User>(new SubscriptionWorkerOptions(id)
                {
                    TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(1),
                    Strategy = SubscriptionOpeningStrategy.Concurrent,
                    MaxDocsPerBatch = 64
                }))
                {
                    var t = subscription.Run(async x =>
                    {
                        using var s = x.OpenAsyncSession();
                        foreach (var item in x.Items)
                        {
                            item.Result.Name = "G";
                        }

                        await s.SaveChangesAsync();

                        foreach (var item in x.Items)
                        {
                            item.Result.Name = "R";
                        }

                        await s.SaveChangesAsync();

                        var res = Interlocked.Add(ref count, x.NumberOfItemsInBatch);
                        if (res >= collectionSize)
                        {
                            mre.Set();
                        }

                    });

                    Assert.True(await mre.WaitAsync(TimeSpan.FromSeconds(60)));

                    using (var session = store.OpenSession())
                    {
                        var u = session.Load<User>("user/1");
                        Assert.Equal("R", u.Name);
                    }

                    var executor = store.GetRequestExecutor();
                    using var _ = executor.ContextPool.AllocateOperationContext(out var ctx);
                    var cmd = new GetSubscriptionResendListCommand(store.Database, id);
                    executor.Execute(cmd, ctx);
                    var res = cmd.Result;

                    // the last batch may still not be cleared
                    Assert.True(res.Results.Count is 0 or 40, "res.Results.Count is 0 or 40");

                    var finalRes = await WaitForValueAsync(() =>
                    {
                        executor.Execute(cmd, ctx);
                        var res = cmd.Result;
                        return res.Results.Count;
                    }, 0);

                    Assert.Equal(0, finalRes);
                }
            }
        }

        [Fact]
        public async Task ShouldClearSubscriptionInfoFromStorageAfterDatabaseDeletion()
        {
            DoNotReuseServer();
            const int expectedNumberOfDocsToResend = 7;

            long id;
            string databaseName = GetDatabaseName();
            using (var store = GetDocumentStore(new Options { ModifyDatabaseName = _ => databaseName }))
            {
                var subscriptionId = await store.Subscriptions.CreateAsync<User>();
                id = long.Parse(subscriptionId);
                await using var subscriptionWorker = store.Subscriptions.GetSubscriptionWorker(new SubscriptionWorkerOptions(subscriptionId)
                {
                    Strategy = SubscriptionOpeningStrategy.Concurrent,
                    TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(2),
                    MaxDocsPerBatch = expectedNumberOfDocsToResend
                });

                using (var session = store.OpenSession())
                {
                    for (int i = 0; i < 10; i++)
                        session.Store(new User { Name = $"UserNo{i}" });

                    session.SaveChanges();
                }

                _ = subscriptionWorker.Run(x =>
                {
                    var tcs = new TaskCompletionSource<bool>();
                    tcs.Task.Wait();
                });

                await AssertWaitForValueAsync(() => Task.FromResult(AbstractSubscriptionConnectionsState.GetNumberOfResendDocuments(Server.ServerStore, store.Database, SubscriptionType.Document, id)), expectedNumberOfDocsToResend);
            }

            // Upon disposing of the store, the database gets deleted.
            // Then we recreate the database to ensure no leftover subscription data from the previous instance.
            using (var _ = GetDocumentStore(new Options { ModifyDatabaseName = _ => databaseName }))
            {
                var count = AbstractSubscriptionConnectionsState.GetNumberOfResendDocuments(Server.ServerStore, databaseName, SubscriptionType.Document, id);

                Assert.Equal(0, count);
            }
        }

        [RavenTheory(RavenTestCategory.Subscriptions)]
        [InlineData(1)]
        [InlineData(3)]
        public async Task ConcurrentSubscriptionsShouldContinueProcessOnNewConnections(int count)
        {
            using (var store = GetDocumentStore())
            {
                var id = await store.Subscriptions.CreateAsync<User>();
                var docs = new HashSet<string>();

                for (int i = 0; i < 10; i++)
                {
                    using (var session = store.OpenSession())
                    {
                        session.Store(new User(), $"users/{i}");
                        session.SaveChanges();
                    }

                    var workers = new List<SubscriptionWorker<User>>();
                    for (int j = 0; j < count; j++)
                    {
                        workers.Add(store.Subscriptions.GetSubscriptionWorker<User>(new SubscriptionWorkerOptions(id)
                        {
                            TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(1),
                            Strategy = SubscriptionOpeningStrategy.Concurrent,
                            MaxDocsPerBatch = 1
                        }));
                    }

                    try
                    {
                        foreach (var worker in workers)
                        {
                            var t = worker.Run(x =>
                            {
                                foreach (var item in x.Items)
                                {
                                    docs.Add(item.Id);
                                }
                            });
                        }

                        await AssertWaitForTrueAsync(() => Task.FromResult(docs.Count == i + 1), Convert.ToInt32(_reasonableWaitTime.TotalMilliseconds));
                        await AssertNoLeftovers(store, id);
                    }
                    finally
                    {
                        foreach (var w in workers)
                        {
                            await w.DisposeAsync();
                        }
                    }
                }
            }
        }

        [RavenTheory(RavenTestCategory.Subscriptions)]
        [InlineData(1)]
        [InlineData(3)]
        public async Task ConcurrentSubscriptionsShouldContinueProcessOnNewConnectionsAfterUpdate(int count)
        {
            using (var store = GetDocumentStore())
            {
                var id = await store.Subscriptions.CreateAsync<User>(options: new SubscriptionCreationOptions<User>()
                {
                    Filter = user => user.Age == 0
                });

                var docs = new HashSet<string>();

                for (int i = 0; i < 10; i++)
                {
                    if (i > 0)
                    {
                        await store.Subscriptions.UpdateAsync(options: new SubscriptionUpdateOptions()
                        {
                            Name = id,
                            Query = @$"declare function predicate() {{ return this.Age==={i} }}
from 'Users' as doc
where predicate.call(doc)"
                        });
                    }

                    using (var session = store.OpenSession())
                    {
                        session.Store(new User()
                        {
                            Age = i
                        }, $"users/{i}");
                        session.SaveChanges();
                    }

                    var workers = new List<SubscriptionWorker<User>>();
                    for (int j = 0; j < count; j++)
                    {
                        workers.Add(store.Subscriptions.GetSubscriptionWorker<User>(new SubscriptionWorkerOptions(id)
                        {
                            TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(1),
                            Strategy = SubscriptionOpeningStrategy.Concurrent,
                            MaxDocsPerBatch = 1
                        }));
                    }

                    try
                    {
                        foreach (var worker in workers)
                        {
                            var t = worker.Run(x =>
                            {
                                foreach (var item in x.Items)
                                {
                                    docs.Add(item.Id);
                                }
                            });
                        }

                        await AssertWaitForTrueAsync(() => Task.FromResult(docs.Count == i + 1), Convert.ToInt32(_reasonableWaitTime.TotalMilliseconds));
                        await AssertNoLeftovers(store, id);
                    }
                    finally
                    {
                        foreach (var w in workers)
                        {
                            await w.DisposeAsync();
                        }
                    }
                }

                var subs = await store.Subscriptions.GetSubscriptionsAsync(0, 1024);
                Assert.Equal(1, subs.Count);
            }
        }

        [RavenFact(RavenTestCategory.Subscriptions)]
        public async Task ConcurrentSubscriptionsShouldContinueProcessOnNewConnectionsAfterUpdate_AndDisposeWhileConnecting()
        {
            using (var store = GetDocumentStore())
            {
                var id = await store.Subscriptions.CreateAsync<User>(options: new SubscriptionCreationOptions<User>()
                {
                    Filter = user => user.Age == 0
                });

                var docs = new HashSet<string>();
                var workers = new List<SubscriptionWorker<User>>();
                try
                {
                    for (int i = 0; i < 10; i++)
                    {
                        if (i > 0)
                        {
                            // we update the subscription on each iteration
                            // so it will process only the new created document
                            await store.Subscriptions.UpdateAsync(options: new SubscriptionUpdateOptions()
                            {
                                Name = id,
                                Query = @$"declare function predicate() {{ return this.Age==={i} }}
from 'Users' as doc
where predicate.call(doc)"
                            });
                        }

                        using (var session = store.OpenSession())
                        {
                            session.Store(new User()
                            {
                                Age = i
                            }, $"users/{i}");
                            session.SaveChanges();
                        }

                        var w = store.Subscriptions.GetSubscriptionWorker<User>(new SubscriptionWorkerOptions(id)
                        {
                            TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(1),
                            Strategy = SubscriptionOpeningStrategy.Concurrent,
                            MaxDocsPerBatch = 1
                        });
                        workers.Add(w);

                        var t2 = w.Run(x =>
                        {
                            foreach (var item in x.Items)
                            {
                                docs.Add(item.Id);
                                Thread.Sleep(1000);
                            }
                        });

                        await AssertWaitForTrueAsync(() => Task.FromResult(docs.Count == i + 1), Convert.ToInt32(_reasonableWaitTime.TotalMilliseconds));
                        await AssertNoLeftovers(store, id);
                    }
                }
                finally
                {
                    foreach (var w in workers)
                    {
                        await w.DisposeAsync();
                    }
                }

                var subs = await store.Subscriptions.GetSubscriptionsAsync(0, 1024);
                Assert.Equal(1, subs.Count);
            }
        }

        [RavenFact(RavenTestCategory.Subscriptions)]
        public async Task ConcurrentSubscriptionsShouldProcessWhen_2_ConnectionsAreSubscribingConcurrently()
        {
            using (var store = GetDocumentStore())
            {
                var id = await store.Subscriptions.CreateAsync<User>();

                var db = await Databases.GetDocumentDatabaseInstanceFor(store);
                var testingStuff = db.ForTestingPurposesOnly();

                using (testingStuff.CallDuringWaitForSubscribe(connections =>
                {
                    while (connections.Count < 2)
                    {
                        Thread.Sleep(111);
                    }
                }))
                {
                    await using (var subscription = store.Subscriptions.GetSubscriptionWorker(new SubscriptionWorkerOptions(id)
                    {
                        TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                        Strategy = SubscriptionOpeningStrategy.Concurrent,
                        MaxDocsPerBatch = 2
                    }))
                    await using (var secondSubscription = store.Subscriptions.GetSubscriptionWorker(new SubscriptionWorkerOptions(id)
                    {
                        Strategy = SubscriptionOpeningStrategy.Concurrent,
                        TimeToWaitBeforeConnectionRetry = TimeSpan.FromSeconds(5),
                        MaxDocsPerBatch = 2
                    }))
                    {
                        using (var session = store.OpenSession())
                        {
                            session.Store(new User(), "user/1");
                            session.Store(new User(), "user/2");
                            session.Store(new User(), "user/3");
                            session.Store(new User(), "user/4");
                            session.Store(new User(), "user/5");
                            session.Store(new User(), "user/6");
                            session.SaveChanges();
                        }

                        var con1Docs = new List<string>();
                        var con2Docs = new List<string>();

                        var t = subscription.Run(x =>
                        {
                            foreach (var item in x.Items)
                            {
                                con1Docs.Add(item.Id);
                            }
                        });

                        var _ = secondSubscription.Run(x =>
                        {
                            foreach (var item in x.Items)
                            {
                                con2Docs.Add(item.Id);
                            }
                        });

                        await AssertWaitForTrueAsync(() => Task.FromResult(con1Docs.Count + con2Docs.Count == 6), Convert.ToInt32(_reasonableWaitTime.TotalMilliseconds));
                        await AssertNoLeftovers(store, id);
                    }
                }
            }
        }
        internal class GetSubscriptionResendListCommand : RavenCommand<ResendListResult>
        {
            private readonly string _database;
            private readonly string _name;

            public GetSubscriptionResendListCommand(string database, string name)
            {
                _database = database;
                _name = name;
            }

            public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
            {
                url = $"{node.Url}/databases/{_database}/debug/subscriptions/resend?name={_name}";

                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Get,
                };

                return request;
            }

            public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject response, bool fromCache)
            {
                if (response == null)
                {
                    Result = null;
                    return;
                }

                var deserialize = JsonDeserializationBase.GenerateJsonDeserializationRoutine<ResendListResult>();
                Result = deserialize.Invoke(response);
            }

            public override bool IsReadRequest => true;
        }

        internal class ResendListResult
        {
#pragma warning disable CS0649
            public List<ResendItem> Results;
#pragma warning restore CS0649
        }

        private class User
        {
            public string Name;
            public int Age;
        }
    }
}
