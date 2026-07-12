// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Source
{
    /// <summary>
    /// Describes one GetDriverIdList query. Every identifier in an instance shares
    /// the same previous anchor, so the Delta flag is uniform for the request.
    /// </summary>
    public sealed class DriverUpdateFilter
    {
        private static readonly StringComparer HardwareIdComparer = StringComparer.OrdinalIgnoreCase;

        /// <summary>Gets optional OS/product category prerequisites.</summary>
        public IReadOnlyList<Guid> Categories { get; }

        /// <summary>Gets OEM computer hardware identifiers.</summary>
        public IReadOnlyList<Guid> ComputerIds { get; }

        /// <summary>Gets PnP hardware and optional compatible identifiers.</summary>
        public IReadOnlyList<string> PnpHardwareIds { get; }

        /// <summary>Gets whether at least one hardware identifier is present.</summary>
        public bool HasIdentifiers => ComputerIds.Count > 0 || PnpHardwareIds.Count > 0;

        /// <summary>Creates a driver query filter.</summary>
        public DriverUpdateFilter(
            IEnumerable<Guid> categories,
            IEnumerable<Guid> computerIds,
            IEnumerable<string> pnpHardwareIds)
        {
            Categories = (categories ?? Array.Empty<Guid>())
                .Where(value => value != Guid.Empty)
                .Distinct()
                .OrderBy(value => value)
                .ToList();

            ComputerIds = (computerIds ?? Array.Empty<Guid>())
                .Where(value => value != Guid.Empty)
                .Distinct()
                .OrderBy(value => value)
                .ToList();

            PnpHardwareIds = (pnpHardwareIds ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(NormalizeHardwareId)
                .Distinct(HardwareIdComparer)
                .OrderBy(value => value, HardwareIdComparer)
                .ToList();
        }

        /// <summary>Normalizes a hardware identifier as Windows reports it.</summary>
        public static string NormalizeHardwareId(string value)
        {
            return value?.Trim().ToUpperInvariant();
        }
    }
}
