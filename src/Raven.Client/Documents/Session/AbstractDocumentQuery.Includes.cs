using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Raven.Client.Documents.Operations.TimeSeries;
using Raven.Client.Documents.Session.Loaders;
using Raven.Client.Documents.Session.Tokens;
using Raven.Client.Extensions;

namespace Raven.Client.Documents.Session
{
    public abstract partial class AbstractDocumentQuery<T, TSelf>
    {
        internal List<TimeSeriesIncludesToken> TimeSeriesIncludesTokens;

        protected List<CounterIncludesToken> CounterIncludesTokens;

        internal List<CompareExchangeValueIncludesToken> CompareExchangeValueIncludesTokens;
        
        internal List<RevisionIncludesToken> RevisionsIncludesTokens;

        /// <summary>
        /// The paths to include when loading the query
        /// </summary>
        protected HashSet<string> DocumentIncludes = new HashSet<string>();

        /// <summary>
        ///   Includes the specified path in the query, loading the document specified in that path
        /// </summary>
        /// <param name = "path">The path.</param>
        public void Include(string path)
        {
            TheSession?.AssertNoIncludesInNonTrackingSession();

            DocumentIncludes.Add(path);
        }

        /// <summary>
        ///   Includes the specified path in the query, loading the document specified in that path
        /// </summary>
        /// <param name = "path">The path.</param>
        public void Include(Expression<Func<T, object>> path)
        {
            Include(path.ToPropertyPath(_conventions));
        }

        public void Include(IncludeBuilder includes)
        {
            if (includes == null)
                return;

            if (includes.DocumentsToInclude is { Count: > 0 })
            {
                TheSession?.AssertNoIncludesInNonTrackingSession();

                foreach (var doc in includes.DocumentsToInclude)
                {
                    DocumentIncludes.Add(doc);
                }
            }

            IncludeCounters(includes.Alias, includes.CountersToIncludeBySourcePath);

            if (includes.TimeSeriesToIncludeBySourceAlias != null)
            {
                IncludeTimeSeries(includes.Alias, includes.TimeSeriesToIncludeBySourceAlias);
            }
            
            if (includes.RevisionsToIncludeByDateTime != null)
            {
                IncludeRevisions(includes.RevisionsToIncludeByDateTime.Value);
            }
            
            if (includes.RevisionsToIncludeByChangeVector != null)
            {
                IncludeRevisions(includes.RevisionsToIncludeByChangeVector);
            }
            
            if (includes.CompareExchangeValuesToInclude is { Count: > 0 })
            {
                TheSession?.AssertNoIncludesInNonTrackingSession();

                CompareExchangeValueIncludesTokens = new List<CompareExchangeValueIncludesToken>();

                foreach (var compareExchangeValue in includes.CompareExchangeValuesToInclude)
                    CompareExchangeValueIncludesTokens.Add(CompareExchangeValueIncludesToken.Create(compareExchangeValue));
            }
        }

        private void IncludeCounters(string alias, Dictionary<string, (bool All, HashSet<string> Counters)> countersToIncludeByDocId)
        {
            if (countersToIncludeByDocId?.Count > 0 == false)
                return;

            TheSession?.AssertNoIncludesInNonTrackingSession();

            CounterIncludesTokens = new List<CounterIncludesToken>();
            _includesAlias = alias;

            foreach (var kvp in countersToIncludeByDocId)
            {
                if (kvp.Value.All)
                {
                    CounterIncludesTokens.Add(CounterIncludesToken.All(kvp.Key));
                    continue;
                }

                if (kvp.Value.Counters?.Count > 0 == false)
                    continue;

                foreach (var name in kvp.Value.Counters)
                {
                    CounterIncludesTokens.Add(CounterIncludesToken.Create(kvp.Key, name));
                }
            }
        }

        private void IncludeTimeSeries(string alias, Dictionary<string, HashSet<AbstractTimeSeriesRange>> timeSeriesToInclude)
        {
            if (timeSeriesToInclude?.Count > 0 == false)
                return;

            TheSession?.AssertNoIncludesInNonTrackingSession();

            TimeSeriesIncludesTokens = new List<TimeSeriesIncludesToken>();
            _includesAlias ??= alias;

            foreach (var kvp in timeSeriesToInclude)
            {
                foreach (var range in kvp.Value)
                {
                    TimeSeriesIncludesTokens.Add(TimeSeriesIncludesToken.Create(kvp.Key, range));
                }
            }
        }
        
        private void IncludeRevisions(DateTime dateTime)
        {
            TheSession?.AssertNoIncludesInNonTrackingSession();

            RevisionsIncludesTokens ??= new List<RevisionIncludesToken>();
            RevisionsIncludesTokens.Add(RevisionIncludesToken.Create(dateTime));
        }
        
        private void IncludeRevisions(HashSet<string> revisionsToIncludeByChangeVector)
        {
            if (revisionsToIncludeByChangeVector == null || revisionsToIncludeByChangeVector.Count == 0)
                return;

            TheSession?.AssertNoIncludesInNonTrackingSession();

            RevisionsIncludesTokens ??= new List<RevisionIncludesToken>();
            foreach (var changeVector in revisionsToIncludeByChangeVector)
            {
                RevisionsIncludesTokens.Add(RevisionIncludesToken.Create(changeVector));
            }
        }
    }
}
