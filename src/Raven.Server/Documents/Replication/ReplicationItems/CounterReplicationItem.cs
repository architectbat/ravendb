﻿using System.Diagnostics;
using System.Globalization;
using System.IO;
using Raven.Client;
using Raven.Server.Documents.Replication.Stats;
using Sparrow;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Server;
using Voron;

namespace Raven.Server.Documents.Replication.ReplicationItems
{
    public sealed class CounterReplicationItem : ReplicationBatchItem
    {
        public LazyStringValue Collection;
        public LazyStringValue Id;

        public BlittableJsonReaderObject Values;

        public override DynamicJsonValue ToDebugJson()
        {
            var djv = base.ToDebugJson();
            djv[nameof(Collection)] = Collection?.ToString(CultureInfo.InvariantCulture) ?? Constants.Documents.Collections.EmptyCollection;
            djv[nameof(Id)] = Id.ToString(CultureInfo.InvariantCulture);
            return djv;
        }

        public override long AssertChangeVectorSize()
        {
            return sizeof(byte) + // type
                   sizeof(int) + // change vector size
                   Encodings.Utf8.GetByteCount(ChangeVector) + // change vector
                   sizeof(short) + // transaction marker
                   sizeof(int) + // size of doc id
                   Id.Size +
                   sizeof(int) + // size of doc collection
                   Collection.Size + // doc collection
                   sizeof(int) // size of data
                   + Values.Size; // data
        }

        public override long Size => Values?.Size ?? 0;

        public override unsafe void Write(Slice changeVector, Stream stream, byte[] tempBuffer, OutgoingReplicationStatsScope stats)
        {
            fixed (byte* pTemp = tempBuffer)
            {
                if (AssertChangeVectorSize() > tempBuffer.Length)
                    ThrowTooManyChangeVectorEntries(this, Id);

                var tempBufferPos = WriteCommon(changeVector, pTemp);

                *(int*)(pTemp + tempBufferPos) = Id.Size;
                tempBufferPos += sizeof(int);
                Memory.Copy(pTemp + tempBufferPos, Id.Buffer, Id.Size);
                tempBufferPos += Id.Size;

                *(int*)(pTemp + tempBufferPos) = Collection.Size;
                tempBufferPos += sizeof(int);
                Memory.Copy(pTemp + tempBufferPos, Collection.Buffer, Collection.Size);
                tempBufferPos += Collection.Size;

                *(int*)(pTemp + tempBufferPos) = Values.Size;
                tempBufferPos += sizeof(int);

                Memory.Copy(pTemp + tempBufferPos, Values.BasePointer, Values.Size);
                tempBufferPos += Values.Size;

                stream.Write(tempBuffer, 0, tempBufferPos);

                Values.TryGet(CountersStorage.Values, out BlittableJsonReaderObject counters);
                stats.RecordCountersOutput(counters?.Count ?? 0);
            }
        }

        public override unsafe void Read(JsonOperationContext context, ByteStringContext allocator, IncomingReplicationStatsScope stats)
        {
            // TODO: add stats RavenDB-13470
            SetLazyStringValueFromString(context, out Id);
            SetLazyStringValueFromString(context, out Collection);
            Debug.Assert(Collection != null);

            var sizeOfData = *(int*)Reader.ReadExactly(sizeof(int));

            var mem = Reader.AllocateMemory(sizeOfData);
            Reader.ReadExactly(mem, sizeOfData);

            Values = new BlittableJsonReaderObject(mem, sizeOfData, context);
            Values.BlittableValidation();

            if (Values.TryGet(CountersStorage.Values, out BlittableJsonReaderObject counters) && counters != null)
                stats.RecordCountersRead(counters.Count);
        }

        protected override ReplicationBatchItem CloneInternal(JsonOperationContext context, ByteStringContext allocator)
        {
            return new CounterReplicationItem
            {
                Values = Values?.Clone(context),
                Collection = Collection.Clone(context),
                Id = Id.Clone(context)
            };
        }

        protected override void InnerDispose()
        {
            Values?.Dispose();
            Collection?.Dispose();
            Id?.Dispose();
        }
    }
}
