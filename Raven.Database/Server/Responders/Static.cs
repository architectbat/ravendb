//-----------------------------------------------------------------------
// <copyright file="Static.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using Raven.Abstractions.Extensions;
using Raven.Database.Data;
using Raven.Database.Extensions;
using Raven.Database.Server.Abstractions;

namespace Raven.Database.Server.Responders
{
	public class Static : AbstractRequestResponder
	{
		public override string UrlPattern
		{
			get { return "^/static/(.+)"; }
		}

		public override string[] SupportedVerbs
		{
			get { return new[] {"GET", "PUT", "DELETE","HEAD"}; }
		}

		public override void Respond(IHttpContext context)
		{
			var match = urlMatcher.Match(context.GetRequestUrl());
			var filename = match.Groups[1].Value;
			var etag = context.GetEtag();
			switch (context.Request.HttpMethod)
			{
				case "GET":
					Raven.Database.TransactionalStorage.Batch(_=> // have to keep the session open for reading of the attachment stream
					{
						var attachmentAndHeaders = Raven.Database.GetStatic(filename);
						if (attachmentAndHeaders == null)
						{
							context.SetStatusToNotFound();
							return;
						}
						if (context.MatchEtag(attachmentAndHeaders.Etag))
						{
							context.SetStatusToNotModified();
							return;
						}
						context.WriteHeaders(attachmentAndHeaders.Metadata, attachmentAndHeaders.Etag);
						using (var stream = attachmentAndHeaders.Data())
						{
							stream.CopyTo(context.Response.OutputStream);
						}
					});
					break;
				case "HEAD":
					Raven.Database.TransactionalStorage.Batch(_ => // have to keep the session open for reading of the attachment stream
					{
						var attachmentAndHeaders = Raven.Database.GetStatic(filename);
						if (attachmentAndHeaders == null)
						{
							context.SetStatusToNotFound();
							return;
						}
						if (context.MatchEtag(attachmentAndHeaders.Etag))
						{
							context.SetStatusToNotModified();
							return;
						}
						context.WriteHeaders(attachmentAndHeaders.Metadata, attachmentAndHeaders.Etag);
						context.Response.ContentLength64 = attachmentAndHeaders.Size;
					});
					break;
				case "PUT":
					var newEtag = Raven.Database.PutStatic(filename, context.GetEtag(), context.Request.InputStream,
					                                 context.Request.Headers.FilterHeadersAttachment());

					context.WriteETag(newEtag);
					context.SetStatusToCreated("/static/" + filename);
					break;
				case "DELETE":
					Raven.Database.DeleteStatic(filename, etag);
					context.SetStatusToDeleted();
					break;
			}
		}
	}
}
