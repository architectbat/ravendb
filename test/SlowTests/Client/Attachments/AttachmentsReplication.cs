﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FastTests;
using FastTests.Utils;
using Orders;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Commands;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Operations.Attachments;
using Raven.Client.Documents.Operations.Replication;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.Documents.Session;
using Raven.Client.Json;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Client.ServerWide.Operations.Configuration;
using Raven.Server;
using Raven.Server.Config;
using Raven.Server.Documents;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Raven.Tests.Core.Utils.Entities;
using Sparrow.Json;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Client.Attachments
{
    public class AttachmentsReplication : ReplicationTestBase
    {
        public AttachmentsReplication(ITestOutputHelper output) : base(output)
        {
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(true, DatabaseMode = RavenDatabaseMode.All)]
        [RavenData(false, DatabaseMode = RavenDatabaseMode.All)]
        public async Task PutAttachments(Options options, bool replicateDocumentFirst)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                using (var session = store1.OpenSession())
                {
                    session.Store(new User { Name = "Fitzchak" }, "users/1");
                    session.SaveChanges();
                }
                if (replicateDocumentFirst)
                {
                    await SetupAttachmentReplicationAsync(store1, store2, false);
                    Assert.True(WaitForDocument(store2, "users/1"));
                }

                var names = new[]
                {
                    "profile.png",
                    "background-photo.jpg",
                    "fileNAME_#$1^%_בעברית.txt"
                };
                using (var profileStream = new MemoryStream(new byte[] { 1, 2, 3 }))
                {
                    var result = store1.Operations.Send(new PutAttachmentOperation("users/1", names[0], profileStream, "image/png"));
                    Assert.Equal(names[0], result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("image/png", result.ContentType);
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", result.Hash);
                }
                using (var backgroundStream = new MemoryStream(new byte[] { 10, 20, 30, 40, 50 }))
                {
                    var result = store1.Operations.Send(new PutAttachmentOperation("users/1", names[1], backgroundStream, "ImGgE/jPeG"));
                    Assert.Equal(names[1], result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("ImGgE/jPeG", result.ContentType);
                    Assert.Equal("igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=", result.Hash);
                }
                using (var fileStream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }))
                {
                    var result = store1.Operations.Send(new PutAttachmentOperation("users/1", names[2], fileStream, null));
                    Assert.Equal(names[2], result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("", result.ContentType);
                    Assert.Equal("Arg5SgIJzdjSTeY6LYtQHlyNiTPmvBLHbr/Cypggeco=", result.Hash);
                }

                using (var session = store1.OpenSession())
                {
                    session.Store(new User { Name = "Marker" }, "marker$users/1");
                    session.SaveChanges();
                }
                if (replicateDocumentFirst == false)
                {
                    await SetupAttachmentReplicationAsync(store1, store2, false);
                }
                Assert.True(WaitForDocument(store2, "marker$users/1"));

                using (var session = store2.OpenSession())
                {
                    var user = session.Load<User>("users/1");
                    var metadata = session.Advanced.GetMetadataFor(user);
                    Assert.Equal((DocumentFlags.HasAttachments | DocumentFlags.FromReplication).ToString(), metadata[Constants.Documents.Metadata.Flags]);
                    var attachments = metadata.GetObjects(Constants.Documents.Metadata.Attachments);
                    Assert.Equal(3, attachments.Length);
                    var orderedNames = names.OrderBy(x => x).ToArray();
                    for (var i = 0; i < names.Length; i++)
                    {
                        var name = orderedNames[i];
                        var attachment = attachments[i];
                        Assert.Equal(name, attachment.GetString(nameof(AttachmentName.Name)));
                        var hash = attachment.GetString(nameof(AttachmentName.Hash));
                        if (i == 0)
                        {
                            Assert.Equal("igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=", hash);
                        }
                        else if (i == 1)
                        {
                            Assert.Equal("Arg5SgIJzdjSTeY6LYtQHlyNiTPmvBLHbr/Cypggeco=", hash);
                        }
                        else if (i == 2)
                        {
                            Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", hash);
                        }
                    }
                }

                await AssertAttachmentCount(store2, 3, 3, 2);

                using (var session = store2.OpenSession())
                {
                    var readBuffer = new byte[8];
                    for (var i = 0; i < names.Length; i++)
                    {
                        var name = names[i];
                        using (var attachmentStream = new MemoryStream(readBuffer))
                        using (var attachment = session.Advanced.Attachments.Get("users/1", name))
                        {
                            attachment.Stream.CopyTo(attachmentStream);

                            Assert.Equal(name, attachment.Details.Name);
                            Assert.Equal(i == 0 ? 3 : 5, attachmentStream.Position);
                            if (i == 0)
                            {
                                Assert.Equal(new byte[] { 1, 2, 3 }, readBuffer.Take(3));
                                Assert.Equal("image/png", attachment.Details.ContentType);
                                Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachment.Details.Hash);
                            }
                            else if (i == 1)
                            {
                                Assert.Equal(new byte[] { 10, 20, 30, 40, 50 }, readBuffer.Take(5));
                                Assert.Equal("ImGgE/jPeG", attachment.Details.ContentType);
                                Assert.Equal("igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=", attachment.Details.Hash);
                            }
                            else if (i == 2)
                            {
                                Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, readBuffer.Take(5));
                                Assert.Equal("", attachment.Details.ContentType);
                                Assert.Equal("Arg5SgIJzdjSTeY6LYtQHlyNiTPmvBLHbr/Cypggeco=", attachment.Details.Hash);
                            }
                        }
                    }

                    using (var notExistsAttachment = session.Advanced.Attachments.Get("users/1", "not-there"))
                    {
                        Assert.Null(notExistsAttachment);
                    }
                }
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData("\r\n", null, DatabaseMode = RavenDatabaseMode.All)]
        [RavenData("\\", "\\", DatabaseMode = RavenDatabaseMode.All)]
        [RavenData("/", "/", DatabaseMode = RavenDatabaseMode.All)]
        [RavenData("5", "5", DatabaseMode = RavenDatabaseMode.All)]
        public async Task PutAndGetSpecialChar(Options options, string nameAndContentType, string expectedContentType)
        {
            var name = "aA" + nameAndContentType;
            if (expectedContentType != null)
                expectedContentType = "aA" + expectedContentType;

            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                using (var session = store1.OpenSession())
                {
                    session.Store(new User { Name = "Fitzchak" }, "users/1");
                    session.SaveChanges();
                }
                using (var profileStream = new MemoryStream(new byte[] { 1, 2, 3 }))
                {
                    var result = store1.Operations.Send(new PutAttachmentOperation("users/1", name, profileStream, name));
                    Assert.Contains("A:2", result.ChangeVector);
                    Assert.Equal(name, result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal(name, result.ContentType);
                }

                await SetupAttachmentReplicationAsync(store1, store2, false);
                Assert.True(WaitForDocument(store2, "users/1"));

                using (var session = store2.OpenSession())
                {
                    var user = session.Load<User>("users/1");
                    var metadata = session.Advanced.GetMetadataFor(user);
                    Assert.Equal((DocumentFlags.HasAttachments | DocumentFlags.FromReplication).ToString(), metadata[Constants.Documents.Metadata.Flags]);
                    var attachments = metadata.GetObjects(Constants.Documents.Metadata.Attachments);
                    var attachment = attachments.Single();
                    Assert.Equal(name, attachment.GetString(nameof(AttachmentName.Name)));
                }

                using (var session = store2.OpenSession())
                {
                    var readBuffer = new byte[8];
                    using (var attachmentStream = new MemoryStream(readBuffer))
                    using (var attachment = session.Advanced.Attachments.Get("users/1", name))
                    {
                        attachment.Stream.CopyTo(attachmentStream);
                        Assert.Equal(name, attachment.Details.Name);
                        Assert.Equal(new byte[] { 1, 2, 3 }, readBuffer.Take(3));
                        Assert.Equal(expectedContentType, attachment.Details.ContentType);
                    }
                }
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task DeleteAttachments(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                using (var session = store1.OpenSession())
                {
                    session.Store(new User { Name = "Fitzchak" }, "users/1");
                    session.SaveChanges();
                }

                for (int i = 1; i <= 3; i++)
                {
                    using (var profileStream = new MemoryStream(Enumerable.Range(1, 3 * i).Select(x => (byte)x).ToArray()))
                        store1.Operations.Send(new PutAttachmentOperation("users/1", "file" + i, profileStream, "image/png"));
                }
                await AssertAttachmentCount(store1, 3);

                store1.Operations.Send(new DeleteAttachmentOperation("users/1", "file2"));

                await SetupAttachmentReplicationAsync(store1, store2);
                await AssertAttachmentCount(store2, 2);

                using (var session = store2.OpenSession())
                {
                    var user = session.Load<User>("users/1");
                    var metadata = session.Advanced.GetMetadataFor(user);
                    Assert.Equal((DocumentFlags.HasAttachments | DocumentFlags.FromReplication).ToString(), metadata[Constants.Documents.Metadata.Flags]);
                    var attachments = metadata.GetObjects(Constants.Documents.Metadata.Attachments);
                    Assert.Equal(2, attachments.Length);
                    Assert.Equal("file1", attachments[0].GetString(nameof(AttachmentName.Name)));
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachments[0].GetString(nameof(AttachmentName.Hash)));
                    Assert.Equal("file3", attachments[1].GetString(nameof(AttachmentName.Name)));
                    Assert.Equal("NRQuixiqj+xvEokF6MdQq1u+uH1dk/gk2PLChJQ58Vo=", attachments[1].GetString(nameof(AttachmentName.Hash)));
                }

                using (var session = store2.OpenSession())
                {
                    var readBuffer = new byte[16];
                    using (var attachmentStream = new MemoryStream(readBuffer))
                    using (var attachment = session.Advanced.Attachments.Get("users/1", "file1"))
                    {
                        attachment.Stream.CopyTo(attachmentStream);
                        Assert.Contains("A:2", attachment.Details.ChangeVector);
                        Assert.Equal("file1", attachment.Details.Name);
                        Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachment.Details.Hash);
                        Assert.Equal(3, attachmentStream.Position);
                        Assert.Equal(new byte[] { 1, 2, 3 }, readBuffer.Take(3));
                    }
                    using (var attachmentStream = new MemoryStream(readBuffer))
                    using (var attachment = session.Advanced.Attachments.Get("users/1", "file3"))
                    {
                        attachment.Stream.CopyTo(attachmentStream);
                        Assert.Contains("A:6", attachment.Details.ChangeVector);
                        Assert.Equal("file3", attachment.Details.Name);
                        Assert.Equal("NRQuixiqj+xvEokF6MdQq1u+uH1dk/gk2PLChJQ58Vo=", attachment.Details.Hash);
                        Assert.Equal(9, attachmentStream.Position);
                        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, readBuffer.Take(9));
                    }
                }

                // Delete document should delete all the attachments
                store1.Commands().Delete("users/1", null);
                using (var session = store1.OpenSession())
                {
                    session.Store(new User { Name = "Marker 2" }, "users/1$marker2");
                    session.SaveChanges();
                }
                Assert.True(WaitForDocument(store2, "users/1$marker2"));
                await AssertAttachmentCount(store2, 0);
            }
        }

        public async ValueTask AssertAttachmentCount(DocumentStore store, long uniqueAttachmentCount, long? attachmentCount = null, long? documentsCount = null)
        {
            var statistics = await GetDatabaseStatisticsAsync(store);
            Assert.Equal(attachmentCount ?? uniqueAttachmentCount, statistics.CountOfAttachments);
            Assert.Equal(uniqueAttachmentCount, statistics.CountOfUniqueAttachments);

            if (documentsCount != null)
                Assert.Equal(documentsCount.Value, statistics.CountOfDocuments);
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task PutAndDeleteAttachmentsWithTheSameStream_AlsoTestBigStreams(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                for (int i = 1; i <= 3; i++)
                {
                    using (var session = store1.OpenSession())
                    {
                        session.Store(new User { Name = "Fitzchak " + i }, "users/" + i);
                        session.SaveChanges();
                    }

                    // Use 128 KB file to test hashing a big file (> 32 KB)
                    using (var stream1 = new MemoryStream(Enumerable.Range(1, 128 * 1024).Select(x => (byte)x).ToArray()))
                        store1.Operations.Send(new PutAttachmentOperation("users/" + i, "file" + i, stream1, "image/png"));
                }
                using (var stream2 = new MemoryStream(Enumerable.Range(1, 999 * 1024).Select(x => (byte)x).ToArray()))
                    store1.Operations.Send(new PutAttachmentOperation("users/1", "big-file", stream2, "image/png"));

                await SetupAttachmentReplicationAsync(store1, store2);
                await EnsureReplicatingAsync(store1, store2);

                await AssertAttachmentCount(store2, 2, 4);

                using (var session = store2.OpenSession())
                {
                    var readBuffer = new byte[1024 * 1024];
                    using (var attachmentStream = new MemoryStream(readBuffer))
                    using (var attachment = session.Advanced.Attachments.Get("users/3", "file3"))
                    {
                        attachment.Stream.CopyTo(attachmentStream);
                        Assert.Equal("file3", attachment.Details.Name);
                        Assert.Equal("uuBtr5rVX6NAXzdW2DhuG04MGGyUzFzpS7TelHw3fJQ=", attachment.Details.Hash);
                        Assert.Equal(128 * 1024, attachmentStream.Position);
                        var expected = Enumerable.Range(1, 128 * 1024).Select(x => (byte)x);
                        var actual = readBuffer.Take((int)attachmentStream.Position);
                        Assert.Equal(expected, actual);
                    }
                    using (var attachmentStream = new MemoryStream(readBuffer))
                    using (var attachment = session.Advanced.Attachments.Get("users/1", "big-file"))
                    {
                        attachment.Stream.CopyTo(attachmentStream);
                        Assert.Equal("big-file", attachment.Details.Name);
                        Assert.Equal("zKHiLyLNRBZti9DYbzuqZ/EDWAFMgOXB+SwKvjPAINk=", attachment.Details.Hash);
                        Assert.Equal(999 * 1024, attachmentStream.Position);
                        Assert.Equal(Enumerable.Range(1, 999 * 1024).Select(x => (byte)x), readBuffer.Take((int)attachmentStream.Position));
                    }
                }

                store1.Operations.Send(new DeleteAttachmentOperation("users/1", "file1"));
                await AssertDeleteAsync(store1, store2, "file1", 2, 3);

                store1.Operations.Send(new DeleteAttachmentOperation("users/2", "file2"));
                await AssertDeleteAsync(store1, store2, "file2", 2);

                store1.Operations.Send(new DeleteAttachmentOperation("users/3", "file3"));
                await AssertDeleteAsync(store1, store2, "file3", 1);

                store1.Operations.Send(new DeleteAttachmentOperation("users/1", "big-file"));
                await AssertDeleteAsync(store1, store2, "big-file", 0);

                for (int i = 1; i <= 3; i++)
                {
                    using (var session = store2.OpenSession())
                    {
                        var user = session.Load<User>("users/" + i);
                        var metadata = session.Advanced.GetMetadataFor(user);
                        Assert.DoesNotContain(DocumentFlags.HasAttachments.ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));
                        Assert.Equal(DocumentFlags.FromReplication.ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));
                        Assert.False(metadata.ContainsKey(Constants.Documents.Metadata.Attachments));
                    }
                }
            }
        }

        private async Task SetupAttachmentReplicationAsync(DocumentStore store1, DocumentStore store2, bool waitOnMarker = true)
        {
            var settings = new Dictionary<string, string>()
            {
                {"Replication.MaxItemsCount" , null},
                {"Replication.MaxSizeToSend" , null}
            };
            await store1.Maintenance.SendAsync(new PutDatabaseSettingsOperation(store1.Database, settings));
            await SetupReplicationAsync(store1, store2);

            if (waitOnMarker)
            {
                WaitForMarker(store1, store2);
            }
        }

        private void WaitForMarker(DocumentStore store1, DocumentStore store2, string id = null)
        {
            id ??= "marker - " + Guid.NewGuid();
            using (var session = store1.OpenSession())
            {
                session.Store(new Product { Name = "Marker" }, id);
                session.SaveChanges();
            }
            Assert.True(WaitForDocument(store2, id));
        }

        private async ValueTask AssertDeleteAsync(DocumentStore store1, DocumentStore store2, string name, long expectedUniqueAttachments, long? expectedAttachments = null)
        {
            using (var session = store1.OpenSession())
            {
                session.Store(new User { Name = "Marker " + name }, "marker-" + name);
                session.SaveChanges();
            }
            Assert.True(WaitForDocument(store2, "marker-" + name));
            await AssertAttachmentCount(store2, expectedUniqueAttachments, expectedAttachments);
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task DeleteDocumentWithAttachmentsThatHaveTheSameStream(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                for (int i = 1; i <= 3; i++)
                {
                    using (var session = store1.OpenSession())
                    {
                        session.Store(new User { Name = "Fitzchak " + i }, "users/" + i);
                        session.SaveChanges();
                    }

                    using (var profileStream = new MemoryStream(Enumerable.Range(1, 3).Select(x => (byte)x).ToArray()))
                        store1.Operations.Send(new PutAttachmentOperation("users/" + i, "file" + i, profileStream, "image/png"));
                }
                using (var profileStream = new MemoryStream(Enumerable.Range(1, 17).Select(x => (byte)x).ToArray()))
                    store1.Operations.Send(new PutAttachmentOperation("users/1", "second-file", profileStream, "image/png"));

                await SetupAttachmentReplicationAsync(store1, store2);
                await AssertAttachmentCount(store2, 2, 4);

                await store1.Commands().DeleteAsync("users/2", null);
                await AssertDeleteAsync(store1, store2, "#1$users/2", 2, 3);

                await store1.Commands().DeleteAsync("users/1", null);
                await AssertDeleteAsync(store1, store2, "#2$users/1", 1);

                await store1.Commands().DeleteAsync("users/3", null);
                await AssertDeleteAsync(store1, store2, "#3$users/3", 0);
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task AttachmentsRevisionsReplicationAfterEnable(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                using (var session = store1.OpenSession())
                {
                    session.Store(new User { Name = "Fitzchak" }, "users/1");
                    session.SaveChanges();
                }

                var names = new[] { "profile.png", "background-photo.jpg", "fileNAME_#$1^%_בעברית.txt" };

                using (var profileStream = new MemoryStream(new byte[] { 1, 2, 3 }))
                using (var backgroundStream = new MemoryStream(new byte[] { 10, 20, 30, 40, 50 }))
                using (var fileStream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }))
                {
                    var result = store1.Operations.Send(new PutAttachmentOperation("users/1", names[0], profileStream, "image/png"));
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", result.Hash);

                    result = store1.Operations.Send(new PutAttachmentOperation("users/1", names[1], backgroundStream, "ImGgE/jPeG"));
                    Assert.Equal("igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=", result.Hash);

                    result = store1.Operations.Send(new PutAttachmentOperation("users/1", names[2], fileStream, null));
                    Assert.Equal("Arg5SgIJzdjSTeY6LYtQHlyNiTPmvBLHbr/Cypggeco=", result.Hash);
                }

                await RevisionsHelper.SetupRevisionsAsync(store1, modifyConfiguration: configuration =>
                {
                    configuration.Collections["Users"].PurgeOnDelete = false;
                    configuration.Collections["Users"].MinimumRevisionsToKeep = 4;
                });
                await RevisionsHelper.SetupRevisionsAsync(store2, modifyConfiguration: configuration =>
                {
                    configuration.Collections["Users"].PurgeOnDelete = false;
                    configuration.Collections["Users"].MinimumRevisionsToKeep = 4;
                });

                await SetupAttachmentReplicationAsync(store1, store2);
                await SetupAttachmentReplicationAsync(store2, store1);

                await store1.Operations.SendAsync(new EnforceRevisionsConfigurationOperation());
                await store2.Operations.SendAsync(new EnforceRevisionsConfigurationOperation());

                var stats1 = await GetDatabaseStatisticsAsync(store1);
                var stats2 = await GetDatabaseStatisticsAsync(store2);

                Assert.Equal(stats1.CountOfDocuments, stats2.CountOfDocuments);
                Assert.Equal(stats1.CountOfRevisionDocuments, stats2.CountOfRevisionDocuments);
                Assert.Equal(stats1.CountOfAttachments, stats2.CountOfAttachments);
                Assert.Equal(stats1.CountOfUniqueAttachments, stats2.CountOfUniqueAttachments);

                using (var session = store1.OpenSession())
                {
                    var u = session.Load<User>("users/1");
                    u.Age = 30;
                    session.SaveChanges();
                }

                WaitForMarker(store1, store2, "marker1$users/1");
                WaitForMarker(store2, store1, "marker2$users/1");

                await store1.Operations.SendAsync(new EnforceRevisionsConfigurationOperation());
                await store2.Operations.SendAsync(new EnforceRevisionsConfigurationOperation());

                stats1 = await GetDatabaseStatisticsAsync(store1);
                stats2 = await GetDatabaseStatisticsAsync(store2);

                Assert.Equal(stats1.CountOfDocuments, stats2.CountOfDocuments);
                Assert.Equal(stats1.CountOfRevisionDocuments, stats2.CountOfRevisionDocuments);
                Assert.Equal(stats1.CountOfAttachments, stats2.CountOfAttachments);
                Assert.Equal(stats1.CountOfUniqueAttachments, stats2.CountOfUniqueAttachments);

                using (var session = store1.OpenSession())
                {
                    var u = session.Load<User>("users/1");
                    u.Age = 40;
                    session.SaveChanges();
                }

                using (var session = store1.OpenSession())
                {
                    var u = session.Load<User>("users/1");
                    u.Age = 50;
                    session.SaveChanges();
                }

                using (var session = store1.OpenSession())
                {
                    var u = session.Load<User>("users/1");
                    u.Age = 60;
                    session.SaveChanges();
                }

                WaitForMarker(store1, store2, "marker3$users/1");
                WaitForMarker(store2, store1, "marker4$users/1");

                await store1.Operations.SendAsync(new EnforceRevisionsConfigurationOperation());
                await store2.Operations.SendAsync(new EnforceRevisionsConfigurationOperation());

                stats1 = await GetDatabaseStatisticsAsync(store1);
                stats2 = await GetDatabaseStatisticsAsync(store2);

                Assert.Equal(stats1.CountOfDocuments, stats2.CountOfDocuments);
                Assert.Equal(stats1.CountOfRevisionDocuments, stats2.CountOfRevisionDocuments);
                Assert.Equal(stats1.CountOfAttachments, stats2.CountOfAttachments);
                Assert.Equal(stats1.CountOfUniqueAttachments, stats2.CountOfUniqueAttachments);
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Revisions | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task AttachmentsRevisionsReplicationAfterEnable2(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                using (var session = store1.OpenSession())
                {
                    session.Store(new User { Name = "Fitzchak" }, "users/1");
                    session.SaveChanges();
                }

                using (var profileStream = new MemoryStream(new byte[] { 1, 2, 3 }))
                {
                    var result = store1.Operations.Send(new PutAttachmentOperation("users/1", "foo", profileStream, "image/png"));
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", result.Hash);
                }

                await RevisionsHelper.SetupRevisionsAsync(store1, modifyConfiguration: configuration =>
                {
                    configuration.Collections["Users"].PurgeOnDelete = false;
                    configuration.Collections["Users"].MinimumRevisionsToKeep = 4;
                });
                await RevisionsHelper.SetupRevisionsAsync(store2, modifyConfiguration: configuration =>
                {
                    configuration.Collections["Users"].PurgeOnDelete = false;
                    configuration.Collections["Users"].MinimumRevisionsToKeep = 4;
                });


                using (var backgroundStream = new MemoryStream(new byte[] { 10, 20, 30, 40, 50 }))
                {
                    var result = store1.Operations.Send(new PutAttachmentOperation("users/1", "foo", backgroundStream, "ImGgE/jPeG"));
                    Assert.Equal("igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=", result.Hash);
                }

                await SetupAttachmentReplicationAsync(store1, store2);
                await SetupAttachmentReplicationAsync(store2, store1);

                WaitForDocumentWithAttachmentToReplicate<User>(store2, "users/1", "foo", 15_000);

                var stats1 = await GetDatabaseStatisticsAsync(store1);
                var stats2 = await GetDatabaseStatisticsAsync(store2);

                Assert.Equal(stats1.CountOfDocuments, stats2.CountOfDocuments);
                Assert.Equal(stats1.CountOfRevisionDocuments, stats2.CountOfRevisionDocuments);
                Assert.Equal(stats1.CountOfAttachments, stats2.CountOfAttachments);
                Assert.Equal(stats1.CountOfUniqueAttachments, stats2.CountOfUniqueAttachments);

                using (var session = store1.OpenSession())
                {
                    var u = session.Load<User>("users/1");
                    u.Age = 30;
                    session.SaveChanges();
                }

                Assert.True(WaitForDocument<User>(store2, "users/1", u => u.Age == 30, 15_000));

                await store1.Operations.SendAsync(new EnforceRevisionsConfigurationOperation());
                await store2.Operations.SendAsync(new EnforceRevisionsConfigurationOperation());

                WaitForMarker(store1, store2);
                WaitForMarker(store2, store1);

                stats1 = await GetDatabaseStatisticsAsync(store1);
                stats2 = await GetDatabaseStatisticsAsync(store2);

                Assert.Equal(stats1.CountOfDocuments, stats2.CountOfDocuments);
                Assert.Equal(stats1.CountOfRevisionDocuments, stats2.CountOfRevisionDocuments);
                Assert.Equal(stats1.CountOfAttachments, stats2.CountOfAttachments);
                Assert.Equal(stats1.CountOfUniqueAttachments, stats2.CountOfUniqueAttachments);

                using (var session = store1.OpenSession())
                {
                    var u = session.Load<User>("users/1");
                    u.Age = 40;
                    session.SaveChanges();
                }

                using (var fileStream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }))
                {
                    var result = store1.Operations.Send(new PutAttachmentOperation("users/1", "bar", fileStream, null));
                    Assert.Equal("Arg5SgIJzdjSTeY6LYtQHlyNiTPmvBLHbr/Cypggeco=", result.Hash);
                }

                using (var session = store1.OpenSession())
                {
                    var u = session.Load<User>("users/1");
                    u.Age = 50;
                    session.SaveChanges();
                }

                using (var session = store1.OpenSession())
                {
                    var u = session.Load<User>("users/1");
                    u.Age = 60;
                    session.SaveChanges();
                }

                Assert.True(WaitForDocument<User>(store2, "users/1", u => u.Age == 60, 15_000));

                await store1.Operations.SendAsync(new EnforceRevisionsConfigurationOperation());
                await store2.Operations.SendAsync(new EnforceRevisionsConfigurationOperation());

                WaitForMarker(store1, store2);
                WaitForMarker(store2, store1);

                stats1 = await GetDatabaseStatisticsAsync(store1);
                stats2 = await GetDatabaseStatisticsAsync(store2);

                Assert.Equal(stats1.CountOfDocuments, stats2.CountOfDocuments);
                Assert.Equal(stats1.CountOfRevisionDocuments, stats2.CountOfRevisionDocuments);
                Assert.Equal(stats1.CountOfAttachments, stats2.CountOfAttachments);
                Assert.Equal(stats1.CountOfUniqueAttachments, stats2.CountOfUniqueAttachments);
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Revisions | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task AttachmentsRevisionsReplication(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                await RevisionsHelper.SetupRevisionsAsync(store1, modifyConfiguration: configuration =>
                {
                    configuration.Collections["Users"].PurgeOnDelete = false;
                    configuration.Collections["Users"].MinimumRevisionsToKeep = 4;
                });
                await RevisionsHelper.SetupRevisionsAsync(store2, modifyConfiguration: configuration =>
                {
                    configuration.Collections["Users"].PurgeOnDelete = false;
                    configuration.Collections["Users"].MinimumRevisionsToKeep = 4;
                });

                using (var session = store1.OpenSession())
                {
                    session.Store(new User { Name = "Fitzchak" }, "users/1");
                    session.SaveChanges();
                }

                var names = new[]
                {
                    "profile.png",
                    "background-photo.jpg",
                    "fileNAME_#$1^%_בעברית.txt"
                };

                using (var profileStream = new MemoryStream(new byte[] { 1, 2, 3 }))
                {
                    var result = store1.Operations.Send(new PutAttachmentOperation("users/1", names[0], profileStream, "image/png"));
                    Assert.Contains("A:3", result.ChangeVector);
                    Assert.Equal(names[0], result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("image/png", result.ContentType);
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", result.Hash);
                }
                using (var backgroundStream = new MemoryStream(new byte[] { 10, 20, 30, 40, 50 }))
                {
                    var result = store1.Operations.Send(new PutAttachmentOperation("users/1", names[1], backgroundStream, "ImGgE/jPeG"));
                    Assert.Contains("A:7", result.ChangeVector);
                    Assert.Equal(names[1], result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("ImGgE/jPeG", result.ContentType);
                    Assert.Equal("igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=", result.Hash);
                }
                using (var fileStream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }))
                {
                    var result = store1.Operations.Send(new PutAttachmentOperation("users/1", names[2], fileStream, null));
                    Assert.Contains("A:12", result.ChangeVector);
                    Assert.Equal(names[2], result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("", result.ContentType);
                    Assert.Equal("Arg5SgIJzdjSTeY6LYtQHlyNiTPmvBLHbr/Cypggeco=", result.Hash);
                }

                await SetupAttachmentReplicationAsync(store1, store2);
                WaitForDocumentWithAttachmentToReplicate<User>(store2, "users/1", names[0], 15000);
                WaitForDocumentWithAttachmentToReplicate<User>(store2, "users/1", names[1], 15000);
                WaitForDocumentWithAttachmentToReplicate<User>(store2, "users/1", names[2], 15000);

                await AssertRevisionsAsync(store2, names, (session, revisions) =>
                {
                    AssertRevisionAttachments(names, 3, revisions[0], session);
                    AssertRevisionAttachments(names, 2, revisions[1], session);
                    AssertRevisionAttachments(names, 1, revisions[2], session);
                    AssertNoRevisionAttachment(revisions[3], session);
                }, 9);

                // Delete document should delete all the attachments
                using (var session = store1.OpenAsyncSession())
                {
                    session.Delete("users/1");
                    await session.SaveChangesAsync();
                }

                Assert.True(WaitForDocumentDeletion(store2, "users/1"));

                await store2.Operations.SendAsync(new EnforceRevisionsConfigurationOperation());
                WaitForMarker(store1, store2, "marker1$users/1");

                await AssertRevisionsAsync(store2, names, (session, revisions) =>
                {
                    AssertNoRevisionAttachment(revisions[0], session, true);
                    AssertRevisionAttachments(names, 3, revisions[1], session);
                    AssertRevisionAttachments(names, 2, revisions[2], session);
                    AssertRevisionAttachments(names, 1, revisions[3], session);
                }, 6);

                // Create another revision which should delete old revision
                using (var session = store1.OpenSession()) // This will delete the revision #1 which is without attachment
                {
                    session.Store(new User { Name = "Fitzchak 2" }, "users/1");
                    session.SaveChanges();
                }

                Assert.True(WaitForDocument<User>(store2, "users/1", u => u.Name == "Fitzchak 2"));

                await store2.Operations.SendAsync(new EnforceRevisionsConfigurationOperation());
                WaitForMarker(store1, store2, "marker2$users/1");

                await AssertRevisionsAsync(store2, names, (session, revisions) =>
                {
                    AssertNoRevisionAttachment(revisions[0], session);
                    AssertNoRevisionAttachment(revisions[1], session, true);
                    AssertRevisionAttachments(names, 3, revisions[2], session);
                    AssertRevisionAttachments(names, 2, revisions[3], session);
                }, 5, expectedCountOfDocuments: 4);

                using (var session = store1.OpenSession()) // This will delete the revision #2 which is with attachment
                {
                    session.Store(new User { Name = "Fitzchak 3" }, "users/1");
                    session.SaveChanges();
                }

                Assert.True(WaitForDocument<User>(store2, "users/1", u => u.Name == "Fitzchak 3"));

                await store2.Operations.SendAsync(new EnforceRevisionsConfigurationOperation());
                WaitForMarker(store1, store2, "marker3$users/1");

                await AssertRevisionsAsync(store2, names, (session, revisions) =>
                {
                    AssertNoRevisionAttachment(revisions[0], session);
                    AssertNoRevisionAttachment(revisions[1], session);
                    AssertNoRevisionAttachment(revisions[2], session, true);
                    AssertRevisionAttachments(names, 3, revisions[3], session);
                }, 3, expectedCountOfDocuments: 5);

                using (var session = store1.OpenSession()) // This will delete the revision #3 which is with attachment
                {
                    session.Store(new User { Name = "Fitzchak 4" }, "users/1");
                    session.SaveChanges();
                }

                Assert.True(WaitForDocument<User>(store2, "users/1", u => u.Name == "Fitzchak 4"));

                await store2.Operations.SendAsync(new EnforceRevisionsConfigurationOperation());
                WaitForMarker(store1, store2, "marker4$users/1");

                await AssertRevisionsAsync(store2, names, (session, revisions) =>
                {
                    AssertNoRevisionAttachment(revisions[0], session);
                    AssertNoRevisionAttachment(revisions[1], session);
                    AssertNoRevisionAttachment(revisions[2], session);
                    AssertNoRevisionAttachment(revisions[3], session, true);
                }, 0, expectedCountOfUniqueAttachments: 0, expectedCountOfDocuments: 6);

                using (var session = store1.OpenSession()) // This will delete the revision #4 which is with attachment
                {
                    session.Store(new User { Name = "Fitzchak 5" }, "users/1");
                    session.SaveChanges();
                }

                Assert.True(WaitForDocument<User>(store2, "users/1", u => u.Name == "Fitzchak 5"));

                await store2.Operations.SendAsync(new EnforceRevisionsConfigurationOperation());
                WaitForMarker(store1, store2, "marker5$users/1");

                await AssertRevisionsAsync(store2, names, (session, revisions) =>
                {
                    AssertNoRevisionAttachment(revisions[0], session);
                    AssertNoRevisionAttachment(revisions[1], session);
                    AssertNoRevisionAttachment(revisions[2], session);
                    AssertNoRevisionAttachment(revisions[3], session);
                }, 0, expectedCountOfUniqueAttachments: 0, expectedCountOfDocuments: 7);
            }
        }

        private async Task AssertRevisionsAsync(DocumentStore store, string[] names, Action<IDocumentSession, List<User>> assertAction,
            long expectedCountOfAttachments, long expectedCountOfDocuments = 2, long expectedCountOfUniqueAttachments = 3)
        {
            var statistics = await GetDatabaseStatisticsAsync(store);
            Assert.Equal(expectedCountOfAttachments, statistics.CountOfAttachments);
            Assert.Equal(expectedCountOfUniqueAttachments, statistics.CountOfUniqueAttachments);
            Assert.Equal(4, statistics.CountOfRevisionDocuments);
            Assert.Equal(expectedCountOfDocuments, statistics.CountOfDocuments);
            Assert.Equal(0, statistics.CountOfIndexes);

            using (var session = store.OpenSession())
            {
                var revisions = session.Advanced.Revisions.GetFor<User>("users/1");
                Assert.Equal(4, revisions.Count);
                assertAction(session, revisions);
            }
        }

        private static void AssertNoRevisionAttachment(User revision, IDocumentSession session, bool isDeleteRevision = false)
        {
            var metadata = session.Advanced.GetMetadataFor(revision);
            var flags = DocumentFlags.HasRevisions | DocumentFlags.Revision | DocumentFlags.FromReplication;
            if (isDeleteRevision)
                flags = DocumentFlags.HasRevisions | DocumentFlags.DeleteRevision | DocumentFlags.FromReplication;
            Assert.Equal(flags.ToString(), metadata[Constants.Documents.Metadata.Flags]);
            Assert.False(metadata.ContainsKey(Constants.Documents.Metadata.Attachments));
        }

        private static void AssertRevisionAttachments(string[] names, int expectedCount, User revision, IDocumentSession session)
        {
            var metadata = session.Advanced.GetMetadataFor(revision);
            Assert.Equal((DocumentFlags.HasRevisions | DocumentFlags.Revision | DocumentFlags.HasAttachments | DocumentFlags.FromReplication).ToString(), metadata[Constants.Documents.Metadata.Flags]);
            var attachments = metadata.GetObjects(Constants.Documents.Metadata.Attachments);
            Assert.Equal(expectedCount, attachments.Length);

            var orderedNames = names.Take(expectedCount).OrderBy(x => x).ToArray();
            for (var i = 0; i < expectedCount; i++)
            {
                var name = orderedNames[i];
                var attachment = attachments[i];
                Assert.Equal(name, attachment.GetString(nameof(AttachmentName.Name)));
                var hash = attachment.GetString(nameof(AttachmentName.Hash));
                if (name == names[1])
                {
                    Assert.Equal("igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=", hash);
                }
                else if (name == names[2])
                {
                    Assert.Equal("Arg5SgIJzdjSTeY6LYtQHlyNiTPmvBLHbr/Cypggeco=", hash);
                }
                else if (name == names[0])
                {
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", hash);
                }
            }

            var changeVector = session.Advanced.GetChangeVectorFor(revision);
            var readBuffer = new byte[8];
            for (var i = 0; i < names.Length; i++)
            {
                var name = names[i];
                if (orderedNames.Contains(name) == false)
                    continue;
                using (var attachmentStream = new MemoryStream(readBuffer))
                using (var attachment = session.Advanced.Attachments.GetRevision("users/1", name, changeVector))
                {
                    attachment.Stream.CopyTo(attachmentStream);
                    if (i >= expectedCount)
                    {
                        Assert.Null(attachment);
                        continue;
                    }

                    Assert.Equal(name, attachment.Details.Name);
                    if (name == names[0])
                    {
                        Assert.Equal(new byte[] { 1, 2, 3 }, readBuffer.Take(3));
                        Assert.Equal("image/png", attachment.Details.ContentType);
                        Assert.Equal(3, attachmentStream.Position);
                        Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachment.Details.Hash);
                    }
                    else if (name == names[1])
                    {
                        Assert.Equal(new byte[] { 10, 20, 30, 40, 50 }, readBuffer.Take(5));
                        Assert.Equal("ImGgE/jPeG", attachment.Details.ContentType);
                        Assert.Equal(5, attachmentStream.Position);
                        Assert.Equal("igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=", attachment.Details.Hash);
                    }
                    else if (name == names[2])
                    {
                        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, readBuffer.Take(5));
                        Assert.Equal("", attachment.Details.ContentType);
                        Assert.Equal(5, attachmentStream.Position);
                        Assert.Equal("Arg5SgIJzdjSTeY6LYtQHlyNiTPmvBLHbr/Cypggeco=", attachment.Details.Hash);
                    }
                }
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task PutDifferentAttachmentsShouldNotConflict(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                using (var session = store1.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Fitzchak" }, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        await store1.Operations.SendAsync(new PutAttachmentOperation("users/1", "a1", a1, "a1/png"));
                    }

                    await session.StoreAsync(new User { Name = "Marker 1" }, "marker 1");
                    await session.SaveChangesAsync();
                }
                using (var session = store2.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Fitzchak" }, "users/1");
                    await session.SaveChangesAsync();

                    using (var a2 = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }))
                    {
                        store2.Operations.Send(new PutAttachmentOperation("users/1", "a2", a2, "a2/jpeg"));
                    }

                    await session.StoreAsync(new User { Name = "Marker 2" }, "marker 2");
                    await session.SaveChangesAsync();
                }

                await SetupReplicationAsync(store1, store2);
                await SetupReplicationAsync(store2, store1);

                Assert.True(WaitForDocument(store2, "marker 1"));
                Assert.True(WaitForDocument(store1, "marker 2"));

                await AssertAttachments(store1, new[] { "a1", "a2" });
                await AssertAttachments(store2, new[] { "a1", "a2" });
            }
        }

        private async Task AssertAttachments(DocumentStore store, string[] names)
        {
            using (var session = store.OpenAsyncSession())
            {
                var user = await session.LoadAsync<User>("users/1");
                var metadata = session.Advanced.GetMetadataFor(user);
                Assert.Contains(DocumentFlags.HasAttachments.ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));
                var attachments = metadata.GetObjects(Constants.Documents.Metadata.Attachments);
                Assert.Equal(names.Length, attachments.Length);
                for (int i = 0; i < names.Length; i++)
                {
                    Assert.Equal(names[i], attachments[i].GetString(nameof(AttachmentName.Name)));
                }
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task PutAndDeleteDifferentAttachmentsShouldNotConflict(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                using (var session = store1.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Fitzchak" }, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        await store1.Operations.SendAsync(new PutAttachmentOperation("users/1", "a1", a1, "a1/png"));
                    }

                    await session.StoreAsync(new User { Name = "Marker 1" }, "marker 1");
                    await session.SaveChangesAsync();
                }
                using (var session = store2.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Fitzchak" }, "users/1");
                    await session.SaveChangesAsync();

                    using (var a2 = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }))
                    {
                        store2.Operations.Send(new PutAttachmentOperation("users/1", "a2", a2, "a1/png"));
                    }
                    store2.Operations.Send(new DeleteAttachmentOperation("users/1", "a2"));

                    await session.StoreAsync(new User { Name = "Marker 2" }, "marker 2");
                    await session.SaveChangesAsync();
                }

                await SetupReplicationAsync(store1, store2);
                await SetupReplicationAsync(store2, store1);

                Assert.True(WaitForDocument(store2, "marker 1"));
                Assert.True(WaitForDocument(store1, "marker 2"));

                await AssertAttachments(store1, new[] { "a1" });
                await AssertAttachments(store2, new[] { "a1" });
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task PutSameAttachmentsShouldNotConflict(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                using (var session = store1.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Fitzchak" }, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        await store1.Operations.SendAsync(new PutAttachmentOperation("users/1", "a1", a1, "a1/png"));
                    }

                    await session.StoreAsync(new User { Name = "Marker 1" }, "marker 1");
                    await session.SaveChangesAsync();
                }
                using (var session = store2.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Fitzchak" }, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        store2.Operations.Send(new PutAttachmentOperation("users/1", "a1", a1, "a1/png"));
                    }

                    await session.StoreAsync(new User { Name = "Marker 2" }, "marker 2");
                    await session.SaveChangesAsync();
                }

                await SetupReplicationAsync(store1, store2);
                await SetupReplicationAsync(store2, store1);

                Assert.True(WaitForDocument(store2, "marker 1"));
                Assert.True(WaitForDocument(store1, "marker 2"));

                await AssertAttachments(store1, new[] { "a1" });
                await AssertAttachments(store2, new[] { "a1" });
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task PutSameAttachmentsDifferentContentTypeShouldConflict(Options options)
        {
            var modifyDatabaseRecord = options.ModifyDatabaseRecord;
            options.ModifyDatabaseRecord = record =>
            {
                modifyDatabaseRecord?.Invoke(record);
                record.ConflictSolverConfig = new ConflictSolver
                {
                    ResolveToLatest = false,
                    ResolveByCollection = new Dictionary<string, ScriptResolver>()
                };
            };
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                using (var session = store1.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Fitzchak" }, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        await store1.Operations.SendAsync(new PutAttachmentOperation("users/1", "a1", a1, "a1/png"));
                    }
                }

                using (var session = store2.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Fitzchak" }, "users/2$users/1");
                    await session.SaveChangesAsync();

                    await session.StoreAsync(new User { Name = "Fitzchak" }, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        store2.Operations.Send(new PutAttachmentOperation("users/1", "a1", a1, "a2/jpeg"));
                    }
                }

                await SetupReplicationAsync(store1, store2);
                await SetupReplicationAsync(store2, store1);

                var conflicts = WaitUntilHasConflict(store1, "users/1");
                Assert.Equal(2, conflicts.Length);
                var hash = "EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=";

                Assert.Equal(2, conflicts.Length);
                AssertConflict(conflicts[0], "a1", hash, "a1/png", 3);
                AssertConflict(conflicts[1], "a1", hash, "a2/jpeg", 3);

                conflicts = WaitUntilHasConflict(store2, "users/1");
                Assert.Equal(2, conflicts.Length);
                Assert.Equal(2, conflicts.Length);
                AssertConflict(conflicts[0], "a1", hash, "a1/png", 3);
                AssertConflict(conflicts[1], "a1", hash, "a2/jpeg", 3);

                await ResolveConflict(store1, store2, conflicts[0].Doc, "a1", hash, "a1/png", 3);
            }
        }

        private async Task ResolveConflict(DocumentStore store1, DocumentStore store2, BlittableJsonReaderObject document,
            string name, string hash, string contentType, long size)
        {
            await store1.Commands().PutAsync("users/1", null, document);

            await Task.Delay(3000); // wait for the replication ping-pong to settle down
            await AssertConflictResolved(store1, name, hash, contentType, size);

            WaitForMarker(store1, store2);
            await AssertConflictResolved(store2, name, hash, contentType, size);
        }

        private async Task AssertConflictResolved(DocumentStore store, string name, string hash, string contentType, long size)
        {
            using (var session = store.OpenAsyncSession())
            {
                var user = await session.LoadAsync<User>("users/1");
                var attachments = session.Advanced.Attachments.GetNames(user);
                var attachment = attachments.Single();
                Assert.Equal(name, attachment.Name);
                Assert.Equal(hash, attachment.Hash);
                Assert.Equal(contentType, attachment.ContentType);
                Assert.Equal(size, attachment.Size);
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task PutDifferentAttachmentsShouldConflict(Options options)
        {
            var modifyDatabaseRecord = options.ModifyDatabaseRecord;
            options.ModifyDatabaseRecord = record =>
            {
                modifyDatabaseRecord?.Invoke(record);
                record.ConflictSolverConfig = new ConflictSolver
                {
                    ResolveToLatest = false,
                    ResolveByCollection = new Dictionary<string, ScriptResolver>()
                };
            };
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                using (var session = store1.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Fitzchak" }, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        await store1.Operations.SendAsync(new PutAttachmentOperation("users/1", "a1", a1, "a1/png"));
                    }
                }
                using (var session = store2.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Fitzchak" }, "users/1");
                    await session.SaveChangesAsync();

                    using (var a2 = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }))
                    {
                        store2.Operations.Send(new PutAttachmentOperation("users/1", "a1", a2, "a1/png"));
                    }
                }

                await SetupReplicationAsync(store1, store2);
                await SetupReplicationAsync(store2, store1);

                var hash1 = "EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=";
                var hash2 = "Arg5SgIJzdjSTeY6LYtQHlyNiTPmvBLHbr/Cypggeco=";

                var conflicts = WaitUntilHasConflict(store1, "users/1");
                Assert.Equal(2, conflicts.Length);
                AssertConflict(conflicts[0], "a1", hash1, "a1/png", 3);
                AssertConflict(conflicts[1], "a1", hash2, "a1/png", 5);

                conflicts = WaitUntilHasConflict(store2, "users/1");
                Assert.Equal(2, conflicts.Length);
                AssertConflict(conflicts[0], "a1", hash1, "a1/png", 3);
                AssertConflict(conflicts[1], "a1", hash2, "a1/png", 5);

                await ResolveConflict(store1, store2, conflicts[1].Doc, "a1", hash2, "a1/png", 5);
            }
        }

        private void AssertConflict(GetConflictsResult.Conflict conflict, string name, string hash, string contentType, long size)
        {
            Assert.True(conflict.Doc.TryGet(Constants.Documents.Metadata.Key, out BlittableJsonReaderObject metadata));

            Assert.True(metadata.TryGet(Constants.Documents.Metadata.Attachments, out BlittableJsonReaderArray attachments));
            var attachment = (BlittableJsonReaderObject)attachments.Single();

            Assert.True(attachment.TryGet(nameof(AttachmentName.Name), out string actualName));
            Assert.Equal(name, actualName);
            Assert.True(attachment.TryGet(nameof(AttachmentName.Hash), out string actualHash));
            Assert.Equal(hash, actualHash);
            Assert.True(attachment.TryGet(nameof(AttachmentName.ContentType), out string actualContentType));
            Assert.Equal(contentType, actualContentType);
            Assert.True(attachment.TryGet(nameof(AttachmentName.Size), out long actualSize));
            Assert.Equal(size, actualSize);
        }

        private void AssertConflictNoAttachment(GetConflictsResult.Conflict conflict)
        {
            Assert.True(conflict.Doc.TryGet(Constants.Documents.Metadata.Key, out BlittableJsonReaderObject metadata));
            Assert.False(metadata.TryGet(Constants.Documents.Metadata.Attachments, out BlittableJsonReaderArray _));
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task PutAndDeleteAttachmentsShouldNotConflict(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                using (var session = store1.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Fitzchak" }, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        await store1.Operations.SendAsync(new PutAttachmentOperation("users/1", "a1", a1, "a1/png"));
                    }

                    await session.StoreAsync(new User { Name = "Marker 1" }, "marker 1");
                    await session.SaveChangesAsync();
                }
                using (var session = store2.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Fitzchak" }, "users/1");
                    await session.SaveChangesAsync();

                    using (var a2 = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }))
                    {
                        store2.Operations.Send(new PutAttachmentOperation("users/1", "a1", a2, "a1/png"));
                    }
                    store2.Operations.Send(new DeleteAttachmentOperation("users/1", "a1"));

                    await session.StoreAsync(new User { Name = "Marker 2" }, "marker 2");
                    await session.SaveChangesAsync();
                }

                await SetupReplicationAsync(store1, store2);
                await SetupReplicationAsync(store2, store1);

                Assert.True(WaitForDocument(store2, "marker 1"));
                Assert.True(WaitForDocument(store1, "marker 2"));

                await AssertAttachments(store1, new[] { "a1" });
                await AssertAttachments(store2, new[] { "a1" });
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task PutAndDeleteAttachmentsShouldNotConflict_OnDocumentWithoutMetadata(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                using (var session = store1.OpenAsyncSession())
                {
                    using (var commands = store1.Commands())
                    {
                        await commands.PutAsync("users/1", null, new User { Name = "Fitzchak" });
                    }

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        await store1.Operations.SendAsync(new PutAttachmentOperation("users/1", "a1", a1, "a1/png"));
                    }

                    await session.StoreAsync(new User { Name = "Marker 1" }, "marker 1");
                    await session.SaveChangesAsync();
                }
                using (var session = store2.OpenAsyncSession())
                {
                    using (var commands = store2.Commands())
                    {
                        await commands.PutAsync("users/1", null, new User { Name = "Fitzchak" });
                    }

                    using (var a2 = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }))
                    {
                        await store2.Operations.SendAsync(new PutAttachmentOperation("users/1", "a1", a2, "a1/png"));
                    }
                    await store2.Operations.SendAsync(new DeleteAttachmentOperation("users/1", "a1"));

                    await session.StoreAsync(new User { Name = "Marker 2" }, "marker 2");
                    await session.SaveChangesAsync();
                }

                await SetupReplicationAsync(store1, store2);
                await SetupReplicationAsync(store2, store1);

                Assert.True(WaitForDocument(store2, "marker 1"));
                Assert.True(WaitForDocument(store1, "marker 2"));

                await AssertAttachments(store1, new[] { "a1" });
                await AssertAttachments(store2, new[] { "a1" });
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task RavenDB_13535(Options options)
        {
            var co = new ServerCreationOptions
            {
                RunInMemory = false,
                CustomSettings = new Dictionary<string, string> { [RavenConfiguration.GetKey(x => x.Replication.MaxSizeToSend)] = 1.ToString() },
                RegisterForDisposal = false
            };

            using (var server = GetNewServer(co))
            using (var store1 = GetDocumentStore(new Options(options) { Server = server, RunInMemory = false }))
            using (var store2 = GetDocumentStore(new Options(options) { Server = server, RunInMemory = false }))
            using (var store3 = GetDocumentStore(new Options(options) { Server = server, RunInMemory = false }))
            {
                using (var session = store1.OpenAsyncSession())
                using (var a1 = new MemoryStream(new byte[2 * 1024 * 1024]))
                {
                    var user = new User();
                    await session.StoreAsync(user, "foo");
                    session.Advanced.Attachments.Store(user, "dummy", a1);
                    await session.SaveChangesAsync();
                }

                using (var session = store1.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("foo");
                    user.Name = "Karmel";
                    await session.SaveChangesAsync();
                }

                var replication = await GetReplicationManagerAsync(store1, store1.Database, options.DatabaseMode, breakReplication: true, new List<RavenServer>() { server });

                await SetupReplicationAsync(store1, store2);
                replication.ReplicateOnce("foo");

                var db2 = await server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(options.DatabaseMode == RavenDatabaseMode.Single
                    ? store2.Database
                    : await Sharding.GetShardDatabaseNameForDocAsync(store2, "foo"));

                var count = WaitForValue(() =>
                {
                    using (db2.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext ctx))
                    using (ctx.OpenReadTransaction())
                    {
                        return db2.DocumentsStorage.AttachmentsStorage.GetNumberOfAttachments(ctx).AttachmentCount;
                    }
                }, 1);
                Assert.Equal(1, count);

                db2.ServerStore.DatabasesLandlord.UnloadDirectly(db2.Name);

                using (var session = store2.OpenAsyncSession())
                {
                    await session.StoreAsync(new User(), "bar");
                    await session.SaveChangesAsync();
                }

                await SetupReplicationAsync(store2, store3);

                var db3 = await server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(options.DatabaseMode == RavenDatabaseMode.Single
                    ? store3.Database
                    : ShardHelper.ToShardName(store3.Database, await Sharding.GetShardNumberForAsync(store3, "foo")));
                count = WaitForValue(() =>
                {
                    using (db3.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext ctx))
                    using (ctx.OpenReadTransaction())
                    {
                        return db3.DocumentsStorage.AttachmentsStorage.GetNumberOfAttachments(ctx).AttachmentCount;
                    }
                }, 1);
                Assert.Equal(1, count);
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task RavenDB_13963(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                string docId1 = "users/1", docId2 = "users/2$users/1";
                var replication = await GetReplicationManagerAsync(store1, store1.Database, options.DatabaseMode, breakReplication: true);

                using (var session = store1.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Karmel" }, docId1);
                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        session.Advanced.Attachments.Store(docId1, "a1", a1, "a1/png");
                        await session.SaveChangesAsync();
                    }
                }

                using (var session = store1.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Karmel" }, docId2);
                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3, 4 }))
                    {
                        session.Advanced.Attachments.Store(docId2, "a2", a1, "a2/png");
                        await session.SaveChangesAsync();
                    }
                }

                await SetupReplicationAsync(store1, store2);
                replication.ReplicateOnce(docId1);

                Assert.True(WaitForDocument(store2, docId1));

                using (var session = store2.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>(docId1);
                    Assert.NotNull(user);

                    var metadata = session.Advanced.GetMetadataFor(user);
                    Assert.Contains(DocumentFlags.HasAttachments.ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));
                    var attachments = metadata.GetObjects(Constants.Documents.Metadata.Attachments);
                    Assert.Equal(1, attachments.Length);

                    Assert.Null(await session.LoadAsync<User>(docId2));
                }

                replication.ReplicateOnce(docId1);
                Assert.True(WaitForDocument(store2, docId2));

                using (var session = store2.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>(docId2);
                    var metadata = session.Advanced.GetMetadataFor(user);
                    Assert.Contains(DocumentFlags.HasAttachments.ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));
                    var attachments = metadata.GetObjects(Constants.Documents.Metadata.Attachments);
                    Assert.Equal(1, attachments.Length);
                }
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication | RavenTestCategory.Cluster)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task RavenDB_15914(Options options)
        {
            DebuggerAttachedTimeout.DisableLongTimespan = true;
            DefaultClusterSettings[RavenConfiguration.GetKey(x => x.Replication.RetryReplicateAfter)] = "1";

            var databaseName = GetDatabaseName();
            var cluster = await CreateRaftCluster(3, shouldRunInMemory: false, watcherCluster: true);

            var mainServer = cluster.Nodes[0];
            var toRemove = mainServer.ServerStore.NodeTag;

            using (var store = GetDocumentStore(new Options(options)
            {
                Server = mainServer,
                ModifyDatabaseName = s => databaseName,
                ReplicationFactor = 3,
                ModifyDocumentStore = s => s.Conventions = new DocumentConventions
                {
                    DisableTopologyUpdates = true
                }
            }))
            using (var temp = GetDocumentStore(new Options(options)
            {
                Server = cluster.Nodes[1],
                ModifyDatabaseName = s => databaseName,
                CreateDatabase = false
            }))
            {
                await using (var ms = new MemoryStream(new byte[] { 1, 2, 3, 4 }))
                using (var session = store.OpenAsyncSession())
                {
                    session.Advanced.WaitForReplicationAfterSaveChanges(replicas: 2);
                    var user = new Core.Utils.Entities.User();
                    await session.StoreAsync(user, "users/1");
                    session.Advanced.Attachments.Store(user, "dummy", ms);
                    await session.SaveChangesAsync();
                }

                await using (var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }))
                using (var session = store.OpenAsyncSession())
                {
                    session.Advanced.WaitForReplicationAfterSaveChanges(replicas: 2);
                    var user = new Core.Utils.Entities.User();
                    await session.StoreAsync(user, "users/2");
                    session.Advanced.Attachments.Store(user, "dummy2", ms);
                    await session.SaveChangesAsync();
                }

                using (var session = store.OpenAsyncSession())
                {
                    session.Advanced.WaitForReplicationAfterSaveChanges(replicas: 2);
                    session.Advanced.Attachments.Delete("users/1", "dummy");
                    await session.SaveChangesAsync();
                }

                var replication = await GetReplicationManagerAsync(store, databaseName, options.DatabaseMode, servers: cluster.Nodes);

                await replication.EnsureNoReplicationLoopAsync();

                if (options.DatabaseMode == RavenDatabaseMode.Single)
                {
                    var result = await store.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(databaseName, hardDelete: true, fromNode: toRemove,
                        timeToWaitForConfirmation: TimeSpan.FromSeconds(60)));
                    await mainServer.ServerStore.Cluster.WaitForIndexNotification(result.RaftCommandIndex);
                }
                else
                {
                    var result0 = await store.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(databaseName, shardNumber: 0, hardDelete: true, fromNode: toRemove, timeToWaitForConfirmation: TimeSpan.FromSeconds(60)));
                    var result1 = await store.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(databaseName, shardNumber: 1, hardDelete: true, fromNode: toRemove, timeToWaitForConfirmation: TimeSpan.FromSeconds(60)));
                    var result2 = await store.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(databaseName, shardNumber: 2, hardDelete: true, fromNode: toRemove, timeToWaitForConfirmation: TimeSpan.FromSeconds(60)));
                    await mainServer.ServerStore.Cluster.WaitForIndexNotification(result0.RaftCommandIndex);
                    await mainServer.ServerStore.Cluster.WaitForIndexNotification(result1.RaftCommandIndex);
                    await mainServer.ServerStore.Cluster.WaitForIndexNotification(result2.RaftCommandIndex);
                }

                using (var session = temp.OpenAsyncSession())
                {
                    session.Advanced.WaitForReplicationAfterSaveChanges(replicas: 1);
                    var user = new Core.Utils.Entities.User();
                    await session.StoreAsync(user, "users/3");
                    await session.SaveChangesAsync();
                }

                if (options.DatabaseMode == RavenDatabaseMode.Single)
                    await store.Maintenance.Server.SendAsync(new AddDatabaseNodeOperation(databaseName, toRemove));
                else
                {
                    await store.Maintenance.Server.SendAsync(new AddDatabaseNodeOperation(databaseName, shardNumber: 0, toRemove));
                    await store.Maintenance.Server.SendAsync(new AddDatabaseNodeOperation(databaseName, shardNumber: 1, toRemove));
                    await store.Maintenance.Server.SendAsync(new AddDatabaseNodeOperation(databaseName, shardNumber: 2, toRemove));
                }

                Assert.True(await WaitForDocumentInClusterAsync<Core.Utils.Entities.User>(new DatabaseTopology { Members = new List<string> { "A", "B", "C" } }, databaseName, "users/1", null,
                    timeout: TimeSpan.FromSeconds(60)));

                await replication.EnsureNoReplicationLoopAsync();
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task AttachmentWithDifferentStreamAndSameNameShouldBeResolvedToLatestAfterConflict(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                using (var session = store1.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "EGR" }, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3, 4 }))
                    {
                        await store1.Operations.SendAsync(new PutAttachmentOperation("users/1", "a1", a1, "a2/jpeg"));
                    }
                }

                using (var session = store2.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "EGOR" }, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        store2.Operations.Send(new PutAttachmentOperation("users/1", "a1", a1, "a2/jpeg"));
                    }
                }

                await SetupReplicationAsync(store1, store2);
                await SetupReplicationAsync(store2, store1);
                WaitForDocumentWithAttachmentToReplicate<User>(store1, "users/1", "a1", Debugger.IsAttached ? 60000 : 15000);
                WaitForDocumentWithAttachmentToReplicate<User>(store2, "users/1", "a1", Debugger.IsAttached ? 60000 : 15000);
                WaitForMarker(store1, store2);

                using (var session = store1.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.Equal("EGOR", user.Name);
                    var attachments = session.Advanced.Attachments.GetNames(user);
                    Assert.Equal(1, attachments.Length);

                    Assert.Equal("a1", attachments[0].Name);
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachments[0].Hash);
                    Assert.Equal("a2/jpeg", attachments[0].ContentType);
                    Assert.Equal(3, attachments[0].Size);
                }

                using (var session = store2.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.Equal("EGOR", user.Name);
                    var attachments = session.Advanced.Attachments.GetNames(user);
                    Assert.Equal(1, attachments.Length);
                    Assert.Equal("a1", attachments[0].Name);
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachments[0].Hash);
                    Assert.Equal("a2/jpeg", attachments[0].ContentType);
                    Assert.Equal(3, attachments[0].Size);
                }
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task SameAttachmentWithDuplicateNameShouldBeNotChangeAfterConflict(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                using (var session = store1.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "EGR" }, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        await store1.Operations.SendAsync(new PutAttachmentOperation("users/1", "a1", a1, "a2/jpeg"));
                    }
                }

                using (var session = store2.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "EGOR" }, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        store2.Operations.Send(new PutAttachmentOperation("users/1", "a1", a1, "a2/jpeg"));
                    }
                }

                await SetupReplicationAsync(store1, store2);
                await SetupReplicationAsync(store2, store1);
                WaitForDocumentWithAttachmentToReplicate<User>(store1, "users/1", "a1", Debugger.IsAttached ? 60000 : 15000);
                WaitForDocumentWithAttachmentToReplicate<User>(store2, "users/1", "a1", Debugger.IsAttached ? 60000 : 15000);
                WaitForMarker(store1, store2);

                using (var session = store1.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.Equal("EGOR", user.Name);
                    var attachments = session.Advanced.Attachments.GetNames(user);
                    Assert.Equal(1, attachments.Length);
                    Assert.Equal("a1", attachments[0].Name);
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachments[0].Hash);
                    Assert.Equal("a2/jpeg", attachments[0].ContentType);
                    Assert.Equal(3, attachments[0].Size);
                }

                using (var session = store2.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.Equal("EGOR", user.Name);
                    var attachments = session.Advanced.Attachments.GetNames(user);
                    Assert.Equal(1, attachments.Length);
                    Assert.Equal("a1", attachments[0].Name);
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachments[0].Hash);
                    Assert.Equal("a2/jpeg", attachments[0].ContentType);
                    Assert.Equal(3, attachments[0].Size);
                }
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task AttachmentWithSameStreamSameNameAndDifferentContentTypeShouldBeResolvedToLatestAfterConflict(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                using (var session = store1.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "EGR" }, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        await store1.Operations.SendAsync(new PutAttachmentOperation("users/1", "a1", a1, "a1/jpeg"));
                    }
                }

                using (var session = store2.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "EGOR" }, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        store2.Operations.Send(new PutAttachmentOperation("users/1", "a1", a1, "a2/jpeg"));
                    }
                }

                await SetupReplicationAsync(store1, store2);
                await SetupReplicationAsync(store2, store1);
                WaitForDocumentWithAttachmentToReplicate<User>(store1, "users/1", "a1", Debugger.IsAttached ? 60000 : 15000);
                WaitForDocumentWithAttachmentToReplicate<User>(store2, "users/1", "a1", Debugger.IsAttached ? 60000 : 15000);
                WaitForMarker(store1, store2);

                using (var session = store1.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.Equal("EGOR", user.Name);
                    var attachments = session.Advanced.Attachments.GetNames(user);
                    Assert.Equal(1, attachments.Length);
                    Assert.Equal("a1", attachments[0].Name);
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachments[0].Hash);
                    Assert.Equal("a2/jpeg", attachments[0].ContentType);
                    Assert.Equal(3, attachments[0].Size);
                }

                using (var session = store2.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.Equal("EGOR", user.Name);
                    var attachments = session.Advanced.Attachments.GetNames(user);
                    Assert.Equal(1, attachments.Length);
                    Assert.Equal("a1", attachments[0].Name);
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachments[0].Hash);
                    Assert.Equal("a2/jpeg", attachments[0].ContentType);
                    Assert.Equal(3, attachments[0].Size);
                }
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task ResolvedToLatestOnAttachmentConflictShouldRemoveDuplicateAttachment(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                using (var session = store1.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "EGR" }, "users/1");
                    await session.SaveChangesAsync();

                    await using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        await store1.Operations.SendAsync(new PutAttachmentOperation("users/1", "a1", a1, "a1/jpeg"));
                    }
                }

                using (var session = store2.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "EGOR" }, "users/1");
                    await session.SaveChangesAsync();

                    await using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        store2.Operations.Send(new PutAttachmentOperation("users/1", "a1", a1, "a2/jpeg"));
                    }
                }

                var externalList1 = await SetupReplicationAsync(store1, store2);
                var externalList2 = await SetupReplicationAsync(store2, store1);
                WaitForDocumentWithAttachmentToReplicate<User>(store1, "users/1", "a1", Debugger.IsAttached ? 60000 : 15000);
                WaitForDocumentWithAttachmentToReplicate<User>(store2, "users/1", "a1", Debugger.IsAttached ? 60000 : 15000);
                WaitForMarker(store1, store2);

                using (var session = store1.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.Equal("EGOR", user.Name);
                    var attachments = session.Advanced.Attachments.GetNames(user);
                    Assert.Equal(1, attachments.Length);
                    Assert.Equal("a1", attachments[0].Name);
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachments[0].Hash);
                    Assert.Equal("a2/jpeg", attachments[0].ContentType);
                    Assert.Equal(3, attachments[0].Size);
                }

                using (var session = store2.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.Equal("EGOR", user.Name);
                    var attachments = session.Advanced.Attachments.GetNames(user);
                    Assert.Equal(1, attachments.Length);
                    Assert.Equal("a1", attachments[0].Name);
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachments[0].Hash);
                    Assert.Equal("a2/jpeg", attachments[0].ContentType);
                    Assert.Equal(3, attachments[0].Size);
                }

                var dbName1 = options.DatabaseMode == RavenDatabaseMode.Sharded ? ShardHelper.ToShardName(store1.Database, await Sharding.GetShardNumberForAsync(store1, "users/1")) : store1.Database;
                var dbName2 = options.DatabaseMode == RavenDatabaseMode.Sharded ? ShardHelper.ToShardName(store2.Database, await Sharding.GetShardNumberForAsync(store2, "users/1")) : store2.Database;
                var db1 = await GetDocumentDatabaseInstanceForAsync(dbName1);
                var db2 = await GetDocumentDatabaseInstanceForAsync(dbName2);
                var replicationConnection1 = db1.ReplicationLoader.OutgoingHandlers.First();
                var replicationConnection2 = db2.ReplicationLoader.OutgoingHandlers.First();

                var external1 = new ExternalReplication(store1.Database, $"ConnectionString-{store2.Identifier}")
                {
                    TaskId = externalList1.First().TaskId,
                    Disabled = true
                };
                var external2 = new ExternalReplication(store2.Database, $"ConnectionString-{store1.Identifier}")
                {
                    TaskId = externalList2.First().TaskId,
                    Disabled = true
                };
                var res1 = await store1.Maintenance.SendAsync(new UpdateExternalReplicationOperation(external1));
                Assert.Equal(externalList1.First().TaskId, res1.TaskId);
                var res2 = await store2.Maintenance.SendAsync(new UpdateExternalReplicationOperation(external2));
                Assert.Equal(externalList2.First().TaskId, res2.TaskId);

                await db1.ServerStore.Cluster.WaitForIndexNotification(res1.RaftCommandIndex);
                await db2.ServerStore.Cluster.WaitForIndexNotification(res1.RaftCommandIndex);
                await db1.ServerStore.Cluster.WaitForIndexNotification(res2.RaftCommandIndex);
                await db2.ServerStore.Cluster.WaitForIndexNotification(res2.RaftCommandIndex);

                Assert.True(await WaitForValueAsync(() => replicationConnection1.IsConnectionDisposed, true));
                Assert.True(await WaitForValueAsync(() => replicationConnection2.IsConnectionDisposed, true));

                await using (var a1 = new MemoryStream(new byte[] { 1, 2, 3, 4 }))
                {
                    await store1.Operations.SendAsync(new PutAttachmentOperation("users/1", "a1", a1, "a1/jpeg"));
                }
                await using (var a1 = new MemoryStream(new byte[] { 1, 2, 3, 5 }))
                {
                    await store2.Operations.SendAsync(new PutAttachmentOperation("users/1", "a1", a1, "a1/jpeg"));
                }
                external1.Disabled = false;
                external2.Disabled = false;

                var res3 = await store1.Maintenance.SendAsync(new UpdateExternalReplicationOperation(external1));
                Assert.Equal(externalList1.First().TaskId, res3.TaskId);
                var res4 = await store2.Maintenance.SendAsync(new UpdateExternalReplicationOperation(external2));
                Assert.Equal(externalList2.First().TaskId, res4.TaskId);

                await db1.ServerStore.Cluster.WaitForIndexNotification(res3.RaftCommandIndex);
                await db2.ServerStore.Cluster.WaitForIndexNotification(res3.RaftCommandIndex);
                await db1.ServerStore.Cluster.WaitForIndexNotification(res4.RaftCommandIndex);
                await db2.ServerStore.Cluster.WaitForIndexNotification(res4.RaftCommandIndex);

                WaitForDocumentWithSpecificAttachmentInfoToReplicate<User>(store1, "users/1", "a1", CheckAttachment, Debugger.IsAttached ? 60000 : 15000);
                WaitForDocumentWithSpecificAttachmentInfoToReplicate<User>(store2, "users/1", "a1", CheckAttachment, Debugger.IsAttached ? 60000 : 15000);

                static bool CheckAttachment(AttachmentDetails att)
                {
                    if (att.Hash == "XiUNwy+pPQdTVBunU26rVydiLOd3Iqgtz4lkmZVfSs4=" && att.ContentType == "a1/jpeg")
                        return true;
                    return false;
                }

                WaitForMarker(store1, store2);

                using (var session = store1.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.Equal("EGOR", user.Name);
                    var attachments = session.Advanced.Attachments.GetNames(user);
                    Assert.Equal(1, attachments.Length);
                    Assert.Equal("a1", attachments[0].Name);
                    Assert.Equal("XiUNwy+pPQdTVBunU26rVydiLOd3Iqgtz4lkmZVfSs4=", attachments[0].Hash);
                    Assert.Equal("a1/jpeg", attachments[0].ContentType);
                    Assert.Equal(4, attachments[0].Size);
                }

                using (var session = store2.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.Equal("EGOR", user.Name);
                    var attachments = session.Advanced.Attachments.GetNames(user);
                    Assert.Equal(1, attachments.Length);
                    Assert.Equal("a1", attachments[0].Name);
                    Assert.Equal("XiUNwy+pPQdTVBunU26rVydiLOd3Iqgtz4lkmZVfSs4=", attachments[0].Hash);
                    Assert.Equal("a1/jpeg", attachments[0].ContentType);
                    Assert.Equal(4, attachments[0].Size);
                }
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task ScriptResolver_ShouldNotRenameAttachment_ShouldRenameAllMissingAttachmentsAndMergeWithOtherAttachmentsOnResolvedDocument(Options options)
        {
            var modifyDatabaseRecord = options.ModifyDatabaseRecord;
            using (var store1 = GetDocumentStore(options: new Options(options)
            {
                ModifyDatabaseRecord = record =>
                {
                    modifyDatabaseRecord?.Invoke(record);
                    record.ConflictSolverConfig = new ConflictSolver
                    {
                        ResolveToLatest = false,
                        ResolveByCollection = new Dictionary<string, ScriptResolver>()
                        {
                            {
                                "Users", new ScriptResolver()
                                {
                                    Script = @"docs[0]['@metadata']['@attachments'][0]['Name'] = 'newName';
                                               docs[0].Name = docs[0].Name + '_RESOLVED';
                                               return docs[0];
                                               "
                                }
                            }
                        }
                    };
                }
            }))
            using (var store2 = GetDocumentStore(options: new Options(options)
            {
                ModifyDatabaseRecord = record =>
                {
                    modifyDatabaseRecord?.Invoke(record);
                    record.ConflictSolverConfig = new ConflictSolver
                    {
                        ResolveToLatest = false,
                        ResolveByCollection = new Dictionary<string, ScriptResolver>()
                        {
                            {
                                "Users", new ScriptResolver()
                                {
                                    Script = @"docs[0]['@metadata']['@attachments'][0]['Name'] = 'newName';
                                               docs[0].Name = docs[0].Name + '_RESOLVED';
                                               return docs[0];
                                               "
                                }
                            }
                        }
                    };
                }
            }))
            {
                var cvs = new List<(string, string)>();
                using (var session = store1.OpenAsyncSession())
                {
                    var u = new User { Name = "EGR" };
                    await session.StoreAsync(u, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3, 4 }))
                    {
                        await store1.Operations.SendAsync(new PutAttachmentOperation("users/1", "a1", a1, "a2/jpeg"));
                    }

                    await session.Advanced.RefreshAsync(u);
                    var cv = session.Advanced.GetChangeVectorFor(u);
                    cvs.Add(("EGR", cv));
                }

                using (var session = store2.OpenAsyncSession())
                {
                    var u = new User { Name = "EGOR" };
                    await session.StoreAsync(u, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        store2.Operations.Send(new PutAttachmentOperation("users/1", "a1", a1, "a2/jpeg"));
                    }

                    await session.Advanced.RefreshAsync(u);
                    var cv = session.Advanced.GetChangeVectorFor(u);
                    cvs.Add(("EGOR", cv));
                }
                cvs.Sort(ChangeVectorComparer.Instance);
                var orderedDocsByEtag = cvs;
                await SetupReplicationAsync(store1, store2);
                await SetupReplicationAsync(store2, store1);
                WaitForDocumentWithAttachmentToReplicate<User>(store1, "users/1", "RESOLVED_#1_a1", Debugger.IsAttached ? 60000 : 15000);
                WaitForDocumentWithAttachmentToReplicate<User>(store2, "users/1", "RESOLVED_#1_a1", Debugger.IsAttached ? 60000 : 15000);
                WaitForMarker(store1, store2);

                var name = orderedDocsByEtag.First().Item1;
                var expectedName = name + "_RESOLVED";
                using (var session = store1.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.Equal(expectedName, user.Name);
                    var attachments = session.Advanced.Attachments.GetNames(user);
                    Assert.Equal(2, attachments.Length);

                    Assert.Equal("a1", attachments[0].Name);
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachments[0].Hash);
                    Assert.Equal("a2/jpeg", attachments[0].ContentType);
                    Assert.Equal(3, attachments[0].Size);

                    Assert.Equal("RESOLVED_#0_a1", attachments[1].Name);
                    Assert.Equal("KFF+TN9skHmMGpg7A3J8p3Q8IaOIBnJCnM/FvRXqX3I=", attachments[1].Hash);
                    Assert.Equal("a2/jpeg", attachments[1].ContentType);
                    Assert.Equal(4, attachments[1].Size);
                }

                using (var session = store2.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.Equal(expectedName, user.Name);
                    var attachments = session.Advanced.Attachments.GetNames(user);
                    Assert.Equal(2, attachments.Length);

                    Assert.Equal("a1", attachments[0].Name);
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachments[0].Hash);
                    Assert.Equal("a2/jpeg", attachments[0].ContentType);
                    Assert.Equal(3, attachments[0].Size);

                    Assert.Equal("RESOLVED_#0_a1", attachments[1].Name);
                    Assert.Equal("KFF+TN9skHmMGpg7A3J8p3Q8IaOIBnJCnM/FvRXqX3I=", attachments[1].Hash);
                    Assert.Equal("a2/jpeg", attachments[1].ContentType);
                    Assert.Equal(4, attachments[1].Size);
                }
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task ScriptResolver_ShouldNotRenameAttachment_ShouldRenameAndMergeAllMissingAttachmentsOnResolvedDocument(Options options)
        {
            var modifyDatabaseRecord = options.ModifyDatabaseRecord;
            using (var store1 = GetDocumentStore(options: new Options(options)
            {
                ModifyDatabaseRecord = record =>
                {
                    modifyDatabaseRecord?.Invoke(record);
                    record.ConflictSolverConfig = new ConflictSolver
                    {
                        ResolveToLatest = false,
                        ResolveByCollection = new Dictionary<string, ScriptResolver>()
                        {
                            {
                                "Users", new ScriptResolver()
                                {
                                    Script = @"docs[0]['@metadata']['@attachments'][0]['Name'] = 'newName';
                                               docs[0].Name = docs[0].Name + '_RESOLVED';
                                               return docs[0];
                                               "
                                }
                            }
                        }
                    };
                }
            }))
            using (var store2 = GetDocumentStore(options: new Options(options)
            {
                ModifyDatabaseRecord = record =>
                {
                    modifyDatabaseRecord?.Invoke(record);
                    record.ConflictSolverConfig = new ConflictSolver
                    {
                        ResolveToLatest = false,
                        ResolveByCollection = new Dictionary<string, ScriptResolver>()
                        {
                            {
                                "Users", new ScriptResolver()
                                {
                                    Script = @"docs[0]['@metadata']['@attachments'][0]['Name'] = 'newName';
                                               docs[0].Name = docs[0].Name + '_RESOLVED';
                                               return docs[0];
                                               "
                                }
                            }
                        }
                    };
                }
            }))
            {
                var cvs = new List<(string, string)>();
                using (var session = store1.OpenAsyncSession())
                {
                    var u = new User { Name = "EGOR" };
                    await session.StoreAsync(u, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3, 4 }))
                    {
                        await store1.Operations.SendAsync(new PutAttachmentOperation("users/1", "a1", a1, "a2/jpeg"));
                        a1.Position = 0;
                        await store1.Operations.SendAsync(new PutAttachmentOperation("users/1", "a10", a1, "a2/jpeg"));
                    }

                    await session.Advanced.RefreshAsync(u);
                    var cv = session.Advanced.GetChangeVectorFor(u);
                    cvs.Add(("EGOR", cv));
                }

                using (var session = store2.OpenAsyncSession())
                {
                    var u = new User { Name = "EGR" };
                    await session.StoreAsync(u, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        store2.Operations.Send(new PutAttachmentOperation("users/1", "a1", a1, "a2/jpeg"));
                    }

                    await session.Advanced.RefreshAsync(u);
                    var cv = session.Advanced.GetChangeVectorFor(u);
                    cvs.Add(("EGR", cv));
                }

                cvs.Sort(ChangeVectorComparer.Instance);
                var orderedDocsByEtag = cvs;
                await SetupReplicationAsync(store1, store2);
                await SetupReplicationAsync(store2, store1);
                WaitForDocumentWithAttachmentToReplicate<User>(store1, "users/1", "RESOLVED_#1_a1", Debugger.IsAttached ? 60000 : 15000);
                WaitForDocumentWithAttachmentToReplicate<User>(store2, "users/1", "RESOLVED_#1_a1", Debugger.IsAttached ? 60000 : 15000);
                WaitForMarker(store1, store2);

                var name = orderedDocsByEtag.First().Item1;
                var expectedName = name + "_RESOLVED";
                using (var session = store1.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.Equal(expectedName, user.Name);
                    var attachments = session.Advanced.Attachments.GetNames(user);
                    Assert.Equal(3, attachments.Length);

                    Assert.Equal("a1", attachments[0].Name);
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachments[0].Hash);
                    Assert.Equal("a2/jpeg", attachments[0].ContentType);
                    Assert.Equal(3, attachments[0].Size);

                    Assert.Equal("a10", attachments[1].Name);
                    Assert.Equal("KFF+TN9skHmMGpg7A3J8p3Q8IaOIBnJCnM/FvRXqX3I=", attachments[1].Hash);
                    Assert.Equal("a2/jpeg", attachments[1].ContentType);
                    Assert.Equal(4, attachments[1].Size);

                    Assert.Equal("RESOLVED_#0_a1", attachments[2].Name);
                    Assert.Equal("KFF+TN9skHmMGpg7A3J8p3Q8IaOIBnJCnM/FvRXqX3I=", attachments[2].Hash);
                    Assert.Equal("a2/jpeg", attachments[2].ContentType);
                    Assert.Equal(4, attachments[2].Size);
                }

                using (var session = store2.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.Equal(expectedName, user.Name);
                    var attachments = session.Advanced.Attachments.GetNames(user);
                    Assert.Equal(3, attachments.Length);

                    Assert.Equal("a1", attachments[0].Name);
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachments[0].Hash);
                    Assert.Equal("a2/jpeg", attachments[0].ContentType);
                    Assert.Equal(3, attachments[0].Size);

                    Assert.Equal("a10", attachments[1].Name);
                    Assert.Equal("KFF+TN9skHmMGpg7A3J8p3Q8IaOIBnJCnM/FvRXqX3I=", attachments[1].Hash);
                    Assert.Equal("a2/jpeg", attachments[1].ContentType);
                    Assert.Equal(4, attachments[1].Size);

                    Assert.Equal("RESOLVED_#0_a1", attachments[2].Name);
                    Assert.Equal("KFF+TN9skHmMGpg7A3J8p3Q8IaOIBnJCnM/FvRXqX3I=", attachments[2].Hash);
                    Assert.Equal("a2/jpeg", attachments[2].ContentType);
                    Assert.Equal(4, attachments[2].Size);
                }
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task ScriptResolver_ShouldNotRemoveAttachment_ShouldRenameAndMergeAllMissingAttachmentsOnResolvedDocument(Options options)
        {
            var modifyDatabaseRecord = options.ModifyDatabaseRecord;
            using (var store1 = GetDocumentStore(options: new Options(options)
            {
                ModifyDatabaseRecord = record =>
                {
                    modifyDatabaseRecord?.Invoke(record);
                    record.ConflictSolverConfig = new ConflictSolver
                    {
                        ResolveToLatest = false,
                        ResolveByCollection = new Dictionary<string, ScriptResolver>()
                        {
                            {
                                "Users", new ScriptResolver()
                                {
                                    Script = @"docs[0]['@metadata']['@attachments'].pop();
                                               docs[0].Name = docs[0].Name + '_RESOLVED';
                                               return docs[0];
                                               "
                                }
                            }
                        }
                    };
                }
            }))
            using (var store2 = GetDocumentStore(options: new Options(options)
            {
                ModifyDatabaseRecord = record =>
                {
                    modifyDatabaseRecord?.Invoke(record);
                    record.ConflictSolverConfig = new ConflictSolver
                    {
                        ResolveToLatest = false,
                        ResolveByCollection = new Dictionary<string, ScriptResolver>()
                        {
                            {
                                "Users", new ScriptResolver()
                                {
                                    Script = @"docs[0]['@metadata']['@attachments'].pop();
                                               docs[0].Name = docs[0].Name + '_RESOLVED';
                                               return docs[0];
                                               "
                                }
                            }
                        }
                    };
                }
            }))
            {
                var cvs = new List<(string, string, string)>();
                using (var session = store1.OpenAsyncSession())
                {
                    var u = new User { Name = "EGR" };
                    await session.StoreAsync(u, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3, 4 }))
                    {
                        await store1.Operations.SendAsync(new PutAttachmentOperation("users/1", "a1", a1, "a2/jpeg"));
                        a1.Position = 0;
                        await store1.Operations.SendAsync(new PutAttachmentOperation("users/1", "a10", a1, "a2/jpeg"));
                    }

                    await session.Advanced.RefreshAsync(u);
                    var cv = session.Advanced.GetChangeVectorFor(u);
                    cvs.Add(("EGR", cv, "KFF+TN9skHmMGpg7A3J8p3Q8IaOIBnJCnM/FvRXqX3I="));
                }

                using (var session = store2.OpenAsyncSession())
                {
                    var u = new User { Name = "EGOR" };
                    await session.StoreAsync(u, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        store2.Operations.Send(new PutAttachmentOperation("users/1", "a1", a1, "a2/jpeg"));
                    }

                    await session.Advanced.RefreshAsync(u);
                    var cv = session.Advanced.GetChangeVectorFor(u);
                    cvs.Add(("EGOR", cv, "EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo="));
                }

                cvs.Sort(ChangeVectorComparer.Instance);

                var orderedDocsByEtag = cvs;
                await SetupReplicationAsync(store1, store2);
                await SetupReplicationAsync(store2, store1);
                WaitForDocumentWithAttachmentToReplicate<User>(store1, "users/1", "a1", Debugger.IsAttached ? 60000 : 15000);
                WaitForDocumentWithAttachmentToReplicate<User>(store2, "users/1", "a1", Debugger.IsAttached ? 60000 : 15000);
                WaitForMarker(store1, store2);

                var name = orderedDocsByEtag.First().Item1;
                var expectedName = name + "_RESOLVED";
                using (var session = store1.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.Equal(expectedName, user.Name);
                    var attachments = session.Advanced.Attachments.GetNames(user);
                    Assert.Equal(3, attachments.Length);
                    if (name == "EGOR")
                    {
                        Assert.Equal("a1", attachments[0].Name);
                        Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachments[0].Hash);
                        Assert.Equal("a2/jpeg", attachments[0].ContentType);
                        Assert.Equal(3, attachments[0].Size);

                        Assert.Equal("a10", attachments[1].Name);
                        Assert.Equal("KFF+TN9skHmMGpg7A3J8p3Q8IaOIBnJCnM/FvRXqX3I=", attachments[1].Hash);
                        Assert.Equal("a2/jpeg", attachments[1].ContentType);
                        Assert.Equal(4, attachments[1].Size);

                        Assert.Equal("RESOLVED_#0_a1", attachments[2].Name);
                        Assert.Equal("KFF+TN9skHmMGpg7A3J8p3Q8IaOIBnJCnM/FvRXqX3I=", attachments[2].Hash);
                        Assert.Equal("a2/jpeg", attachments[2].ContentType);
                        Assert.Equal(4, attachments[2].Size);
                    }
                    else
                    {
                        Assert.Equal("a1", attachments[0].Name);
                        Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachments[0].Hash);
                        Assert.Equal("a2/jpeg", attachments[0].ContentType);
                        Assert.Equal(3, attachments[0].Size);

                        Assert.Equal("a10", attachments[1].Name);
                        Assert.Equal("KFF+TN9skHmMGpg7A3J8p3Q8IaOIBnJCnM/FvRXqX3I=", attachments[1].Hash);
                        Assert.Equal("a2/jpeg", attachments[1].ContentType);
                        Assert.Equal(4, attachments[1].Size);

                        Assert.Equal("RESOLVED_#0_a1", attachments[2].Name);
                        Assert.Equal("KFF+TN9skHmMGpg7A3J8p3Q8IaOIBnJCnM/FvRXqX3I=", attachments[2].Hash);
                        Assert.Equal("a2/jpeg", attachments[2].ContentType);
                        Assert.Equal(4, attachments[2].Size);
                    }
                }

                using (var session = store2.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.Equal(expectedName, user.Name);
                    var attachments = session.Advanced.Attachments.GetNames(user);
                    Assert.Equal(3, attachments.Length);

                    if (name == "EGOR")
                    {
                        Assert.Equal("a1", attachments[0].Name);
                        Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachments[0].Hash);
                        Assert.Equal("a2/jpeg", attachments[0].ContentType);
                        Assert.Equal(3, attachments[0].Size);

                        Assert.Equal("a10", attachments[1].Name);
                        Assert.Equal("KFF+TN9skHmMGpg7A3J8p3Q8IaOIBnJCnM/FvRXqX3I=", attachments[1].Hash);
                        Assert.Equal("a2/jpeg", attachments[1].ContentType);
                        Assert.Equal(4, attachments[1].Size);

                        Assert.Equal("RESOLVED_#0_a1", attachments[2].Name);
                        Assert.Equal("KFF+TN9skHmMGpg7A3J8p3Q8IaOIBnJCnM/FvRXqX3I=", attachments[2].Hash);
                        Assert.Equal("a2/jpeg", attachments[2].ContentType);
                        Assert.Equal(4, attachments[2].Size);
                    }
                    else
                    {
                        Assert.Equal("a1", attachments[0].Name);
                        Assert.Equal("KFF+TN9skHmMGpg7A3J8p3Q8IaOIBnJCnM/FvRXqX3I=", attachments[0].Hash);
                        Assert.Equal("a2/jpeg", attachments[0].ContentType);
                        Assert.Equal(4, attachments[0].Size);

                        Assert.Equal("a10", attachments[1].Name);
                        Assert.Equal("KFF+TN9skHmMGpg7A3J8p3Q8IaOIBnJCnM/FvRXqX3I=", attachments[1].Hash);
                        Assert.Equal("a2/jpeg", attachments[1].ContentType);
                        Assert.Equal(4, attachments[1].Size);

                        Assert.Equal("RESOLVED_#0_a1", attachments[2].Name);
                        Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachments[2].Hash);
                        Assert.Equal("a2/jpeg", attachments[2].ContentType);
                        Assert.Equal(3, attachments[2].Size);
                    }
                }
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task ScriptResolver_ShouldNotAddAttachment_ShouldRenameAndMergeDuplicateAttachments(Options options)
        {
            var modifyDatabaseRecord = options.ModifyDatabaseRecord;
            using (var store1 = GetDocumentStore(options: new Options(options)
            {
                ModifyDatabaseRecord = record =>
                {
                    modifyDatabaseRecord?.Invoke(record);
                    record.ConflictSolverConfig = new ConflictSolver
                    {
                        ResolveToLatest = false,
                        ResolveByCollection = new Dictionary<string, ScriptResolver>()
                        {
                            {
                                "Users", new ScriptResolver()
                                {
                                    Script = @"var attachment = {Name:'newnewnew', Hash:'322+228skHmEGRg7A3J8p3Q8IaOIBnJCnM/FvRXqX3I=', Size:50, ContentType:'image/gif'};
                                               docs[0]['@metadata']['@attachments'].push(attachment);
                                               docs[0].Name = docs[0].Name + '_RESOLVED';
                                               return docs[0];
                                               "
                                }
                            }
                        }
                    };
                }
            }))
            using (var store2 = GetDocumentStore(options: new Options(options)
            {
                ModifyDatabaseRecord = record =>
                {
                    modifyDatabaseRecord?.Invoke(record);
                    record.ConflictSolverConfig = new ConflictSolver
                    {
                        ResolveToLatest = false,
                        ResolveByCollection = new Dictionary<string, ScriptResolver>()
                        {
                            {
                                "Users", new ScriptResolver()
                                {
                                    Script = @"var attachment = {Name:'newnewnew', Hash:'322+228skHmEGRg7A3J8p3Q8IaOIBnJCnM/FvRXqX3I=', Size:50, ContentType:'image/gif'};
                                               docs[0]['@metadata']['@attachments'].push(attachment);
                                               docs[0].Name = docs[0].Name + '_RESOLVED';
                                               return docs[0];
                                               "
                                }
                            }
                        }
                    };
                }
            }))
            {
                var cvs = new List<(string, string, string)>();
                using (var session = store1.OpenAsyncSession())
                {
                    var u = new User { Name = "EGOR" };
                    await session.StoreAsync(u, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3, 4 }))
                    {
                        await store1.Operations.SendAsync(new PutAttachmentOperation("users/1", "a1", a1, "a2/jpeg"));
                        a1.Position = 0;
                        await store1.Operations.SendAsync(new PutAttachmentOperation("users/1", "a10", a1, "a2/jpeg"));
                    }

                    await session.Advanced.RefreshAsync(u);
                    var cv = session.Advanced.GetChangeVectorFor(u);
                    cvs.Add(("EGOR", cv, "KFF+TN9skHmMGpg7A3J8p3Q8IaOIBnJCnM/FvRXqX3I="));
                }

                using (var session = store2.OpenAsyncSession())
                {
                    var u = new User { Name = "EGR" };
                    await session.StoreAsync(u, "users/1");
                    await session.SaveChangesAsync();

                    using (var a1 = new MemoryStream(new byte[] { 1, 2, 3 }))
                    {
                        store2.Operations.Send(new PutAttachmentOperation("users/1", "a1", a1, "a2/jpeg"));
                    }

                    await session.Advanced.RefreshAsync(u);
                    var cv = session.Advanced.GetChangeVectorFor(u);
                    cvs.Add(("EGR", cv, "EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo="));
                }

                cvs.Sort(ChangeVectorComparer.Instance);

                var orderedDocsByEtag = cvs;
                await SetupReplicationAsync(store1, store2);
                await SetupReplicationAsync(store2, store1);
                WaitForDocumentWithAttachmentToReplicate<User>(store1, "users/1", "RESOLVED_#0_a1", Debugger.IsAttached ? 60000 : 15000);
                WaitForDocumentWithAttachmentToReplicate<User>(store2, "users/1", "RESOLVED_#0_a1", Debugger.IsAttached ? 60000 : 15000);
                WaitForMarker(store1, store2);

                var name = orderedDocsByEtag.First().Item1;
                var expectedName = name + "_RESOLVED";
                using (var session = store1.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.Equal(expectedName, user.Name);
                    var attachments = session.Advanced.Attachments.GetNames(user);
                    Assert.Equal(3, attachments.Length);

                    Assert.Equal("a1", attachments[0].Name);
                    Assert.Equal(orderedDocsByEtag.First().Item3, attachments[0].Hash);
                    Assert.Equal("a2/jpeg", attachments[0].ContentType);
                    Assert.Equal(orderedDocsByEtag.First().Item1.Length, attachments[0].Size);

                    Assert.Equal("a10", attachments[1].Name);
                    Assert.Equal("KFF+TN9skHmMGpg7A3J8p3Q8IaOIBnJCnM/FvRXqX3I=", attachments[1].Hash);
                    Assert.Equal("a2/jpeg", attachments[1].ContentType);
                    Assert.Equal(4, attachments[1].Size);

                    Assert.Equal("RESOLVED_#0_a1", attachments[2].Name);
                    Assert.Equal(orderedDocsByEtag.Last().Item3, attachments[2].Hash);
                    Assert.Equal("a2/jpeg", attachments[2].ContentType);
                    Assert.Equal(orderedDocsByEtag.Last().Item1.Length, attachments[2].Size);
                }

                using (var session = store2.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/1");
                    Assert.Equal(expectedName, user.Name);
                    var attachments = session.Advanced.Attachments.GetNames(user);
                    Assert.Equal(3, attachments.Length);

                    Assert.Equal("a1", attachments[0].Name);
                    Assert.Equal(orderedDocsByEtag.First().Item3, attachments[0].Hash);
                    Assert.Equal("a2/jpeg", attachments[0].ContentType);
                    Assert.Equal(orderedDocsByEtag.First().Item1.Length, attachments[0].Size);

                    Assert.Equal("a10", attachments[1].Name);
                    Assert.Equal("KFF+TN9skHmMGpg7A3J8p3Q8IaOIBnJCnM/FvRXqX3I=", attachments[1].Hash);
                    Assert.Equal("a2/jpeg", attachments[1].ContentType);
                    Assert.Equal(4, attachments[1].Size);

                    Assert.Equal("RESOLVED_#0_a1", attachments[2].Name);
                    Assert.Equal(orderedDocsByEtag.Last().Item3, attachments[2].Hash);
                    Assert.Equal("a2/jpeg", attachments[2].ContentType);
                    Assert.Equal(orderedDocsByEtag.Last().Item1.Length, attachments[2].Size);
                }
            }
        }


        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task ConflictOfAttachmentAndDocument3StoresDifferentLastModifiedOrder(Options options)
        {
            var modifyDatabaseRecord = options.ModifyDatabaseRecord;
            options.ModifyDatabaseRecord = record =>
            {
                modifyDatabaseRecord?.Invoke(record);
                record.Settings[RavenConfiguration.GetKey(x => x.Replication.MaxItemsCount)] = 1.ToString();
            };
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            using (var store3 = GetDocumentStore(options))
            {
                var user = new User { Name = "Karmel", Id = "users/1" };
                using (var session = store1.OpenSession())
                {
                    session.Store(user, user.Id);
                    session.SaveChanges();
                }

                using (var profileStream = new MemoryStream(new byte[] { 1, 2, 3 }))
                {
                    var result = store1.Operations.Send(new PutAttachmentOperation("users/1", "foo/bar", profileStream, "image/png"));
                    Assert.Equal("foo/bar", result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("image/png", result.ContentType);
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", result.Hash);
                }
                await SetupReplicationAsync(store1, store2);
                await SetupReplicationAsync(store1, store3);

                await EnsureReplicatingAsync(store1, store2);
                await EnsureReplicatingAsync(store1, store3);

                using (var backgroundStream = new MemoryStream(new byte[] { 10, 20, 30, 40, 50 }))
                {
                    var result = store2.Operations.Send(new PutAttachmentOperation("users/1", "foo/bar", backgroundStream, "image/png"));
                    Assert.Equal("foo/bar", result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("image/png", result.ContentType);
                    Assert.Equal("igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=", result.Hash);
                }

                using (var backgroundStream = new MemoryStream(new byte[] { 10, 20, 30, 40, 50, 60 }))
                {
                    var result = store3.Operations.Send(new PutAttachmentOperation("users/1", "foo/bar", backgroundStream, "image/png"));
                    Assert.Equal("foo/bar", result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("image/png", result.ContentType);
                    Assert.Equal("7hoAZadly0e2TKk4NC6+MrtVuqZblV3+UDW7/Iz9H5U=", result.Hash);
                }

                var stores = new DocumentStore[] { store1, store2, store3 };
                await WriteStatus(stores, "Stage 1");

                using (var session = store1.OpenAsyncSession())
                {
                    var u = await session.LoadAsync<User>("users/1");
                    u.Age = 30;
                    await session.SaveChangesAsync();
                }

                await EnsureReplicatingAsync(store1, store2);
                await EnsureReplicatingAsync(store1, store3);

                await WriteStatus(stores, "Stage 2");

                await SetupReplicationAsync(store2, store1);
                await SetupReplicationAsync(store2, store3);
                await EnsureReplicatingAsync(store2, store1);
                await EnsureReplicatingAsync(store2, store3);

                await WriteStatus(stores, "Stage 3");

                await SetupReplicationAsync(store3, store2);
                await EnsureReplicatingAsync(store3, store2);

                await WriteStatus(stores, "Stage 4");

                var dbName1 = options.DatabaseMode == RavenDatabaseMode.Single ? store1.Database : await Sharding.GetShardDatabaseNameForDocAsync(store1, "users/1");
                var storage = await Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(dbName1);

                using (storage.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
                {
                    var res = await WaitForValueAsync(async () =>
                    {
                        using (var session = store1.OpenAsyncSession())
                        using (var session2 = store2.OpenAsyncSession())
                        using (var session3 = store3.OpenAsyncSession())
                        {
                            var attachment = await session.Advanced.Attachments.GetAsync("users/1", "foo/bar");
                            var attachment2 = await session2.Advanced.Attachments.GetAsync("users/1", "foo/bar");
                            var attachment3 = await session3.Advanced.Attachments.GetAsync("users/1", "foo/bar");

                            if (attachment != null && attachment2 != null && attachment3 != null &&
                                attachment.Details.Name == "foo/bar" &&
                                AreAttachmentDetailsEqual(attachment.Details, attachment2.Details, context, excludeChangeVector: true) &&
                                AreAttachmentDetailsEqual(attachment.Details, attachment3.Details, context, excludeChangeVector: true))
                            {
                                return true;
                            }

                            return false;
                        }
                    }, true, 30_000, interval: 333);

                    await WriteStatus(stores, "Stage 5");

                    using (var session = store1.OpenAsyncSession())
                    using (var session2 = store2.OpenAsyncSession())
                    using (var session3 = store3.OpenAsyncSession())
                    {
                        var u1 = await session.LoadAsync<User>("users/1");
                        var u2 = await session2.LoadAsync<User>("users/1");
                        var u3 = await session3.LoadAsync<User>("users/1");

                        var cv1 = context.GetChangeVector(session.Advanced.GetChangeVectorFor(u1)).Version.AsString();
                        var cv2 = context.GetChangeVector(session2.Advanced.GetChangeVectorFor(u2)).Version.AsString();
                        var cv3 = context.GetChangeVector(session3.Advanced.GetChangeVectorFor(u3)).Version.AsString();

                        Console.WriteLine($"\ncv1: {cv1}");
                        Console.WriteLine($"cv2: {cv2}");
                        Console.WriteLine($"cv3: {cv3}\n");

                        Assert.True(cv1.Equals(cv2));
                        Assert.True(cv1.Equals(cv3));

                        var attachment = await session.Advanced.Attachments.GetAsync("users/1", "foo/bar");
                        var attachment2 = await session2.Advanced.Attachments.GetAsync("users/1", "foo/bar");
                        var attachment3 = await session3.Advanced.Attachments.GetAsync("users/1", "foo/bar");

                        Assert.NotNull(attachment);
                        Assert.NotNull(attachment2);
                        Assert.NotNull(attachment3);

                        var attachmentChangeVector = context.GetChangeVector(attachment.Details.ChangeVector).Version.AsString();
                        var attachmentChangeVector2 = context.GetChangeVector(attachment2.Details.ChangeVector).Version.AsString();
                        var attachmentChangeVector3 = context.GetChangeVector(attachment3.Details.ChangeVector).Version.AsString();

                        user = await session.LoadAsync<User>("users/1");
                        Assert.True(("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=" == attachment.Details.Hash && user.Age == 30) ||
                                    ("7hoAZadly0e2TKk4NC6+MrtVuqZblV3+UDW7/Iz9H5U=" == attachment.Details.Hash && user.Age == 0));
                        Assert.Equal("foo/bar", attachment.Details.Name);
                        Assert.Equal(attachment.Details.Hash, attachment2.Details.Hash);
                        Assert.Equal(attachment.Details.Name, attachment2.Details.Name);
                        Assert.Equal(attachment3.Details.Hash, attachment2.Details.Hash);
                        Assert.Equal(attachment3.Details.Name, attachment2.Details.Name);

                        // RavenDB-21650
                        //Assert.Equal(attachmentChangeVector, attachmentChangeVector2);
                        //Assert.Equal(attachmentChangeVector3, attachmentChangeVector2);
                    }
                }
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task ConflictOfAttachmentAndDocument_SameMetadataDifferentAttachmentChangeVectors_RevisionsDisabled(Options options)
        {
            var modifyDatabaseRecord = options.ModifyDatabaseRecord;
            options.ModifyDatabaseRecord = record =>
            {
                modifyDatabaseRecord?.Invoke(record);
                record.Settings[RavenConfiguration.GetKey(x => x.Replication.MaxItemsCount)] = 1.ToString();
            };

            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                var revisionCollectionConfiguration = new RevisionsCollectionConfiguration { Disabled = true };
                await store1.Maintenance.Server.SendAsync(new ConfigureRevisionsForConflictsOperation(store1.Database, revisionCollectionConfiguration));
                await store2.Maintenance.Server.SendAsync(new ConfigureRevisionsForConflictsOperation(store2.Database, revisionCollectionConfiguration));

                using (var session = store1.OpenSession())
                {
                    session.Store(new User { Name = "Karmel" }, "users/1");
                    session.SaveChanges();
                }

                using (var profileStream = new MemoryStream(new byte[] { 1, 2, 3 }))
                {
                    var result = store1.Operations.Send(new PutAttachmentOperation("users/1", "foo/bar", profileStream, "image/png"));
                    Assert.Equal("foo/bar", result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("image/png", result.ContentType);
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", result.Hash);
                }
                await SetupReplicationAsync(store1, store2);
                await SetupReplicationAsync(store2, store1);

                await EnsureReplicatingAsync(store1, store2);
                await EnsureReplicatingAsync(store2, store1);

                var stores = new DocumentStore[] { store1, store2 };

                await WriteStatus(stores, "Stage 1");

                var replication1 = await GetReplicationManagerAsync(store1, store1.Database, options.DatabaseMode);
                var replication2 = await GetReplicationManagerAsync(store2, store2.Database, options.DatabaseMode);

                replication1.Break();
                replication2.Break();

                using (var backgroundStream = new MemoryStream(new byte[] { 10, 20, 30, 40, 50 }))
                {
                    var result = store2.Operations.Send(new PutAttachmentOperation("users/1", "foo/bar", backgroundStream, "image/png"));
                    Assert.Equal("foo/bar", result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("image/png", result.ContentType);
                    Assert.Equal("igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=", result.Hash);
                }

                using (var backgroundStream = new MemoryStream(new byte[] { 10, 20, 30, 40, 50 }))
                {
                    var result = store1.Operations.Send(new PutAttachmentOperation("users/1", "foo/bar", backgroundStream, "image/png"));
                    Assert.Equal("foo/bar", result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("image/png", result.ContentType);
                    Assert.Equal("igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=", result.Hash);
                }

                await WriteStatus(stores, "Stage 2");

                replication1.Mend();
                replication2.Mend();

                await EnsureReplicatingAsync(store1, store2);
                await EnsureReplicatingAsync(store2, store1);

                await WriteStatus(stores, "Stage 3");

                var dbName1 = options.DatabaseMode == RavenDatabaseMode.Single ? store1.Database : await Sharding.GetShardDatabaseNameForDocAsync(store1, "users/1");
                var storage = await Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(dbName1);

                using (storage.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
                {
                    var res = await WaitForValueAsync(async () =>
                    {
                        using (var session = store1.OpenAsyncSession())
                        using (var session2 = store2.OpenAsyncSession())
                        {
                            var attachment = await session.Advanced.Attachments.GetAsync("users/1", "foo/bar");
                            var attachment2 = await session2.Advanced.Attachments.GetAsync("users/1", "foo/bar");

                            if (attachment != null && attachment2 != null &&
                                attachment.Details.Hash == "igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=" &&
                                attachment.Details.Name == "foo/bar" &&
                                AreAttachmentDetailsEqual(attachment.Details, attachment2.Details, context))
                            {
                                return true;
                            }
                            return false;
                        }
                    }, true, 15_000, 500);

                    Assert.True(res);

                    using (var session = store1.OpenAsyncSession())
                    using (var session2 = store2.OpenAsyncSession())
                    {
                        var attachment = await session.Advanced.Attachments.GetAsync("users/1", "foo/bar");
                        var attachment2 = await session2.Advanced.Attachments.GetAsync("users/1", "foo/bar");

                        Assert.NotNull(attachment);
                        Assert.NotNull(attachment2);

                        var attachmentChangeVector = context.GetChangeVector(attachment.Details.ChangeVector).Version.AsString();
                        var attachmentChangeVector2 = context.GetChangeVector(attachment2.Details.ChangeVector).Version.AsString();

                        Assert.Equal("igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=", attachment.Details.Hash);
                        Assert.Equal("igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=", attachment2.Details.Hash);
                        Assert.Equal(attachment.Details.Hash, attachment2.Details.Hash);
                        Assert.Equal(attachmentChangeVector, attachmentChangeVector2);
                        Assert.Equal(attachment.Details.Name, attachment2.Details.Name);
                    }
                }
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task ConflictOfAttachmentAndDocument(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            {
                using (var session = store1.OpenSession())
                {
                    session.Store(new User { Name = "Karmel" }, "users/1");
                    session.SaveChanges();
                }

                using (var profileStream = new MemoryStream(new byte[] { 1, 2, 3 }))
                {
                    var result = store1.Operations.Send(new PutAttachmentOperation("users/1", "foo/bar", profileStream, "image/png"));
                    Assert.Equal("foo/bar", result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("image/png", result.ContentType);
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", result.Hash);
                }

                await SetupReplicationAsync(store2, store1);
                await SetupReplicationAsync(store1, store2);

                await EnsureReplicatingAsync(store2, store1);
                await EnsureReplicatingAsync(store1, store2);

                var stores = new DocumentStore[] { store1, store2 };
                await WriteStatus(stores, "Stage 1");

                var replication1 = await GetReplicationManagerAsync(store1, store1.Database, options.DatabaseMode);
                var replication2 = await GetReplicationManagerAsync(store2, store2.Database, options.DatabaseMode);

                replication1.Break();
                replication2.Break();

                using (var backgroundStream = new MemoryStream(new byte[] { 10, 20, 30, 40, 50 }))
                {
                    var result = store2.Operations.Send(new PutAttachmentOperation("users/1", "foo/bar", backgroundStream, "image/png"));
                    Assert.Equal("foo/bar", result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("image/png", result.ContentType);
                    Assert.Equal("igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=", result.Hash);
                }

                using (var session = store1.OpenAsyncSession())
                {
                    var u = await session.LoadAsync<User>("users/1");
                    u.Age = 30;
                    await session.SaveChangesAsync();
                }

                await WriteStatus(stores, "Stage 2");

                replication1.Mend();

                replication2.Mend();

                await EnsureReplicatingAsync(store2, store1);
                await EnsureReplicatingAsync(store1, store2);

                await WriteStatus(stores, "Stage 3");

                var dbName1 = options.DatabaseMode == RavenDatabaseMode.Single ? store1.Database : await Sharding.GetShardDatabaseNameForDocAsync(store1, "users/1");
                var storage = await Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(dbName1);

                using (storage.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
                {
                    await WaitForValueAsync(async () =>
                    {
                        using var session1 = store1.OpenAsyncSession();
                        using var session2 = store2.OpenAsyncSession();
                        var attachment = await session1.Advanced.Attachments.GetAsync("users/1", "foo/bar");
                        var attachment2 = await session2.Advanced.Attachments.GetAsync("users/1", "foo/bar");

                        if (attachment != null && attachment2 != null &&
                            attachment.Details.Hash == "EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=" &&
                            attachment.Details.Name == "foo/bar" &&
                            AreAttachmentDetailsEqual(attachment.Details, attachment2.Details, context, excludeChangeVector: true))
                        {
                            return true;
                        }

                        return false;
                    }, true, 10000, 500);


                    using var session1 = store1.OpenAsyncSession();
                    using var session2 = store2.OpenAsyncSession();

                    var user = await session1.LoadAsync<User>("users/1");
                    Assert.Equal(30, user.Age);
                    var user2 = await session2.LoadAsync<User>("users/1");
                    Assert.Equal(30, user2.Age);

                    await WriteAttachmentDetails(stores);

                    var attachment = await session1.Advanced.Attachments.GetAsync("users/1", "foo/bar");
                    var attachment2 = await session2.Advanced.Attachments.GetAsync("users/1", "foo/bar");

                    Assert.NotNull(attachment);
                    Assert.NotNull(attachment2);

                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachment.Details.Hash);
                    Assert.Equal("foo/bar", attachment.Details.Name);

                    Assert.Equal(attachment.Details.Hash, attachment2.Details.Hash);
                    Assert.Equal(attachment.Details.Name, attachment2.Details.Name);

                    // RavenDB-21650
                    //var attachmentChangeVector = context.GetChangeVector(attachment.Details.ChangeVector).Version.AsString();
                    //var attachmentChangeVector2 = context.GetChangeVector(attachment2.Details.ChangeVector).Version.AsString();
                    //Assert.Equal(attachmentChangeVector, attachmentChangeVector2);

                    await EnsureNoReplicationLoopAsync(store1, mode: options.DatabaseMode);
                    await EnsureNoReplicationLoopAsync(store2, mode: options.DatabaseMode);
                }
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task ConflictOfAttachmentAndDocument3Stores(Options options)
        {
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            using (var store3 = GetDocumentStore(options))
            {
                using (var session = store1.OpenSession())
                {
                    session.Store(new User { Name = "Karmel" }, "users/1");
                    session.SaveChanges();
                }

                using (var profileStream = new MemoryStream(new byte[] { 1, 2, 3 }))
                {
                    var result = store1.Operations.Send(new PutAttachmentOperation("users/1", "foo/bar", profileStream, "image/png"));
                    Assert.Equal("foo/bar", result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("image/png", result.ContentType);
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", result.Hash);
                }

                await SetupReplicationAsync(store1, store2);
                await SetupReplicationAsync(store1, store3);

                await SetupReplicationAsync(store2, store1);
                await SetupReplicationAsync(store2, store3);

                await SetupReplicationAsync(store3, store1);
                await SetupReplicationAsync(store3, store2);

                await EnsureReplicatingAsync(store1, store2);
                await EnsureReplicatingAsync(store1, store3);

                await EnsureReplicatingAsync(store2, store1);
                await EnsureReplicatingAsync(store2, store3);

                await EnsureReplicatingAsync(store3, store1);
                await EnsureReplicatingAsync(store3, store2);

                var replication1 = await GetReplicationManagerAsync(store1, store1.Database, options.DatabaseMode, servers: new List<RavenServer>() { Server });
                var replication2 = await GetReplicationManagerAsync(store2, store2.Database, options.DatabaseMode, servers: new List<RavenServer>() { Server });
                var replication3 = await GetReplicationManagerAsync(store3, store3.Database, options.DatabaseMode, servers: new List<RavenServer>() { Server });

                replication1.Break();
                replication2.Break();
                replication3.Break();

                var stores = new DocumentStore[] { store1, store2, store3 };

                await WriteAttachmentDetails(stores);
                await WriteStatus(stores, "Stage 1");

                using (var backgroundStream = new MemoryStream(new byte[] { 10, 20, 30, 40, 50 }))
                {
                    var result = store2.Operations.Send(new PutAttachmentOperation("users/1", "foo/bar", backgroundStream, "image/png"));
                    Assert.Equal("foo/bar", result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("image/png", result.ContentType);
                    Assert.Equal("igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=", result.Hash);
                }

                using (var backgroundStream = new MemoryStream(new byte[] { 10, 20, 30, 40, 50, 60 }))
                {
                    var result = store3.Operations.Send(new PutAttachmentOperation("users/1", "foo/bar", backgroundStream, "image/png"));
                    Assert.Equal("foo/bar", result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("image/png", result.ContentType);
                    Assert.Equal("7hoAZadly0e2TKk4NC6+MrtVuqZblV3+UDW7/Iz9H5U=", result.Hash);
                }

                using (var session = store1.OpenAsyncSession())
                {
                    var u = await session.LoadAsync<User>("users/1");
                    u.Age = 30;
                    await session.SaveChangesAsync();
                }

                await WriteStatus(stores, "Stage 2");

                replication1.Mend();
                replication2.Mend();
                replication3.Mend();

                await EnsureReplicatingAsync(store1, store2);
                await EnsureReplicatingAsync(store1, store3);

                await EnsureReplicatingAsync(store2, store1);
                await EnsureReplicatingAsync(store2, store3);

                await EnsureReplicatingAsync(store3, store1);
                await EnsureReplicatingAsync(store3, store2);

                await WriteStatus(stores, "Stage 3");

                var dbName1 = options.DatabaseMode == RavenDatabaseMode.Single ? store1.Database : await Sharding.GetShardDatabaseNameForDocAsync(store1, "users/1");
                var storage = await Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(dbName1);

                using (storage.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
                {
                    await WaitForValueAsync(async () =>
                    {
                        using (var session = store1.OpenAsyncSession())
                        using (var session2 = store2.OpenAsyncSession())
                        using (var session3 = store3.OpenAsyncSession())
                        {
                            var attachment = await session.Advanced.Attachments.GetAsync("users/1", "foo/bar");
                            var attachment2 = await session2.Advanced.Attachments.GetAsync("users/1", "foo/bar");
                            var attachment3 = await session3.Advanced.Attachments.GetAsync("users/1", "foo/bar");

                            if (attachment != null && attachment2 != null && attachment3 != null &&
                                attachment.Details.Name == "foo/bar" &&
                                AreAttachmentDetailsEqual(attachment.Details, attachment2.Details, context) &&
                                AreAttachmentDetailsEqual(attachment.Details, attachment3.Details, context))
                            {
                                return true;
                            }

                            return false;
                        }
                    }, true, 30_000, 500);

                    await WriteAttachmentDetails(stores);

                    using (var session = store1.OpenAsyncSession())
                    using (var session2 = store2.OpenAsyncSession())
                    using (var session3 = store3.OpenAsyncSession())
                    {
                        var user = await session.LoadAsync<User>("users/1");

                        var attachment = await session.Advanced.Attachments.GetAsync("users/1", "foo/bar");
                        var attachment2 = await session2.Advanced.Attachments.GetAsync("users/1", "foo/bar");
                        var attachment3 = await session3.Advanced.Attachments.GetAsync("users/1", "foo/bar");

                        Assert.NotNull(attachment);
                        Assert.NotNull(attachment2);
                        Assert.NotNull(attachment3);

                        var attachmentChangeVector = context.GetChangeVector(attachment.Details.ChangeVector).Version.AsString();
                        var attachmentChangeVector2 = context.GetChangeVector(attachment2.Details.ChangeVector).Version.AsString();
                        var attachmentChangeVector3 = context.GetChangeVector(attachment3.Details.ChangeVector).Version.AsString();

                        Assert.True(("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=" == attachment.Details.Hash && user.Age == 30) ||
                                    ("7hoAZadly0e2TKk4NC6+MrtVuqZblV3+UDW7/Iz9H5U=" == attachment.Details.Hash && user.Age == 0));
                        Assert.Equal("foo/bar", attachment.Details.Name);

                        Assert.Equal(attachment.Details.Hash, attachment2.Details.Hash);
                        Assert.Equal(attachmentChangeVector, attachmentChangeVector2);
                        Assert.Equal(attachment.Details.Name, attachment2.Details.Name);

                        Assert.Equal(attachment3.Details.Hash, attachment2.Details.Hash);
                        Assert.Equal(attachmentChangeVector3, attachmentChangeVector2);
                        Assert.Equal(attachment3.Details.Name, attachment2.Details.Name);
                    }
                }
            }
        }

        // the original issue RavenDB-19421
        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All)]
        public async Task ReplicationShouldSendMissingAttachments(Options options)
        {
            using (var source = GetDocumentStore(options))
            using (var destination = GetDocumentStore(options))
            {
                await SetupReplicationAsync(source, destination);

                using (var session = source.OpenAsyncSession())
                using (var fooStream = new MemoryStream(new byte[] { 1, 2, 3 }))
                {
                    for (int i = 0; i < 25; i++)
                    {
                        fooStream.Position = 0;
                        await session.StoreAsync(new User { Name = "Foo" }, $"FoObAr/{i}");
                        session.Advanced.Attachments.Store($"FoObAr/{i}", "foo.png", fooStream, "image/png");
                        await session.SaveChangesAsync();

                        Assert.NotNull(WaitForDocumentToReplicate<User>(destination, $"FoObAr/{i}", 15 * 1000));
                    }
                }

                using (var session = destination.OpenAsyncSession())
                {
                    for (int i = 0; i < 25; i++)
                    {
                        session.Delete($"FoObAr/{i}");
                    }

                    await session.SaveChangesAsync();
                }

                using (var session = source.OpenAsyncSession())
                using (var fooStream2 = new MemoryStream(new byte[] { 4, 5, 6 }))
                {
                    for (int i = 0; i < 25; i++)
                    {
                        fooStream2.Position = 0;
                        session.Advanced.Attachments.Store($"FoObAr/{i}", "foo2.png", fooStream2, "image/png");
                        await session.SaveChangesAsync();

                        Assert.NotNull(WaitForDocumentWithAttachmentToReplicate<User>(destination, $"FoObAr/{i}", "foo2.png", 30 * 1000));
                    }
                }

                var buffer = new byte[3];
                using (var session = destination.OpenAsyncSession())
                {
                    session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;
                    for (int i = 0; i < 25; i++)
                    {
                        var user = await session.LoadAsync<User>($"FoObAr/{i}");
                        var attachments = session.Advanced.Attachments.GetNames(user);
                        Assert.Equal(2, attachments.Length);

                        foreach (var name in attachments)
                        {
                            using (var attachment = await session.Advanced.Attachments.GetAsync(user, name.Name))
                            {
                                Assert.NotNull(attachment);
                                Assert.Equal(3, await attachment.Stream.ReadAsync(buffer, 0, 3));
                                if (attachment.Details.Name == "foo.png")
                                {
                                    Assert.Equal(1, buffer[0]);
                                    Assert.Equal(2, buffer[1]);
                                    Assert.Equal(3, buffer[2]);
                                }
                            }
                        }
                    }
                }
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.All, Skip = "fix me")]
        public async Task ConflictOfAttachmentAndDocument3StoresDifferentLastModifiedOrder_RevisionsDisabled_MissingAttachmentLoop(Options options)
        {
            var modifyDatabaseRecord = options.ModifyDatabaseRecord;
            options.ModifyDatabaseRecord = record =>
            {
                modifyDatabaseRecord?.Invoke(record);
                record.Settings[RavenConfiguration.GetKey(x => x.Replication.MaxItemsCount)] = 1.ToString();
            };
            using (var store1 = GetDocumentStore(options))
            using (var store2 = GetDocumentStore(options))
            using (var store3 = GetDocumentStore(options))
            {
                await store1.Maintenance.Server.SendAsync(new ConfigureRevisionsForConflictsOperation(store1.Database, new RevisionsCollectionConfiguration
                {
                    Disabled = true
                }));
                await store2.Maintenance.Server.SendAsync(new ConfigureRevisionsForConflictsOperation(store2.Database, new RevisionsCollectionConfiguration
                {
                    Disabled = true
                }));
                await store3.Maintenance.Server.SendAsync(new ConfigureRevisionsForConflictsOperation(store3.Database, new RevisionsCollectionConfiguration
                {
                    Disabled = true
                }));

                using (var session = store1.OpenSession())
                {
                    session.Store(new User { Name = "Karmel" }, "users/1");
                    session.SaveChanges();
                }

                using (var profileStream = new MemoryStream(new byte[] { 1, 2, 3 }))
                {
                    var result = store1.Operations.Send(new PutAttachmentOperation("users/1", "foo/bar", profileStream, "image/png"));
                    Assert.Equal("foo/bar", result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("image/png", result.ContentType);
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", result.Hash);
                }

                await SetupReplicationAsync(store2, store1);
                await SetupReplicationAsync(store2, store3);
                await SetupReplicationAsync(store1, store2);
                await SetupReplicationAsync(store1, store3);
                await SetupReplicationAsync(store3, store1);
                await SetupReplicationAsync(store3, store2);

                await EnsureReplicatingAsync(store2, store1);
                await EnsureReplicatingAsync(store2, store3);
                await EnsureReplicatingAsync(store1, store2);
                await EnsureReplicatingAsync(store1, store3);
                await EnsureReplicatingAsync(store3, store1);
                await EnsureReplicatingAsync(store3, store2);

                var replication1 = await GetReplicationManagerAsync(store1, store1.Database, options.DatabaseMode);
                var replication2 = await GetReplicationManagerAsync(store2, store2.Database, options.DatabaseMode);
                var replication3 = await GetReplicationManagerAsync(store3, store3.Database, options.DatabaseMode);

                replication1.Break();
                replication2.Break();
                replication3.Break();

                using (var backgroundStream = new MemoryStream(new byte[] { 10, 20, 30, 40, 50 }))
                {
                    var result = store2.Operations.Send(new PutAttachmentOperation("users/1", "foo/bar", backgroundStream, "image/png"));
                    Assert.Equal("foo/bar", result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("image/png", result.ContentType);
                    Assert.Equal("igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=", result.Hash);
                }

                using (var session = store2.OpenAsyncSession())
                {
                    var u = await session.LoadAsync<User>("users/1");
                    u.Age = 20;
                    await session.SaveChangesAsync();
                }
                using (var backgroundStream = new MemoryStream(new byte[] { 10, 20, 30, 40, 50, 60 }))
                {
                    var result = store3.Operations.Send(new PutAttachmentOperation("users/1", "foo/bar", backgroundStream, "image/png"));
                    Assert.Equal("foo/bar", result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("image/png", result.ContentType);
                    Assert.Equal("7hoAZadly0e2TKk4NC6+MrtVuqZblV3+UDW7/Iz9H5U=", result.Hash);
                }

                using (var session = store1.OpenAsyncSession())
                {
                    var u = await session.LoadAsync<User>("users/1");
                    u.Age = 30;
                    await session.SaveChangesAsync();
                }

                replication2.Mend();

                await EnsureReplicatingAsync(store2, store1);
                await EnsureReplicatingAsync(store2, store3);

                replication3.Mend();

                await EnsureReplicatingAsync(store3, store2);
                await EnsureReplicatingAsync(store3, store1);

                await Task.Delay(3000); // wait for the replication ping-pong to settle down

                replication1.Mend();

                await EnsureReplicatingAsync(store1, store2);
                await EnsureReplicatingAsync(store1, store3);

                var dbName1 = options.DatabaseMode == RavenDatabaseMode.Single ? store1.Database : await Sharding.GetShardDatabaseNameForDocAsync(store1, "users/1");
                var storage = await Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(dbName1);

                using (storage.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
                {
                    var res = await WaitForValueAsync(async () =>
                    {
                        using (var session = store1.OpenAsyncSession())
                        using (var session2 = store2.OpenAsyncSession())
                        using (var session3 = store3.OpenAsyncSession())
                        {
                            var user = await session.LoadAsync<User>("users/1");
                            var user2 = await session2.LoadAsync<User>("users/1");
                            var user3 = await session3.LoadAsync<User>("users/1");

                            return user.Age == user2.Age && user.Age == user3.Age;
                        }
                    }, true, 30_000, 500);
                    Assert.True(res);

                    res = await WaitForValueAsync(async () =>
                    {
                        using (var session = store1.OpenAsyncSession())
                        using (var session2 = store2.OpenAsyncSession())
                        using (var session3 = store3.OpenAsyncSession())
                        {
                            var attachment = await session.Advanced.Attachments.GetAsync("users/1", "foo/bar");
                            var attachment2 = await session2.Advanced.Attachments.GetAsync("users/1", "foo/bar");
                            var attachment3 = await session3.Advanced.Attachments.GetAsync("users/1", "foo/bar");

                            if (attachment != null && attachment2 != null && attachment3 != null &&
                                attachment.Details.Name == "foo/bar" &&
                                AreAttachmentDetailsEqual(attachment.Details, attachment2.Details, context) &&
                                AreAttachmentDetailsEqual(attachment.Details, attachment3.Details, context))
                            {
                                return true;
                            }

                            return false;
                        }
                    }, true, 30_000, 500);

                    using (var session = store1.OpenAsyncSession())
                    using (var session2 = store2.OpenAsyncSession())
                    using (var session3 = store3.OpenAsyncSession())
                    {
                        var attachment = await session.Advanced.Attachments.GetAsync("users/1", "foo/bar");
                        var attachment2 = await session2.Advanced.Attachments.GetAsync("users/1", "foo/bar");
                        var attachment3 = await session3.Advanced.Attachments.GetAsync("users/1", "foo/bar");

                        Assert.NotNull(attachment);
                        Assert.NotNull(attachment2);
                        Assert.NotNull(attachment3);

                        var user = await session.LoadAsync<User>("users/1");
                        switch (options.DatabaseMode)
                        {
                            case RavenDatabaseMode.Single:
                                Assert.True(("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=" == attachment.Details.Hash && user.Age == 30) ||
                                            ("7hoAZadly0e2TKk4NC6+MrtVuqZblV3+UDW7/Iz9H5U=" == attachment.Details.Hash && user.Age == 0), $"age: {user.Age}, hash: {attachment.Details.Hash}");

                                break;
                            case RavenDatabaseMode.Sharded:
                                //Assert.True("7hoAZadly0e2TKk4NC6+MrtVuqZblV3+UDW7/Iz9H5U=" == attachment.Details.Hash && user.Age == 30, $"age: {user.Age}, hash: {attachment.Details.Hash}");

                                break;
                            default:
                                throw new ArgumentOutOfRangeException();
                        }

                        Assert.Equal("foo/bar", attachment.Details.Name);

                        var attachmentChangeVector = context.GetChangeVector(attachment.Details.ChangeVector).Version.AsString();
                        var attachmentChangeVector2 = context.GetChangeVector(attachment2.Details.ChangeVector).Version.AsString();
                        var attachmentChangeVector3 = context.GetChangeVector(attachment3.Details.ChangeVector).Version.AsString();

                        Assert.Equal(attachment.Details.Hash, attachment2.Details.Hash);
                        Assert.Equal(attachmentChangeVector, attachmentChangeVector2);
                        Assert.Equal(attachment.Details.Name, attachment2.Details.Name);

                        Assert.Equal(attachment3.Details.Hash, attachment2.Details.Hash);
                        Assert.Equal(attachmentChangeVector3, attachmentChangeVector2);
                        Assert.Equal(attachment3.Details.Name, attachment2.Details.Name);
                    }
                }
            }
        }

        [RavenTheory(RavenTestCategory.Attachments | RavenTestCategory.Replication)]
        [RavenData(DatabaseMode = RavenDatabaseMode.Single)]
        public async Task ReplicationWhenAttachmentConflictResolutionDisabledOnOneDatabase_RavenDB_20726(Options options)
        {
            using (var source = GetDocumentStore(options))
            using (var destination = GetDocumentStore(options))
            {
                //disable conflict resolution on destination
                await destination.Maintenance.Server.SendAsync(new ModifyConflictSolverOperation(destination.Database, null, false));

                using (var session = source.OpenAsyncSession())
                using (var fooStream = new MemoryStream(new byte[] { 1, 2, 3 }))
                {
                    fooStream.Position = 0;
                    await session.StoreAsync(new User { Name = "Foo" }, "users/1");
                    session.Advanced.Attachments.Store("users/1", "foo.png", fooStream, "image/png");
                    await session.SaveChangesAsync();
                }

                using (var session = destination.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Foo2" }, "users/1");

                    using (var fooStream = new MemoryStream(new byte[] { 1, 2, 3, 4 }))
                    {
                        fooStream.Position = 0;
                        await session.StoreAsync(new User { Name = "Extra" }, "users/2");
                        session.Advanced.Attachments.Store("users/2", "extra.png", fooStream, "image/png");
                        await session.SaveChangesAsync();
                    }
                }

                await SetupReplicationAsync(source, destination);

                var conflicts = WaitUntilHasConflict(destination, "users/1", count: 2);
                Assert.Equal(2, conflicts.Length);

                using (var session = destination.OpenAsyncSession())
                {
                    session.Delete("users/1");
                    await session.SaveChangesAsync();
                }

                await EnsureReplicatingAsync(source, destination);

                conflicts = destination.Commands().GetConflictsFor("users/1");
                Assert.Equal(0, conflicts.Length);

                using (var session = destination.OpenAsyncSession())
                {
                    var doc = await session.LoadAsync<User>("users/1");
                    Assert.Null(doc);
                    var attachment = await session.Advanced.Attachments.GetAsync("users/1", "foo.png");
                    Assert.Null(attachment);

                    //check extra doc and its attachments remained untouched
                    doc = await session.LoadAsync<User>("users/2");
                    Assert.NotNull(doc);
                    attachment = await session.Advanced.Attachments.GetAsync("users/2", "extra.png");
                    Assert.NotNull(attachment);
                }
            }
        }

        private async Task WriteStatus(DocumentStore[] stores, string s)
        {
            Console.WriteLine(s + "\n");
            for (int i = 0; i < stores.Length; i++)
            {
                using (var session = stores[i].OpenAsyncSession())
                {
                    var u = await session.LoadAsync<User>("users/1");
                    var cv = session.Advanced.GetChangeVectorFor(u);
                    Console.WriteLine($"cv{i}: {cv}");
                }
            }
            Console.WriteLine();
            Console.WriteLine("-----");
        }

        private async Task WriteAttachmentDetails(DocumentStore[] stores)
        {
            Console.WriteLine();
            for (int i = 0; i < stores.Length; i++)
            {
                using (var session = stores[i].OpenAsyncSession())
                {
                    var u = await session.LoadAsync<User>("users/1");
                    var names = session.Advanced.Attachments.GetNames(u);
                    Console.WriteLine($"Attachments {i}: {string.Join(", ", names.Select(x => new string($"{x.Name}; Hash: {x.Hash}; ContentType: {x.ContentType}")))}");
                }
            }
            Console.WriteLine();
            Console.WriteLine("-----");
        }

        private bool AreAttachmentDetailsEqual(AttachmentDetails attachment1, AttachmentDetails attachment2, DocumentsOperationContext context, bool excludeChangeVector = false)
        {
            if (attachment1.DocumentId == attachment2.DocumentId &&
                (excludeChangeVector || context.GetChangeVector(attachment1.ChangeVector).Version.AsString() == context.GetChangeVector(attachment2.ChangeVector).Version.AsString()) &&
                attachment1.Hash == attachment2.Hash &&
                attachment1.Name == attachment2.Name &&
                attachment1.ContentType == attachment2.ContentType)
                return true;
            return false;
        }

        private class ChangeVectorComparer : IComparer<(string Name, string ChangeVector)>, IComparer<(string Name, string ChangeVector, string Hash)>
        {
            public static ChangeVectorComparer Instance = new ChangeVectorComparer();

            public int Compare((string Name, string ChangeVector) x, (string Name, string ChangeVector) y) => Compare(x.ChangeVector, y.ChangeVector);

            public int Compare((string Name, string ChangeVector, string Hash) x, (string Name, string ChangeVector, string Hash) y) => Compare(x.ChangeVector, y.ChangeVector);

            private int Compare(string x, string y)
            {
                var cvx = new ChangeVector(x, NoChangeVectorContext.Instance).Version.AsString();
                var cvy = new ChangeVector(y, NoChangeVectorContext.Instance).Version.AsString();

                return string.CompareOrdinal(cvx, cvy);
            }
        }
    }
}
