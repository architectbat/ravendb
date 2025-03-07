﻿using System.Threading.Tasks;
using FastTests;
using FastTests.Utils;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Tests.Core.Utils.Entities;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_12477 : RavenTestBase
    {
        public RavenDB_12477(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public async Task Can_handle_delete_revision_of_doc_that_changed_collection()
        {
            using (var store = GetDocumentStore())
            {
                // setup revision with PurgeOnDelete = false
                await RevisionsHelper.SetupRevisionsAsync(store, configuration: new RevisionsConfiguration
                {
                    Default = new RevisionsCollectionConfiguration()
                });

                // store a document "users/1" under 'Users' collection
                // and create some revisions for it
                using (var session = store.OpenAsyncSession())
                {
                    var user = new User { Name = "Aviv " };
                    await session.StoreAsync(user, "users/1");
                    await session.SaveChangesAsync();
                }

                for (int i = 0; i < 9; i++)
                {
                    using (var session = store.OpenAsyncSession())
                    {
                        var user = await session.LoadAsync<User>("users/1");
                        user.Name += i;
                        await session.StoreAsync(user);
                        await session.SaveChangesAsync();
                    }
                }

                using (var session = store.OpenAsyncSession())
                {
                    var revisions = await session.Advanced.Revisions.GetForAsync<User>("users/1");
                    Assert.Equal(10, revisions.Count);
                }

                // delete document "users/1"
                using (var session = store.OpenSession())
                {
                    session.Delete("users/1");
                    session.SaveChanges();
                }

                using (var session = store.OpenAsyncSession())
                {
                    var revisions = await session.Advanced.Revisions.GetForAsync<User>("users/1");
                    Assert.Equal(11, revisions.Count);
                }

                // store a new document with the same id - "users/1"
                // but under 'Companies' collection
                using (var session = store.OpenAsyncSession())
                {
                    var company = new Company { Name = "HR " };
                    await session.StoreAsync(company, "users/1");
                    await session.SaveChangesAsync();
                }

                using (var session = store.OpenAsyncSession())
                {
                    var revisions = await session.Advanced.Revisions.GetForAsync<object>("users/1");
                    Assert.Equal(12, revisions.Count);
                }

                // enable PurgeOnDelete
                var configuration = new RevisionsConfiguration
                {
                    Default = new RevisionsCollectionConfiguration
                    {
                        PurgeOnDelete = true
                    }
                };

                await RevisionsHelper.SetupRevisionsAsync(store, configuration: configuration);

                // make sure we don't have a tombstone for "users/1"
                var database = await GetDocumentDatabaseInstanceFor(store);
                await database.TombstoneCleaner.ExecuteCleanup();

                // delete document "users/1"
                using (var session = store.OpenSession())
                {
                    session.Delete("users/1");
                    session.SaveChanges();
                }

                // all the revisions for "users/1" should be deleted - 
                // the old ones ('Users' collection) as well as the new ones ('Companies' collection)
                using (var session = store.OpenAsyncSession())
                {
                    var revisions = await session.Advanced.Revisions.GetForAsync<object>("users/1");

                    Assert.Empty(revisions);
                }
            }
        }
    }
}
