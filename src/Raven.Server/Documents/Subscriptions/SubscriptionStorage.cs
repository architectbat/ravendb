﻿// -----------------------------------------------------------------------
//  <copyright file="SubscriptionStorage.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// ----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents.DataArchival;
using Raven.Client.Documents.Subscriptions;
using Raven.Client.Exceptions.Documents.Subscriptions;
using Raven.Client.Json.Serialization;
using Raven.Client.ServerWide;
using Raven.Client.Util;
using Raven.Server.Documents.Subscriptions.Stats;
using Raven.Server.Documents.TcpHandlers;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Commands.Subscriptions;
using Raven.Server.ServerWide.Context;
using Sparrow.Logging;

namespace Raven.Server.Documents.Subscriptions
{
    public class SubscriptionStorage : AbstractSubscriptionStorage<SubscriptionConnectionsState>
    {
        internal readonly DocumentDatabase _db;

        public event Action<string> OnAddTask;
        public event Action<string> OnRemoveTask;
        public event Action<SubscriptionConnection> OnEndConnection;
        public event Action<string, SubscriptionBatchStatsAggregator> OnEndBatch;

        public SubscriptionStorage(DocumentDatabase db, ServerStore serverStore, string name) : base(serverStore, db.Configuration.Subscriptions.MaxNumberOfConcurrentConnections)
        {
            _db = db;
            _databaseName = name; // this is full name for sharded db 
            _logger = LoggingSource.Instance.GetLogger<SubscriptionStorage>(name);
        }

        protected override void DropSubscriptionConnections(SubscriptionConnectionsState state, SubscriptionException ex)
        {
            foreach (var subscriptionConnection in state.GetConnections())
            {
                state.DropSingleConnection(subscriptionConnection, ex);
            }
        }

        public async Task<(long Index, long SubscriptionId)> PutSubscription(SubscriptionCreationOptions options, string raftRequestId, long? subscriptionId = null, bool? disabled = false, string mentor = null)
        {
            var command = new PutSubscriptionCommand(_databaseName, options.Query, mentor, raftRequestId)
            {
                InitialChangeVector = options.ChangeVector,
                SubscriptionName = options.Name,
                SubscriptionId = subscriptionId,
                PinToMentorNode = options.PinToMentorNode,
                Disabled = disabled ?? false,
                ArchivedDataProcessingBehavior = options.ArchivedDataProcessingBehavior ?? _db.Configuration.Subscriptions.ArchivedDataProcessingBehavior
            };

            var (etag, _) = await _serverStore.SendToLeaderAsync(command);

            if (_logger.IsInfoEnabled)
                _logger.Info($"New Subscription with index {etag} was created");

            await _db.RachisLogIndexNotifications.WaitForIndexNotification(etag, _serverStore.Engine.OperationTimeout);

            if (subscriptionId != null)
            {
                // updated existing subscription
                return (etag, subscriptionId.Value);
            }

            _db.SubscriptionStorage.RaiseNotificationForTaskAdded(options.Name);

            return (etag, etag);
        }

        public SubscriptionState GetSubscriptionFromServerStore(string name)
        {
            using (_serverStore.Engine.ContextPool.AllocateOperationContext(out ClusterOperationContext context))
            using (context.OpenReadTransaction())
            {
                return GetSubscriptionByName(context, name);
            }
        }

        public string GetResponsibleNode(ClusterOperationContext context, string name)
        {
            var subscription = GetSubscriptionByName(context, name);
            return GetSubscriptionResponsibleNode(context, subscription);
        }

        public static async Task DeleteSubscriptionInternal(ServerStore serverStore, string databaseName, string name, string raftRequestId, Logger logger)
        {
            var command = new DeleteSubscriptionCommand(databaseName, name, raftRequestId);
            var (etag, _) = await serverStore.SendToLeaderAsync(command);
            await serverStore.Cluster.WaitForIndexNotification(etag, serverStore.Engine.OperationTimeout);
            if (logger.IsInfoEnabled)
            {
                logger.Info($"Subscription with name {name} was deleted");
            }
        }

        
        public string GetLastDocumentChangeVectorForSubscription(DocumentsOperationContext context, string collection)
        {
            long lastEtag = collection == Constants.Documents.Collections.AllDocumentsCollection
                ? DocumentsStorage.ReadLastDocumentEtag(context.Transaction.InnerTransaction)
                : _db.DocumentsStorage.GetLastDocumentEtag(context.Transaction.InnerTransaction, collection);

            return _db.DocumentsStorage.GetNewChangeVector(context, lastEtag);
        }

        protected override void SetConnectionException(SubscriptionConnectionsState state, SubscriptionException ex)
        {
            foreach (var connection in state.GetConnections())
            {
                // this is just to set appropriate exception, the connections will be dropped on state dispose
                connection.ConnectionException = ex;
            }
        }

        protected override string GetNodeFromState(SubscriptionState taskStatus) => taskStatus.NodeTag;

        protected override DatabaseTopology GetTopology(ClusterOperationContext context) => _serverStore.Cluster.ReadDatabaseTopology(context, _db.Name);

        protected override bool SubscriptionChangeVectorHasChanges(SubscriptionConnectionsState state, SubscriptionState taskStatus)
        {
            return taskStatus.LastClientConnectionTime == null &&
                   taskStatus.ChangeVectorForNextBatchStartingPoint != state.LastChangeVectorSent;
        }

        public override bool DropSingleSubscriptionConnection(long subscriptionId, string workerId, SubscriptionException ex)
        {
            if (_subscriptions.TryGetValue(subscriptionId, out SubscriptionConnectionsState subscriptionConnectionsState) == false)
                return false;

            var connectionToDrop = subscriptionConnectionsState.GetConnections().FirstOrDefault(conn => conn.WorkerId == workerId);
            if (connectionToDrop == null)
                return false;

            subscriptionConnectionsState.DropSingleConnection(connectionToDrop, ex);
            if (_logger.IsInfoEnabled)
                _logger.Info(
                    $"Connection with id {workerId} in subscription with id '{subscriptionId}' and name '{subscriptionConnectionsState.SubscriptionName}' was dropped.", ex);

            return true;
        }

        public override ArchivedDataProcessingBehavior GetDefaultArchivedDataProcessingBehavior()
        {
            return _db.Configuration.Subscriptions.ArchivedDataProcessingBehavior;
        }

        public IEnumerable<SubscriptionGeneralDataAndStats> GetAllSubscriptions(ClusterOperationContext context, bool history, int start, int take)
        {
            foreach (var keyValue in ClusterStateMachine.ReadValuesStartingWith(context, SubscriptionState.SubscriptionPrefix(_databaseName)))
            {
                if (start > 0)
                {
                    start--;
                    continue;
                }

                if (take-- <= 0)
                    yield break;

                var task = JsonDeserializationClient.SubscriptionState(keyValue.Value);
                var subscriptionGeneralData = new SubscriptionGeneralDataAndStats(task);

                GetSubscriptionInternal(subscriptionGeneralData, history);
                yield return subscriptionGeneralData;
            }
        }

        public SubscriptionConnectionsState GetSubscriptionStateById(long id) => _subscriptions[id];

        public IEnumerable<SubscriptionGeneralDataAndStats> GetAllRunningSubscriptions(ClusterOperationContext context, bool history, int start, int take)
        {
            foreach (var kvp in _subscriptions)
            {
                var subscriptionConnectionsState = kvp.Value;

                if (subscriptionConnectionsState.IsSubscriptionActive() == false)
                    continue;

                if (start > 0)
                {
                    start--;
                    continue;
                }

                if (take-- <= 0)
                    yield break;

                var state = GetSubscriptionFromServerStore(context, subscriptionConnectionsState.SubscriptionName);
                var subscriptionData = GetRunningSubscriptionInternal(history, state, subscriptionConnectionsState);
                yield return subscriptionData;
            }
        }

        public int GetNumberOfRunningSubscriptions()
        {
            var c = 0;
            foreach ((_, SubscriptionConnectionsState value) in _subscriptions)
            {
                if (value.IsSubscriptionActive() == false)
                    continue;

                c++;
            }

            return c;
        }

        public SubscriptionGeneralDataAndStats GetSubscription(ClusterOperationContext context, long? id, string name, bool history)
        {
            SubscriptionState state;

            if (string.IsNullOrEmpty(name) == false)
            {
                state = GetSubscriptionFromServerStore(context, name);
            }
            else if (id.HasValue)
            {
                state = GetSubscriptionFromServerStore(context, id.ToString());
            }
            else
            {
                throw new ArgumentNullException("Must receive either subscription id or subscription name in order to provide subscription data");
            }

            var subscription = GetSubscriptionInternal(state, history);

            return subscription;
        }

        public SubscriptionState GetSubscriptionFromServerStore(ClusterOperationContext context, string name)
        {
            var subscriptionBlittable = _serverStore.Cluster.Read(context, SubscriptionState.GenerateSubscriptionItemKeyName(_databaseName, name));

            if (subscriptionBlittable == null)
                throw new SubscriptionDoesNotExistException($"Subscription with name '{name}' was not found in server store");

            var subscriptionState = JsonDeserializationClient.SubscriptionState(subscriptionBlittable);
            subscriptionState.ArchivedDataProcessingBehavior ??= _db.Configuration.Subscriptions.ArchivedDataProcessingBehavior; // persisted state from v5.x  
            
            return subscriptionState;
        }

        public SubscriptionGeneralDataAndStats GetRunningSubscription(ClusterOperationContext context, long? id, string name, bool history)
        {
            SubscriptionState state;
            if (string.IsNullOrEmpty(name) == false)
            {
                state = GetSubscriptionFromServerStore(context, name);
            }
            else if (id.HasValue)
            {
                name = GetSubscriptionNameById(context, id.Value);
                state = GetSubscriptionFromServerStore(context, name);
            }
            else
            {
                throw new ArgumentNullException("Must receive either subscription id or subscription name in order to provide subscription data");
            }

            if (_subscriptions.TryGetValue(state.SubscriptionId, out SubscriptionConnectionsState subscriptionConnectionsState) == false)
                return null;

            if (subscriptionConnectionsState.IsSubscriptionActive() == false)
                return null;

            var subscription = GetRunningSubscriptionInternal(history, state, subscriptionConnectionsState);
            return subscription;
        }

        public sealed class SubscriptionGeneralDataAndStats : SubscriptionState
        {
            public List<SubscriptionConnection> Connections;
            public IEnumerable<SubscriptionConnectionInfo> RecentConnections;
            public IEnumerable<SubscriptionConnectionInfo> RecentRejectedConnections;
            public IEnumerable<SubscriptionConnectionInfo> CurrentPendingConnections;

            public SubscriptionGeneralDataAndStats() { }

            public SubscriptionGeneralDataAndStats(SubscriptionState @base)
            {
                Query = @base.Query;
                ChangeVectorForNextBatchStartingPoint = @base.ChangeVectorForNextBatchStartingPoint;
                SubscriptionId = @base.SubscriptionId;
                SubscriptionName = @base.SubscriptionName;
                ArchivedDataProcessingBehavior = @base.ArchivedDataProcessingBehavior;
                MentorNode = @base.MentorNode;
                PinToMentorNode = @base.PinToMentorNode;
                NodeTag = @base.NodeTag;
                LastBatchAckTime = @base.LastBatchAckTime;
                LastClientConnectionTime = @base.LastClientConnectionTime;
                Disabled = @base.Disabled;
                ShardingState = @base.ShardingState;
                RaftCommandIndex = @base.RaftCommandIndex;
            }
        }

        public long GetRunningCount()
        {
            return _subscriptions.Count(x => x.Value.IsSubscriptionActive());
        }

        private static void SetSubscriptionHistory(SubscriptionConnectionsState subscriptionConnectionsState, SubscriptionGeneralDataAndStats subscriptionData)
        {
            subscriptionData.RecentConnections = subscriptionConnectionsState.RecentConnections;
            subscriptionData.RecentRejectedConnections = subscriptionConnectionsState.RecentRejectedConnections;
            subscriptionData.CurrentPendingConnections = subscriptionConnectionsState.PendingConnections;
        }

        private static SubscriptionGeneralDataAndStats GetRunningSubscriptionInternal(bool history, SubscriptionState state, SubscriptionConnectionsState subscriptionConnectionsState)
        {
            var subscriptionData = new SubscriptionGeneralDataAndStats(state)
            {
                Connections = subscriptionConnectionsState.GetConnections()
            };

            if (history) // Only valid for this node
                SetSubscriptionHistory(subscriptionConnectionsState, subscriptionData);

            return subscriptionData;
        }

        private SubscriptionGeneralDataAndStats GetSubscriptionInternal(SubscriptionState state, bool history)
        {
            var subscriptionData = new SubscriptionGeneralDataAndStats(state);
            if (_subscriptions.TryGetValue(subscriptionData.SubscriptionId, out SubscriptionConnectionsState concurrentSubscription))
            {
                subscriptionData.Connections = concurrentSubscription.GetConnections();

                if (history)//Only valid if this is my subscription
                    SetSubscriptionHistory(concurrentSubscription, subscriptionData);
            }

            return subscriptionData;
        }

        internal virtual void CleanupSubscriptions()
        {
            var maxTaskLifeTime = _db.Is32Bits ? TimeSpan.FromHours(12) : TimeSpan.FromDays(2);
            var oldestPossibleIdleSubscription = SystemTime.UtcNow - maxTaskLifeTime;

            foreach (var kvp in _subscriptions)
            {
                if (kvp.Value.IsSubscriptionActive())
                    continue;

                var recentConnection = kvp.Value.MostRecentEndedConnection();

                if (recentConnection != null && recentConnection.Date < oldestPossibleIdleSubscription)
                {
                    if (_subscriptions.TryRemove(kvp.Key, out var subsState))
                    {
                        subsState.Dispose();
                    }
                }
            }
        }

        public void RaiseNotificationForTaskAdded(string subscriptionName)
        {
            OnAddTask?.Invoke(subscriptionName);
        }

        public void RaiseNotificationForTaskRemoved(string subscriptionName)
        {
            OnRemoveTask?.Invoke(subscriptionName);
        }

        public void RaiseNotificationForConnectionEnded(SubscriptionConnection connection)
        {
            OnEndConnection?.Invoke(connection);
        }

        public void RaiseNotificationForBatchEnded(string subscriptionName, SubscriptionBatchStatsAggregator batchAggregator)
        {
            OnEndBatch?.Invoke(subscriptionName, batchAggregator);
        }
    }
}
