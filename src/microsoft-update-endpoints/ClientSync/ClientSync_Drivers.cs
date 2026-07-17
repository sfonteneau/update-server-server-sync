// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Drivers;
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
        /// Handles driver discovery directly from SQLite. The direct store queries
        /// only metadata rows matching the device hardware IDs.
        /// </summary>
        private Task<SyncInfo> DoDriversSync(SyncUpdateParameters parameters)
        {
            if (MetadataSource == null)
            {
                throw new FaultException("Metadata source is not configured.");
            }

            var cachedDrivers = GetUpdateIdentitiesFromClientIndexes(
                parameters.CachedDriverIDs);
            var installedNonLeaf = GetInstalledNotLeafGuidsFromSyncParameters(parameters);
            var computerHardwareIds = parameters.ComputerSpec?.HardwareIDs?.ToList()
                ?? new List<Guid>();
            var driverUpdates = new List<UpdateInfo>();
            var unapprovedDriversMatched = new List<DriverUpdate>();
            var addedRevisionIds = new HashSet<int>();

            var syncResult = new SyncInfo
            {
                NewCookie = new Cookie
                {
                    Expiration = DateTime.Now.AddDays(5),
                    EncryptedData = new byte[12]
                },
                DriverSyncNotNeeded = "false",
                Truncated = false
            };

            var deviceInputs = new List<(Device Device, List<string> HardwareIds)>();
            foreach (var device in parameters.SystemSpec ?? Array.Empty<Device>())
            {
                if (device == null)
                {
                    continue;
                }

                var hardwareIdsToMatch = new List<string>(
                    device.HardwareIDs ?? Array.Empty<string>());
                if (device.CompatibleIDs != null)
                {
                    hardwareIdsToMatch.AddRange(device.CompatibleIDs);
                }

                deviceInputs.Add((device, hardwareIdsToMatch));
            }

            var projectionStore = MetadataSource as IClientSyncProjectionStore;
            var matchRecords = projectionStore?.MatchDrivers(
                deviceInputs
                    .Select(input => new ClientSyncDriverMatchRequest(input.HardwareIds))
                    .ToList(),
                computerHardwareIds,
                installedNonLeaf);

            for (var deviceIndex = 0; deviceIndex < deviceInputs.Count; deviceIndex++)
            {
                var device = deviceInputs[deviceIndex].Device;
                var hardwareIdsToMatch = deviceInputs[deviceIndex].HardwareIds;
                ClientSyncDriverMatchRecord matchRecord = null;
                DriverMatchResult driverMatchResult;
                if (projectionStore != null)
                {
                    matchRecord = matchRecords != null && deviceIndex < matchRecords.Count
                        ? matchRecords[deviceIndex]
                        : null;
                    if (matchRecord == null)
                    {
                        continue;
                    }

                    driverMatchResult = matchRecord.MatchResult;
                }
                else
                {
                    driverMatchResult = MetadataSource.MatchDriver(
                        hardwareIdsToMatch,
                        computerHardwareIds,
                        installedNonLeaf);
                    if (driverMatchResult == null)
                    {
                        continue;
                    }
                }
                if (cachedDrivers.Contains(driverMatchResult.Driver.Id)
                    || (device.installedDriver != null
                        && IsInstalledDriverBetterMatch(
                            device.installedDriver,
                            driverMatchResult,
                            hardwareIdsToMatch,
                            computerHardwareIds)))
                {
                    continue;
                }

                if (!IsDriverUpdateApproved(driverMatchResult.Driver.Id))
                {
                    unapprovedDriversMatched.Add(driverMatchResult.Driver);
                    continue;
                }

                var revisionId = matchRecord?.RevisionId
                    ?? MetadataSource.GetRevisionId(driverMatchResult.Driver.Id);
                if (revisionId < 0 || !addedRevisionIds.Add(revisionId))
                {
                    continue;
                }

                driverUpdates.Add(new UpdateInfo
                {
                    Deployment = new Deployment
                    {
                        Action = DeploymentAction.Install,
                        ID = 25000,
                        AutoDownload = "0",
                        AutoSelect = "0",
                        SupersedenceBehavior = "0",
                        IsAssigned = true,
                        LastChangeTime = "2019-08-06"
                    },
                    ID = revisionId,
                    IsLeaf = true,
                    Xml = matchRecord?.CoreXml
                        ?? GetCoreFragment(driverMatchResult.Driver.Id),
                    IsShared = false,
                    Verification = null
                });

                if (driverUpdates.Count >= MaxUpdatesInResponse)
                {
                    syncResult.Truncated = true;
                    break;
                }
            }

            if (unapprovedDriversMatched.Count > 0)
            {
                OnUnApprovedDriverUpdatesRequested?.Invoke(unapprovedDriversMatched);
            }

            syncResult.NewUpdates = driverUpdates.ToArray();
            System.Diagnostics.Trace.TraceInformation(
                $"Client driver sync: devices={parameters.SystemSpec?.Length ?? 0}, " +
                $"offered={driverUpdates.Count}, unapproved={unapprovedDriversMatched.Count}, " +
                $"truncated={syncResult.Truncated}.");
            return Task.FromResult(syncResult);
        }

        private static int GetEffectiveMatchIndex(int index)
        {
            return index < 0 ? int.MaxValue : index;
        }

        private static int IndexOfHardwareId(
            IReadOnlyList<string> hardwareIds,
            string hardwareId)
        {
            if (hardwareIds == null || hardwareId == null)
            {
                return int.MaxValue;
            }

            for (var index = 0; index < hardwareIds.Count; index++)
            {
                if (string.Equals(
                    hardwareIds[index],
                    hardwareId,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return int.MaxValue;
        }

        /// <summary>
        /// Check if the currently installed driver is a better match than the best driver we found in the updates source
        /// </summary>
        /// <param name="installedDriver">The installed driver</param>
        /// <param name="matchResult">The best driver match found in the updates source</param>
        /// <param name="hardwareIdList">The list of hardware ids for the device</param>
        /// <param name="computerHardwareIds">List of computer hardware ids</param>
        /// <returns>True if the installed driver is a better match, false otherwise</returns>
        private static bool IsInstalledDriverBetterMatch(InstalledDriver installedDriver, DriverMatchResult matchResult, List<string> hardwareIdList, List<Guid> computerHardwareIds)
        {
            if (installedDriver.MatchingComputerHWID.HasValue)
            {
                if (!matchResult.MatchedComputerHardwareId.HasValue)
                {
                    // The installed driver matched a computer HW ID while the candidate did not.
                    return true;
                }
                else
                {
                    // Both installed and matched driver matched a computer hardware id
                    // Compare them on how specific the match was
                    var installedDriverComputerMatchIndex = GetEffectiveMatchIndex(
                        computerHardwareIds.IndexOf(installedDriver.MatchingComputerHWID.Value));
                    var matchedDriverComputerMatchIndex = GetEffectiveMatchIndex(
                        computerHardwareIds.IndexOf(matchResult.MatchedComputerHardwareId.Value));

                    if (installedDriverComputerMatchIndex == matchedDriverComputerMatchIndex)
                    {
                        // The installed and matched drivers matched on the same computer hardware id;
                        // Compare them based on feature score

                        // Get the installed driver's features score
                        // A driver rank is formatted as 0xSSGGTHHH, where the value of 0x00GG0000 is the feature score
                        var installedDriverFeatureScore = (byte)((installedDriver.DriverRank & 0x00FF0000) >> 16);

                        // If the match result does not have a feature score, consider the score to be 255
                        var matchResultEffectiveFeatureScore = matchResult.MatchedFeatureScore == null ? byte.MaxValue : matchResult.MatchedFeatureScore.Score;

                        if (installedDriverFeatureScore != matchResultEffectiveFeatureScore)
                        {
                            // The installed driver is a better match if the feature score is less that the match result feature score
                            return installedDriverFeatureScore < matchResultEffectiveFeatureScore;
                        }
                    }
                    else
                    {
                        // Installed driver is better if it matched on a more specific computer hardware id (appears sooner in the list of computer hardware ids)
                        return installedDriverComputerMatchIndex < matchedDriverComputerMatchIndex;
                    }
                }
            } else if (matchResult.MatchedComputerHardwareId.HasValue)
            {
                // The candidate matched a computer hardware ID while the installed driver did not.
                return false;
            }

            // The installed and matched drivers have the same ranking so far; compare them by how specific is the hardware id match

            var installedDriverMatchIndex = IndexOfHardwareId(
                hardwareIdList,
                installedDriver.MatchingID);
            var matchResultMatchIndex = IndexOfHardwareId(
                hardwareIdList,
                matchResult.MatchedHardwareId);
            if (installedDriverMatchIndex == matchResultMatchIndex)
            {
                // Both our driver match and the installed driver matched the same HWID. Figure out the best one by comparing versions
                if (matchResult.MatchedVersion.Date == installedDriver.DriverVerDate)
                {
                    return (ulong)installedDriver.DriverVerVersion >= matchResult.MatchedVersion.Version;
                }
                else
                {
                    // The installed driver is better if it has a higher timestamp
                    return installedDriver.DriverVerDate > matchResult.MatchedVersion.Date;
                }
            }
            else
            {
                // Installed driver is better if it matched on a more specific device hardware id (appears sooner in the list of device hardware ids)
                return installedDriverMatchIndex < matchResultMatchIndex;
            }
        }
    }
}
