namespace ModPak

open System
open System.Security.Cryptography
open System.IO.Hashing

module Hash =

    let sha256 (bytes: byte[]) =
        Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

    let sha256Stream (stream: IO.Stream) =
        use algo = SHA256.Create()
        Convert.ToHexString(algo.ComputeHash stream).ToLowerInvariant()

    /// IEEE CRC-32 over the given span (same as System.IO.Hashing.Crc32).
    let crc32 (bytes: byte[]) =
        Crc32.HashToUInt32(ReadOnlySpan<byte>(bytes))

    let crc32Range (bytes: byte[]) (offset: int) (count: int) =
        Crc32.HashToUInt32(ReadOnlySpan<byte>(bytes, offset, count))
