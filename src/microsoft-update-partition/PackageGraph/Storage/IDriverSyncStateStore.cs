// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.ObjectModel;
using System;
using System.Collections.Generic;

namespace Microsoft.PackageGraph.Storage
{
    /// <summary>Identifies the kind of hardware identifier used by GetDriverIdList.</summary>
    public enum DriverSyncIdentifierType
    {
        /// <summary>A Plug and Play hardware or compatible identifier.</summary>
        PnpHardwareId = 1,

        /// <summary>An OEM computer hardware identifier.</summary>
        ComputerHardwareId = 2
    }

    /// <summary>
    /// One normalized hardware identifier whose upstream driver anchor is tracked
    /// independently from the other identifiers observed in the fleet.
    /// </summary>
    public sealed class DriverSyncIdentifier : IEquatable<DriverSyncIdentifier>
    {
        /// <summary>Gets the identifier kind.</summary>
        public DriverSyncIdentifierType Type { get; }

        /// <summary>Gets the normalized identifier value.</summary>
        public string Value { get; }

        /// <summary>Creates a normalized driver synchronization identifier.</summary>
        public DriverSyncIdentifier(DriverSyncIdentifierType type, string value)
        {
            Type = type;
            Value = Normalize(type, value);
        }

        /// <summary>Creates a PnP identifier.</summary>
        public static DriverSyncIdentifier Pnp(string hardwareId)
        {
            return new DriverSyncIdentifier(DriverSyncIdentifierType.PnpHardwareId, hardwareId);
        }

        /// <summary>Creates a computer hardware identifier.</summary>
        public static DriverSyncIdentifier Computer(Guid computerId)
        {
            if (computerId == Guid.Empty)
            {
                throw new ArgumentException("The computer hardware identifier cannot be empty.", nameof(computerId));
            }

            return new DriverSyncIdentifier(
                DriverSyncIdentifierType.ComputerHardwareId,
                computerId.ToString("D"));
        }

        /// <summary>Parses the value as a computer hardware identifier.</summary>
        public Guid GetComputerId()
        {
            if (Type != DriverSyncIdentifierType.ComputerHardwareId
                || !Guid.TryParse(Value, out var computerId))
            {
                throw new InvalidOperationException($"'{Value}' is not a computer hardware identifier.");
            }

            return computerId;
        }

        /// <inheritdoc />
        public bool Equals(DriverSyncIdentifier other)
        {
            return other != null
                && Type == other.Type
                && string.Equals(Value, other.Value, StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return Equals(obj as DriverSyncIdentifier);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return HashCode.Combine((int)Type, StringComparer.Ordinal.GetHashCode(Value));
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{Type}:{Value}";
        }

        private static string Normalize(DriverSyncIdentifierType type, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("A driver synchronization identifier is required.", nameof(value));
            }

            switch (type)
            {
                case DriverSyncIdentifierType.PnpHardwareId:
                    return value.Trim().ToUpperInvariant();

                case DriverSyncIdentifierType.ComputerHardwareId:
                    if (!Guid.TryParse(value.Trim(), out var computerId) || computerId == Guid.Empty)
                    {
                        throw new ArgumentException(
                            $"Invalid computer hardware identifier '{value}'.",
                            nameof(value));
                    }

                    return computerId.ToString("D").ToLowerInvariant();

                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown driver identifier type.");
            }
        }
    }

    /// <summary>Describes the last successful upstream anchor for one identifier.</summary>
    public sealed class DriverSyncIdentifierState
    {
        /// <summary>Gets the synchronization scope key.</summary>
        public string ScopeKey { get; }

        /// <summary>Gets the hardware identifier.</summary>
        public DriverSyncIdentifier Identifier { get; }

        /// <summary>Gets the opaque upstream anchor.</summary>
        public string Anchor { get; }

        /// <summary>Gets when the anchor was last promoted.</summary>
        public DateTimeOffset UpdatedAt { get; }

        /// <summary>Creates an identifier state.</summary>
        public DriverSyncIdentifierState(
            string scopeKey,
            DriverSyncIdentifier identifier,
            string anchor,
            DateTimeOffset updatedAt)
        {
            ScopeKey = scopeKey;
            Identifier = identifier;
            Anchor = anchor;
            UpdatedAt = updatedAt;
        }
    }

    /// <summary>
    /// Describes a resumable GetDriverIdList result and the identifiers whose
    /// anchors will be promoted when all returned revision metadata is durable.
    /// </summary>
    public sealed class DriverSyncCheckpointInfo
    {
        /// <summary>Gets the synchronization scope key.</summary>
        public string ScopeKey { get; }

        /// <summary>Gets the generic metadata checkpoint information.</summary>
        public SyncCheckpointInfo Checkpoint { get; }

        /// <summary>Gets the identifiers covered by the upstream request.</summary>
        public IReadOnlyList<DriverSyncIdentifier> Identifiers { get; }

        /// <summary>Creates driver checkpoint information.</summary>
        public DriverSyncCheckpointInfo(
            string scopeKey,
            SyncCheckpointInfo checkpoint,
            IReadOnlyList<DriverSyncIdentifier> identifiers)
        {
            ScopeKey = scopeKey;
            Checkpoint = checkpoint;
            Identifiers = identifiers ?? Array.Empty<DriverSyncIdentifier>();
        }
    }

    /// <summary>
    /// Optional metadata-store capability used by observed-driver synchronization.
    /// Anchors are stored per hardware identifier, while a grouped network request
    /// uses one resumable metadata checkpoint.
    /// </summary>
    public interface IDriverSyncStateStore
    {
        /// <summary>Gets saved anchors for the supplied identifiers in one exact scope.</summary>
        IReadOnlyList<DriverSyncIdentifierState> GetDriverSyncIdentifierStates(
            string scopeKey,
            IEnumerable<DriverSyncIdentifier> identifiers);

        /// <summary>Gets unfinished driver checkpoints for one exact scope.</summary>
        IReadOnlyList<DriverSyncCheckpointInfo> GetDriverSyncCheckpoints(string scopeKey);

        /// <summary>
        /// Creates a metadata checkpoint and records the identifiers that will
        /// receive AnchorTo when the checkpoint completes.
        /// </summary>
        void CreateDriverSyncCheckpoint(
            string checkpointAnchorKey,
            string scopeKey,
            string anchorFrom,
            string anchorTo,
            IReadOnlyList<IPackageIdentity> packageIdentities,
            IReadOnlyCollection<DriverSyncIdentifier> identifiers);

        /// <summary>
        /// Atomically promotes AnchorTo to every checkpoint identifier and removes
        /// the completed checkpoint. Throws while metadata items remain pending.
        /// </summary>
        void CompleteDriverSyncCheckpoint(string checkpointAnchorKey);

        /// <summary>Discards one unfinished driver checkpoint without changing stable anchors.</summary>
        void ClearDriverSyncCheckpoint(string checkpointAnchorKey);
    }
}
