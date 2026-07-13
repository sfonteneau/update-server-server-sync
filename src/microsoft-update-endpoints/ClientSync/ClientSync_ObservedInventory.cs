// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.Storage;
using Microsoft.UpdateServices.WebServices.ClientSync;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Endpoints.ClientSync
{
    public partial class ClientSyncWebService
    {
        /// <summary>
        /// Records the fleet-wide union of detectoids and hardware identifiers
        /// reported by a Windows Update client. This method only writes to SQLite
        /// and never contacts Microsoft Update.
        /// </summary>
        private void RecordObservedInventory(SyncUpdateParameters parameters)
        {
            if (parameters == null || MetadataSource == null)
            {
                return;
            }

            // InstalledNonLeafUpdateIDs can contain broad product-category nodes.
            // The SQLite resolver deliberately returns only actual detectoid packages
            // so a category node alone cannot activate an unrelated product fetch.
            var detectorIdentities = MetadataSource.GetObservedDetectorIdentities(
                parameters.InstalledNonLeafUpdateIDs ?? Array.Empty<int>());
            var detectoids = detectorIdentities.Values
                .Select(identity => new ObservedDetectoidIdentity(
                    identity.ID,
                    identity.Revision))
                .ToList();

            var devices = parameters.SystemSpec ?? Array.Empty<Device>();
            var pnpHardwareIds = devices
                .Where(device => device != null)
                .SelectMany(device => device.HardwareIDs ?? Array.Empty<string>())
                .ToList();
            var compatibleIds = devices
                .Where(device => device != null)
                .SelectMany(device => device.CompatibleIDs ?? Array.Empty<string>())
                .ToList();
            var computerIds = parameters.ComputerSpec?.HardwareIDs
                ?? Array.Empty<Guid>();

            MetadataSource.RecordObservedInventory(new ObservedInventoryBatch(
                DateTimeOffset.UtcNow,
                detectoids,
                pnpHardwareIds,
                compatibleIds,
                computerIds));
        }
    }
}
