namespace ModPak.Scripts

open System
open System.IO
open System.Text
open ModPak

module private Paths =

    /// bin/Debug/net8.0 → ModPak.Scripts → src → apps/ModPak
    let modPakRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."))

    let scratch = Path.Combine(modPakRoot, ".modpak-scratch")

module private Mp64Demo =

    let run () =
        Directory.CreateDirectory Paths.scratch |> ignore
        let archivePath = Path.Combine(Paths.scratch, "demo.mp64")
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
        let js = Encoding.UTF8.GetBytes "console.log('onmount');\n"
        let execs: Mp64.ExecSpec list = [
            {
                Name = None
                Technology = ExecTechnology.Js
                Trigger = ExecutionTrigger.OnMount
                Codec = Codec.Lzma
                Bytes = js
            }
        ]
        printfn "Writing %s" archivePath
        match Mp64.writeFile archivePath None execs files with
        | Error error ->
            eprintfn "WRITE FAILED: %O" error
            1
        | Ok () ->
            let raw = File.ReadAllBytes archivePath
            printfn "Size: %d bytes" raw.Length
            match Mp64.readBytes raw with
            | Error error ->
                eprintfn "READ FAILED: %O" error
                1
            | Ok archive ->
                match Mp64.listFiles archive raw, Mp64.listExecs archive raw with
                | Error error, _
                | _, Error error ->
                    eprintfn "LIST FAILED: %O" error
                    1
                | Ok listed, Ok execListed ->
                    let mutable code = 0
                    for path, mime, compression, bytes, meta in listed do
                        let expected = files |> List.find (fun f -> f.Path = path)
                        printfn " - %s  mime=%s  %d B (codec=%d crc=0x%08X)" path mime bytes.Length (int compression) meta.Crc32
                        if expected.Bytes <> bytes || expected.Mime <> mime || expected.Codec <> compression then
                            eprintfn "   MISMATCH %s" path
                            code <- 1
                        elif meta.Crc32 <> Hash.crc32 bytes then
                            eprintfn "   CRC MISMATCH %s" path
                            code <- 1
                    printfn " - EXEC count=%d" execListed.Length
                    match execListed with
                    | [ e ] when e.Technology = ExecTechnology.Js && e.Bytes = js -> ()
                    | _ ->
                        eprintfn "   EXEC MISMATCH"
                        code <- 1
                    if code = 0 then printfn "OK round-trip (FILE + EXEC + lz4 toc)"
                    code

module private ModPakDemo =

    let run () =
        let payload = Path.Combine(Paths.scratch, "payload")
        let archive = Path.Combine(Paths.scratch, "demo.mpx")
        let unpacked = Path.Combine(Paths.scratch, "unpacked")
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
                            Technology = ExecTechnology.Lua
                            Trigger = ExecutionTrigger.OnMount
                            Bytes = Encoding.UTF8.GetBytes "print('onmount')\n"
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
            1
        | Ok () ->
            match Validate.archive archive with
            | Error error ->
                eprintfn "VALIDATE FAILED: %O" error
                1
            | Ok () ->
                if Directory.Exists unpacked then Directory.Delete(unpacked, true)
                match Unpack.extract archive unpacked with
                | Error error ->
                    eprintfn "UNPACK FAILED: %O" error
                    1
                | Ok () ->
                    match Unpack.read archive with
                    | Error error ->
                        eprintfn "READ FAILED: %O" error
                        1
                    | Ok package ->
                        let name, slug =
                            match package.Manifest with
                            | Some item -> item.Record.Name, item.Record.Slug
                            | None -> "(no MANI)", "-"
                        let icon = Encoding.UTF8.GetBytes "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 8 8\"></svg>"
                        match package.Manifest with
                        | Some item when
                            item.Record.Name = "Demo"
                            && item.Record.Slug = "demo"
                            && item.Record.Icon = "icon.svg"
                            && item.IconMime = "image/svg+xml"
                            && item.IconBytes = icon
                            && item.Record.Texts |> Array.exists (fun text -> text.Locale = "en" && text.Label = "Demo")
                            ->
                            printfn "OK %s / %s (%d files, EXEC=%d, MANI=%s)"
                                name
                                slug
                                package.Entries.Length
                                package.Exec.Length
                                item.Record.Name
                            0
                        | _ ->
                            eprintfn "MANI MISMATCH"
                            1

module Program =

    [<EntryPoint>]
    let main argv =
        let which =
            match argv |> Array.tryHead with
            | Some ("modpak" | "ModPak" | "pack") -> "modpak"
            | _ -> "mp64"
        printfn "ModPak.Scripts: %s  (args: mp64 | modpak)" which
        match which with
        | "modpak" -> ModPakDemo.run ()
        | _ -> Mp64Demo.run ()
