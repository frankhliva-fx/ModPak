namespace ModPak

open System

/// One locale row of a MANI JSON (`modules_txts` columns).
[<CLIMutable>]
type ModPakManifestText = {
    Locale: string
    Label: string
    SectionCaption: string
}

/// `modules` row stored as the JSON prefix of a MANI chunk. Icon bytes are not in this record.
[<CLIMutable>]
type ModPakManifest = {
    Id: string
    Name: string
    Slug: string
    Order: int
    System: bool
    IsActive: bool
    Visible: bool
    IsSection: bool
    Dashboard: bool
    Locked: bool
    Icon: string
    Tone: string
    IconType: string
    MaxRoleLevel: Nullable<int>
    Texts: ModPakManifestText[]
}

/// MANI payload: manifest JSON plus the icon bytes packed after it.
type ModPakManifestPayload = {
    Record: ModPakManifest
    IconBytes: byte[]
    IconMime: string
}

module ModPakFormat =
    /// Reserved relative path skipped when packing from disk (legacy name).
    [<Literal>]
    let ManifestName = "manifest.json"

    /// Canonical on-disk extension.
    [<Literal>]
    let Extension = ".mpx"

    /// Extra extensions accepted by Open (legacy / aliases).
    let OpenPatterns = [| "*.mpx"; "*.modpak"; "*.mod"; "*.modx"; "*.m64" |]

/// EXEC technology kind (wire Technology byte).
type ExecTechnology =
    /// Wire 0 — followed by custom name bytes.
    | Custom of string
    /// Wire 1
    | Wasm
    /// Wire 2
    | Js
    /// Wire 3
    | Lua
    /// Wire 4
    | MySql
    /// Wire 5
    | PostgreSql
    /// Wire 6
    | Sqlite

module ExecTechnology =

    [<Literal>]
    let WireCustom = 0uy

    [<Literal>]
    let WireWasm = 1uy

    [<Literal>]
    let WireJs = 2uy

    [<Literal>]
    let WireLua = 3uy

    [<Literal>]
    let WireMySql = 4uy

    [<Literal>]
    let WirePostgreSql = 5uy

    [<Literal>]
    let WireSqlite = 6uy

    let toWire (technology: ExecTechnology) =
        match technology with
        | Custom _ -> WireCustom
        | Wasm -> WireWasm
        | Js -> WireJs
        | Lua -> WireLua
        | MySql -> WireMySql
        | PostgreSql -> WirePostgreSql
        | Sqlite -> WireSqlite

    let customName (technology: ExecTechnology) =
        match technology with
        | Custom name -> Some name
        | _ -> None

    let tryOfWire (code: byte) (custom: string option) =
        match code with
        | WireWasm -> Some Wasm
        | WireJs -> Some Js
        | WireLua -> Some Lua
        | WireMySql -> Some MySql
        | WirePostgreSql -> Some PostgreSql
        | WireSqlite -> Some Sqlite
        | WireCustom ->
            match custom with
            | Some name when not (String.IsNullOrWhiteSpace name) -> Some(Custom name)
            | _ -> None
        | _ -> None

    let sameKind (a: ExecTechnology) (b: ExecTechnology) =
        match a, b with
        | Custom x, Custom y ->
            String.Equals(x, y, StringComparison.OrdinalIgnoreCase)
        | Wasm, Wasm
        | Js, Js
        | Lua, Lua
        | MySql, MySql
        | PostgreSql, PostgreSql
        | Sqlite, Sqlite -> true
        | _ -> false

/// EXEC lifecycle trigger (wire ExecutionTrigger byte).
type ExecutionTrigger =
    | Manual
    | BeforeMount
    | OnMount
    | OnStart
    | BeforeUnmount
    | OnUnmount

module ExecutionTrigger =

    [<Literal>]
    let WireManual = 0uy

    [<Literal>]
    let WireBeforeMount = 1uy

    [<Literal>]
    let WireOnMount = 2uy

    [<Literal>]
    let WireOnStart = 3uy

    [<Literal>]
    let WireBeforeUnmount = 4uy

    [<Literal>]
    let WireOnUnmount = 5uy

    /// Default when packing from the app.
    let Default = OnStart

    let toWire (trigger: ExecutionTrigger) =
        match trigger with
        | Manual -> WireManual
        | BeforeMount -> WireBeforeMount
        | OnMount -> WireOnMount
        | OnStart -> WireOnStart
        | BeforeUnmount -> WireBeforeUnmount
        | OnUnmount -> WireOnUnmount

    let tryOfWire (code: byte) =
        match code with
        | WireManual -> Some Manual
        | WireBeforeMount -> Some BeforeMount
        | WireOnMount -> Some OnMount
        | WireOnStart -> Some OnStart
        | WireBeforeUnmount -> Some BeforeUnmount
        | WireOnUnmount -> Some OnUnmount
        | _ -> None

    let label (trigger: ExecutionTrigger) =
        match trigger with
        | Manual -> "Manual"
        | BeforeMount -> "BeforeMount"
        | OnMount -> "OnMount"
        | OnStart -> "OnStart"
        | BeforeUnmount -> "BeforeUnmount"
        | OnUnmount -> "OnUnmount"

    /// UI / list caption (Title Case English).
    let displayLabel (trigger: ExecutionTrigger) =
        match trigger with
        | Manual -> "Manual"
        | BeforeMount -> "Before Mount"
        | OnMount -> "On Mount"
        | OnStart -> "On Start"
        | BeforeUnmount -> "Before Unmount"
        | OnUnmount -> "On Unmount"

/// Per-FILE directory metadata. Times are Unix epoch milliseconds UTC.
type FileMeta = {
    CreatedTime: uint64
    ModifiedTime: uint64
    AccessedTime: uint64
    UnpackedSize: uint64
    /// Unix permission bits (e.g. 0o644, 0o755).
    Permissions: uint16
    /// CRC-32 of uncompressed file bytes.
    Crc32: uint32
}

type ModPakEntry = {
    RelativePath: string
    Mime: string
    Bytes: byte[]
    /// FILE payload codec (0=none, 1=lzma, 2=lz4, 3=zstd).
    Codec: byte
    Meta: FileMeta
}

/// EXEC chunk — executable payload for CMS (not a FILE).
type ModPakExec = {
    /// Optional display / logical name (NameSize=0 → None).
    Name: string option
    Technology: ExecTechnology
    /// When CMS should run this payload.
    Trigger: ExecutionTrigger
    Bytes: byte[]
    /// Payload codec; default LZMA.
    Codec: byte
}

type ModPakPackage = {
    /// Optional MANI chunk — CMS module row plus icon. 0 or 1.
    Manifest: ModPakManifestPayload option
    /// EXEC chunks (0…N).
    Exec: ModPakExec list
    Entries: ModPakEntry list
}

type ModPakError =
    | InvalidPath of string
    | MissingManifest
    | InvalidManifest of string
    | UnsupportedFormat of int
    | MissingFile of string
    | HashMismatch of path: string * expected: string * actual: string
    | BadArchive of string
    | CrcMismatch of expected: uint32 * actual: uint32
    | UnsupportedCompression of byte
    | Io of string

    override self.ToString() =
        match self with
        | InvalidPath path -> $"Invalid path: {path}"
        | MissingManifest -> "Archive is missing MANI manifest chunk."
        | InvalidManifest message -> $"Invalid manifest: {message}"
        | UnsupportedFormat version -> $"Unsupported modpak format version: {version}."
        | MissingFile path -> $"Missing packed file: {path}"
        | HashMismatch (path, expected, actual) -> $"Hash mismatch for {path}: expected {expected}, got {actual}."
        | BadArchive message -> $"Bad archive: {message}"
        | CrcMismatch (expected, actual) -> $"CRC32 mismatch: expected 0x{expected:X8}, got 0x{actual:X8}."
        | UnsupportedCompression kind -> $"Unsupported compression: {kind}."
        | Io message -> message

type ModPakResult<'a> = Result<'a, ModPakError>
