namespace ModPak

open System
open System.IO

module Unpack =

    let private writeBytes (root: string) (relative: string) (bytes: byte[]) (meta: FileMeta option) =
        let path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))
        let dir = Path.GetDirectoryName path
        if not (String.IsNullOrWhiteSpace dir) then
            Directory.CreateDirectory dir |> ignore
        File.WriteAllBytes(path, bytes)
        match meta with
        | Some m -> FileMetas.applyToDisk path m
        | None -> ()

    let read (path: string) : ModPakResult<ModPakPackage> =
        try
            if not (File.Exists path) then
                Error (InvalidPath path)
            else
                let fileBytes = File.ReadAllBytes path
                match Mp64.readBytes fileBytes with
                | Error error -> Error error
                | Ok archive ->
                    match Mp64.listFiles archive fileBytes with
                    | Error error -> Error error
                    | Ok files ->
                        match Mp64.listExecs archive fileBytes with
                        | Error error -> Error error
                        | Ok execs ->
                            match Mp64.tryManifestBytes archive fileBytes with
                            | Error error -> Error error
                            | Ok manifestBytes ->
                                let manifestResult =
                                    match manifestBytes with
                                    | None -> Ok None
                                    | Some (json, icon, mime) ->
                                        Manifest.ofBytes json
                                        |> Result.map (fun record ->
                                            Some {
                                                Record = record
                                                IconBytes = icon
                                                IconMime = mime
                                            })
                                match manifestResult with
                                | Error error -> Error error
                                | Ok mani ->
                                    let entries =
                                        files
                                        |> List.map (fun (p, mime, compression, bytes, meta) ->
                                            {
                                                RelativePath = Pathing.normalize p
                                                Mime = mime
                                                Bytes = bytes
                                                Codec = compression
                                                Meta = meta
                                            })
                                        |> List.sortBy (fun entry -> entry.RelativePath)
                                    Ok {
                                        Manifest = mani
                                        Exec = execs
                                        Entries = entries
                                    }
        with error ->
            Error (Io error.Message)

    let private writeSafe (root: string) (relative: string) (bytes: byte[]) (meta: FileMeta option) =
        if not (Pathing.isSafe relative) then
            failwith $"Unsafe path: {relative}"
        writeBytes root relative bytes meta

    let private tryWriteSelected (package: ModPakPackage) (root: string) (path: string) =
        let relative = Pathing.normalize path
        match
            package.Entries
            |> List.tryFind (fun entry ->
                String.Equals(Pathing.normalize entry.RelativePath, relative, StringComparison.OrdinalIgnoreCase))
        with
        | Some entry -> writeSafe root entry.RelativePath entry.Bytes (Some entry.Meta); Ok ()
        | None -> Error (Io $"Missing {relative}")

    let toDirectory (package: ModPakPackage) (target: string) : ModPakResult<unit> =
        try
            let root = Path.GetFullPath target
            Directory.CreateDirectory root |> ignore
            // EXEC / MANI chunks are not files — CMS reads them from the archive.
            for entry in package.Entries do
                writeSafe root entry.RelativePath entry.Bytes (Some entry.Meta)
            Ok ()
        with error ->
            Error (Io error.Message)

    let selectedToDirectory (package: ModPakPackage) (paths: string list) (target: string) : ModPakResult<unit> =
        try
            if paths.IsEmpty then
                Error (Io "No files selected.")
            else
                let root = Path.GetFullPath target
                Directory.CreateDirectory root |> ignore
                let rec writeAll remaining =
                    match remaining with
                    | [] -> Ok ()
                    | path :: rest ->
                        match tryWriteSelected package root path with
                        | Error error -> Error error
                        | Ok () -> writeAll rest
                writeAll paths
        with error ->
            Error (Io error.Message)

    let extract (archivePath: string) (target: string) =
        read archivePath |> Result.bind (fun package -> toDirectory package target)
