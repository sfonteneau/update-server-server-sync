// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.Storage;
using Microsoft.UpdateServices.WebServices.ClientSync;
using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Threading.Tasks;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Endpoints.ClientSync
{
    public partial class ClientSyncWebService
    {
        /// <summary>
        /// Handles software discovery directly from SQLite. Only the packages sent
        /// in this response are materialized in memory.
        /// </summary>
        private Task<SyncInfo> DoSoftwareUpdateSync(SyncUpdateParameters parameters)
        {
            if (MetadataSource == null)
            {
                throw new FaultException("Metadata source is not configured.");
            }

            var installedNonLeaf = GetInstalledNotLeafGuidsFromSyncParameters(parameters);
            var otherCached = GetOtherCachedUpdateGuidsFromSyncParameters(parameters);
            var response = new SyncInfo
            {
                NewCookie = new Cookie
                {
                    Expiration = DateTime.Now.AddDays(5),
                    EncryptedData = new byte[12]
                },
                DriverSyncNotNeeded = "false"
            };

            if (AddSoftwareStage(
                ClientSyncSoftwareStage.Root,
                installedNonLeaf,
                otherCached,
                response,
                nonLeafResponse: true))
            {
                return Task.FromResult(response);
            }

            if (AddSoftwareStage(
                ClientSyncSoftwareStage.NonLeaf,
                installedNonLeaf,
                otherCached,
                response,
                nonLeafResponse: true))
            {
                return Task.FromResult(response);
            }

            if (AddSoftwareStage(
                ClientSyncSoftwareStage.BundledLeaf,
                installedNonLeaf,
                otherCached,
                response,
                nonLeafResponse: false))
            {
                return Task.FromResult(response);
            }

            AddSoftwareStage(
                ClientSyncSoftwareStage.Leaf,
                installedNonLeaf,
                otherCached,
                response,
                nonLeafResponse: false);
            return Task.FromResult(response);
        }

        private bool AddSoftwareStage(
            ClientSyncSoftwareStage stage,
            IReadOnlyCollection<Guid> installedNonLeaf,
            IReadOnlyCollection<Guid> otherCached,
            SyncInfo response,
            bool nonLeafResponse)
        {
            var packages = MetadataSource.GetSoftwareCandidates(
                stage,
                installedNonLeaf,
                otherCached,
                AreAllSoftwareUpdatesApproved,
                ApprovedSoftwareUpdates.ToArray(),
                MaxUpdatesInResponse,
                out var truncated);

            if (packages.Count == 0)
            {
                return false;
            }

            response.NewUpdates = nonLeafResponse
                ? CreateUpdateInfoListFromNonLeafUpdates(packages).ToArray()
                : CreateUpdateInfoListFromSoftwareUpdates(packages).ToArray();

            // Discovery remains staged, but Truncated is only set when the current
            // stage overflowed or a later graph level actually contains a candidate.
            response.Truncated = truncated || MetadataSource.HasLaterSoftwareCandidates(
                stage,
                installedNonLeaf,
                otherCached,
                AreAllSoftwareUpdatesApproved,
                ApprovedSoftwareUpdates.ToArray());
            return true;
        }

        private List<UpdateInfo> CreateUpdateInfoListFromSoftwareUpdates(
            IReadOnlyList<ClientSyncPackageRecord> records)
        {
            var result = new List<UpdateInfo>(records.Count);
            foreach (var record in records)
            {
                if (record.Package is not SoftwareUpdate)
                {
                    continue;
                }

                var action = record.IsBundle || !record.IsBundled
                    ? DeploymentAction.Install
                    : DeploymentAction.Bundle;

                result.Add(new UpdateInfo
                {
                    Deployment = new Deployment
                    {
                        Action = action,
                        ID = record.IsBundle ? 20000 : (record.IsBundled ? 20001 : 20002),
                        AutoDownload = "0",
                        AutoSelect = "0",
                        SupersedenceBehavior = "0",
                        IsAssigned = true,
                        LastChangeTime = "2019-08-06"
                    },
                    IsLeaf = true,
                    ID = record.RevisionId,
                    IsShared = false,
                    Verification = null,
                    Xml = GetCoreFragment(record.Package.Id)
                });
            }

            return result;
        }

        private List<UpdateInfo> CreateUpdateInfoListFromNonLeafUpdates(
            IReadOnlyList<ClientSyncPackageRecord> records)
        {
            return records
                .Select(record => new UpdateInfo
                {
                    Deployment = new Deployment
                    {
                        Action = DeploymentAction.Evaluate,
                        ID = 15000,
                        AutoDownload = "0",
                        AutoSelect = "0",
                        SupersedenceBehavior = "0",
                        IsAssigned = true,
                        LastChangeTime = "2019-08-06"
                    },
                    IsLeaf = false,
                    ID = record.RevisionId,
                    IsShared = false,
                    Verification = null,
                    Xml = GetCoreFragment(record.Package.Id)
                })
                .ToList();
        }
    }
}
