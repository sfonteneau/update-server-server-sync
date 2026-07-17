// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Endpoints.ClientSync
{

    public partial class ClientSyncWebService
    {
        readonly private HashSet<MicrosoftUpdatePackageIdentity> ApprovedSoftwareUpdates;
        readonly private object ApprovalLock = new();
        private bool AreAllSoftwareUpdatesApproved = true;

        private enum DriverApprovalMode
        {
            All,
            AllowList,
            DenyList
        }

        private DriverApprovalMode DriverApprovals = DriverApprovalMode.All;
        readonly private HashSet<MicrosoftUpdatePackageIdentity> ApprovedDriverUpdates;
        readonly private HashSet<MicrosoftUpdatePackageIdentity> DeniedDriverUpdates;

        /// <summary>
        /// Delegate method called to report updates applicable to a client but which are not approved and thus not offered
        /// </summary>
        /// <param name="requestedUnapprovedUpdates"></param>
        public delegate void UnApprovedUpdatesRequestedDelegate(IEnumerable<MicrosoftUpdatePackage> requestedUnapprovedUpdates);

#pragma warning disable 0067
        /// <summary>
        /// Event raised when software updates are applicable to a client but are not approved for distribution
        /// </summary>
        public event UnApprovedUpdatesRequestedDelegate OnUnApprovedSoftwareUpdatesRequested;
#pragma warning restore 0067

        /// <summary>
        /// Event raised when driver updates are applicable to a client but are not approved for distribution
        /// </summary>
        public event UnApprovedUpdatesRequestedDelegate OnUnApprovedDriverUpdatesRequested;

        /// <summary>
        /// Adds an update identity to the list of approved software updates.
        /// Approved updates are made available to clients connecting to this service.
        /// </summary>
        /// <param name="approvedUpdate">Approved update</param>
        public void AddApprovedSoftwareUpdate(MicrosoftUpdatePackageIdentity approvedUpdate)
        {
            lock (ApprovalLock)
            {
                AreAllSoftwareUpdatesApproved = false;
                ApprovedSoftwareUpdates.Add(approvedUpdate);
            }
        }

        /// <summary>
        /// Adds a list of update identities to the list of approved software updates.
        /// Approved updates are made available to clients connecting to this service.
        /// </summary>
        /// <param name="approvedUpdates">List of approved updates</param>
        public void AddApprovedSoftwareUpdates(IEnumerable<MicrosoftUpdatePackageIdentity> approvedUpdates)
        {
            lock (ApprovalLock)
            {
                AreAllSoftwareUpdatesApproved = false;
                foreach (var approvedUpdate in approvedUpdates)
                {
                    ApprovedSoftwareUpdates.Add(approvedUpdate);
                }
            }
        }

        /// <summary>
        /// Adds an update identities to the list of approved driver updates.
        /// Approved updates are made available to clients connecting to this service.
        /// </summary>
        /// <param name="approvedUpdate">Approved driver update</param>
        public void AddApprovedDriverUpdate(MicrosoftUpdatePackageIdentity approvedUpdate)
        {
            lock (ApprovalLock)
            {
                AddApprovedDriverUpdateUnsafe(approvedUpdate);
            }
        }

        private void AddApprovedDriverUpdateUnsafe(MicrosoftUpdatePackageIdentity approvedUpdate)
        {
            if (DriverApprovals == DriverApprovalMode.All)
            {
                DriverApprovals = DriverApprovalMode.AllowList;
                ApprovedDriverUpdates.Clear();
            }

            if (DriverApprovals == DriverApprovalMode.DenyList)
            {
                DeniedDriverUpdates.Remove(approvedUpdate);
            }
            else
            {
                ApprovedDriverUpdates.Add(approvedUpdate);
            }
        }

        /// <summary>
        /// Adds a list of update identities to the list of approved driver updates.
        /// Approved updates are made available to clients connecting to this service.
        /// </summary>
        /// <param name="approvedUpdates"></param>
        public void AddApprovedDriverUpdates(IEnumerable<MicrosoftUpdatePackageIdentity> approvedUpdates)
        {
            lock (ApprovalLock)
            {
                foreach (var approvedUpdate in approvedUpdates)
                {
                    AddApprovedDriverUpdateUnsafe(approvedUpdate);
                }
            }
        }

        /// <summary>
        /// Removes an approved software update from the list of approved software updates.
        /// The software update will not be given to connecting clients anymore.
        /// </summary>
        /// <param name="updateIdentity">Identity of update to un-approve</param>
        public void RemoveApprovedSoftwareUpdate(MicrosoftUpdatePackageIdentity updateIdentity)
        {
            lock (ApprovalLock)
            {
                ApprovedSoftwareUpdates.Remove(updateIdentity);
            }
        }

        /// <summary>
        /// Removes an approved software update from the list of approved software updates.
        /// The software update will not be given to connecting clients anymore.
        /// </summary>
        /// <param name="updateIdentity">Identity of update to un-approve</param>
        public void RemoveApprovedDriverUpdate(MicrosoftUpdatePackageIdentity updateIdentity)
        {
            lock (ApprovalLock)
            {
                if (DriverApprovals == DriverApprovalMode.All)
                {
                    DriverApprovals = DriverApprovalMode.DenyList;
                    DeniedDriverUpdates.Clear();
                }

                if (DriverApprovals == DriverApprovalMode.AllowList)
                {
                    ApprovedDriverUpdates.Remove(updateIdentity);
                }
                else
                {
                    DeniedDriverUpdates.Add(updateIdentity);
                }
            }
        }

        /// <summary>
        /// Clears the list of approved driver updates.
        /// Un-approved updates are not made available to connecting clients.
        /// </summary>
        public void ClearApprovedDriverUpdates()
        {
            lock (ApprovalLock)
            {
                DriverApprovals = DriverApprovalMode.AllowList;
                ApprovedDriverUpdates.Clear();
                DeniedDriverUpdates.Clear();
            }
        }

        private bool IsDriverUpdateApproved(MicrosoftUpdatePackageIdentity updateIdentity)
        {
            lock (ApprovalLock)
            {
                return DriverApprovals switch
                {
                    DriverApprovalMode.All => true,
                    DriverApprovalMode.AllowList => ApprovedDriverUpdates.Contains(updateIdentity),
                    DriverApprovalMode.DenyList => !DeniedDriverUpdates.Contains(updateIdentity),
                    _ => false
                };
            }
        }

        private void GetSoftwareApprovalSnapshot(
            out bool approveAllSoftwareUpdates,
            out MicrosoftUpdatePackageIdentity[] approvedSoftwareUpdates)
        {
            lock (ApprovalLock)
            {
                approveAllSoftwareUpdates = AreAllSoftwareUpdatesApproved;
                approvedSoftwareUpdates = ApprovedSoftwareUpdates.ToArray();
            }
        }

        /// <summary>
        /// Clears the list of approved software updates.
        /// Un-approved updates are not made available to connecting clients.
        /// </summary>
        public void ClearApprovedSoftwareUpdates()
        {
            lock (ApprovalLock)
            {
                ApprovedSoftwareUpdates.Clear();
            }
        }
    }
}
