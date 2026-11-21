namespace ModPak

open System

module Validate =

    let package (package: ModPakPackage) : ModPakResult<unit> =
        match package.Manifest with
        | None -> Ok ()
        | Some item when String.IsNullOrWhiteSpace item.Record.Name ->
            Error (InvalidManifest "manifest name is required")
        | Some item when not (isNull item.IconBytes) && item.IconBytes.Length > 0 && String.IsNullOrWhiteSpace item.IconMime ->
            Error (InvalidManifest "manifest icon mime is required")
        | Some _ -> Ok ()

    let private validateEntryMeta (entries: ModPakEntry list) =
        let rec walk remaining =
            match remaining with
            | [] -> Ok ()
            | entry :: rest ->
                if entry.Meta.Crc32 <> 0u && entry.Meta.Crc32 <> Hash.crc32 entry.Bytes then
                    Error (BadArchive $"FILE CRC mismatch for {entry.RelativePath}")
                elif entry.Meta.UnpackedSize <> 0UL && entry.Meta.UnpackedSize <> uint64 entry.Bytes.Length then
                    Error (BadArchive $"FILE unpacked size mismatch for {entry.RelativePath}")
                else
                    walk rest
        walk entries

    let archive (path: string) =
        Unpack.read path
        |> Result.bind (fun pkg ->
            match validateEntryMeta pkg.Entries with
            | Error e -> Error e
            | Ok () -> package pkg)
