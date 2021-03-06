// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.ContractsLight;
using System.Runtime.CompilerServices;
using System.Text;
using BuildXL.Cache.ContentStore.UtilitiesCore;

namespace BuildXL.Cache.ContentStore.Hashing
{
    /// <summary>
    /// An abridged representation of a <see cref="ContentHash"/>.
    /// Format:
    /// byte[0]: HashType
    /// byte[1-11]: ContentHash[0-10]
    /// </summary>
    public readonly struct ShortHash : IEquatable<ShortHash>, IComparable<ShortHash>, IToStringConvertible
    {
        /// <summary>
        /// The length in bytes of a short hash. NOTE: This DOES include the byte for the hash type
        /// </summary>
        public const int SerializedLength = 12;

        /// <summary>
        /// The length in bytes of the hash portion of a short hash. NOTE: This does NOT include the byte for the hash type
        /// </summary>
        public const int HashLength = SerializedLength - 1;

        /// <nodoc />
        public ShortHash(ContentHash hash) : this(ToOrdinal(hash)) { }

        /// <nodoc />
        public ShortHash(FixedBytes bytes) => Value = ReadOnlyFixedBytes.FromFixedBytes(ref bytes);

        /// <nodoc />
        public ShortHash(ReadOnlyFixedBytes bytes) => Value = bytes;

        /// <nodoc />
        public ReadOnlyFixedBytes Value { get; }

        /// <nodoc />
        public byte this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Value[index];
        }

        /// <nodoc />
        public HashType HashType => (HashType)Value[0];

        /// <inheritdoc />
        public bool Equals(ShortHash other) => Value == other.Value;

        /// <inheritdoc />
        public override bool Equals(object? obj)
        {
            return obj is ShortHash hash && Equals(hash);
        }

        /// <inheritdoc />
        public override int GetHashCode() => Value.GetHashCode();

        /// <summary>
        /// Attempts to create a <see cref="ShortHash"/> instance from a given string.
        /// </summary>
        public static bool TryParse(string str, out ShortHash result)
        {
            var longHashAsString = str.PadRight(ContentHash.SerializedLength * 2 + 3, '0');
            if (ContentHash.TryParse(longHashAsString, out var longHash))
            {
                result = longHash.AsShortHash();
                return true;
            }

            result = default;
            return false;
        }

        /// <nodoc />
        public static bool operator ==(ShortHash left, ShortHash right) => left.Equals(right);

        /// <nodoc />
        public static bool operator !=(ShortHash left, ShortHash right) => !left.Equals(right);

        /// <nodoc />
        public static bool operator <(ShortHash left, ShortHash right) => left.CompareTo(right) < 0;

        /// <nodoc />
        public static bool operator >(ShortHash left, ShortHash right) => left.CompareTo(right) > 0;

        /// <nodoc />
        public static implicit operator ShortHash(ContentHash hash) => new ShortHash(hash);

        /// <nodoc />
        public byte[] ToByteArray()
        {
            return Value.ToByteArray(SerializedLength);
        }

        private static FixedBytes ToOrdinal(ContentHash hash)
        {
            var hashBytes = hash.ToFixedBytes();
            var result = new FixedBytes();

            unchecked
            {
                result[0] = (byte)hash.HashType;
            }
            
            for (int i = 0; i < HashLength; i++)
            {
                result[i + 1] = hashBytes[i];
            }

            return result;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return ToString(HashLength);
        }

        /// <summary>
        /// Gets string representation of the short hash with a given length.
        /// </summary>
        public string ToString(int hashLength)
        {
            Contract.Check(hashLength <= HashLength)?.Requires($"hashLength should be <= HashLength. hashLength={hashLength}, HashLength={HashLength}");
            return $"{HashType.Serialize()}{ContentHash.SerializedDelimiter.ToString()}{Value.ToHex(1, hashLength)}";
        }

        /// <nodoc />
        public void ToString(StringBuilder sb)
        {
            sb.Append(HashType.Serialize())
                .Append(ContentHash.SerializedDelimiter.ToString());
            Value.ToHex(sb, 1, HashLength);
        }

        /// <inheritdoc />
        public int CompareTo(ShortHash other)
        {
            return Value.CompareTo(other.Value);
        }
    }
}
