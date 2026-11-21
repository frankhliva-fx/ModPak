# ModPak

F# knižnica a Avalonia appka na balenie Kingston CMS modulov do binárneho kontajnera **MODPAK64** (prípona `.mpx`).

Špecifikácia formátu: [ModPak.md](ModPak.md).

| Projekt | Účel |
|---------|------|
| `src/ModPak` | Knižnica: pack / unpack / validate |
| `src/ModPak.App` | Desktop UI |
| `src/ModPak.Scripts` | Runnable demá + FSX v Solution Explorer |
| `scripts/*.fsx` | FSI round-trip testy |

---

## App (ModPak.App)

Toolbar: **New / Open** · **Add** · **Save / Pak / Unpak / Unpak All** · **Validate / Info** · **About**.

- **Save** — pri otvorenom `.mpx` prepíše súbor; bez cesty (New / staging) ponúkne Save As. Zapne sa pri dirty zmene (súbory, manifest, skripty).
- **Pak** — vždy Save As (výber cieľa).
- **Add** — staging súborov z disku; cesta dialógu je samostatná v TOML (`Dialogs.AddFile`), nezávislá od Open `.mpx`.
- Dvojpanel: **vľavo Explorer** (Windows disk, editovateľná cesta, `..`, `<DIR>`, výber súborov/priečinkov, stromové Add to package s progress) · **vpravo** taby **Package** · **Manifest** · **Scripts**.

Štart:

- `ModPak.App.exe cesta\k\baliku.mpx` — otvorí balík (aj dvojklik na `.mpx` po per-user asociácii).
- Bez argumentu, ak je v TOML `Session.LastPackage` a súbor existuje — Yes/No dialóg „Open last package?“.

---

## Knižnica (API skratka)

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

Nízkoúrovňové IO: modul `Mp64` (`readBytes` / `writeBytes` / `writeFile`).

---

## App nastavenia

`%AppData%\Kingston\ModPak\settings.toml`

Posledné priečinky dialógov a session cesty sa ukladajú samostatne — Open `.mpx` neprepíše Add files a naopak.

```toml
[Dialogs]
OpenFile = "…"      # Open .mpx
AddFile = "…"       # Add files (staging)
OpenScript = "…"    # Scripts → Open
SaveFile = "…"      # Save / Pak (cieľ .mpx)
OpenFolder = "…"    # Unpak folder

[Session]
LastPackage = "…"   # posledný otvorený / uložený .mpx (full path)

[MainWindow]
WindowState = "Normal"
Width = 980
Height = 720

[Metadata]
Tab = "Package"     # Package | Manifest | Scripts
ScriptKind = "JS"   # JS | Wasm | MySql | PostgreSql | Sqlite | Lua  (šablóna pre New/Add)
ExecutionTrigger = "OnStart"    # Manual | BeforeMount | OnMount | OnStart | BeforeUnmount | OnUnmount
```

Formulár manifestu sa do TOML neukladá. Wire chunk **MANI** = Manifest (JSON + ikona). Samostatný package-manifest chunk formát už nemá.

---

## Build / test

```powershell
dotnet build apps/ModPak/ModPak.slnx
dotnet run --project apps/ModPak/src/ModPak.Scripts -- mp64
dotnet run --project apps/ModPak/src/ModPak.Scripts -- modpak
dotnet fsi apps/ModPak/scripts/Mp64.fsx
dotnet fsi apps/ModPak/scripts/ModPak.fsx
```

---

## Závislosti (knižnica)

- `Lzma.Net` — LZMA Alone (meta + voliteľné FILE)
- `K4os.Compression.LZ4` — LZ4 block (default FILE)
- `ZstdSharp.Port` — voliteľný Zstd pre FILE
- `System.IO.Hashing` — CRC32
- `MimeTypesMap` — MIME z prípony
