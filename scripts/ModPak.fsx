#r "nuget: Lzma.Net, 5.8.4.3"
#r "nuget: K4os.Compression.LZ4, 1.3.8"
#r "nuget: ZstdSharp.Port, 0.8.8"
#r "nuget: System.IO.Hashing, 8.0.0"
#r "nuget: MimeTypesMap, 1.0.9"
#r "../src/ModPak/bin/Debug/net8.0/ModPak.dll"

open System
open System.IO
open System.Text
open ModPak

let root = Path.Combine(__SOURCE_DIRECTORY__, "..", ".modpak-scratch")
let payload = Path.Combine(root, "payload")
let archive = Path.Combine(root, "demo.mpx")
let unpacked = Path.Combine(root, "unpacked")

Directory.CreateDirectory payload |> ignore
File.WriteAllText(Path.Combine(payload, "readme.md"), "# Demo module\n")
Directory.CreateDirectory(Path.Combine(payload, "sql")) |> ignore
File.WriteAllText(Path.Combine(payload, "sql", "schema.sql"), String.replicate 20 "create table demo (id char(36));\n")

printfn "Packing %s -> %s (lz4 + EXEC)" payload archive
let packed =
    Pack.entriesFromDirectory payload
    |> Result.map (List.map (fun e -> { e with Codec = Codec.Lz4 }))
    |> Result.bind Pack.build
    |> Result.map (fun pkg ->
        { pkg with
            Manifest =
                Some {
                    Record =
                        { Manifest.empty() with
                            Id = "00000000-0000-0000-0000-000000000001"
                            Name = "Demo"
                            Slug = "demo"
                            Icon = "icon.svg"
                            IconType = "image/svg+xml"
                            Texts =
                                Manifest.empty().Texts
                                |> Array.map (fun item ->
                                    if item.Locale = "en" then { item with Label = "Demo" } else item) }
                    IconBytes = Encoding.UTF8.GetBytes "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 8 8\"></svg>"
                    IconMime = "image/svg+xml"
                }
            Exec = [
                {
                    Name = None
                    Technology = ExecTechnology.Js
                    Trigger = ExecutionTrigger.OnMount
                    Bytes = Encoding.UTF8.GetBytes "console.log('onmount');\n"
                    Codec = Codec.Lzma
                }
                {
                    Name = None
                    Technology = ExecTechnology.Wasm
                    Trigger = ExecutionTrigger.OnStart
                    Bytes = [| 0uy; 97uy; 115uy; 109uy; 1uy; 0uy; 0uy; 0uy |]
                    Codec = Codec.Lzma
                }
                {
                    Name = None
                    Technology = ExecTechnology.MySql
                    Trigger = ExecutionTrigger.BeforeMount
                    Bytes = Encoding.UTF8.GetBytes "create table if not exists demo (id char(36));\n"
                    Codec = Codec.Lzma
                }
                {
                    Name = None
                    Technology = ExecTechnology.PostgreSql
                    Trigger = ExecutionTrigger.OnMount
                    Bytes = Encoding.UTF8.GetBytes "create table if not exists demo (id uuid);\n"
                    Codec = Codec.Lzma
                }
                {
                    Name = None
                    Technology = ExecTechnology.Sqlite
                    Trigger = ExecutionTrigger.OnUnmount
                    Bytes = Encoding.UTF8.GetBytes "create table if not exists demo (id text);\n"
                    Codec = Codec.Lzma
                }
            ]
        })
    |> Result.bind (Pack.write archive)

match packed with
| Error error ->
    eprintfn "PACK FAILED: %O" error
    Environment.ExitCode <- 1
| Ok () ->
    let raw = File.ReadAllBytes archive
    printfn "Archive header: %s  size=%d" (Text.Encoding.ASCII.GetString(raw, 0, 8)) raw.Length
    match Validate.archive archive with
    | Error error ->
        eprintfn "VALIDATE FAILED: %O" error
        Environment.ExitCode <- 1
    | Ok () ->
        if Directory.Exists unpacked then Directory.Delete(unpacked, true)
        match Unpack.extract archive unpacked with
        | Error error ->
            eprintfn "UNPACK FAILED: %O" error
            Environment.ExitCode <- 1
        | Ok () ->
            match Unpack.read archive with
            | Error error ->
                eprintfn "READ FAILED: %O" error
                Environment.ExitCode <- 1
            | Ok package ->
                printfn "OK"
                match package.Manifest with
                | Some item -> printfn "Manifest (MANI): %s / %s" item.Record.Name item.Record.Slug
                | None -> printfn "Manifest: (none)"
                printfn "EXEC: %d" package.Exec.Length
                for ex in package.Exec do
                    printfn " - technology=%A  %d B  codec=%d" ex.Technology ex.Bytes.Length (int ex.Codec)
                printfn "Files:"
                for entry in package.Entries do
                    printfn " - %s  mime=%s  %d B" entry.RelativePath entry.Mime entry.Bytes.Length
