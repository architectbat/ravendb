using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Corax.Analyzers;
using Corax.Mappings;
using Corax.Pipeline;
using Corax.Utils;
using Sparrow;
using Sparrow.Binary;
using Sparrow.Compression;
using Sparrow.Extensions;
using Sparrow.Json;
using Sparrow.Server;
using Sparrow.Server.Utils.VxSort;
using Sparrow.Threading;
using Voron;
using Voron.Data;
using Voron.Data.BTrees;
using Voron.Data.CompactTrees;
using Voron.Data.Containers;
using Voron.Data.Fixed;
using Voron.Data.Lookups;
using Voron.Data.PostingLists;
using Voron.Impl;
using Voron.Util;
using Voron.Util.PFor;
using InvalidOperationException = System.InvalidOperationException;


namespace Corax.Indexing
{
    public unsafe partial class IndexWriter : IDisposable // single threaded, controlled by caller
    {
        private long _numberOfModifications;
        private readonly HashSet<Slice> _indexedEntries = new(SliceComparer.Instance);
        private List<(long EntryId, float Boost)> _boostedDocs;
        private readonly IndexFieldsMapping _fieldsMapping;
        private FixedSizeTree _documentBoost;
        private Tree _indexMetadata;
        private Tree _persistedDynamicFieldsAnalyzers;
        private long _numberOfTermModifications;
        private CompactKeyCacheScope _compactKeyScope;

        private bool _ownsTransaction;
        private JsonOperationContext _jsonOperationContext;
        private readonly Transaction _transaction;
        private NativeIntegersList _entriesToTermsBuffer;
        private NativeList<(long EntryId, long TermId)> _entriesForTermsAdditionsBuffer;
        private NativeIntegersList _entriesForTermsRemovalsBuffer;

        private Token[] _tokensBufferHandler;
        private byte[] _encodingBufferHandler;
        private byte[] _utf8ConverterBufferHandler;

        private bool _hasSuggestions;
        private readonly IndexedField[] _knownFieldsTerms;
        private Dictionary<Slice, IndexedField> _dynamicFieldsTerms;
        private readonly HashSet<long> _deletedEntries = new();
        private FieldsCache _fieldsCache;

        private long _postingListContainerId;
        private long _storedFieldsContainerId;
        private long _entriesTermsContainerId;
        private Lookup<Int64LookupKey> _entryIdToLocation;
        private IndexFieldsMapping _dynamicFieldsMapping;
        private PostingList _largePostingListSet;

        public void UpdateDynamicFieldsMapping(IndexFieldsMapping current)
        {
            _dynamicFieldsMapping = current;
        }

        // The reason why we want to have the transaction open for us is so that we avoid having
        // to explicitly provide the index writer with opening semantics and also every new
        // writer becomes essentially a unit of work which makes reusing assets tracking more explicit.

        private IndexWriter(IndexFieldsMapping fieldsMapping)
        {
            _indexDebugDumper = new IndexOperationsDumper(fieldsMapping);
            _builder = new IndexEntryBuilder(this);
            _fieldsMapping = fieldsMapping;
            _encodingBufferHandler = Analyzer.BufferPool.Rent(fieldsMapping.MaximumOutputSize);
            _tokensBufferHandler = Analyzer.TokensPool.Rent(fieldsMapping.MaximumTokenSize);
            _utf8ConverterBufferHandler = Analyzer.BufferPool.Rent(fieldsMapping.MaximumOutputSize * 10);

            var bufferSize = fieldsMapping!.Count;
            _knownFieldsTerms = new IndexedField[bufferSize];
            for (int i = 0; i < bufferSize; ++i)
            {
                _knownFieldsTerms[i] = new IndexedField(fieldsMapping.GetByFieldId(i)) { FieldRootPage = -1 };
            }

            _entriesAlreadyAdded = new HashSet<long>();
            _additionsForTerm = new List<long>();
            _removalsForTerm = new List<long>();
        }

        public IndexWriter([NotNull] StorageEnvironment environment, IndexFieldsMapping fieldsMapping) : this(fieldsMapping)
        {
            TransactionPersistentContext transactionPersistentContext = new(true);
            _transaction = environment.WriteTransaction(transactionPersistentContext);

            _ownsTransaction = true;
            Init();
        }

        private void Init()
        {
            Debug.Assert(_transaction.LowLevelTransaction.Flags == TransactionFlags.ReadWrite);
            _compactKeyScope = new(_transaction.LowLevelTransaction);
            _postingListContainerId = _transaction.OpenContainer(Constants.IndexWriter.PostingListsSlice);
            _storedFieldsContainerId = _transaction.OpenContainer(Constants.IndexWriter.StoredFieldsSlice);
            _entriesTermsContainerId = _transaction.OpenContainer(Constants.IndexWriter.EntriesTermsContainerSlice);
            _entryIdToLocation = _transaction.LookupFor<Int64LookupKey>(Constants.IndexWriter.EntryIdToLocationSlice);
            _jsonOperationContext = JsonOperationContext.ShortTermSingleUse();
            _fieldsTree = _transaction.CreateTree(Constants.IndexWriter.FieldsSlice);
            _persistedDynamicFieldsAnalyzers = _transaction.CreateTree(Constants.IndexWriter.DynamicFieldsAnalyzersSlice);

            _indexMetadata = _transaction.CreateTree(Constants.IndexMetadataSlice);
            _initialNumberOfEntries = _indexMetadata?.ReadInt64(Constants.IndexWriter.NumberOfEntriesSlice) ?? 0;
            _lastEntryId =  _indexMetadata?.ReadInt64(Constants.IndexWriter.LastEntryIdSlice) ?? 0;

            _documentBoost = _transaction.FixedTreeFor(Constants.DocumentBoostSlice, sizeof(float));
            _nullEntriesPostingLists = _transaction.CreateTree(Constants.IndexWriter.NullPostingLists);
            _entriesAllocator = new ByteStringContext(SharedMultipleUseFlag.None);

            _pforDecoder = new FastPForDecoder(_entriesAllocator);
            _tempListBuffer = new NativeIntegersList(_entriesAllocator);
            _entriesToTermsBuffer = new NativeIntegersList(_entriesAllocator);
            _entriesForTermsRemovalsBuffer = new NativeIntegersList(_entriesAllocator);
            _entriesForTermsAdditionsBuffer = new NativeList<(long EntryId, long TermId)>();
        }
        
        public IndexWriter([NotNull] Transaction tx, IndexFieldsMapping fieldsMapping) : this(fieldsMapping)
        {
            _transaction = tx;

            _ownsTransaction = false;
            Init();
        }
        
        private void InitializeFieldRootPage(IndexedField field)
        {
            if (field.FieldRootPage == -1)
            {
                _fieldsTree ??= _transaction.CreateTree(Constants.IndexWriter.FieldsSlice);
                field.FieldRootPage = _fieldsCache.GetFieldRootPage(field.Name, _fieldsTree);
            }
        }
        
        private readonly IndexEntryBuilder _builder;

        public IndexEntryBuilder Update(ReadOnlySpan<byte> key)
        {
            // We do not dispose because we will be storing the slice in the hash set.
            Slice.From(_transaction.Allocator, key, ByteStringType.Immutable, out var keySlice);

            if (TryDeleteEntry(keySlice, out var entryId))
            {
                _numberOfModifications++;
            }
            else
            {
                entryId = InitBuilder();
            }

            _indexedEntries.Add(keySlice); // Register entry by key.
            int index = InsertTermsPerEntry(entryId);
            _builder.Init(entryId, index);
            return _builder;
        }

        private int InsertTermsPerEntry(long entryId)
        {
            int index = _termsPerEntryId.Count;
            _termsPerEntryId.EnsureCapacityFor(_entriesAllocator, 1);
            _termsPerEntryIds.EnsureCapacityFor(_entriesAllocator, 1);
            _termsPerEntryId.AddByRefUnsafe() = new NativeList<RecordedTerm>();
            _termsPerEntryIds.PushUnsafe(entryId);
            return index;
        }

        public IndexEntryBuilder Index(string key) => Index(Encoding.UTF8.GetBytes(key));
        
        public IndexEntryBuilder Index(ReadOnlySpan<byte> key)
        {
            long entryId = InitBuilder();

            // We do not dispose because we will be storing the slice in the hash set.
            Slice.From(_transaction.Allocator, key, ByteStringType.Immutable, out var keySlice);
            _indexedEntries.Add(keySlice);  // Register entry by key. 
            int index = InsertTermsPerEntry(entryId);
            _builder.Init(entryId, index);

            return _builder;
        }

        private long InitBuilder()
        {
            if (_builder.Active)
                ThrowPreviousBuilderIsNotDisposed();

            _numberOfModifications++;
            var entryId = ++_lastEntryId;

            return entryId;
        }
        
        //Document Boost should add priority to some documents but also should not be the main component of boosting.
        //The natural logarithm slows down our scoring increase for a document so that the ranking calculated at query time is not forgotten.
        //We've to add entry container id (without frequency etc) here because in 'SortingMatch' we have already decoded ids.
        private void BoostEntry(long entryId, float documentBoost)
        {
            if (documentBoost.AlmostEquals(1f))
            {
                // We don't store `1` but if user update boost value to 1
                // we've to delete the previous one, we don't need to do this explicitly
                // since we'll delete it during ProcessDeletes()
                return;
            }

            // probably user want this to be at the same end.
            if (documentBoost <= 0f)
                documentBoost = 0;

            documentBoost = MathF.Log(documentBoost + 1); // ensure we've positive number
            _boostedDocs ??= new();
            _boostedDocs.Add((entryId, documentBoost));
        }


        /// <param name="entryId">Container id of entry (without encodings)</param>
        private void RemoveDocumentBoost(long entryId)
        {
            _documentBoost.Delete(entryId);
        }

        private IndexedField GetDynamicIndexedField(ByteStringContext context, string currentFieldName)
        {
            using var _ = Slice.From(context, currentFieldName, out var slice);
            return GetDynamicIndexedField(slice);
        }
        
        private IndexedField GetDynamicIndexedField(ByteStringContext context, Span<byte> currentFieldName)
        {
            using var _ = Slice.From(context, currentFieldName, out var slice);
            return GetDynamicIndexedField(slice);
        }


        private IndexedField GetDynamicIndexedField(Slice fieldName)
        {
            //We have to use transaction context here for storing slices in _dynamicFieldsTerms since we may reset other
            //allocators during the document insertion.
            var context = _transaction.LowLevelTransaction.Allocator;
            if (_fieldsMapping.TryGetByFieldName(fieldName, out var indexFieldBinding))
                return _knownFieldsTerms[indexFieldBinding.FieldId];

            _dynamicFieldsTerms ??= new(SliceComparer.Instance);
            if (_dynamicFieldsTerms.TryGetValue(fieldName, out var indexedField))
                return indexedField;

            var clonedFieldName = fieldName.Clone(context);
            if (_dynamicFieldsMapping is null || _persistedDynamicFieldsAnalyzers is null)
            {
                indexedField = CreateDynamicField(null, FieldIndexingMode.Normal);
                indexedField.FieldRootPage = _fieldsCache.GetFieldRootPage(indexedField.Name, _fieldsTree);
                return indexedField;
            }

            var persistedAnalyzer = _persistedDynamicFieldsAnalyzers.Read(clonedFieldName);
            if (_dynamicFieldsMapping?.TryGetByFieldName(clonedFieldName, out var binding) is true)
            {
                indexedField = new IndexedField(Constants.IndexWriter.DynamicField, binding.FieldName, binding.FieldNameLong,
                    binding.FieldNameDouble, binding.FieldTermTotalSumField, binding.Analyzer,
                    binding.FieldIndexingMode, binding.HasSuggestions, binding.ShouldStore);

                if (persistedAnalyzer != null)
                {
                    var originalIndexingMode = (FieldIndexingMode)persistedAnalyzer.Reader.ReadByte();
                    if (binding.FieldIndexingMode != originalIndexingMode) 
                        ThrowInconsistentDynamicFieldCreation(binding, originalIndexingMode);
                }

                if (binding.FieldIndexingMode != FieldIndexingMode.Normal && persistedAnalyzer == null)
                {
                    _persistedDynamicFieldsAnalyzers.Add(clonedFieldName, (byte)binding.FieldIndexingMode);
                }

                _dynamicFieldsTerms[clonedFieldName] = indexedField;
            }
            else
            {
                FieldIndexingMode mode;
                if (persistedAnalyzer == null)
                {
                    mode = FieldIndexingMode.Normal;
                }
                else
                {
                    mode = (FieldIndexingMode)persistedAnalyzer.Reader.ReadByte();
                }

                Analyzer analyzer = mode switch
                {
                    FieldIndexingMode.No => null,
                    FieldIndexingMode.Exact => _dynamicFieldsMapping!.ExactAnalyzer(clonedFieldName.ToString()),
                    FieldIndexingMode.Search => _dynamicFieldsMapping!.SearchAnalyzer(clonedFieldName.ToString()),
                    _ => _dynamicFieldsMapping!.DefaultAnalyzer
                };

                indexedField = CreateDynamicField(analyzer, mode);
            }
            indexedField.FieldRootPage = _fieldsCache.GetFieldRootPage(indexedField.Name, _fieldsTree);
            return indexedField;

            IndexedField CreateDynamicField(Analyzer analyzer, FieldIndexingMode mode)
            {
                IndexFieldsMappingBuilder.GetFieldNameForLongs(context, clonedFieldName, out var fieldNameLong);
                IndexFieldsMappingBuilder.GetFieldNameForDoubles(context, clonedFieldName, out var fieldNameDouble);
                IndexFieldsMappingBuilder.GetFieldForTotalSum(context, clonedFieldName, out var nameSum);
                var field = new IndexedField(Constants.IndexWriter.DynamicField, clonedFieldName, fieldNameLong, fieldNameDouble, nameSum, analyzer, mode, false, false);
                _dynamicFieldsTerms[clonedFieldName] = field;
                return field;
            }
        }
        
        private long _initialNumberOfEntries;
        private readonly HashSet<long> _entriesAlreadyAdded;
        private readonly List<long> _additionsForTerm, _removalsForTerm;
        private readonly IndexOperationsDumper _indexDebugDumper;
        private FastPForDecoder _pforDecoder;
        private long _lastEntryId;
        private NativeIntegersList _tempListBuffer;
        private FastPForEncoder _pForEncoder;

        private NativeList<long> _termsPerEntryIds;
        private NativeList<NativeList<RecordedTerm>> _termsPerEntryId;
        private ByteStringContext _entriesAllocator;
        private Tree _fieldsTree;
        private Tree _nullEntriesPostingLists;
        
        public long GetNumberOfEntries() => _initialNumberOfEntries + _numberOfModifications;

        private int[] _suggestionsTermsLengths;

        private void AddSuggestions(IndexedField field, Slice slice)
        {
            _hasSuggestions = true;
            field.Suggestions ??= new Dictionary<Slice, int>();

            if (_suggestionsTermsLengths == null || _suggestionsTermsLengths.Length < slice.Size)
                _suggestionsTermsLengths = new int[Math.Max(2 * slice.Size, 32)];

            var termsLength = _suggestionsTermsLengths;

            var keys = SuggestionsKeys.Generate(_entriesAllocator, Constants.Suggestions.DefaultNGramSize, slice.AsReadOnlySpan(), termsLength, out int keysCount);

            var suggestionsToAdd = field.Suggestions;

            int idx = 0;
            int currentOffset = 0;
            while (idx < keysCount)
            {
                int keySize = termsLength[idx];

                var key = new Slice(_entriesAllocator.Slice(keys, currentOffset, keySize, ByteStringType.Immutable));
                if (suggestionsToAdd.TryGetValue(key, out int counter) == false)
                    counter = 0;

                counter++;
                suggestionsToAdd[key] = counter;

                currentOffset += keySize;
                idx++;
            }
        }

        private void RemoveSuggestions(IndexedField field, ReadOnlySpan<byte> sequence)
        {
            _hasSuggestions = true;
            field.Suggestions ??= new Dictionary<Slice, int>();

            if (_suggestionsTermsLengths == null || _suggestionsTermsLengths.Length < sequence.Length)
                _suggestionsTermsLengths = new int[Math.Max(2 * sequence.Length, 32)];

            var termsLength = _suggestionsTermsLengths;

            var keys = SuggestionsKeys.Generate(_entriesAllocator, Constants.Suggestions.DefaultNGramSize, sequence, termsLength, out int keysCount);

            var suggestionsToRemove = field.Suggestions;

            int idx = 0;
            int currentOffset = 0;
            while (idx < keysCount)
            {
                int keySize = termsLength[idx];

                var key = new Slice(_entriesAllocator.Slice(keys, currentOffset, keySize, ByteStringType.Immutable));
                if (suggestionsToRemove.TryGetValue(key, out int counter) == false)
                    counter = 0;

                counter--;
                suggestionsToRemove[key] = counter;
                idx++;

                currentOffset += keySize;
            }
        }

        internal static ByteStringContext<ByteStringMemoryCache>.InternalScope CreateNormalizedTerm(ByteStringContext context, ReadOnlySpan<byte> value, out Slice slice)
        {
            if (value.Length <= Constants.Terms.MaxLength)
                return Slice.From(context, value, ByteStringType.Mutable, out slice);

            return UnlikelyCreateLargeTerm(context, value, out slice);
        }

        private static ByteStringContext<ByteStringMemoryCache>.InternalScope UnlikelyCreateLargeTerm(ByteStringContext context, ReadOnlySpan<byte> value, out Slice slice)
        {
            int hashStartingPoint = Constants.Terms.MaxLength - 2 * sizeof(ulong);
            ulong hash = Hashing.XXHash64.Calculate(value.Slice(hashStartingPoint));

            Span<byte> localValue = stackalloc byte[Constants.Terms.MaxLength];
            value.Slice(0, Constants.Terms.MaxLength).CopyTo(localValue);
            int hexSize = Numbers.FillAsHex(localValue.Slice(hashStartingPoint), hash);
            Debug.Assert(Constants.Terms.MaxLength == hashStartingPoint + hexSize, "Constants.Terms.MaxLength == hashStartingPoint + hexSize");

            return Slice.From(context, localValue, ByteStringType.Mutable, out slice);
        }

        private void ProcessDeletes()
        {
            if (_deletedEntries.Count == 0)
                return;
            
            var llt = _transaction.LowLevelTransaction;
            Page lastVisitedPage = default;

            var fieldsByRootPage = GetIndexedFieldByRootPage(_fieldsTree);
            var nullTermsMarkers = new HashSet<long>();
            Querying.IndexSearcher.LoadNullTermMarkers(_nullEntriesPostingLists, nullTermsMarkers);
            
            long dicId = CompactTree.GetDictionaryId(llt);

            _termsPerEntryId.EnsureCapacityFor(_entriesAllocator, _deletedEntries.Count);
            
            foreach (long entryToDelete in _deletedEntries)
            {
                if (_entryIdToLocation.TryRemove(entryToDelete, out var entryTermsId) == false)
                    ThrowUnableToLocateEntry(entryToDelete);

                RemoveDocumentBoost(entryToDelete);
                var entryTerms = Container.MaybeGetFromSamePage(llt, ref lastVisitedPage, entryTermsId);

                var termsPerEntryIndex = InsertTermsPerEntry(entryToDelete);
                
                RecordTermDeletionsForEntry(entryTerms, llt, fieldsByRootPage, nullTermsMarkers, dicId, entryToDelete, termsPerEntryIndex);
                Container.Delete(llt, _entriesTermsContainerId, entryTermsId);
            }
        }
        
        private void RecordTermDeletionsForEntry(Container.Item entryTerms, LowLevelTransaction llt, Dictionary<long, IndexedField> fieldsByRootPage, HashSet<long> nullTermMarkers, long dicId, long entryToDelete, int termsPerEntryIndex)
        {
            var reader = new EntryTermsReader(llt, nullTermMarkers, entryTerms.Address, entryTerms.Length, dicId);
            reader.Reset();
            while (reader.MoveNextStoredField())
            {
                //Null/empty is not stored in container, just exists as marker.
                if (reader.TermId == -1)
                    continue;
                
                
                Container.Delete(llt, _storedFieldsContainerId, reader.TermId);
            }
            reader.Reset();
            while (reader.MoveNext())
            {
                if (fieldsByRootPage.TryGetValue(reader.FieldRootPage, out var field) == false)
                {
                    ThrowUnableToFindMatchingField(reader);
                }

                if (reader.IsNull)
                {
                    ref var nullTermLocation = ref CollectionsMarshal.GetValueRefOrAddDefault(field.Textual, Constants.NullValueSlice, out var nullExists);
                    if (nullExists == false)
                    {
                        nullTermLocation = field.Storage.Count;
                        field.Storage.AddByRef(new EntriesModifications(_entriesAllocator, 1));
                          // We dont want to reclaim the term name
                    }
                    ref var nullTerm = ref field.Storage.GetAsRef(nullTermLocation);
                    nullTerm.Removal(_entriesAllocator, entryToDelete, termsPerEntryIndex, reader.Frequency);
                    continue;
                }
                
                var decodedKey = reader.Current.Decoded();
                var scope = Slice.From(_entriesAllocator, decodedKey, out Slice termSlice);
                if(field.HasSuggestions)
                    RemoveSuggestions(field, decodedKey);
                
                ref var termLocation = ref CollectionsMarshal.GetValueRefOrAddDefault(field.Textual, termSlice, out var exists);
                if (exists == false)
                {
                    termLocation = field.Storage.Count;
                    field.Storage.AddByRef(new EntriesModifications(_entriesAllocator, decodedKey.Length));
                    scope = default; // We dont want to reclaim the term name
                }

                ref var term = ref field.Storage.GetAsRef(termLocation);
                term.Removal(_entriesAllocator, entryToDelete, termsPerEntryIndex, reader.Frequency);
                scope.Dispose();
                
                if(reader.HasNumeric == false)
                    continue;

                termLocation = ref CollectionsMarshal.GetValueRefOrAddDefault(field.Longs, reader.CurrentLong, out exists);
                if (exists == false)
                {
                    termLocation = field.Storage.Count;
                    field.Storage.AddByRef(new EntriesModifications(_entriesAllocator, sizeof(long)));
                }

                term = ref field.Storage.GetAsRef(termLocation);
                term.Removal(_entriesAllocator, entryToDelete, termsPerEntryIndex, freq: 1);

                termLocation = ref CollectionsMarshal.GetValueRefOrAddDefault(field.Doubles, reader.CurrentDouble, out exists);
                if (exists == false)
                {
                    termLocation = field.Storage.Count;
                    field.Storage.AddByRef(new EntriesModifications(_entriesAllocator, sizeof(long)));
                }

                term = ref field.Storage.GetAsRef(termLocation);
                term.Removal(_entriesAllocator, entryToDelete, termsPerEntryIndex, freq: 1);
            }
        }
        
        public Dictionary<long, string> GetIndexedFieldNamesByRootPage()
        {
            var pageToField = new Dictionary<long, string>();
            var it = _fieldsTree.Iterate(prefetch: false);
            if (it.Seek(Slices.BeforeAllKeys))
            {
                do
                {
                    var state = (LookupState*)it.CreateReaderForCurrent().Base;
                    if (state->RootObjectType == RootObjectType.Lookup)
                    {
                        pageToField.Add(state->RootPage, it.CurrentKey.ToString());
                    }
                } while (it.MoveNext());
            }

            return pageToField;
        }


        private Dictionary<long, IndexedField> GetIndexedFieldByRootPage(Tree fieldsTree)
        {
            var pageToField = new Dictionary<long, IndexedField>();
            
            var it = fieldsTree.Iterate(prefetch: false);
            if (it.Seek(Slices.BeforeAllKeys))
            {
                do
                {
                    var state = (LookupState*)it.CreateReaderForCurrent().Base;
                    if (state->RootObjectType != RootObjectType.Lookup) 
                        continue;
                    
                    var found = _fieldsMapping.TryGetByFieldName(it.CurrentKey, out var field);
                    if (found == false)
                    {
                        if(it.CurrentKey.EndsWith("-D"u8) || it.CurrentKey.EndsWith("-L"u8))
                            continue; // numeric postfix values
                        var dynamicIndexedField = GetDynamicIndexedField(_entriesAllocator, it.CurrentKey.AsSpan());
                        pageToField.Add(state->RootPage, dynamicIndexedField);

                    }
                    else
                    {
                        pageToField.Add(state->RootPage, _knownFieldsTerms[field.FieldId]);
                    }
                } while (it.MoveNext());
            }
            
            return pageToField;
        }

        public bool TryDeleteEntry(string term)
        {
            using var _ = Slice.From(_transaction.Allocator, term, ByteStringType.Immutable, out var termSlice);
            return TryDeleteEntry(termSlice);
        }
        
        public bool TryDeleteEntry(ReadOnlySpan<byte> term)
        {
            using var __ = Slice.From(_transaction.Allocator, term, ByteStringType.Immutable, out var termSlice);
            return TryDeleteEntry(termSlice, out _);
        }

        private bool TryDeleteEntry(Slice termSlice, out long entryId)
        {
            if (_indexedEntries.Contains(termSlice) == false)
            {
                _compactKeyScope.Key.Set(termSlice);
                //
                var exists = _fieldsTree.CompactTreeFor(_fieldsMapping.GetByFieldId(0).FieldName).TryGetValue(_compactKeyScope.Key, out var containerId);
                if (exists)
                {
                    // note that the containerId may be a single value or many(!), if it is many items
                    // we'll delete them, but treat this as a _new_ entry, not an update to an existing
                    // one
                    return RecordDeletion(containerId, out entryId);
                }
                entryId = -1;
                return false;
            }

            FlushBatch();
            return TryDeleteEntry(termSlice, out entryId);

            void FlushBatch()
            {
                // calling ResetWriter will reset the _entriesAllocator, so we need to clone it to the side
                var cloned = _transaction.Allocator.Clone(termSlice.Content);

                // We cannot actually handles modifications to the same entry in the same batch, so we cheat
                // we do a side channel flush at this point, then reset the state of the writer back to its initial level
                bool prevValue = _ownsTransaction;
                _ownsTransaction = false;
                try
                {
                    Commit();
                    ResetWriter();
                }
                finally
                {
                    _transaction.Allocator.Release(ref cloned);
                    _ownsTransaction = prevValue;
                }
            }
        }

        private void ResetWriter()
        {
            _pforDecoder.Dispose();
            _indexedEntries.Clear();

            for (int i = 0; i < _termsPerEntryId.Count; i++)
            {
                _termsPerEntryId.RawItems[i].Dispose(_entriesAllocator);
            }
            _termsPerEntryId.Dispose(_entriesAllocator);
            _termsPerEntryIds.Dispose(_entriesAllocator);
            _deletedEntries.Clear();
            _entriesAlreadyAdded.Clear();
            _additionsForTerm.Clear();
            _removalsForTerm.Clear();
            _tempListBuffer.Dispose();
            
            for (int i = 0; i < _knownFieldsTerms.Length; i++)
            {
                _knownFieldsTerms[i].Clear();
            }
            
            if (_dynamicFieldsTerms != null)
            {
                foreach (var (_, field)  in _dynamicFieldsTerms)
                {
                    field.Clear();
                }
            }
            
            _entriesAllocator.Reset();
            _entriesToTermsBuffer = new(_entriesAllocator);
            _entriesForTermsAdditionsBuffer = new NativeList<(long EntryId, long TermId)>();
            _entriesForTermsRemovalsBuffer = new NativeIntegersList(_entriesAllocator);
            _tempListBuffer = new NativeIntegersList(_entriesAllocator);
            _termsPerEntryId = new NativeList<NativeList<RecordedTerm>>();
            _termsPerEntryIds = new NativeList<long>();

            _pforDecoder = new FastPForDecoder(_entriesAllocator);
        }

        public void TryDeleteEntryByField(string field, string term)
        {
            using var __ = Slice.From(_entriesAllocator, term, ByteStringType.Immutable, out var termSlice);
            using var ___ = Slice.From(_entriesAllocator, field, ByteStringType.Immutable, out var fieldSlice);
            if (TryGetEntryTermId(fieldSlice, termSlice.AsSpan(), out long idInTree) == false) 
                return;

            RecordDeletion(idInTree, out _);
        }
  
        
        /// <summary>
        /// Record term for deletion from Index.
        /// </summary>
        /// <param name="idInTree">With frequencies and container type.</param>
        [SkipLocalsInit]
        private bool RecordDeletion(long idInTree, out long singleEntryId)
        {
            var containerId = EntryIdEncodings.GetContainerId(idInTree);
            if ((idInTree & (long)TermIdMask.PostingList) != 0)
            {
                var setSpace = Container.GetMutable(_transaction.LowLevelTransaction, containerId);
                ref var setState = ref MemoryMarshal.AsRef<PostingListState>(setSpace);
                
                using var set = new PostingList(_transaction.LowLevelTransaction, Slices.Empty, setState);
                var iterator = set.Iterate();
                Span<long> buffer = stackalloc long[1024];
                while (iterator.Fill(buffer, out var read))
                {
                    // since this is also encoded we've to delete frequency and container type as well
                    EntryIdEncodings.DecodeAndDiscardFrequency(buffer, read);
                    for (int i = 0; i < read; i++)
                    {
                        long entryId = buffer[i];
                        Debug.Assert(entryId>0);
                        _deletedEntries.Add(entryId);
                    }
                    _numberOfModifications -= read;
                }

                singleEntryId = -1;
                return false;
                
            }

            if ((idInTree & (long)TermIdMask.SmallPostingList) != 0)
            {
                var smallSet = Container.Get(_transaction.LowLevelTransaction, containerId);
                // combine with existing value
                _ = VariableSizeEncoding.Read<int>(smallSet.Address, out var pos);
                
                _pforDecoder.Init(smallSet.Address + pos, smallSet.Length - pos);
                var output = stackalloc long[1024];
                while (true)
                {
                    var read = _pforDecoder.Read(output, 1024);
                    if (read == 0) 
                        break;
                    EntryIdEncodings.DecodeAndDiscardFrequency(new Span<long>(output, 1024), read);
                    for (int i = 0; i < read; i++)
                    {
                        long entryId = output[i];
                        Debug.Assert(entryId>0);
                        _deletedEntries.Add(entryId);
                    }
                    _numberOfModifications -= read;
                }
                singleEntryId = -1;
                return false;
            }

            singleEntryId = containerId;
            Debug.Assert(containerId>0);
            _deletedEntries.Add(containerId);
            _numberOfModifications--;
            return true;
        }

        /// <summary>
        /// Get TermId (id of container) from FieldTree 
        /// </summary>
        /// <param name="idInTree">Has frequency and container type inside idInTree.</param>
        /// <returns></returns>
        private bool TryGetEntryTermId(Slice fieldName, ReadOnlySpan<byte> term, out long idInTree)
        {
            var fieldTree = _fieldsTree.CompactTreeFor(fieldName);

            // We need to normalize the term in case we have a term bigger than MaxTermLength.
            using var __ = CreateNormalizedTerm(_entriesAllocator, term, out var termSlice);

            var termValue = termSlice.AsReadOnlySpan();
            return fieldTree.TryGetValue(termValue, out idInTree);
        }

        public void Commit() => Commit<EmptyStatsScope>(default);
        
        public void Commit<TStatsScope>(TStatsScope stats)
            where TStatsScope : struct, ICoraxStatsScope
        {
            _indexDebugDumper.Commit();
            using var _ = _entriesAllocator.Allocate(Container.MaxSizeInsideContainerPage, out Span<byte> workingBuffer);

            using (stats.For(CommitOperation.Deletions))
                ProcessDeletes();
            
            Tree entriesToTermsTree = _transaction.CreateTree(Constants.IndexWriter.EntriesToTermsSlice);
            Tree entriesToSpatialTree = _transaction.CreateTree(Constants.IndexWriter.EntriesToSpatialSlice);
            _indexMetadata.Increment(Constants.IndexWriter.NumberOfEntriesSlice, _numberOfModifications);
            _indexMetadata.Increment(Constants.IndexWriter.NumberOfTermsInIndex, _numberOfTermModifications);
            _indexMetadata.Add(Constants.IndexWriter.LastEntryIdSlice, _lastEntryId);
            _pForEncoder = new FastPForEncoder(_entriesAllocator);

            if (_boostedDocs != null)
                AppendDocumentsBoost();
            
            for (int fieldId = 0; fieldId < _fieldsMapping.Count; ++fieldId)
            {
                var indexedField = _knownFieldsTerms[fieldId];
                using var staticFieldScope = stats.For(indexedField.NameForStatistics);

                if (indexedField.Textual.Count == 0)
                    continue;

                using (staticFieldScope.For(CommitOperation.TextualValues))
                {
                    using var inserter = new TextualFieldInserter(this, entriesToTermsTree, indexedField, workingBuffer);
                    inserter.InsertTextualField();
                }
                
                using (staticFieldScope.For(CommitOperation.IntegerValues))
                    InsertNumericFieldLongs(entriesToTermsTree, indexedField, workingBuffer);
                
                using (staticFieldScope.For(CommitOperation.FloatingValues))
                    InsertNumericFieldDoubles(entriesToTermsTree, indexedField, workingBuffer);

                using (staticFieldScope.For(CommitOperation.SpatialValues))
                    InsertSpatialField(entriesToSpatialTree, indexedField);

                if (indexedField.HasMultipleTermsPerField)
                {
                    RecordFieldHasMultipleTerms(indexedField);
                }
            }

            if (_dynamicFieldsTerms != null)
            {
                foreach (var (_, indexedField) in _dynamicFieldsTerms)
                {
                    if(indexedField.Textual.Count ==0)
                        continue;
                    
                    using var dynamicFieldScope = stats.For(indexedField.NameForStatistics);

                    using (dynamicFieldScope.For(CommitOperation.TextualValues))
                    {
                        using var inserter = new TextualFieldInserter(this, entriesToTermsTree, indexedField, workingBuffer);
                        inserter.InsertTextualField();
                    }
                    using (dynamicFieldScope.For(CommitOperation.IntegerValues))
                        InsertNumericFieldLongs(entriesToTermsTree, indexedField, workingBuffer);
                    
                    using (dynamicFieldScope.For(CommitOperation.FloatingValues))
                        InsertNumericFieldDoubles(entriesToTermsTree, indexedField, workingBuffer);
                    
                    using (dynamicFieldScope.For(CommitOperation.SpatialValues))
                        InsertSpatialField(entriesToSpatialTree,  indexedField);
                    
                    if (indexedField.HasMultipleTermsPerField)
                    {
                        RecordFieldHasMultipleTerms(indexedField);
                    }
                }
            }

            using(stats.For(CommitOperation.StoredValues))
                WriteIndexEntries();

            _pForEncoder.Dispose();
            _pForEncoder = null;
            
            // Check if we have suggestions to deal with. 
            if (_hasSuggestions)
            {
                using var __ = stats.For(CommitOperation.Suggestions);
                for (var fieldId = 0; fieldId < _knownFieldsTerms.Length; fieldId++)
                {
                    IndexedField indexedField = _knownFieldsTerms[fieldId];

                    // If there are no suggestion to add, we can continue
                    if (indexedField.Suggestions == null) 
                        continue;

                    Slice.From(_entriesAllocator, $"{Constants.IndexWriter.SuggestionsTreePrefix}{fieldId}", out var treeName);

                    var tree = _transaction.CompactTreeFor(treeName);
                    foreach (var (key, counter) in indexedField.Suggestions)
                    {
                        if (tree.TryGetValue(key, out var storedCounter) == false)
                            storedCounter = 0;

                        long finalCounter = storedCounter + counter;
                        if (finalCounter > 0)
                            tree.Add(key, finalCounter);
                        else
                            tree.TryRemove(key, out storedCounter);
                    }
                }
            }
            

            if (_ownsTransaction)
            {
                _transaction.Commit();
            }
        }

        private void RecordFieldHasMultipleTerms(IndexedField indexedField)
        {
            var tree = _transaction.CreateTree(Constants.IndexWriter.MultipleTermsInField);
            tree.Add(indexedField.Name, 1);
        }

        private void AppendDocumentsBoost()
        {
            _boostedDocs.Sort();
            foreach (var (entryId, documentBoost) in _boostedDocs)
            {
                using var __ = _documentBoost.DirectAdd(entryId, out _, out byte* boostPtr);
                float* floatBoostPtr = (float*)boostPtr;
                *floatBoostPtr = documentBoost;
            }
        }

        private void WriteIndexEntries()
        {
            using var writer = new EntryTermsWriter(_entriesAllocator);
            for (int i = 0; i < _termsPerEntryId.Count; i++)
            {
                ref var termsRef = ref _termsPerEntryId.RawItems[i];
                if (termsRef.Count == 0)
                    continue;
                int size = writer.Encode(termsRef);
                long entryTermsId = Container.Allocate(_transaction.LowLevelTransaction, _entriesTermsContainerId, size, out var space);
                writer.Write(space);
                var entry = _termsPerEntryIds.RawItems[i];
                _entryIdToLocation.Add(entry, entryTermsId);
            }
        }

        private void InsertSpatialField(Tree entriesToSpatialTree, IndexedField indexedField)
        {
            if (indexedField.Spatial == null)
                return;

            var fieldRootPage = _fieldsTree.GetLookupRootPage(indexedField.Name);
            Debug.Assert(fieldRootPage != -1);
            var termContainerId = fieldRootPage << 3 | 0b010;
            Debug.Assert(termContainerId >>> 3 == fieldRootPage, "field root too high?");
            var entriesToTerms = entriesToSpatialTree.FixedTreeFor(indexedField.Name, sizeof(double)+sizeof(double));
            foreach (var (entry, spatialEntry)  in indexedField.Spatial)
            {
                spatialEntry.Locations.Sort();

                ref var entryTerms = ref GetEntryTerms(spatialEntry.TermsPerEntryIndex);
                var locations = CollectionsMarshal.AsSpan(spatialEntry.Locations);
                foreach (var item in locations)
                {
                    var (lat,lng) = item;
                    var recordedTerm = new RecordedTerm
                    (
                        termContainerId: termContainerId,
                        lat: lat,
                        lng: lng
                    );

                    if (entryTerms.TryPush(recordedTerm) == false)
                    {
                        entryTerms.Grow(_entriesAllocator, 1);
                        entryTerms.PushUnsafe(recordedTerm);
                    }
                }

                {
                    var (lat, lng) = locations[0];
                    using (entriesToTerms.DirectAdd(entry, out _, out var ptr))
                    {
                        Unsafe.WriteUnaligned(ptr, lat);
                        Unsafe.WriteUnaligned(ptr + sizeof(double), lng);
                    }
                }
            }
        }

        
        private class TextualFieldBuffers : IDisposable
        {
            private readonly IndexWriter _parent;
            public const int BatchSize = 1024;

            private Slice[] _sortedTerms;
            private int[] _termIndexes;
            
            public CompactTree.CompactKeyLookup[] Keys;
            public int[] PageOffsets;
            public long[] PostListIds;
            private int[] _entriesOffsets;

            public void PrepareTerms(IndexedField field, out Span<Slice> terms, out Span<int> indexes)
            {
                int termsCount = field.Textual.Count;
                if (_sortedTerms == null || _sortedTerms.Length < termsCount)
                {
                    if (_sortedTerms != null)
                    {
                        ArrayPool<Slice>.Shared.Return(_sortedTerms);
                        ArrayPool<int>.Shared.Return(_termIndexes);
                    }
                    _sortedTerms = ArrayPool<Slice>.Shared.Rent(termsCount);
                    _termIndexes = ArrayPool<int>.Shared.Rent(termsCount);
                }

                int idx = 0;
                foreach (var (k,v) in field.Textual)
                {
                    _sortedTerms[idx] = k;
                    _termIndexes[idx] = v;
                    idx++;
                }

                terms = new Span<Slice>(_sortedTerms, 0, termsCount);
                indexes = new Span<int>(_termIndexes, 0, termsCount);

                terms.Sort(indexes, SliceComparer.Instance);
            }

            public TextualFieldBuffers(IndexWriter parent)
            {
                _parent = parent;
                Keys = ArrayPool<CompactTree.CompactKeyLookup>.Shared.Rent(BatchSize);
                PageOffsets = ArrayPool<int>.Shared.Rent(BatchSize);
                PostListIds = ArrayPool<long>.Shared.Rent(BatchSize);
                _entriesOffsets = ArrayPool<int>.Shared.Rent(BatchSize);
            }

            public void Dispose()
            {
                if (PostListIds != null) ArrayPool<long>.Shared.Return(PostListIds);
                if (PageOffsets != null) ArrayPool<int>.Shared.Return(PageOffsets);
                if (_entriesOffsets != null) ArrayPool<int>.Shared.Return(_entriesOffsets);

                if (_sortedTerms != null) ArrayPool<Slice>.Shared.Return(_sortedTerms);
                if (_termIndexes != null) ArrayPool<int>.Shared.Return(_termIndexes);
                
                if (Keys != null)
                {
                    var llt = _parent._transaction.LowLevelTransaction;
                    for (int i = 0; i < Keys.Length; i++)
                    {
                        ref var k = ref Keys[i].Key;
                        if (k != null)
                        {
                           llt.ReleaseCompactKey(ref k); 
                        }
                    }
                    ArrayPool<CompactTree.CompactKeyLookup>.Shared.Return(Keys);
                }

                PostListIds = null;
                PageOffsets = null;
                _entriesOffsets = null;
                Keys = null;
            }
        }

        private TextualFieldBuffers _textualFieldBuffers;


        private ref struct TextualFieldInserter
        {
            private readonly IndexWriter _writer;
            private readonly Tree _entriesToTermsTree;
            private readonly IndexedField _indexedField;
            private readonly Span<byte> _tmpBuf;
            private CompactTree _fieldTree;
            private IndexTermDumper _dumper;
            private NativeList<TermInEntryModification> _entriesForTerm;
            private NativeIntegersList _pagesToPrefetch;
            private TextualFieldBuffers _buffers;
            private int _offsetAdjustment;
            private long _curPage;

            public TextualFieldInserter(IndexWriter writer, Tree entriesToTermsTree, IndexedField indexedField, Span<byte> tmpBuf)
            {
                _writer = writer;
                _entriesToTermsTree = entriesToTermsTree;
                _indexedField = indexedField;
                _tmpBuf = tmpBuf;
                _fieldTree = writer._fieldsTree.CompactTreeFor(_indexedField.Name);
                _dumper = new IndexTermDumper(writer._fieldsTree, _indexedField.Name);
                _writer.ClearEntriesForTerm();
                _fieldTree.InitializeStateForTryGetNextValue();
                _entriesForTerm = new NativeList<TermInEntryModification>();
                _entriesForTerm.Initialize(_writer._entriesAllocator);
                _pagesToPrefetch = new NativeIntegersList(_writer._entriesAllocator);
                _buffers = _writer._textualFieldBuffers ??= new TextualFieldBuffers(_writer);
            }

            public void Dispose()
            {
                _dumper.Dispose();
                _entriesForTerm.Dispose(_writer._entriesAllocator);
                _pagesToPrefetch.Dispose();
            }

            public void InsertTextualField()
            {
                long totalLengthOfTerm = 0;
                _buffers.PrepareTerms(_indexedField, out var sortedTerms, out var termsOffsets);
                Debug.Assert(sortedTerms.Length > 0, "sortedTerms.Length > 0 (checked by the caller)");

                var firstTerm = sortedTerms[0]; // if we have null, it will always sort first
                if (firstTerm.AsReadOnlySpan().SequenceEqual(Constants.NullValueSlice.AsReadOnlySpan()))
                {
                    (long postingListId, long termContainerId)  = GetOrCreateNullTermPostingList();
                    ref var entries = ref _indexedField.Storage.GetAsRef(termsOffsets[0]);
                    var nullLookup = new CompactTree.CompactKeyLookup(CompactKey.NullInstance);
                    totalLengthOfTerm += ProcessSingleEntry(ref entries, ref nullLookup, isNullTerm: true,
                        sortedTerms[0], postingListId, termContainerId, -1);
                    
                    // now skip it the null so we don't have the special case it
                    sortedTerms = sortedTerms[1..]; 
                    termsOffsets = termsOffsets[1..];
                }
                
                while (true)
                {
                    if (sortedTerms.IsEmpty)
                        break;

                    PrepareTextualFieldBatch(_buffers,
                        _indexedField,
                        _fieldTree,
                        sortedTerms,
                        termsOffsets,
                        out var keys,
                        out var postListIds,
                        out var pageOffsets);

                    var entriesOffsets = termsOffsets; // a copy that we trim internally in the loop belows
                    while (keys.IsEmpty == false)
                    {
                        var treeChanged = _fieldTree.CheckTreeStructureChanges();

                        _offsetAdjustment = 0;
                        int read = _fieldTree.BulkUpdateStart(keys, postListIds, pageOffsets, out _curPage);

                        PrefetchContainerPages(ref _pagesToPrefetch, postListIds[..read]);

                        int idx = 0;
                        for (; idx < read; idx++)
                        {
                            ref var entries = ref _indexedField.Storage.GetAsRef(entriesOffsets[idx]);
                            totalLengthOfTerm += ProcessSingleEntry(ref entries, ref keys[idx], isNullTerm: false,
                                sortedTerms[idx], postListIds[idx],
                                keys[idx].ContainerId, pageOffsets[idx]);

                            // if the tree structure changed, the bulk insert details are wrong
                            // and will need to restart the operation with a new BulkUpdateStart
                            if (treeChanged.Changed)
                            {
                                // next time, we start from the _next_ key, not the current one
                                idx++;
                                for (int j = idx; j < read; j++)
                                {
                                    // Reset the known container id, since we modified the tree structure.
                                    // The issue is that we may have a term id that was remembered by a separator key
                                    // and we'll lose that after a page merge, so we'll have a reference to a deleted key
                                    // see: RavenDB-21272
                                    keys[j].ContainerId = -1;
                                }
                                break;
                            }
                        }

                        keys = keys[idx..];
                        postListIds = postListIds[idx..];
                        pageOffsets = pageOffsets[idx..];
                        entriesOffsets = entriesOffsets[idx..];
                        sortedTerms = sortedTerms[idx..];
                        termsOffsets = termsOffsets[idx..];
                    }
                }

                _writer.InsertEntriesForTermBulk(_entriesToTermsTree, _indexedField.Name);

                _writer._indexMetadata.Increment(_indexedField.NameTotalLengthOfTerms, totalLengthOfTerm);
            }

            private long ProcessSingleEntry(ref EntriesModifications entries, ref CompactTree.CompactKeyLookup key,
                bool isNullTerm, Slice term, long postListId, long termContainerId, int pageOffset)
            {
                UpdateEntriesForTerm(ref _entriesForTerm, in entries);
                if (_indexedField.Spatial == null) // For spatial, we handle this in InsertSpatialField, so we skip it here
                {
                    _writer.SetRange(_writer._additionsForTerm, entries.Additions);
                    _writer.SetRange(_writer._removalsForTerm, entries.Removals);
                }

                bool found = postListId != -1;
                Debug.Assert(found || entries.Removals.Count == 0, "Cannot remove entries from term that isn't already there");

                int totalLengthOfTerm = 0;
                if (entries.HasChanges)
                {
                    long termId;
                    if (entries.Additions.Count > 0 && found == false)
                    {
                        if (entries.Removals.Count != 0)
                            throw new InvalidOperationException($"Attempt to remove entries from new term: '{term}' for field {_indexedField.Name}! This is a bug.");

                        _writer.AddNewTerm(ref entries, _tmpBuf, out termId);
                        totalLengthOfTerm = entries.TermSize;

                        _dumper.WriteAddition(term, termId);
                        _fieldTree.BulkUpdateSet(ref key, termId, _curPage, pageOffset, ref _offsetAdjustment);
                    }
                    else
                    {
                        var entriesToTermResult = _writer.AddEntriesToTerm(_tmpBuf, postListId, isNullTerm, ref entries, out termId);
                        switch (entriesToTermResult)
                        {
                            case AddEntriesToTermResult.UpdateTermId:
                                if (termId != postListId)
                                {
                                    _dumper.WriteRemoval(term, postListId);
                                }

                                Debug.Assert(isNullTerm == false, "isNullTerm == false - we pre-generate the ids, after all");
                                
                                _dumper.WriteAddition(term, termId);
                                _fieldTree.BulkUpdateSet(ref key, termId, _curPage, pageOffset, ref _offsetAdjustment);
                                break;
                            case AddEntriesToTermResult.RemoveTermId:
                                Debug.Assert(isNullTerm == false, "isNullTerm == false, checked inside AddEntriesToTerm");
                                if (_fieldTree.BulkUpdateRemove(ref key, _curPage, pageOffset, ref _offsetAdjustment, out long oldValue) == false)
                                {
                                    _dumper.WriteRemoval(term, termId);
                                    ThrowTriedToDeleteTermThatDoesNotExists(term, _indexedField);
                                }

                                totalLengthOfTerm = -entries.TermSize;
                                _dumper.WriteRemoval(term, oldValue);
                                _writer._numberOfTermModifications--;
                                break;
                            case AddEntriesToTermResult.NothingToDo:
                                break;
                            default:
                                throw new ArgumentOutOfRangeException(entriesToTermResult.ToString());
                        }
                    }
                }
                
                RecordTermsForEntries(_entriesForTerm, entries, termContainerId);

                if (_indexedField.Spatial == null)
                {
                    Debug.Assert(termContainerId > 0);
                    _writer.InsertEntriesForTerm(termContainerId);
                }

                return totalLengthOfTerm;
            }
            
            private void RecordTermsForEntries(in NativeList<TermInEntryModification> entriesForTerm, in EntriesModifications entries, long termContainerId)
            {
                var entriesForTermCount = entriesForTerm.Count;
                var rawItems = entriesForTerm.RawItems;
            
                for (int i = 0; i < entriesForTermCount; i++)
                {
                    var entry = rawItems[i];
                
                    ref var recordedTermList = ref _writer.GetEntryTerms(entry.TermsPerEntryIndex);

                    if ( recordedTermList.HasCapacityFor(1) == false)
                        recordedTermList.Grow(_writer._entriesAllocator, 1);

                    ref var recordedTerm = ref recordedTermList.AddByRefUnsafe();

                    Debug.Assert((termContainerId & 0b111) == 0); // ensure that the three bottom bits are cleared
                
                    long recordedTermContainerId = entry.Frequency switch
                    {
                        > 1 => termContainerId << 8 | // note, bottom 3 are cleared, so we have 11 bits to play with
                               EntryIdEncodings.FrequencyQuantization(entry.Frequency) << 3 |
                               0b100, // marker indicating that we have a term frequency here
                        _ => termContainerId
                    };
                
                    if (entries.Long != null)
                    {
                        recordedTermContainerId |= 1; // marker!
                        recordedTerm.Long = entries.Long.Value;

                        // only if the double value can not be computed by casting from long, we store it 
                        if (entries.Double != null && Math.Abs(entries.Double.Value - recordedTerm.Long) > double.Epsilon)
                        {
                            recordedTermContainerId |= 2; // marker!
                            recordedTerm.Double = entries.Double.Value;
                        }
                    }

                    recordedTerm.TermContainerId = recordedTermContainerId;
                }
            }


            private void UpdateEntriesForTerm(ref NativeList<TermInEntryModification> entriesForTerm, in EntriesModifications entries)
            {
                entriesForTerm.ResetAndEnsureCapacity(_writer._entriesAllocator, entries.Additions.Count + entries.Updates.Count);
                entriesForTerm.AddRangeUnsafe(entries.Additions.RawItems, entries.Additions.Count);
                entriesForTerm.AddRangeUnsafe(entries.Updates.RawItems, entries.Updates.Count);
            }

            private void PrepareTextualFieldBatch(TextualFieldBuffers buffers,
                IndexedField indexedField,
                CompactTree fieldTree,
                Span<Slice> sortedTerms,
                Span<int> termsIndexes,
                out Span<CompactTree.CompactKeyLookup> keys,
                out Span<long> postListIds,
                out Span<int> pageOffsets)
            {
                var max = Math.Min(TextualFieldBuffers.BatchSize, sortedTerms.Length);
                var llt = _writer._transaction.LowLevelTransaction;
                for (int i = 0; i < max; i++)
                {
                    var term = sortedTerms[i];

                    var key = buffers.Keys[i].Key ??= llt.AcquireCompactKey();
                    buffers.Keys[i].ContainerId = -1;
                    key.Set(term.AsSpan());
                    key.ChangeDictionary(fieldTree.DictionaryId);
                    key.EncodedWithCurrent(out _);

                    ref var entries = ref indexedField.Storage.GetAsRef(termsIndexes[i]);
                    entries.Prepare(_writer._entriesAllocator);
                }

                keys = new Span<CompactTree.CompactKeyLookup>(buffers.Keys, 0, max);
                postListIds = new Span<long>(buffers.PostListIds, 0, max);
                pageOffsets = new Span<int>(buffers.PageOffsets, 0, max);
            }

            private (long NullListId, long NullTermId) GetOrCreateNullTermPostingList()
            {
                // In the case where the field does not have any null values, we will create a *large* posting list (an empty one)
                // then we'll insert data to it as if it was any other term
                var entry = _writer._nullEntriesPostingLists.Read(_indexedField.Name);

                if (entry != null)
                {
                    Debug.Assert(sizeof(long) * 2 == sizeof((long, long)));
                    Debug.Assert(entry.Reader.Length == sizeof((long, long)));
                    return *((long,long)*)entry.Reader.Base;
                }

                long setId = Container.Allocate(_writer._transaction.LowLevelTransaction, _writer._postingListContainerId, sizeof(PostingListState), out var setSpace);

                _writer.InitializeFieldRootPage(_indexedField);
                
                long nullMarkerId = Container.Allocate(_writer._transaction.LowLevelTransaction, _writer._entriesTermsContainerId,
                    1, _indexedField.FieldRootPage, out var nullBuffer);
                nullBuffer.Clear();
                
                // we need to account for the size of the posting lists, once a term has been switch to a posting list
                // it will always be in this model, so we don't need to do any cleanup
                _writer._largePostingListSet ??= _writer._transaction.OpenPostingList(Constants.IndexWriter.LargePostingListsSetSlice);
                _writer._largePostingListSet.Add(setId);

                ref var postingListState = ref MemoryMarshal.AsRef<PostingListState>(setSpace);
                PostingList.Create(_writer._transaction.LowLevelTransaction, ref postingListState);
                var encodedPostingListId = EntryIdEncodings.Encode(setId, 0, TermIdMask.PostingList);

                using (_writer._nullEntriesPostingLists.DirectAdd(_indexedField.Name, sizeof((long, long)), out var p))
                {
                    *((long, long)*)p = (encodedPostingListId, nullMarkerId);
                }

                return (encodedPostingListId, nullMarkerId);
            }
            
            

            private void PrefetchContainerPages(ref NativeIntegersList pagesToPrefetch, Span<long> postListIds)
            {
                pagesToPrefetch.Clear();
                pagesToPrefetch.EnsureCapacity(postListIds.Length);

                foreach (var cur in postListIds)
                {
                    if (cur == -1)
                        continue;
                    if ((cur & (long)TermIdMask.EnsureIsSingleMask) == 0) 
                        continue;
                
                    long containerId = EntryIdEncodings.GetContainerId(cur);
                    pagesToPrefetch.Add(containerId / Voron.Global.Constants.Storage.PageSize);
                }

                pagesToPrefetch.SortAndRemoveDuplicates();
            
                _writer._transaction.LowLevelTransaction.DataPager.MaybePrefetchMemory(pagesToPrefetch.GetEnumerator());
            }

        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref NativeList<RecordedTerm> GetEntryTerms(int termsPerEntryIndex)
        {
            return ref _termsPerEntryId.ToSpan()[termsPerEntryIndex];
        }

        private void ClearEntriesForTerm()
        {
            _entriesAlreadyAdded.Clear();
            _entriesForTermsRemovalsBuffer.Clear();
            _entriesForTermsAdditionsBuffer.Clear();
        }

        private void InsertEntriesForTermBulk(Tree entriesToTermsTree, Slice name)
        {
            var entriesToTerms = entriesToTermsTree.LookupFor<Int64LookupKey>(name);
            if (_entriesForTermsRemovalsBuffer.Count > 0)
            {
                Sort.Run(_entriesForTermsRemovalsBuffer.RawItems, _entriesForTermsRemovalsBuffer.Count);
                entriesToTerms.InitializeCursorState();
                for (int i = 0; i < _entriesForTermsRemovalsBuffer.Count; i++)
                {
                    Int64LookupKey key = _entriesForTermsRemovalsBuffer.RawItems[i];
                    if (entriesToTerms.TryGetNextValue(ref key, out _))
                        entriesToTerms.TryRemoveExistingValue(ref key, out _);
                }
            }

            if (_entriesForTermsAdditionsBuffer.Count > 0)
            {
                _entriesForTermsAdditionsBuffer.Sort();
                entriesToTerms.InitializeCursorState();
                for (int i = 0; i < _entriesForTermsAdditionsBuffer.Count; i++)
                {
                    ref var cur = ref _entriesForTermsAdditionsBuffer.RawItems[i];
                    Int64LookupKey key = cur.EntryId;
                    entriesToTerms.TryGetNextValue(ref key, out _);
                    entriesToTerms.AddOrSetAfterGetNext(ref key, cur.TermId);
                }
            }
        }

        

        private void SetRange(List<long> list, in NativeList<TermInEntryModification> span)
        {
            list.Clear();
            for (int i = 0; i < span.Count; i++)
            {
                list.Add(span.RawItems[i].EntryId);
            }
        }

        private enum AddEntriesToTermResult
        {
            NothingToDo,
            UpdateTermId,
            RemoveTermId,
        }


        /// <param name="idInTree">encoded</param>
        /// <param name="termId">encoded</param>
        private AddEntriesToTermResult AddEntriesToTerm(Span<byte> tmpBuf, long idInTree, bool isNullTerm, ref EntriesModifications entries, out long termId)
        {
            if ((idInTree & (long)TermIdMask.PostingList) != 0)
            {
                return AddEntriesToTermResultViaLargePostingList(ref entries, out termId, isNullTerm, idInTree & Constants.StorageMask.ContainerType);
            }
            if ((idInTree & (long)TermIdMask.SmallPostingList) != 0)
            {
                return AddEntriesToTermResultViaSmallPostingList(tmpBuf, ref entries, out termId, idInTree & Constants.StorageMask.ContainerType);
            }
            return AddEntriesToTermResultSingleValue(tmpBuf, idInTree, ref entries, out termId);
        }

        private AddEntriesToTermResult AddEntriesToTermResultViaSmallPostingList(Span<byte> tmpBuf, ref EntriesModifications entries, out long termIdInTree, long idInTree)
        {
            var containerId = EntryIdEncodings.GetContainerId(idInTree);
            
            var llt = _transaction.LowLevelTransaction;
            Container.Get(llt, containerId, out var item);

            Debug.Assert(entries.Removals.ToSpan().ToArray().Distinct().Count() == entries.Removals.Count, $"Removals list is not distinct.");

            
            // combine with existing values

            // PERF: We use SkipLocalsInit because we don't need to ensure this stack space to be filled with zeroes
            // which diminish the amount of work this method has to do.
           
            var count = VariableSizeEncoding.Read<int>(item.Address, out var offset);

            int capacity = Math.Max(256, count + entries.Additions.Count + entries.Removals.Count);
            _entriesToTermsBuffer.EnsureCapacity(capacity);
            _pforDecoder.Init(item.Address + offset, item.Length - offset);
            Debug.Assert(_entriesToTermsBuffer.Capacity > 0 && _entriesToTermsBuffer.Capacity % 256 ==0, "The buffer must be multiple of 256 for PForDecoder.REad");
            _entriesToTermsBuffer.Count = _pforDecoder.Read(_entriesToTermsBuffer.RawItems, _entriesToTermsBuffer.Capacity);
            entries.GetEncodedAdditionsAndRemovals(_entriesAllocator, out long* additions, out long* removals);

            // Merging between existing, additions and removals, there is one scenario where we can just concat the lists together
            // if we have no removals and all of the new additions are *after* the existing ones. Since everything is sorted, this is
            // a very cheap check.
            // existing: [ 10 .. 20 ], removals: [], additions: [ 30 .. 40 ], so result should be [ 10 .. 40 ]
            // In all other scenarios, we have to sort and remove duplicates & removals
            var needSorting = entries.Removals.Count > 0 || // any removal force sorting
                              // here we test if the first new addition is smaller than the largest existing, requiring sorting  
                              (entries.Additions.Count > 0 && additions[0] <= _entriesToTermsBuffer.RawItems[_entriesToTermsBuffer.Count - 1]);
            
            _entriesToTermsBuffer.Add(additions, entries.Additions.Count);
            _entriesToTermsBuffer.Add(removals, entries.Removals.Count);

            if (needSorting)
            {
                _entriesToTermsBuffer.SortAndRemoveDuplicatesAndRemovals();
            }
            

            if (_entriesToTermsBuffer.Count == 0)
            {
                Container.Delete(llt, _postingListContainerId, containerId);
                termIdInTree = -1;
                return AddEntriesToTermResult.RemoveTermId;
            }

            
            if (TryEncodingToBuffer(_entriesToTermsBuffer.RawItems, _entriesToTermsBuffer.Count, tmpBuf, out var encoded) == false)
            {

                AddNewTermToSet(out termIdInTree);
                return AddEntriesToTermResult.UpdateTermId;
            }
            
            if (encoded.Length == item.Length)
            {
                var mutableSpace = Container.GetMutable(llt, containerId);
                encoded.CopyTo(mutableSpace);

                // can update in place
                termIdInTree = -1;
                return AddEntriesToTermResult.NothingToDo;
            }

            Container.Delete(llt, _postingListContainerId, containerId);
         
            termIdInTree = AllocatedSpaceForSmallSet(encoded,llt, out Span<byte> space);

            encoded.CopyTo(space);

            return AddEntriesToTermResult.UpdateTermId;
        }

        private long AllocatedSpaceForSmallSet(Span<byte> encoded, LowLevelTransaction llt, out Span<byte> space)
        {
            long termIdInTree = Container.Allocate(llt, _postingListContainerId, encoded.Length, out space);
            
            return EntryIdEncodings.Encode(termIdInTree, 0, TermIdMask.SmallPostingList);
        }

        private AddEntriesToTermResult AddEntriesToTermResultSingleValue(Span<byte> tmpBuf, long idInTree, ref EntriesModifications entries, out long termId)
        {
            entries.AssertPreparationIsNotFinished();
            
            var (existingEntryId, existingFrequency) = EntryIdEncodings.Decode(idInTree);
            
            // In case when existingEntryId and only addition is the same:
            // Let's assert whether the current document will output the same ID as the previous one.
            // We can assume that removals are "agnostic" for us since the already stored document has the same ID as this one.
            // In any other case, where did the different ID come from?
            var additions = entries.Additions.RawItems;
            if (entries.Additions.Count == 1) 
            {
                ref var single = ref additions[0];
                if (single.EntryId == existingEntryId)
                {
                    Debug.Assert(entries.Removals.Count == 0 || entries.Removals.RawItems[0].EntryId == existingEntryId);

                    var newId = EntryIdEncodings.Encode(single.EntryId, single.Frequency, (long)TermIdMask.Single);
                    if (newId == idInTree)
                    {
                        termId = -1;
                        return AddEntriesToTermResult.NothingToDo;
                    }
                }
            }
            
            if (entries.Additions.Count == 0 && entries.Removals.Count > 0) 
            {
                if (entries.Removals.Count > 1)
                {
                    ThrowMoreThanOneRemovalFoundForSingleItem(idInTree, entries, existingEntryId, existingFrequency);
                }
                
                Debug.Assert(EntryIdEncodings.QuantizeAndDequantize(entries.Removals.RawItems[0].Frequency) == existingFrequency, "The item stored and the item we're trying to delete are different, which is impossible.");
                
                termId = -1;
                return AddEntriesToTermResult.RemoveTermId;
            }

            // Another document contains the same term. Let's check if the currently indexed document is in EntriesModification.
            // If it's not, we have to add it (since it has to be included in Small/Set too).
            if (entries.Additions.Count >= 1)
            {
                bool isIncluded = false;
                for (int idX = 0; idX < entries.Additions.Count && isIncluded == false; ++idX)
                {
                    if (entries.Additions.RawItems[idX].EntryId == existingEntryId)
                        isIncluded = true;
                }
                
                //User may wants to delete it.
                for (int idX = 0; idX < entries.Removals.Count && isIncluded == false; ++idX)
                {
                    if (entries.Removals.RawItems[idX].EntryId == existingEntryId)
                        isIncluded = true;
                }

                if (isIncluded == false)
                    entries.Addition(_entriesAllocator, existingEntryId,-1,  existingFrequency);
            }
            
            
            AddNewTerm(ref entries, tmpBuf, out termId);
            return AddEntriesToTermResult.UpdateTermId;
        }
        
        private AddEntriesToTermResult AddEntriesToTermResultViaLargePostingList(ref EntriesModifications entries, out long termId, bool isNullTerm, long id)
        {
            var containerId = EntryIdEncodings.GetContainerId(id);
            var llt = _transaction.LowLevelTransaction;
            var setSpace = Container.GetMutable(llt, containerId);
            ref var postingListState = ref MemoryMarshal.AsRef<PostingListState>(setSpace);
            
            entries.GetEncodedAdditionsAndRemovals(_entriesAllocator, out var additions, out var removals);

            var numberOfEntries = PostingList.Update(_transaction.LowLevelTransaction, ref postingListState, additions, entries.Additions.Count, removals,
                entries.Removals.Count, _pForEncoder, ref _tempListBuffer, ref _pforDecoder );

            termId = -1;

            if (numberOfEntries == 0)
            {
                if (isNullTerm) // we don't want to remove the null term posting list 
                    return AddEntriesToTermResult.NothingToDo;
                
                llt.FreePage(postingListState.RootPage);

                Container.Delete(llt, _postingListContainerId, containerId);
                RemovePostingListFromLargePostingListsSet(containerId);

                return AddEntriesToTermResult.RemoveTermId;
            }

            return AddEntriesToTermResult.NothingToDo;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RemovePostingListFromLargePostingListsSet(long containerId)
        {
            _largePostingListSet ??= _transaction.OpenPostingList(Constants.IndexWriter.LargePostingListsSetSlice);
            _largePostingListSet.Remove(containerId);
        }

        private void InsertNumericFieldLongs(Tree entriesToTermsTree, IndexedField indexedField, Span<byte> tmpBuf)
        {
            var fieldTree = _fieldsTree.LookupFor<Int64LookupKey>(indexedField.NameLong);

            ClearEntriesForTerm();

            foreach (var (term, entriesLocation) in indexedField.Longs)
            {
                ref var entries = ref indexedField.Storage.GetAsRef(entriesLocation);

                // We are not going to be using these entries anymore after this. 
                // Therefore, we can copy and we dont need to get a reference to the entry in the dictionary.
                // IMPORTANT: No modification to the dictionary can happen from this point onwards. 
                var localEntry = entries;
                localEntry.Prepare(_entriesAllocator);
                if (localEntry.HasChanges == false)
                    continue;
                  
                UpdateEntriesForTerm(entries, term);
                
                long termId;
                var hasTerm = fieldTree.TryGetValue(term, out var existing);
                if (localEntry.Additions.Count > 0 && hasTerm == false)
                {
                    Debug.Assert(localEntry.Removals.Count == 0, "entries.TotalRemovals == 0");
                    AddNewTerm(ref localEntry, tmpBuf, out termId);
                    
                    fieldTree.Add(term, termId);
                    continue;
                }

                switch (AddEntriesToTerm(tmpBuf, existing, isNullTerm: false, ref localEntry, out termId))
                {
                    case AddEntriesToTermResult.UpdateTermId:
                        fieldTree.Add(term, termId);
                        break;
                    case AddEntriesToTermResult.RemoveTermId:
                        fieldTree.TryRemove(term);
                        break;
                }
            }
            
            InsertEntriesForTermBulk(entriesToTermsTree,indexedField.NameLong);
        }

        private void UpdateEntriesForTerm(EntriesModifications entries, long term)
        {
            SetRange(_additionsForTerm, entries.Additions);
            SetRange(_removalsForTerm, entries.Removals);

            InsertEntriesForTerm( term);
        }

        private void InsertEntriesForTerm(long term)
        {
            _entriesForTermsRemovalsBuffer.EnsureCapacity(_removalsForTerm.Count + _entriesForTermsRemovalsBuffer.Count);
            foreach (long removal in _removalsForTerm)
            {
                // if already added, we don't need to remove it in this batch
                if (_entriesAlreadyAdded.Contains(removal))
                    continue;
                _entriesForTermsRemovalsBuffer.AddUnsafe(removal);
            }

            if (_entriesForTermsAdditionsBuffer.HasCapacityFor(_additionsForTerm.Count) == false)
                _entriesForTermsAdditionsBuffer.Grow(_entriesAllocator, _additionsForTerm.Count);
            foreach (long addition in _additionsForTerm)
            {
                if (_entriesAlreadyAdded.Add(addition) == false)
                    continue;
                ref var tuple = ref _entriesForTermsAdditionsBuffer.AddByRefUnsafe();
                tuple = (addition, term);
            }
        }

        private void InsertNumericFieldDoubles(Tree entriesToTermsTree, IndexedField indexedField, Span<byte> tmpBuf)
        {
            var fieldTree = _fieldsTree.LookupFor<DoubleLookupKey>(indexedField.NameDouble);

            ClearEntriesForTerm();

            foreach (var (term, entriesLocation) in indexedField.Doubles)
            {
                ref var entries = ref indexedField.Storage.GetAsRef(entriesLocation);

                // We are not going to be using these entries anymore after this. 
                // Therefore, we can copy and we dont need to get a reference to the entry in the dictionary.
                // IMPORTANT: No modification to the dictionary can happen from this point onwards. 
                var localEntry = entries;
                localEntry.Prepare(_entriesAllocator);
                
                if (localEntry.HasChanges == false)
                    continue;

                UpdateEntriesForTerm(entries, BitConverter.DoubleToInt64Bits(term));
                
                var hasTerm = fieldTree.TryGetValue(term, out var existing);

                long termId;
                if (localEntry.Additions.Count > 0 && hasTerm == false) // no existing value
                {
                    Debug.Assert(localEntry.Removals.Count == 0, "entries.TotalRemovals == 0");
                    AddNewTerm(ref localEntry, tmpBuf, out termId);
                    fieldTree.Add(term, termId);
                    continue;
                }

                switch (AddEntriesToTerm(tmpBuf, existing,  isNullTerm: false, ref localEntry, out termId))
                {
                    case AddEntriesToTermResult.UpdateTermId:
                        fieldTree.Add(term, termId);
                        break;
                    case AddEntriesToTermResult.RemoveTermId:
                        fieldTree.TryRemove(term);
                        break;
                }
            }
            
            InsertEntriesForTermBulk(entriesToTermsTree,indexedField.NameDouble);
        }
        
        private bool TryEncodingToBuffer(long * additions, int additionsCount, Span<byte> tmpBuf, out Span<byte> encoded)
        {
            fixed (byte* pOutput = tmpBuf)
            {
                var offset = VariableSizeEncoding.Write(pOutput, additionsCount);

                var size = _pForEncoder.Encode(additions, additionsCount);
                if (size >= tmpBuf.Length - offset)
                {

                    encoded = default;
                    return false;
                }

                (int count, int sizeUsed) = _pForEncoder.Write(pOutput + offset, tmpBuf.Length - offset);
                Debug.Assert(count == additionsCount);
                Debug.Assert(sizeUsed == size);

                encoded = tmpBuf[..(size + offset)];
                return true;
            }
        }

        private void AddNewTerm(ref EntriesModifications entries, Span<byte> tmpBuf, out long termId)
        {
            _numberOfTermModifications += 1;
            Debug.Assert(entries.Additions.Count > 0, "entries.TotalAdditions > 0");
            // common for unique values (guid, date, etc)
            if (entries.Additions.Count == 1)
            {
                entries.AssertPreparationIsNotFinished();
                ref var single = ref entries.Additions.RawItems[0]; 
                termId = EntryIdEncodings.Encode(single.EntryId, single.Frequency, (long)TermIdMask.Single);                
                return;
            }

            entries.GetEncodedAdditions(_entriesAllocator, out var additions);
            if (TryEncodingToBuffer(additions, entries.Additions.Count, tmpBuf, out var encoded) == false)
            {
                // too big, convert to a set
                AddNewTermToSet(out termId);
                return;
            }

            termId = AllocatedSpaceForSmallSet(encoded,  _transaction.LowLevelTransaction, out Span<byte> space);
            encoded.CopyTo(space);
        }


        private void AddNewTermToSet(out long termId)
        {
            long setId = Container.Allocate(_transaction.LowLevelTransaction, _postingListContainerId, sizeof(PostingListState), out var setSpace);
            
            // we need to account for the size of the posting lists, once a term has been switch to a posting list
            // it will always be in this model, so we don't need to do any cleanup
            _largePostingListSet ??= _transaction.OpenPostingList(Constants.IndexWriter.LargePostingListsSetSlice);
            _largePostingListSet.Add(setId); 

            ref var postingListState = ref MemoryMarshal.AsRef<PostingListState>(setSpace);
            PostingList.Create(_transaction.LowLevelTransaction, ref postingListState, _pForEncoder);
            termId = EntryIdEncodings.Encode(setId, 0, TermIdMask.PostingList);
        }

        private void UnlikelyGrowAnalyzerBuffer(int newBufferSize, int newTokenSize)
        {
            if (newBufferSize > _encodingBufferHandler.Length)
            {
                Analyzer.BufferPool.Return(_encodingBufferHandler);
                _encodingBufferHandler = null;
                _encodingBufferHandler = Analyzer.BufferPool.Rent(newBufferSize);
            }

            if (newTokenSize > _tokensBufferHandler.Length)
            {
                Analyzer.TokensPool.Return(_tokensBufferHandler);
                _tokensBufferHandler = null;
                _tokensBufferHandler = Analyzer.TokensPool.Rent(newTokenSize);
            }
        }
        
        public void Dispose()
        {
            _compactKeyScope.Dispose();
            _termsPerEntryId.Dispose(_entriesAllocator);
            _termsPerEntryIds.Dispose(_entriesAllocator);
            _pforDecoder.Dispose();
            _entriesAllocator.Dispose();
            _jsonOperationContext?.Dispose();
            if (_ownsTransaction)
                _transaction?.Dispose();

       
            if (_encodingBufferHandler != null)
            {
                Analyzer.BufferPool.Return(_encodingBufferHandler);
                _encodingBufferHandler = null;
            }
                
            if (_tokensBufferHandler != null)
            {
                Analyzer.TokensPool.Return(_tokensBufferHandler);
                _tokensBufferHandler = null;
            }

            if (_utf8ConverterBufferHandler != null)
            {
                Analyzer.BufferPool.Return(_utf8ConverterBufferHandler);
                _utf8ConverterBufferHandler = null;
            }
            
            _indexDebugDumper.Dispose();
            _builder.Clean();
        }

        public void ReduceModificationCount()
        {
            _numberOfModifications--;
        }
    }
}
