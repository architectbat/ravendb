using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Corax.Mappings;
using Corax.Querying.Matches.Meta;
using Voron;
using Voron.Data.CompactTrees;
using Voron.Data.Lookups;
using Range = Corax.Querying.Matches.Meta.Range;

namespace Corax.Querying.Matches.TermProviders;

[DebuggerDisplay("{DebugView,nq}")]
public struct TermRangeProvider<TLookupIterator, TLow, THigh> : ITermProvider
    where TLookupIterator : struct, ILookupIterator
    where TLow : struct, Range.Marker
    where THigh : struct, Range.Marker
{
    private readonly CompactTree _tree;
    private readonly Querying.IndexSearcher _indexSearcher;
    private readonly FieldMetadata _field;
    private Slice _low, _high;

    private CompactTree.Iterator<TLookupIterator> _iterator;

    private readonly bool _isForward;
    private bool _skipRangeCheck;
    private bool _isEmpty;
    private bool _shouldIncludeLastTerm;
    private long _endContainerId;

    public TermRangeProvider(Querying.IndexSearcher indexSearcher, CompactTree tree, FieldMetadata field, Slice low, Slice high)
    {
        _indexSearcher = indexSearcher;
        _field = field;
        _iterator = tree.Iterate<TLookupIterator>();
        _isForward = default(TLookupIterator).IsForward;


        _low = low;
        _high = high;
        _tree = tree;

        // Optimization for unbounded ranges. We seek the proper term (depending on the iterator) and iterate through all left items.
        _skipRangeCheck = _isForward
            ? _high.Options is SliceOptions.AfterAllKeys
            : _low.Options is SliceOptions.BeforeAllKeys;
        PrepareKeys();
        Reset();
    }


    private void PrepareKeys()
    {
        CompactKey key;
        ReadOnlySpan<byte> termSlice;
        
        var startKey = _isForward ? _low : _high;
        var finalKey = _isForward ? _high : _low;

        if (ShouldSeek())
        {
            _iterator.Seek(startKey);
            if (_iterator.MoveNext(out key, out _, out _) == false)
            {
                _isEmpty = true;
                return; //empty set, we will go out of range immediately 
}

            termSlice = key.Decoded();
            var shouldInclude = _isForward switch
            {
                false when typeof(THigh) == typeof(Range.Exclusive) && termSlice.SequenceCompareTo(_high.AsSpan()) >= 0 => false,
                false when typeof(THigh) == typeof(Range.Inclusive) && _high.Options != SliceOptions.AfterAllKeys &&
                           termSlice.SequenceCompareTo(_high.AsSpan()) > 0 => false,
                true when typeof(TLow) == typeof(Range.Exclusive) && termSlice.SequenceCompareTo(_low.AsSpan()) <= 0 => false,
                true when typeof(TLow) == typeof(Range.Inclusive) && _low.Options != SliceOptions.BeforeAllKeys &&
                          termSlice.SequenceCompareTo(_low.AsSpan()) < 0 => false,
                _ => true
            };

            if (shouldInclude == false)
            {
                if (_iterator.MoveNext(out key, out _, out _) == false)
                {
                    _isEmpty = true;
                    return; //empty set, we will go out of range immediately
                }

                termSlice = key.Decoded();

                //Next seek will go immediately to the right term.
                if (_isForward)
                    Slice.From(_indexSearcher.Allocator, termSlice, out _low);
                else
                    Slice.From(_indexSearcher.Allocator, termSlice, out _high);
            }
        }
        
        if (_skipRangeCheck)
        {
            // In this case we will accept all items left.
            _endContainerId = long.MaxValue;
            _shouldIncludeLastTerm = true;
            return;
        }


        _iterator.Seek(finalKey);
        if (_iterator.MoveNext(out key, out _endContainerId, out var hasPreviousValue) == false)
        {
            _skipRangeCheck = true; //we are out of item anyway that means we can accept all items
            _endContainerId = long.MaxValue;
            return;
        }

        termSlice = key.Decoded();
        var finalCmp = termSlice.SequenceCompareTo(finalKey.AsSpan());

        _shouldIncludeLastTerm = _isForward switch
        {
            false when typeof(TLow) == typeof(Range.Exclusive) && finalCmp <= 0 => false,
            false when typeof(TLow) == typeof(Range.Inclusive) && finalCmp < 0 => false,
            true when typeof(THigh) == typeof(Range.Exclusive) && finalCmp >= 0 => false,
            true when typeof(THigh) == typeof(Range.Inclusive) && _high.Options != SliceOptions.AfterAllKeys && finalCmp > 0 => false,
            _ => true
        };
        if(_shouldIncludeLastTerm == false && hasPreviousValue == false)
        {
            _isEmpty = true;
        }
    }

    public bool IsFillSupported => true;

    public int Fill(Span<long> containers)
    {
        if (_isEmpty)
            return 0;

        return _iterator.Fill(containers, _endContainerId, _shouldIncludeLastTerm);
    }

    public void Reset()
    {
        var shouldSeek = ShouldSeek();
        if (shouldSeek)
            _iterator.Seek(_isForward ? _low : _high);
        else
            _iterator.Reset();
    }
    
    private bool ShouldSeek()
    {
        return _isForward switch
        {
            true when _low.Options != SliceOptions.BeforeAllKeys => true,
            false when _high.Options != SliceOptions.AfterAllKeys => true,
            _ => false
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Next(out TermMatch term)
    {
        if (_isEmpty || _iterator.MoveNext(out var termId) == false)
            goto ReturnEmpty;


        if (termId == _endContainerId)
        {
            _isEmpty = true;
            
            if (_shouldIncludeLastTerm == false)
                goto ReturnEmpty;
        }

        term = _indexSearcher.TermQuery(_field, termId, 1D);
        return true;

        ReturnEmpty:
        term = TermMatch.CreateEmpty(_indexSearcher, _indexSearcher.Allocator);
        return false;
    }

    public QueryInspectionNode Inspect()
    {
        return new QueryInspectionNode($"TermRangeProvider<{typeof(TLookupIterator).Name}, {typeof(TLow).Name}, {typeof(THigh).Name}>",
            parameters: new Dictionary<string, string>()
            {
                { "Field", _field.ToString() },
                { "Low", _low.ToString()},
                { "High", _high.ToString()}
            });
    }

    public string DebugView => Inspect().ToString();
}
