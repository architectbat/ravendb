﻿// -----------------------------------------------------------------------
//  <copyright file="DataDumper.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Raven.Abstractions.Data;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Indexing;
using Raven.Abstractions.Smuggler;
using Raven.Abstractions.Util;
using Raven.Imports.Newtonsoft.Json;
using Raven.Json.Linq;

namespace Raven.Database.Smuggler
{
	public class DataDumper : SmugglerApiBase
	{
		public DataDumper(DocumentDatabase database, SmugglerOptions options) : base(options)
		{
			_database = database;
		}

		private readonly DocumentDatabase _database;

		protected override void EnsureDatabaseExists()
		{
			ensuredDatabaseExists = true;
		}

		protected override Guid ExportAttachments(JsonTextWriter jsonWriter, Guid lastEtag)
		{
			var totalCount = 0;
			while (true)
			{
				var array = GetAttachments(totalCount, lastEtag);
				if (array.Length == 0)
				{
					var databaseStatistics = GetStats();
					var lastEtagComparable = new ComparableByteArray(lastEtag);
					if (lastEtagComparable.CompareTo(databaseStatistics.LastAttachmentEtag) < 0)
					{
						lastEtag = Etag.Increment(lastEtag, smugglerOptions.BatchSize);
						ShowProgress("Got no results but didn't get to the last attachment etag, trying from: {0}", lastEtag);
						continue;
					}
					ShowProgress("Done with reading attachments, total: {0}", totalCount);
					return lastEtag;
				}
				totalCount += array.Length;
				ShowProgress("Reading batch of {0,3} attachments, read so far: {1,10:#,#;;0}", array.Length, totalCount);
				foreach (var item in array)
				{
					item.WriteTo(jsonWriter);
				}
				lastEtag = new Guid(array.Last().Value<string>("Etag"));
			}
		}

		protected override RavenJArray GetDocuments(Guid lastEtag)
		{
			const int dummy = 0;
			return _database.GetDocuments(dummy, smugglerOptions.BatchSize, lastEtag);
		}

		protected override RavenJArray GetIndexes(int totalCount)
		{
			return _database.GetIndexes(totalCount, 128);
		}

		protected override void PutAttachment(AttachmentExportInfo attachmentExportInfo)
		{
			// we filter out content length, because getting it wrong will cause errors 
			// in the server side when serving the wrong value for this header.
			// worse, if we are using http compression, this value is known to be wrong
			// instead, we rely on the actual size of the data provided for us
			attachmentExportInfo.Metadata.Remove("Content-Length");
			_database.PutStatic(attachmentExportInfo.Key, null, new MemoryStream(attachmentExportInfo.Data), attachmentExportInfo.Metadata);
		}

		protected override void PutDocument(RavenJObject document)
		{
			var metadata = document.Value<RavenJObject>("@metadata");
			var key = metadata.Value<string>("@id");
			document.Remove("@metadata");

			_database.Put(key, null, document, metadata, null);
		}

		protected override void PutIndex(string indexName, RavenJToken index)
		{
			_database.PutIndex(indexName, index.Value<RavenJObject>("definition").JsonDeserialization<IndexDefinition>());
		}

		protected override DatabaseStatistics GetStats()
		{
			return _database.Statistics;
		}

		protected override void ShowProgress(string format, params object[] args)
		{
			if (Progress != null)
			{
				Progress(string.Format(format, args));
			}
		}

		private RavenJArray GetAttachments(int start, Guid? etag)
		{
			var array = new RavenJArray();
			var attachmentInfos = _database.GetAttachments(start, 128, etag, null, 1024*1024*10);

			foreach (var attachmentInfo in attachmentInfos)
			{
				var attachment = _database.GetStatic(attachmentInfo.Key);
				if (attachment == null)
					return null;
				var data = attachment.Data;
				attachment.Data = () =>
				{
					var memoryStream = new MemoryStream();
					_database.TransactionalStorage.Batch(accessor => data().CopyTo(memoryStream));
					memoryStream.Position = 0;
					return memoryStream;
				};

				var bytes = attachment.Data().ReadData();
				array.Add(
					new RavenJObject
					{
						{"Data", bytes},
						{"Metadata", attachmentInfo.Metadata},
						{"Key", attachmentInfo.Key},
						{"Etag", new RavenJValue(attachmentInfo.Etag.ToString())}
					});
			}
			return array;
		}

		public Action<string> Progress { get; set; }
	}
}