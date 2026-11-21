namespace ModPak

open System
open System.Buffers.Binary
open System.IO
open K4os.Compression.LZ4
open LzmaNet
open ZstdSharp

/// Payload codecs for MODPAK64 FILE entries (0=none, 1=lzma, 2=lz4, 3=zstd).
/// MANI (module+icon) always uses LZMA. EXEC defaults to LZMA (codec byte on disk). FILE default is LZ4.
module Codec =

    [<Literal>]
    let None = 0uy

    /// LZMA Alone (props + int64 size + payload) — JS-friendly (`lzma` / xz --format=lzma).
    [<Literal>]
    let Lzma = 1uy

    [<Literal>]
    let Lz4 = 2uy

    /// Optional FILE codec (Zstd frame). Not used for meta chunks.
    [<Literal>]
    let Zstd = 3uy

    /// LZ4 wire layout: uint32 LE uncompressed length + raw LZ4 block.
    let private compressLz4 (data: byte[]) =
        let max = LZ4Codec.MaximumOutputSize data.Length
        let buffer = Array.zeroCreate (4 + max)
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), data.Length)
        let written = LZ4Codec.Encode(ReadOnlySpan<byte>(data), buffer.AsSpan(4))
        if written < 0 then
            failwith "LZ4 encode failed"
        Array.sub buffer 0 (4 + written)

    let private decompressLz4 (data: byte[]) =
        if data.Length < 4 then
            failwith "LZ4 payload too short"
        let size = BinaryPrimitives.ReadInt32LittleEndian(ReadOnlySpan<byte>(data, 0, 4))
        if size < 0 || size > 512 * 1024 * 1024 then
            failwith $"LZ4 uncompressed size invalid: {size}"
        let output = Array.zeroCreate size
        let decoded = LZ4Codec.Decode(ReadOnlySpan<byte>(data, 4, data.Length - 4), output.AsSpan())
        if decoded <> size then
            failwith $"LZ4 decode length mismatch: got {decoded}, expected {size}"
        output

    let private compressLzma (data: byte[]) =
        use input = new MemoryStream(data, writable = false)
        use output = new MemoryStream()
        // Dispose encoder before reading bytes — finalizes the LZMA Alone trailer.
        do
            use encoder = new LzmaAloneCompressStream(output, leaveOpen = true)
            input.CopyTo encoder
        output.ToArray()

    let private decompressLzma (data: byte[]) =
        use input = new MemoryStream(data, writable = false)
        use output = new MemoryStream()
        do
            use decoder = new LzmaAloneDecompressStream(input, leaveOpen = true)
            decoder.CopyTo output
        output.ToArray()

    let private compressZstd (data: byte[]) =
        use compressor = new Compressor()
        compressor.Wrap(ReadOnlySpan<byte>(data)).ToArray()

    let private decompressZstd (data: byte[]) =
        use decompressor = new Decompressor()
        decompressor.Unwrap(ReadOnlySpan<byte>(data)).ToArray()

    let compress (kind: byte) (data: byte[]) : ModPakResult<byte[]> =
        try
            match kind with
            | None -> Ok data
            | Lzma -> Ok (compressLzma data)
            | Lz4 -> Ok (compressLz4 data)
            | Zstd -> Ok (compressZstd data)
            | other -> Error (UnsupportedCompression other)
        with error ->
            Error (BadArchive $"compress failed: {error.Message}")

    let decompress (kind: byte) (data: byte[]) : ModPakResult<byte[]> =
        try
            match kind with
            | None -> Ok data
            | Lzma -> Ok (decompressLzma data)
            | Lz4 -> Ok (decompressLz4 data)
            | Zstd -> Ok (decompressZstd data)
            | other -> Error (UnsupportedCompression other)
        with error ->
            Error (BadArchive $"decompress failed: {error.Message}")
