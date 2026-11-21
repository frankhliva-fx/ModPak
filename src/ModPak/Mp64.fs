namespace ModPak

open System
open System.Buffers.Binary
open System.IO
open System.Text

module Mp64 =

    [<Literal>]
    let HeaderId = "MODPAK64"

    [<Literal>]
    let ChunkFile = "FILE"

    /// Executable payload (segments + compression).
    [<Literal>]
    let ChunkExec = "EXEC"

    /// Manifest row (JSON) and icon bytes, one LZMA blob. No codec byte.
    [<Literal>]
    let ChunkManifest = "MANI"

    [<Literal>]
    let HeaderSize = 12 // 8 + 2 + 2

    /// Footer: DirOffset (int64 LE) + CRC32 (uint32 LE) over all bytes before the CRC.
    [<Literal>]
    let FooterSize = 12 // 8 + 4

    [<Literal>]
    let CrcSize = 4

    /// Fixed FILE trailer after Path: 3×u64 times + u64 size + u16 perms + u32 crc.
    [<Literal>]
    let FileMetaSize = 38

    /// Current container. The reader accepts only this pair; older majors and chunks are not read.
    [<Literal>]
    let Minor = 0us

    [<Literal>]
    let Major = 5us

    module Compression =
        [<Literal>]
        let None = Codec.None
        [<Literal>]
        let Lzma = Codec.Lzma
        [<Literal>]
        let Lz4 = Codec.Lz4
        [<Literal>]
        let Zstd = Codec.Zstd

    type Segment = {
        Begin: int64
        End: int64
    }

    /// Directory entries.
    type Entry =
        | ManifestChunk of begin': int64 * length: int32 * iconSize: uint32 * mime: string
        | Exec of name: string option * technology: byte * customName: string option * trigger: byte * segments: Segment list * compression: byte
        | File of chunkId: string * segments: Segment list * compression: byte * mime: string * path: string * meta: FileMeta
        | Unknown of chunkId: string * raw: byte[]

    type Archive = {
        Minor: uint16
        Major: uint16
        Entries: Entry list
        Payload: byte[]
        DirOffset: int64
    }

    let private utf8 = Encoding.UTF8

    let private readExact (stream: Stream) (count: int) =
        let buffer = Array.zeroCreate count
        let mutable offset = 0
        while offset < count do
            let n = stream.Read(buffer, offset, count - offset)
            if n <= 0 then
                failwith "Unexpected end of stream"
            offset <- offset + n
        buffer

    let private readAscii (stream: Stream) (count: int) =
        let bytes = readExact stream count
        utf8.GetString bytes

    let private writeAscii (stream: Stream) (text: string) (count: int) =
        let bytes = Encoding.ASCII.GetBytes text
        if bytes.Length <> count then
            failwith $"ASCII field must be {count} bytes, got {bytes.Length}: '{text}'"
        stream.Write(bytes, 0, count)

    let gatherFromFile (fileBytes: byte[]) (segments: Segment list) (compression: byte) : ModPakResult<byte[]> =
        try
            use ms = new MemoryStream()
            for seg in segments do
                if seg.Begin < 0L || seg.End < seg.Begin || seg.End > int64 fileBytes.Length then
                    failwith $"Segment out of range: {seg.Begin}..{seg.End}"
                let len = int (seg.End - seg.Begin)
                ms.Write(fileBytes, int seg.Begin, len)
            Codec.decompress compression (ms.ToArray())
        with error ->
            Error (BadArchive error.Message)

    let private readLz4Slice (chunkId: string) (fileBytes: byte[]) (begin': int64) (length: int32) : ModPakResult<byte[]> =
        try
            if begin' < 0L || length < 0 || begin' + int64 length > int64 fileBytes.Length then
                Error (BadArchive $"{chunkId} out of range: begin={begin'} length={length}")
            else
                let compressed = Array.sub fileBytes (int begin') (int length)
                Codec.decompress Codec.Lzma compressed
        with error ->
            Error (BadArchive error.Message)

    let tryManifestBytes (archive: Archive) (fileBytes: byte[]) : ModPakResult<(byte[] * byte[] * string) option> =
        let rec walk entries =
            match entries with
            | [] -> Ok None
            | ManifestChunk(begin', length, iconSize, mime) :: _ ->
                match readLz4Slice ChunkManifest fileBytes begin' length with
                | Error error -> Error error
                | Ok raw ->
                    if iconSize > uint32 raw.Length then
                        Error (BadArchive "MANI icon is longer than the decompressed payload")
                    else
                        let size = int iconSize
                        let jsonLen = raw.Length - size
                        let json = Array.sub raw 0 jsonLen
                        let icon = if size = 0 then [||] else Array.sub raw jsonLen size
                        Ok (Some(json, icon, mime))
            | _ :: rest -> walk rest
        walk archive.Entries

    let private readFileMeta (reader: BinaryReader) : FileMeta =
        {
            CreatedTime = reader.ReadUInt64()
            ModifiedTime = reader.ReadUInt64()
            AccessedTime = reader.ReadUInt64()
            UnpackedSize = reader.ReadUInt64()
            Permissions = reader.ReadUInt16()
            Crc32 = reader.ReadUInt32()
        }

    let private writeFileMeta (writer: BinaryWriter) (meta: FileMeta) =
        writer.Write meta.CreatedTime
        writer.Write meta.ModifiedTime
        writer.Write meta.AccessedTime
        writer.Write meta.UnpackedSize
        writer.Write meta.Permissions
        writer.Write meta.Crc32

    let private readSegments (reader: BinaryReader) =
        let segmentCount = int (reader.ReadByte())
        if segmentCount < 1 || segmentCount > 255 then
            failwith $"Invalid segment count: {segmentCount}"
        [
            for _ in 1..segmentCount do
                let begin' = reader.ReadInt64()
                let end' = reader.ReadInt64()
                if end' < begin' then
                    failwith $"Invalid segment {begin'}..{end'}"
                { Begin = begin'; End = end' }
        ]

    let private writeSegments (writer: BinaryWriter) (segments: Segment list) =
        let count = segments.Length
        if count < 1 || count > 255 then
            failwith $"Segment count out of range: {count}"
        writer.Write(byte count)
        for seg in segments do
            writer.Write seg.Begin
            writer.Write seg.End

    let private readFileEntry (reader: BinaryReader) : Entry =
        let segments = readSegments reader
        let compression = reader.ReadByte()
        let mimeLen = int (reader.ReadByte())
        let mime =
            if mimeLen = 0 then ""
            else utf8.GetString(reader.ReadBytes mimeLen)
        let pathLen = reader.ReadUInt32() |> int
        if pathLen < 0 || pathLen > 1_048_576 then
            failwith $"Invalid path length: {pathLen}"
        let path = utf8.GetString(reader.ReadBytes pathLen)
        let meta = readFileMeta reader
        File(ChunkFile, segments, compression, mime, path, meta)

    let private readExecEntry (reader: BinaryReader) : Entry =
        let nameSize = int (reader.ReadByte())
        let name =
            if nameSize = 0 then None
            else Some(utf8.GetString(reader.ReadBytes nameSize))
        let technology = reader.ReadByte()
        let custom =
            if technology = ExecTechnology.WireCustom then
                let len = int (reader.ReadByte())
                if len = 0 then
                    failwith "EXEC Custom technology requires a non-empty name"
                Some(utf8.GetString(reader.ReadBytes len))
            else
                None
        let trigger = reader.ReadByte()
        let segments = readSegments reader
        let compression = reader.ReadByte()
        Exec(name, technology, custom, trigger, segments, compression)

    let private readManifestEntry (reader: BinaryReader) : Entry =
        let begin' = reader.ReadInt64()
        let length = reader.ReadInt32()
        if length < 0 then
            failwith $"Invalid {ChunkManifest} length: {length}"
        let iconSize = reader.ReadUInt32()
        let mimeLen = int (reader.ReadByte())
        let mime = if mimeLen = 0 then "" else utf8.GetString(reader.ReadBytes mimeLen)
        ManifestChunk(begin', length, iconSize, mime)

    let private writeFileEntry (writer: BinaryWriter) (segments: Segment list) (compression: byte) (mime: string) (path: string) (meta: FileMeta) =
        writeAscii writer.BaseStream ChunkFile 4
        writeSegments writer segments
        writer.Write compression
        let mimeBytes = utf8.GetBytes(if isNull mime then "" else mime)
        if mimeBytes.Length > 255 then
            failwith $"Mime too long ({mimeBytes.Length}): use at most 255 bytes"
        writer.Write(byte mimeBytes.Length)
        writer.Write mimeBytes
        let pathBytes = utf8.GetBytes path
        writer.Write(uint32 pathBytes.Length)
        writer.Write pathBytes
        writeFileMeta writer meta

    let private writeExecEntry
        (writer: BinaryWriter)
        (name: string option)
        (technology: byte)
        (customName: string option)
        (trigger: byte)
        (segments: Segment list)
        (compression: byte)
        =
        writeAscii writer.BaseStream ChunkExec 4
        let nameBytes =
            match name with
            | None -> [||]
            | Some value -> utf8.GetBytes value
        if nameBytes.Length > 255 then
            failwith $"EXEC name too long ({nameBytes.Length})"
        writer.Write(byte nameBytes.Length)
        if nameBytes.Length > 0 then
            writer.Write nameBytes
        writer.Write technology
        if technology = ExecTechnology.WireCustom then
            let customBytes =
                match customName with
                | Some value when not (String.IsNullOrWhiteSpace value) -> utf8.GetBytes value
                | _ -> failwith "EXEC Custom technology requires a non-empty name"
            if customBytes.Length > 255 then
                failwith $"EXEC custom technology name too long ({customBytes.Length})"
            writer.Write(byte customBytes.Length)
            writer.Write customBytes
        writer.Write trigger
        writeSegments writer segments
        writer.Write compression

    let private writeManifestEntry (writer: BinaryWriter) (begin': int64) (length: int32) (iconSize: uint32) (mime: string) =
        writeAscii writer.BaseStream ChunkManifest 4
        writer.Write begin'
        writer.Write length
        writer.Write iconSize
        let mimeBytes = utf8.GetBytes(if isNull mime then "" else mime)
        if mimeBytes.Length > 255 then
            failwith $"MANI mime too long ({mimeBytes.Length})"
        writer.Write(byte mimeBytes.Length)
        if mimeBytes.Length > 0 then
            writer.Write mimeBytes

    let private parseDirectory (dirBytes: byte[]) : Entry list =
        use ms = new MemoryStream(dirBytes, writable = false)
        use reader = new BinaryReader(ms, utf8, leaveOpen = true)
        let entries = ResizeArray<Entry>()
        while ms.Position < ms.Length do
            let chunkId = readAscii ms 4
            match chunkId with
            | ChunkFile -> entries.Add(readFileEntry reader)
            | ChunkExec -> entries.Add(readExecEntry reader)
            | ChunkManifest -> entries.Add(readManifestEntry reader)
            | other -> failwith $"unsupported chunk '{other}'"
        entries |> Seq.toList

    let readBytes (fileBytes: byte[]) : ModPakResult<Archive> =
        try
            if fileBytes.Length < HeaderSize + FooterSize then
                Error (BadArchive "file too small")
            else
                use ms = new MemoryStream(fileBytes, writable = false)
                use reader = new BinaryReader(ms, utf8, leaveOpen = true)

                let id = readAscii ms 8
                if id <> HeaderId then
                    Error (BadArchive $"bad header id '{id}'")
                else
                    let minor = reader.ReadUInt16()
                    let major = reader.ReadUInt16()
                    if major <> Major || minor <> Minor then
                        Error (BadArchive $"unsupported container version {major}.{minor}")
                    else
                        let crcOffset = fileBytes.Length - CrcSize
                        let expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan<byte>(fileBytes, crcOffset, 4))
                        let actualCrc = Hash.crc32Range fileBytes 0 crcOffset
                        if expectedCrc <> actualCrc then
                            Error (CrcMismatch(expectedCrc, actualCrc))
                        else
                            let dirOffset = BinaryPrimitives.ReadInt64LittleEndian(ReadOnlySpan<byte>(fileBytes, fileBytes.Length - FooterSize, 8))
                            if dirOffset < int64 HeaderSize || dirOffset > int64 (fileBytes.Length - FooterSize) then
                                Error (BadArchive $"bad dir offset {dirOffset}")
                            else
                                let dirLen = fileBytes.Length - FooterSize - int dirOffset
                                let dirSlice = Array.sub fileBytes (int dirOffset) dirLen
                                match Codec.decompress Codec.Lz4 dirSlice with
                                | Error error -> Error error
                                | Ok dirRaw ->
                                    let entries = parseDirectory dirRaw
                                    let payloadLen = int dirOffset
                                    let payload = Array.zeroCreate payloadLen
                                    Buffer.BlockCopy(fileBytes, 0, payload, 0, payloadLen)
                                    Ok {
                                        Minor = minor
                                        Major = major
                                        Entries = entries
                                        Payload = payload
                                        DirOffset = dirOffset
                                    }
        with error ->
            Error (BadArchive error.Message)

    let readFile (path: string) : ModPakResult<Archive> =
        try
            if not (File.Exists path) then
                Error (InvalidPath path)
            else
                readBytes (File.ReadAllBytes path)
        with error ->
            Error (Io error.Message)

    type FileSpec = {
        Path: string
        Mime: string
        Codec: byte
        Bytes: byte[]
        Meta: FileMeta
    }

    type ExecSpec = {
        Name: string option
        Technology: ExecTechnology
        Trigger: ExecutionTrigger
        Codec: byte
        Bytes: byte[]
    }

    type ManifestSpec = {
        Json: byte[]
        Icon: byte[]
        Mime: string
    }

    type private Planned =
        | PlannedManifest of begin': int64 * length: int32 * iconSize: uint32 * mime: string
        | PlannedExec of name: string option * technology: byte * customName: string option * trigger: byte * codec: byte * segments: Segment list
        | PlannedFile of path: string * mime: string * codec: byte * meta: FileMeta * segments: Segment list

    let private appendManifest (ms: MemoryStream) (writer: BinaryWriter) (planned: ResizeArray<Planned>) (spec: ManifestSpec) =
        let json = if isNull spec.Json then [||] else spec.Json
        let icon = if isNull spec.Icon then [||] else spec.Icon
        let raw = Array.append json icon
        match Codec.compress Codec.Lzma raw with
        | Error error -> failwith (string error)
        | Ok compressed ->
            if int64 compressed.Length > int64 Int32.MaxValue then
                failwith "MANI compressed payload too large for int32 length"
            let begin' = ms.Position
            writer.Write compressed
            let mime = if icon.Length = 0 || isNull spec.Mime then "" else spec.Mime
            planned.Add(PlannedManifest(begin', int32 compressed.Length, uint32 icon.Length, mime))

    let private appendPayload
        (ms: MemoryStream)
        (writer: BinaryWriter)
        (codec: byte)
        (raw: byte[])
        =
        match Codec.compress codec raw with
        | Error error -> Error error
        | Ok data ->
            let begin' = ms.Position
            writer.Write data
            let end' = ms.Position
            Ok [ { Begin = begin'; End = end' } ]

    /// Build archive (major 4 minor 0): MANI (module+icon) + EXEC + FILE + LZ4 directory + CRC footer.
    let writeBytes (manifest: ManifestSpec option) (execs: ExecSpec list) (files: FileSpec list) : ModPakResult<byte[]> =
        try
            use ms = new MemoryStream()
            use writer = new BinaryWriter(ms, utf8, leaveOpen = true)

            writeAscii ms HeaderId 8
            writer.Write Minor
            writer.Write Major

            let planned = ResizeArray<Planned>()

            match manifest with
            | Some spec -> appendManifest ms writer planned spec
            | None -> ()

            let rec writeExec remaining =
                match remaining with
                | [] -> Ok ()
                | (spec: ExecSpec) :: rest ->
                    match appendPayload ms writer spec.Codec spec.Bytes with
                    | Error error -> Error error
                    | Ok segments ->
                        planned.Add(
                            PlannedExec(
                                spec.Name,
                                ExecTechnology.toWire spec.Technology,
                                ExecTechnology.customName spec.Technology,
                                ExecutionTrigger.toWire spec.Trigger,
                                spec.Codec,
                                segments))
                        writeExec rest

            match writeExec execs with
            | Error error -> Error error
            | Ok () ->
                let rec writeFiles remaining =
                    match remaining with
                    | [] -> Ok ()
                    | (file: FileSpec) :: rest ->
                        let mime = if String.IsNullOrWhiteSpace file.Mime then "" else file.Mime
                        let meta =
                            { file.Meta with
                                UnpackedSize = uint64 file.Bytes.Length
                                Crc32 = Hash.crc32 file.Bytes
                            }
                        match appendPayload ms writer file.Codec file.Bytes with
                        | Error error -> Error error
                        | Ok segments ->
                            planned.Add(PlannedFile(file.Path, mime, file.Codec, meta, segments))
                            writeFiles rest

                match writeFiles files with
                | Error error -> Error error
                | Ok () ->
                    use dirMs = new MemoryStream()
                    use dirWriter = new BinaryWriter(dirMs, utf8, leaveOpen = true)
                    for item in planned do
                        match item with
                        | PlannedManifest(begin', length, iconSize, mime) ->
                            writeManifestEntry dirWriter begin' length iconSize mime
                        | PlannedExec(name, technology, custom, trigger, codec, segments) ->
                            writeExecEntry dirWriter name technology custom trigger segments codec
                        | PlannedFile(path, mime, codec, meta, segments) ->
                            writeFileEntry dirWriter segments codec mime path meta
                    dirWriter.Flush()
                    let dirRaw = dirMs.ToArray()
                    match Codec.compress Codec.Lz4 dirRaw with
                    | Error error -> Error error
                    | Ok dirCompressed ->
                        let dirOffset = ms.Position
                        writer.Write dirCompressed
                        writer.Write dirOffset
                        writer.Flush()
                        let prefix = ms.ToArray()
                        let crc = Hash.crc32 prefix
                        writer.Write crc
                        writer.Flush()
                        Ok (ms.ToArray())
        with error ->
            Error (BadArchive error.Message)

    let writeFile (path: string) (manifest: ManifestSpec option) (execs: ExecSpec list) (files: FileSpec list) : ModPakResult<unit> =
        match writeBytes manifest execs files with
        | Error error -> Error error
        | Ok bytes ->
            try
                let target = Path.GetFullPath path
                let dir = Path.GetDirectoryName target
                if not (String.IsNullOrWhiteSpace dir) then
                    Directory.CreateDirectory dir |> ignore
                File.WriteAllBytes(target, bytes)
                Ok ()
            with error ->
                Error (Io error.Message)

    let listFiles (archive: Archive) (fileBytes: byte[]) : ModPakResult<(string * string * byte * byte[] * FileMeta) list> =
        let rec walk (entries: Entry list) (acc: (string * string * byte * byte[] * FileMeta) list) =
            match entries with
            | [] -> Ok (List.rev acc)
            | ManifestChunk _ :: rest -> walk rest acc
            | Exec _ :: rest -> walk rest acc
            | File(_, segments, compression, mime, path, meta) :: rest ->
                match gatherFromFile fileBytes segments compression with
                | Error error -> Error error
                | Ok data ->
                    let meta =
                        if meta.Crc32 = 0u && meta.UnpackedSize = 0UL then
                            { meta with
                                UnpackedSize = uint64 data.Length
                                Crc32 = Hash.crc32 data
                            }
                        else
                            meta
                    walk rest ((path, mime, compression, data, meta) :: acc)
            | Unknown _ :: rest -> walk rest acc
        walk archive.Entries []

    let listExecs (archive: Archive) (fileBytes: byte[]) : ModPakResult<ModPakExec list> =
        let rec walk (entries: Entry list) (acc: ModPakExec list) =
            match entries with
            | [] -> Ok (List.rev acc)
            | Exec(name, technologyByte, custom, triggerByte, segments, compression) :: rest ->
                match ExecTechnology.tryOfWire technologyByte custom with
                | None -> Error (BadArchive $"Invalid EXEC technology byte {technologyByte}")
                | Some technology ->
                    match ExecutionTrigger.tryOfWire triggerByte with
                    | None -> Error (BadArchive $"Invalid EXEC ExecutionTrigger byte {triggerByte}")
                    | Some trigger ->
                        match gatherFromFile fileBytes segments compression with
                        | Error error -> Error error
                        | Ok data ->
                            walk rest ({
                                Name = name
                                Technology = technology
                                Trigger = trigger
                                Bytes = data
                                Codec = compression
                            } :: acc)
            | ManifestChunk _ :: rest -> walk rest acc
            | _ :: rest -> walk rest acc
        walk archive.Entries []
