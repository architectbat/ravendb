using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Corax.Queries.SortingMatches.Comparers;
using Corax.Utils;
using Sparrow;
using Sparrow.Compression;
using Sparrow.Server;
using Voron;
using Voron.Data.CompactTrees;
using Voron.Data.Containers;
using Voron.Data.Lookups;
using Voron.Data.PostingLists;
using Voron.Impl;

namespace Corax.Queries.SortingMatches;

[DebuggerDisplay("{DebugView,nq}")]
public unsafe partial struct SortingMatch<TInner> : IQueryMatch
    where TInner : IQueryMatch
{
    private readonly IndexSearcher _searcher;
    private readonly TInner _inner;
    private readonly OrderMetadata _orderMetadata;
    private readonly delegate*<ref SortingMatch<TInner>, Span<long>, int> _fillFunc;

    private const int NotStarted = -1;
        
    private ByteStringContext<ByteStringMemoryCache>.InternalScope _entriesBufferScope;

    private Results _results;
    public long TotalResults;

    public SortingMatch(IndexSearcher searcher, in TInner inner, OrderMetadata orderMetadata, int take = -1)
    {
        _searcher = searcher;
        _inner = inner;
        _orderMetadata = orderMetadata;
        _results = new Results(searcher.Transaction.LowLevelTransaction, searcher.Allocator, take);

        TotalResults = 0;

        if (_orderMetadata.HasBoost)
        {
            _fillFunc = SortBy<EntryComparerByScore, CompactTree.ForwardIterator, CompactTree.BackwardIterator>(orderMetadata);
        }
        else
        {
            _fillFunc = _orderMetadata.FieldType switch
            {
                MatchCompareFieldType.Sequence => SortBy<EntryComparerByTerm, CompactTree.ForwardIterator, CompactTree.BackwardIterator>(orderMetadata),
                MatchCompareFieldType.Alphanumeric => SortBy<EntryComparerByTermAlphaNumeric, CompactTree.ForwardIterator, CompactTree.BackwardIterator>(orderMetadata),
                MatchCompareFieldType.Integer => SortBy<EntryComparerByLong, Lookup<long>.ForwardIterator, Lookup<long>.BackwardIterator>(orderMetadata),
                MatchCompareFieldType.Floating => SortBy<EntryComparerByDouble,  Lookup<double>.ForwardIterator, Lookup<double>.BackwardIterator>(orderMetadata),
                MatchCompareFieldType.Spatial => SortBy<EntryComparerBySpatial, CompactTree.ForwardIterator, CompactTree.BackwardIterator>(orderMetadata),
                _ => throw new ArgumentOutOfRangeException(_orderMetadata.FieldType.ToString())
            };
        }
    }
        
    private static delegate*<ref SortingMatch<TInner>, Span<long>, int> 
        SortBy<TEntryComparer, TFwdIt,TBackIt>(OrderMetadata metadata)
        where TEntryComparer : struct, IEntryComparer, IComparer<UnmanagedSpan>
        where TFwdIt : struct, ITreeIterator
        where TBackIt : struct, ITreeIterator
    {
        if (metadata.Ascending)
        {
            return &Fill<TEntryComparer, TFwdIt>;
        }

        return &Fill<Descending<TEntryComparer>, TBackIt>;
    }


    private static int Fill<TEntryComparer, TDirection>(ref SortingMatch<TInner> match, Span<long> matches)
        where TEntryComparer : struct, IEntryComparer, IComparer<UnmanagedSpan> 
        where TDirection : struct, ITreeIterator
    {
        // This method should also be re-entrant for the case where we have already pre-sorted everything and 
        // we will just need to acquire via pages the totality of the results. 
        if (match._results.Count == NotStarted)
        {
            var memoizer = match._searcher.Memoize(match._inner);
            var allMatches = memoizer.FillAndRetrieve();
            match.TotalResults = allMatches.Length;
            switch (allMatches.Length)
            {
                case 0:
                    match._results.Count = 0;
                    return 0;
                // case <= 4096:
                //     SortSmallResult<TEntryComparer>(ref match, allMatches);
                //     break;
                default:
                    SortLargeResult<TEntryComparer, TDirection>(ref match, allMatches);
                    break;
            }
            memoizer.Dispose();
        }

        var read = match._results.CopyTo(matches);

        if (read != 0) 
            return read;
            
        match._results.Dispose();
        match._entriesBufferScope.Dispose();

        return 0;
    }

    private ref struct SortedIndexReader<TIterator>
        where TIterator : struct, ITreeIterator
    {
        private PostingList.Iterator _postListIt;
        private Span<byte> _smallPostingListBuffer;
        private PForDecoder.DecoderState _state;
        private TIterator _termsIt;
        private readonly IndexSearcher _searcher;
        private readonly LowLevelTransaction _llt;

        public SortedIndexReader(LowLevelTransaction llt, IndexSearcher searcher, TIterator it)
        {
            _termsIt = it;
            _termsIt.Reset();
            _llt = llt;
            _searcher = searcher;
            _postListIt = default;
            _smallPostingListBuffer = default;
            _state = default;
        }

        public int Read(Span<long> sortedIds)
        {
            int currentIdx = 0;
            // here we resume the *previous* operation
            if (_state.BufferSize != 0)
            {
                ReadSmallPostingList(sortedIds, ref currentIdx);
            }
            else if (_postListIt.IsValid)
            {
                ReadLargePostingList(sortedIds, ref currentIdx);
            }

            while (currentIdx < sortedIds.Length)
            {
                if (_termsIt.MoveNext(out var postingListId) == false)
                    break;

                var termType = (TermIdMask)postingListId & TermIdMask.EnsureIsSingleMask;
                switch (termType)
                {
                    case TermIdMask.Single:
                        sortedIds[currentIdx++] = EntryIdEncodings.GetContainerId(postingListId);
                        break;
                    case TermIdMask.SmallPostingList:
                        var smallSetId = EntryIdEncodings.GetContainerId(postingListId);
                        var item = Container.Get(_llt, smallSetId);
                        VariableSizeEncoding.Read<int>(item.Address, out var offset);
                        item = item.IncrementOffset(offset);
                        _state = new(item.Length);
                        _smallPostingListBuffer = item.ToSpan();
                        ReadSmallPostingList(sortedIds, ref currentIdx);
                        break;
                    case TermIdMask.PostingList:
                        var postingList = _searcher.GetPostingList(postingListId);
                        _postListIt = postingList.Iterate();
                        ReadLargePostingList(sortedIds, ref currentIdx);
                        break;
                    default:
                        throw new OutOfMemoryException(termType.ToString());
                }
            }

            return currentIdx;
        }

        private void ReadLargePostingList(Span<long> sortedIds, ref int currentIdx)
        {
            if (_postListIt.Fill(sortedIds[currentIdx..], out var read) == false)
                _postListIt = default;
            currentIdx += read;
        }

        private void ReadSmallPostingList(Span<long> sortedIds, ref int currentIdx)
        {
            while (currentIdx< sortedIds.Length)
            {
                var buffer = sortedIds[currentIdx..];
                var read = PForDecoder.Decode(ref _state, _smallPostingListBuffer, buffer);
                EntryIdEncodings.DecodeAndDiscardFrequency(buffer, read);
                currentIdx += read;
                if (read == 0)
                {
                    _state = default;
                    break;
                }
            }
        }
    }

    private static void SortLargeResult<TEntryComparer,TDirection>(ref SortingMatch<TInner> match, Span<long> allMatches) 
        where TDirection : struct, ITreeIterator
        where TEntryComparer : struct, IEntryComparer
    {
        var llt = match._searcher.Transaction.LowLevelTransaction;
        var allocator = match._searcher.Allocator;
        var entryCmp = default(TEntryComparer);

        int maxResults = match._results.Max;

        var indexesScope = allocator.Allocate(SortBatchSize * sizeof(long), out ByteString bs);
        Span<long> indexesBuffer = new(bs.Ptr, SortBatchSize);
        var sortedIdsScope = allocator.Allocate( sizeof(long) * SortBatchSize, out bs);
        Span<long> sortedIdBuffer = new(bs.Ptr, SortBatchSize);

        SortedIndexReader<TDirection> reader = GetReader(ref match);
        
        match._results.Init();
        
        while (match._results.Count < maxResults)
        {
            var read = reader.Read(sortedIdBuffer);
            if (read == 0)
                break;
            var sortedIds = sortedIdBuffer[..read];
            var indexes = indexesBuffer[..read];
            // we effectively permute the indexes as well as the sortedIds to get a sorted list to compare
            // with the allMatches
            InitializeIndexesTopHalf(indexes);
            sortedIds.Sort(indexes);
            InitializeIndexesBottomHalf(indexes);
            read = SortHelper.FindMatches(indexes, sortedIds, allMatches);
            indexes = indexes[..read];
            indexes.Sort();
            match._results.EnsureAdditionalCapacity(indexes.Length);
            // now get the *actual* matches in their sorted order
            for (int i = 0; i < indexes.Length; i++)
            {
                match._results.Append(sortedIds[(int)indexes[i]]);
            }
        }

        sortedIdsScope.Dispose();
        indexesScope.Dispose();
        
        
        SortedIndexReader<TDirection> GetReader(ref SortingMatch<TInner> match)
        {
            if (typeof(TDirection) == typeof(CompactTree.ForwardIterator) ||
                typeof(TDirection) == typeof(CompactTree.BackwardIterator))
            {
                var termsTree = match._searcher.GetTermsFor(entryCmp.GetSortFieldName(ref match));
                return new SortedIndexReader<TDirection>(llt, match._searcher, termsTree.Iterate<TDirection>());
            }

            if (typeof(TDirection) == typeof(Lookup<long>.ForwardIterator) ||
                typeof(TDirection) == typeof(Lookup<long>.BackwardIterator))
            {
                var termsTree = match._searcher.GetLongTermsFor(entryCmp.GetSortFieldName(ref match));
                return new SortedIndexReader<TDirection>(llt, match._searcher, termsTree.Iterate<TDirection>());
            }

            if (typeof(TDirection) == typeof(Lookup<double>.ForwardIterator) ||
                typeof(TDirection) == typeof(Lookup<double>.BackwardIterator))
            {
                var termsTree = match._searcher.GetDoubleTermsFor(entryCmp.GetSortFieldName(ref match));
                return new SortedIndexReader<TDirection>(llt, match._searcher, termsTree.Iterate<TDirection>());
            }

            throw new NotSupportedException(typeof(TDirection).FullName);
        }
        

    }

    private static void InitializeIndexesTopHalf(Span<long> span)
    {
        for (int i = 0; i < span.Length; i++)
            span[i] = (long)i << 32;
    }
    
    private static void InitializeIndexesBottomHalf(Span<long> span)
    {
        for (int i = 0; i < span.Length; i++)
            span[i] |= (uint)i;
    }

    private static string[] DebugTerms(LowLevelTransaction llt, Span<UnmanagedSpan> terms)
    {
        using var s = new CompactKeyCacheScope(llt);
        var l = new string[terms.Length];
        for (int i = 0; i < terms.Length; i++)
        {
            var item = terms[i];
            int remainderBits = item.Address[0] >> 4;
            int encodedKeyLengthInBits = (item.Length - 1) * 8 - remainderBits;
            long dicId = PersistentDictionary.CreateDefault(llt);
            s.Key.Set(encodedKeyLengthInBits, item.ToSpan()[1..], dicId);
            l[i] = s.Key.ToString();
        }

        return l;
    }
    
    private const int SortBatchSize = 4096;

    private static void SortSmallResult<TEntryComparer>(ref SortingMatch<TInner> match, Span<long> batchResults) 
        where TEntryComparer : struct,  IEntryComparer, IComparer<UnmanagedSpan>
    {
        var llt = match._searcher.Transaction.LowLevelTransaction;
        var allocator = match._searcher.Allocator;

        var bufScope = allocator.Allocate( batchResults.Length * (sizeof(long)+sizeof(UnmanagedSpan)), out ByteString bs);
        Span<long> batchTermIds = new(bs.Ptr, batchResults.Length);
        UnmanagedSpan* termsPtr = (UnmanagedSpan*)(bs.Ptr + batchResults.Length * sizeof(long));

        // Initialize the important infrastructure for the sorting.
        TEntryComparer entryComparer = new();
        entryComparer.Init(ref match);
        match._results.Init();
            
        var pageCache = new PageLocator(llt, 1024);
        
        var indexes = entryComparer.SortBatch(ref match, llt, pageCache, batchResults, batchTermIds, termsPtr);

        match._results.Merge(entryComparer, indexes, batchResults, termsPtr);

        pageCache.Release();
        bufScope.Dispose();
    }


    public long Count => _inner.Count;

    public QueryCountConfidence Confidence => throw new NotSupportedException();

    public bool IsBoosting => _inner.IsBoosting || _orderMetadata.FieldType == MatchCompareFieldType.Score;

    public int AndWith(Span<long> buffer, int matches)
    {
        throw new NotSupportedException($"{nameof(SortingMatch<TInner>)} does not support the operation of {nameof(AndWith)}.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Fill(Span<long> matches)
    {
        return _fillFunc(ref this, matches);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Score(Span<long> matches, Span<float> scores, float boostFactor) 
    {
    }

    public QueryInspectionNode Inspect()
    {
        return new QueryInspectionNode($"{nameof(SortingMatch)} [{_orderMetadata}]",
            children: new List<QueryInspectionNode> { _inner.Inspect()},
            parameters: new Dictionary<string, string>()
            {
                { nameof(IsBoosting), IsBoosting.ToString() },
            });
    }

    string DebugView => Inspect().ToString();
}
