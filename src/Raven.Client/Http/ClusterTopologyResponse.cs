﻿using System.Collections.Generic;

namespace Raven.Client.Http
{
    public sealed class ClusterTopologyResponse
    {
        public string Leader;
        public string NodeTag;
        public ServerNode.Role ServerRole;
        public ClusterTopology Topology;
        public long Etag;
        public Dictionary<string, NodeStatus> Status;
    }
}
