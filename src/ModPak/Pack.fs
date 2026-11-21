namespace ModPak

open System
open System.IO

module Pathing =

    let normalize (path: string) =
        path.Replace('\\', '/').Trim().TrimStart('/')

    let isSafe (path: string) =
        let normalized = normalize path
        normalized <> ""
        && not (normalized.StartsWith("../", StringComparison.Ordinal))
        && not (normalized.Contains("/../", StringComparison.Ordinal))
        && normalized <> ".."
        && not (Path.IsPathRooted path)

module Pack =

    let private isReservedPath (path: string) =
        Pathing.normalize path = ModPakFormat.ManifestName

    let private commonDirectory (paths: string list) =
        match paths with
        | [] -> ""
        | first :: rest ->
            let mutable root = Path.GetDirectoryName first
            for path in rest do
                while
                    not (String.IsNullOrEmpty root)
                    && not (
                        path.StartsWith(root + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith(root + string Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || String.Equals(Path.GetDirectoryName path, root, StringComparison.OrdinalIgnoreCase)
                    )
                    do
                    root <- Path.GetDirectoryName root
            if root = null then "" else root

    /// Resolve a package-relative path back to a full disk path among staged files.
    let tryFullPath (paths: string list) (relative: string) : string option =
        let wanted = Pathing.normalize relative
        let fulls =
            paths
            |> List.map Path.GetFullPath
            |> List.distinct
            |> List.filter File.Exists
        let root = commonDirectory fulls
        fulls
        |> List.tryFind (fun path ->
            let rel =
                if String.IsNullOrWhiteSpace root then
                    Path.GetFileName path
                else
                    Path.GetRelativePath(root, path)
                |> Pathing.normalize
            String.Equals(rel, wanted, StringComparison.OrdinalIgnoreCase))

    let entriesFromDirectory (root: string) : ModPakResult<ModPakEntry list> =
        try
            let full = Path.GetFullPath root
            if not (Directory.Exists full) then
                Error (InvalidPath root)
            else
                Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                |> Seq.map (fun path ->
                    let relative = Path.GetRelativePath(full, path) |> Pathing.normalize
                    let bytes = File.ReadAllBytes path
                    {
                        RelativePath = relative
                        Mime = Mime.ofPath relative
                        Bytes = bytes
                        Codec = Codec.Lz4
                        Meta = FileMetas.ofFilePath path bytes
                    })
                |> Seq.filter (fun entry ->
                    Pathing.isSafe entry.RelativePath && not (isReservedPath entry.RelativePath))
                |> Seq.sortBy (fun entry -> entry.RelativePath)
                |> Seq.toList
                |> Ok
        with error ->
            Error (Io error.Message)

    let entriesFromFiles (paths: string list) : ModPakResult<ModPakEntry list> =
        try
            let fulls =
                paths
                |> List.map Path.GetFullPath
                |> List.distinct
                |> List.filter File.Exists
            if fulls.IsEmpty then
                Error (InvalidPath "No files selected.")
            else
                let root = commonDirectory fulls
                fulls
                |> List.map (fun path ->
                    let relative =
                        if String.IsNullOrWhiteSpace root then
                            Path.GetFileName path
                        else
                            Path.GetRelativePath(root, path)
                        |> Pathing.normalize
                    let bytes = File.ReadAllBytes path
                    {
                        RelativePath = relative
                        Mime = Mime.ofPath relative
                        Bytes = bytes
                        Codec = Codec.Lz4
                        Meta = FileMetas.ofFilePath path bytes
                    })
                |> List.filter (fun entry ->
                    Pathing.isSafe entry.RelativePath && not (isReservedPath entry.RelativePath))
                |> List.sortBy (fun entry -> entry.RelativePath)
                |> Ok
        with error ->
            Error (Io error.Message)

    let build (entries: ModPakEntry list) : ModPakResult<ModPakPackage> =
        let unsafe = entries |> List.tryFind (fun entry -> not (Pathing.isSafe entry.RelativePath))
        match unsafe with
        | Some entry -> Error (InvalidPath entry.RelativePath)
        | None ->
            let files =
                entries
                |> List.filter (fun e -> not (isReservedPath e.RelativePath))
                |> List.sortBy (fun e -> e.RelativePath)
            Ok {
                Manifest = None
                Exec = []
                Entries = files
            }

    let private toExecSpec (item: ModPakExec) : Mp64.ExecSpec =
        {
            Name = item.Name
            Technology = item.Technology
            Trigger = item.Trigger
            Codec = if item.Codec = 0uy then Codec.Lzma else item.Codec
            Bytes = item.Bytes
        }

    let write (path: string) (package: ModPakPackage) : ModPakResult<unit> =
        let manifestChunk =
            match package.Manifest with
            | None -> None
            | Some item ->
                let icon = if isNull item.IconBytes then [||] else item.IconBytes
                let mime =
                    if icon.Length = 0 then ""
                    elif String.IsNullOrWhiteSpace item.IconMime then "application/octet-stream"
                    else item.IconMime
                Some ({
                    Json = Manifest.toBytes item.Record
                    Icon = icon
                    Mime = mime
                } : Mp64.ManifestSpec)
        let execs = package.Exec |> List.map toExecSpec
        let fileSpecs =
            package.Entries
            |> List.map (fun entry ->
                let spec: Mp64.FileSpec =
                    {
                        Path = Pathing.normalize entry.RelativePath
                        Mime = if String.IsNullOrWhiteSpace entry.Mime then Mime.ofPath entry.RelativePath else entry.Mime
                        Codec = entry.Codec
                        Bytes = entry.Bytes
                        Meta = entry.Meta
                    }
                spec)
        Mp64.writeFile path manifestChunk execs fileSpecs

    /// Apply the same codec to every FILE entry, then write.
    let writeWithCodec (codec: byte) (path: string) (package: ModPakPackage) : ModPakResult<unit> =
        write path {
            package with
                Entries = package.Entries |> List.map (fun entry -> { entry with Codec = codec })
        }

    let private withCodec (codec: byte) (entries: ModPakEntry list) =
        entries |> List.map (fun entry -> { entry with Codec = codec })

    let fromDirectoryWithCodec (codec: byte) (path: string) (output: string) =
        entriesFromDirectory path
        |> Result.map (withCodec codec)
        |> Result.bind build
        |> Result.bind (write output)

    let fromDirectory (path: string) (output: string) =
        fromDirectoryWithCodec Codec.Lz4 path output

    let fromFilesWithCodec (codec: byte) (paths: string list) (output: string) =
        entriesFromFiles paths
        |> Result.map (withCodec codec)
        |> Result.bind build
        |> Result.bind (write output)

    let fromEntries (entries: ModPakEntry list) (output: string) =
        build entries
        |> Result.bind (write output)
