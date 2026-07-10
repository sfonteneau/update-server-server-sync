// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.PackageGraph.Storage
{
    /// <summary>
    /// Optional capability implemented by metadata stores that can persist upstream
    /// synchronization anchors. Anchors are opaque strings returned by WSUS
    /// GetRevisionIdList and must be scoped by endpoint and filter.
    /// </summary>
    public interface ISyncAnchorStore
    {
        bool TryGetSyncAnchor(string anchorKey, out string anchor);

        void SetSyncAnchor(string anchorKey, string anchor);

        void ClearSyncAnchor(string anchorKey);
    }
}
