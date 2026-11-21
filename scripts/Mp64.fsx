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
Directory.CreateDirectory root |> ignore

let archivePath = Path.Combine(root, "demo.mp64")

let payloadSql = Encoding.UTF8.GetBytes(String.replicate 40 "create table demo (id char(36));\n")

let md = Encoding.UTF8.GetBytes "# Demo\nPacked as MODPAK64.\n"
let blob = Array.init 4096 (fun i -> byte (i % 251))
let note = Encoding.UTF8.GetBytes(String.replicate 80 "optional zstd file payload\n")
let files: Mp64.FileSpec list = [
    {
        Path = "readme.md"
        Mime = "text/markdown"
        Codec = Codec.None
        Bytes = md
        Meta = FileMetas.ofBytes md
    }
    {
        Path = "sql/schema.sql"
        Mime = "application/sql"
        Codec = Codec.Lzma
        Bytes = payloadSql
        Meta = FileMetas.ofBytes payloadSql
    }
    {
        Path = "assets/blob.bin"
        Mime = "application/octet-stream"
        Codec = Codec.Lz4
        Bytes = blob
        Meta = FileMetas.ofBytes blob
    }
    {
        Path = "assets/note.txt"
        Mime = "text/plain"
        Codec = Codec.Zstd
        Bytes = note
        Meta = FileMetas.ofBytes note
    }
]

printfn "Writing %s" archivePath
match Mp64.writeFile archivePath None [] files with
| Error error ->
    eprintfn "WRITE FAILED: %O" error
    Environment.ExitCode <- 1
| Ok () ->
    let raw = File.ReadAllBytes archivePath
    printfn "Size: %d bytes" raw.Length
    printfn "Header: %s" (Encoding.ASCII.GetString(raw, 0, 8))
    let crc = BitConverter.ToUInt32(raw, raw.Length - 4)
    printfn "CRC32 : 0x%08X" crc

    match Mp64.readBytes raw with
    | Error error ->
        eprintfn "READ FAILED: %O" error
        Environment.ExitCode <- 1
    | Ok archive ->
        printfn "Version: %d.%d  entries=%d  dirOffset=%d"
            archive.Major archive.Minor archive.Entries.Length archive.DirOffset

        match Mp64.listFiles archive raw with
        | Error error ->
            eprintfn "LIST FAILED: %O" error
            Environment.ExitCode <- 1
        | Ok listed ->
            printfn "Files:"
            for path, mime, compression, bytes, meta in listed do
                let expected = files |> List.find (fun f -> f.Path = path)
                let codecName =
                    match compression with
                    | Codec.None -> "none"
                    | Codec.Lzma -> "lzma"
                    | Codec.Lz4 -> "lz4"
                    | Codec.Zstd -> "zstd"
                    | c -> string c
                printfn " - %s  mime=%s  codec=%s  %d B  crc=0x%08X  mode=%o" path mime codecName bytes.Length meta.Crc32 meta.Permissions
                if expected.Bytes <> bytes then
                    eprintfn "   DATA MISMATCH for %s" path
                    Environment.ExitCode <- 1
                elif expected.Mime <> mime || expected.Codec <> compression then
                    eprintfn "   MIME/CODEC MISMATCH for %s" path
                elif meta.Crc32 <> Hash.crc32 bytes then
                    eprintfn "   CRC MISMATCH for %s" path
                    Environment.ExitCode <- 1
                    Environment.ExitCode <- 1

            if Environment.ExitCode = 0 then
                printfn "OK round-trip (none/lzma/lz4/zstd)"
