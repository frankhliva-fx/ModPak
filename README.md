# ModPak

F# library and Avalonia desktop application for packaging Kingston CMS modules into the **MODPAK64** binary container (`.mpx` extension).

**Format Specification:** [Specification.md](Specification.md)

| Project | Purpose |
|---------|---------|
| `src/ModPak` | Library: pack / unpack / validate |
| `src/ModPak.App` | Desktop UI |
| `src/ModPak.Scripts` | Runnable demos + FSX in Solution Explorer |
| `scripts/*.fsx` | FSI round-trip tests |

---

## App (ModPak.App)

Toolbar: **New / Open** · **Add** · **Save / Pak / Unpak / Unpak All** · **Validate / Info** · **About**.

- **Save** — overwrites the file when an `.mpx` archive is open; prompts Save As if there is no path yet (New / staging). Enabled on dirty changes (files, manifest, scripts).
- **Pak** — always triggers Save As (choose target destination).
- **Add** — stages files from disk; the dialog path is stored separately in TOML (`Dialogs.AddFile`), independent of Open `.mpx`.
- Dual-pane layout: **left Explorer** (Windows drive, editable path, `..`, `<DIR>`, file/folder selection, tree-based Add to package with progress) · **right** tabs **Package** · **Manifest** · **Scripts**.

Startup:

- `ModPak.App.exe path\to\package.mpx` — opens the package (also works by double-clicking `.mpx` once per-user file association is configured).
- Without arguments, if `Session.LastPackage` is set in TOML and the file exists — prompts a Yes/No dialog: "Open last package?".

---

## Library (API Quick Reference)

```fsharp
open ModPak

// Pack
Pack.fromFilesWithCodec Codec.Lz4 paths outputPath
Pack.fromDirectoryWithCodec Codec.Lz4 root outputPath

// Unpack / validate
Unpack.read path
Unpack.extract archivePath targetDir
Validate.archive path
```

Low-level IO: `Mp64` module (`readBytes` / `writeBytes` / `writeFile`).

---

## App Settings

`%AppData%\Kingston\ModPak\settings.toml`

Last dialog directories and session paths are stored independently — opening an `.mpx` file does not overwrite the Add files path and vice versa.

```toml
[Dialogs]
OpenFile = "…"      # Open .mpx
AddFile = "…"       # Add files (staging)
OpenScript = "…"    # Scripts → Open
SaveFile = "…"      # Save / Pak (target .mpx)
OpenFolder = "…"    # Unpak folder

[Session]
LastPackage = "…"   # last opened / saved .mpx (full path)

[MainWindow]
WindowState = "Normal"
Width = 980
Height = 720

[Metadata]
Tab = "Package"     # Package | Manifest | Scripts
ScriptKind = "JS"   # JS | Wasm | MySql | PostgreSql | Sqlite | Lua  (template for New/Add)
ExecutionTrigger = "OnStart"    # Manual | BeforeMount | OnMount | OnStart | BeforeUnmount | OnUnmount
```

The manifest form state is not persisted in TOML. The wire chunk **MANI** represents the Manifest (JSON + icon). The format no longer uses a separate package-manifest chunk.

---

## Build / Test

```powershell
dotnet build apps/ModPak/ModPak.slnx
dotnet run --project apps/ModPak/src/ModPak.Scripts -- mp64
dotnet run --project apps/ModPak/src/ModPak.Scripts -- modpak
dotnet fsi apps/ModPak/scripts/Mp64.fsx
dotnet fsi apps/ModPak/scripts/ModPak.fsx
```

---

## Dependencies (Library)

- `Lzma.Net` — LZMA Alone (metadata + optional FILE)
- `K4os.Compression.LZ4` — LZ4 block (default for FILE)
- `ZstdSharp.Port` — optional Zstd for FILE
- `System.IO.Hashing` — CRC32
- `MimeTypesMap` — MIME type detection from file extension