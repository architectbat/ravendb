﻿using System.Net.Http;
using Raven.Client.Http;
using Sparrow.Json;

namespace Raven.Server.Documents.Commands.Replication
{
    internal class GetIncomingReplicationRejectionInfoCommand : RavenCommand<object>
    {
        public GetIncomingReplicationRejectionInfoCommand()
        {
        }

        public GetIncomingReplicationRejectionInfoCommand(string nodeTag)
        {
            SelectedNodeTag = nodeTag;
        }

        public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
        {
            url = $"{node.Url}/databases/{node.Database}/replication/debug/incoming-rejection-info";

            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Get
            };

            return request;
        }

        public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject response, bool fromCache)
        {
            if (response == null)
                ThrowInvalidResponse();

            Result = response;
        }

        public override bool IsReadRequest => true;
    }
}
