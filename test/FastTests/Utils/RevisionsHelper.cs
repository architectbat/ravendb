using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Operations.Revisions;
using Sparrow.Json;

namespace FastTests.Utils
{
    public class RevisionsHelper
    {
        public static async Task<long> SetupRevisionsAsync(
            IDocumentStore store,
            string database = null,
            RevisionsConfiguration configuration = null,
            Action<RevisionsConfiguration> modifyConfiguration = null)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));

            configuration ??= Default;

            modifyConfiguration?.Invoke(configuration);

            var result = await store.Maintenance.ForDatabase(database ?? store.Database).SendAsync(new ConfigureRevisionsOperation(configuration));
            return result.RaftCommandIndex ?? -1;
        }

       

        public static async Task<long> SetupRevisions(Raven.Server.ServerWide.ServerStore serverStore, string database, Action<RevisionsConfiguration> modifyConfiguration = null, int minRevisionToKeep = 5)
        {
            var configuration = new RevisionsConfiguration
            {
                Default = new RevisionsCollectionConfiguration
                {
                    Disabled = false,
                    MinimumRevisionsToKeep = minRevisionToKeep
                },
                Collections = new Dictionary<string, RevisionsCollectionConfiguration>
                {
                    ["Users"] = new RevisionsCollectionConfiguration
                    {
                        Disabled = false,
                        PurgeOnDelete = true,
                        MinimumRevisionsToKeep = 123
                    },
                    ["People"] = new RevisionsCollectionConfiguration
                    {
                        Disabled = false,
                        MinimumRevisionsToKeep = 10
                    },
                    ["Comments"] = new RevisionsCollectionConfiguration
                    {
                        Disabled = true
                    },
                    ["Products"] = new RevisionsCollectionConfiguration
                    {
                        Disabled = true
                    }
                }
            };

            modifyConfiguration?.Invoke(configuration);
            var index = await SetupRevisions(serverStore, database, configuration);
            var documentDatabase = await serverStore.DatabasesLandlord.TryGetOrCreateResourceStore(database);
            await documentDatabase.RachisLogIndexNotifications.WaitForIndexNotification(index, serverStore.Engine.OperationTimeout);
            return index;
        }

        public static async Task<long> SetupRevisions(IDocumentStore documentStore, Raven.Server.ServerWide.ServerStore serverStore)
        {
            var configuration = new RevisionsConfiguration
            {
                Default = new RevisionsCollectionConfiguration
                {
                    Disabled = false
                }
            };

            var database = documentStore.Database;
            var index = await SetupRevisions(serverStore, database, configuration);

            var documentDatabase = await serverStore.DatabasesLandlord.TryGetOrCreateResourceStore(database);
            await documentDatabase.RachisLogIndexNotifications.WaitForIndexNotification(index, serverStore.Engine.OperationTimeout);

            return index;
        }

        public static async Task<long> SetupRevisions(Raven.Server.ServerWide.ServerStore serverStore, string database, RevisionsConfiguration configuration)
        {
            using (var context = JsonOperationContext.ShortTermSingleUse())
            {
                var configurationJson = DocumentConventions.Default.Serialization.DefaultConverter.ToBlittable(configuration, context);
                var (index, _) = await serverStore.ModifyDatabaseRevisions(context, database, configurationJson, Guid.NewGuid().ToString());
                return index;
            }
        }

        private static RevisionsConfiguration Default => new RevisionsConfiguration
        {
            Default = new RevisionsCollectionConfiguration
            {
                Disabled = false,
                MinimumRevisionsToKeep = 5
            },
            Collections = new Dictionary<string, RevisionsCollectionConfiguration>
            {
                ["Users"] = new RevisionsCollectionConfiguration
                {
                    Disabled = false,
                    PurgeOnDelete = true,
                    MinimumRevisionsToKeep = 123
                },
                ["People"] = new RevisionsCollectionConfiguration
                {
                    Disabled = false,
                    MinimumRevisionsToKeep = 10
                },
                ["Comments"] = new RevisionsCollectionConfiguration
                {
                    Disabled = true
                },
                ["Products"] = new RevisionsCollectionConfiguration
                {
                    Disabled = true
                }
            }
        };
    }
}
