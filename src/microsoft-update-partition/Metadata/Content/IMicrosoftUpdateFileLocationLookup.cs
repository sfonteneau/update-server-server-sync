// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Content
{
    /// <summary>
    /// Optional fast lookup interface for metadata stores that can resolve Microsoft Update
    /// content files directly by SHA1 digest without enumerating every package.
    /// </summary>
    public interface IMicrosoftUpdateFileLocationLookup
    {
        /// <summary>
        /// Finds update content files by SHA1 digest bytes.
        /// </summary>
        /// <param name="sha1Digests">Requested SHA1 digests.</param>
        /// <returns>Matching update files, including their Microsoft Update URLs when available.</returns>
        IReadOnlyList<UpdateFile> FindFilesBySha1(IEnumerable<byte[]> sha1Digests);
    }
}
