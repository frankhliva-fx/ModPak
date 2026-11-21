# MODPAK64 — Format Specification

Binary container for Kingston CMS modules (**`.mpx`** extension).

Inspired by IFF (FourCC chunks) and WAD (directory at the end of the file).

**Container version (header):** major `4`, minor `1`. The reader accepts only this pair.  
**Endianness:** **little-endian (LE)** — across the entire MODPAK64 container: all multi-byte integer fields (`uint16` / `uint32` / `uint64` / `int64`, offsets, lengths, CRCs, timestamps, permissions, etc.) are little-endian.  
**Signedness:** offsets (`Begin`, `End`, `DirOffset`) and MANI `Length` are **signed** (`int64` / `int32`). Timestamps and `UnpackedSize` are **unsigned** (`uint64`). The reader must reject negative offsets/`Length`, `End` less than or equal to `Begin`, and overflow in `Begin + Length` / `sum(End − Begin)`. MANI compressed size is practically limited by `int32` (~2 GiB − 1).  
**File extension:** `.mpx`  
**Text encoding:** UTF-8 (JSON, paths, MIME)

App / library: [README.md](README.md).

---

## 1. Overall File Structure

```text
┌──────────────────────────────────────┐
│ Header          (12 B)               │
├──────────────────────────────────────┤
│ Payload         (variable length)    │  ← compressed chunk bytes
├──────────────────────────────────────┤
│ Directory       (variable length)    │  ← LZ4 block
├──────────────────────────────────────┤
│ DirOffset       (int64, 8 B)         │
│ CRC32           (uint32, 4 B)        │
└──────────────────────────────────────┘
```

Offsets in the directory (`begin` / segments) are **absolute from the start of the file** (0 = first byte of the header).  
On disk, the directory is **LZ4** (same wire layout as FILE codec 2); after decompression, records are parsed.

---

## 2. Header (12 bytes)

| Offset | Size | Type | Description |
|--------|------|------|-------------|
| 0 | 8 | ASCII | `"MODPAK64"` |
| 8 | 2 | `uint16` LE | **Minor** (currently `0`) |
| 10 | 2 | `uint16` LE | **Major** (currently `5`) |

Order in file: **minor, then major**.

Breaking changes → increment `Major`. Additive changes → `Minor`.

---

## 3. Payload

Contiguous blob of compressed (or raw) data. No internal structure — everything is described in the directory.

Recommended write order (not mandatory):

1. `MANI` (if present)
2. `EXEC` (0…N)
3. `FILE` chunks

---

## 4. Directory

After decompression: a sequence of records.  
Each record starts with a **FourCC** (4 ASCII bytes).

### 4.1 `MANI` (LZMA blob, module + icon)

| Field | Size | Type | Description |
|-------|------|------|-------------|
| `Id` | 4 | ASCII | `MANI` |
| `Begin` | 8 | `int64` LE | absolute offset in file |
| `Length` | 4 | `int32` LE | length of **compressed** bytes |
| `IconSize` | 4 | `uint32` LE | icon length after decompression (`0` = no icon) |
| `MimeLength` | 1 | `uint8` | `0…255` |
| `Mime` | `MimeLength` | UTF-8 | icon MIME type |

Compression is **always LZMA Alone**. **No codec byte.** `MANI` is **optional** (0 or 1).

The raw blob prior to LZMA is UTF-8 JSON immediately followed by the icon bytes. After decompression, the icon is the last `IconSize` bytes, and the remainder is JSON. JSON uses camelCase and carries the columns of a `modules` row (`id`, `name`, `slug`, `order`, `system`, `isActive`, `visible`, `isSection`, `dashboard`, `locked`, `icon`, `tone`, `iconType`, `maxRoleLevel`) plus `texts[]` (`locale`, `label`, `sectionCaption`) for `en`, `sk`, `ru`, and `uz`. The form asks for label and section caption once and writes them into all four languages. The `id` field is not in the form; when writing the package, an empty `id` is populated with a new GUID. The `icon` field is a path in MinIO `icons/{slug}`, not raw bytes. `iconType` is the same MIME type as in the directory. `order` in the package is `0`; when adding the module, the store sets it to the highest `order` plus one.

### 4.1b `EXEC` (segmented executable)

**0…N** records. **Not a `FILE`** — the CMS reads it from the archive; unpacking to disk does not write it out.

`Technology` is a runtime / language enum — it tells the CMS **what** to run or interpret the payload with (WASM, JS, LUA, MySQL, PostgreSQL, SQLite, or custom).  
`ExecutionTrigger` is an enum for **when** the CMS runs the script (mount / start / unmount / manual).  
Neither is compression or MIME — those are handled by `Compression`, and for `FILE` also `Mime` / `Path`.

| Field | Size | Type | Description |
|-------|------|------|-------------|
| `Id` | 4 | ASCII | `EXEC` |
| `NameSize` | 1 | `uint8` | `0` = no name |
| `Name` | `NameSize` | UTF-8 | optional name |
| `Technology` | 1 | `uint8` | see table below |
| `CustomLen` | 1 | `uint8` | **only if** `Technology = 0`; otherwise the field **is not on disk** (0 B, not length 0) |
| `CustomName` | `CustomLen` | UTF-8 | **only if** `Technology = 0`; otherwise the field **is not on disk** |
| `ExecutionTrigger` | 1 | `uint8` | see table; **immediately after** Technology / Custom* |
| `SegmentCount` | 1 | `uint8` | `1…255` — immediately after `ExecutionTrigger` |
| `Segments` | `N × 16` | | same as `FILE` |
| `Compression` | 1 | `uint8` | always **after** segments; same codes as `FILE` (**default `1` LZMA**) |

Wire order by `Technology`:

```text
Technology = 0:  Id | NameSize | Name | Technology | CustomLen | CustomName | ExecutionTrigger | SegmentCount | Segments | Compression
Technology ≠ 0:  Id | NameSize | Name | Technology | ExecutionTrigger | SegmentCount | Segments | Compression
```

| Technology | Meaning |
|------------|---------|
| `0` | Custom — must be followed after `Technology` by `CustomLen` (at least 1) and a non-empty `CustomName` |
| `1` | Web Assembly |
| `2` | JS |
| `3` | LUA |
| `4` | MySQL |
| `5` | PostgreSQL |
| `6` | SQLite |

| ExecutionTrigger | Meaning |
|------------------|---------|
| `0` | Manual |
| `1` | BeforeMount |
| `2` | OnMount |
| `3` | OnStart (**default** when packing from the app) |
| `4` | BeforeUnmount |
| `5` | OnUnmount |

Payload: concatenate segments → **single** decompress according to `Compression` (not per-segment stream).  
Segment/compression invariants are the same as for `FILE` (§4.2).  
ModPak.App / library **does not execute** `EXEC` — it only packs and reads it. Execution / trigger selection is handled by the CMS.

### 4.2 `FILE` chunk

A directory record has two parts: **header** (FourCC → path) and **file meta trailer** (fixed 38 B).

**Directory header**

| Field | Size | Type | Description |
|-------|------|------|-------------|
| `Id` | 4 | ASCII | `"FILE"` |
| `SegmentCount` | 1 | `uint8` | `1…255` |
| `Segments` | `N × 16` | | see below |
| `Compression` | 1 | `uint8` | codec (see §5) |
| `MimeLength` | 1 | `uint8` | `0…255` |
| `Mime` | `MimeLength` | UTF-8 | MIME type (no NUL) |
| `PathLength` | 4 | `uint32` LE | path length |
| `Path` | `PathLength` | UTF-8 | relative path to CMS root |

**File meta trailer** (always **38 B**, immediately after `Path` in the same directory record — not a separate blob)

| Field | Size | Type | Description |
|-------|------|------|-------------|
| `CreatedTime` | 8 | `uint64` LE | Unix epoch **ms** UTC |
| `ModifiedTime` | 8 | `uint64` LE | Unix epoch **ms** UTC |
| `AccessedTime` | 8 | `uint64` LE | Unix epoch **ms** UTC |
| `UnpackedSize` | 8 | `uint64` LE | size after decompression |
| `Permissions` | 2 | `uint16` LE | Unix mode bits (e.g., `0o644`) |
| `Crc32` | 4 | `uint32` LE | CRC-32 of **uncompressed** bytes |

**Segment** (`16` B):

| Field | Size | Type | Description |
|-------|------|------|-------------|
| `Begin` | 8 | `int64` LE | absolute start (inclusive); must be at least `0` |
| `End` | 8 | `int64` LE | absolute end (**exclusive**); must be greater than `Begin` |

The interval is **`[Begin, End)`**.

**Multi-segment + compression** (also applies to `EXEC`):

- Segments are assembled **in order** into a single contiguous blob.
- `sum(Endᵢ − Beginᵢ)` = length of **compressed** bytes (one codec stream).
- Only then is `Compression` applied to the result — **not** a separate stream per segment.
- For LZMA Alone (§5.1), `UncompressedLength` in the stream must match the decompressed size of the entire file/exec (`UnpackedSize` for `FILE`).

`MimeLength = 0` → MIME unknown / default `application/octet-stream` when needed.

**Path rules:**

- UTF-8, separator `/`
- no leading `/`
- no `..` segments
- relative to the CMS root of that module

Example: `assets/logo.png`, `sql/schema.sql`

---

## 5. Compression

### 5.1 `MANI`

Always **LZMA Alone** (`Lzma.Net` / `xz --format=lzma` compatible — suitable for JS `lzma`).

```text
Properties         : 5 B
UncompressedLength : int64 LE
Payload            : LZMA stream
```

### 5.2 `FILE` — `Compression` field

| Value | Name | Wire format |
|-------|------|-------------|
| `0` | None | raw bytes |
| `1` | LZMA | LZMA Alone (same as §5.1) |
| `2` | LZ4 | see below |
| `3` | Zstd | Zstd frame (`ZstdSharp` Wrap/Unwrap) |

**LZ4 wire layout** (custom, not LZ4 frame):

```text
UncompressedLength : uint32 LE
Block              : raw LZ4 block (K4os.Compression.LZ4 / LZ4Codec)
```

Default when packing from the app / `Pack.write`: **LZ4** for `FILE`, **LZMA** for `EXEC`.  
Zstd and LZMA are optional for `FILE`.  
`MANI` is always LZMA.

---

## 6. Footer (12 bytes)

| Offset from end | Size | Type | Description |
|-----------------|------|------|-------------|
| −12 | 8 | `int64` LE | `DirOffset` — start of LZ4 directory |
| −4 | 4 | `uint32` LE | CRC32 |

**CRC32** (IEEE, `System.IO.Hashing.Crc32`) is computed over all bytes of the file **except** the CRC field itself, i.e., over:

```text
[0 .. fileLength - 4)
```

including header, payload, directory, and `DirOffset`.

**Directory:** bytes `[DirOffset .. fileLength-12)` are LZ4 (`uint32` unpacked length + block). After decompression, it is a sequence of FourCC records including the FILE meta trailer.

Reader:

1. verifies CRC  
2. reads `DirOffset`  
3. reads directory `[DirOffset .. fileLength - 12)`

---

## 7. Pack Notes

`EXEC` is added via the Scripts tab / API (`ModPakExec`), not via files in the tree.  
DB / lifecycle scripts are handled by `EXEC` (Technology + ExecutionTrigger), not `MANI`.

---

## 8. Reading — Recommended Algorithm

```text
1. Verify length >= 24 B (header 12 + footer 12)
2. Verify HeaderId == "MODPAK64"
3. Read minor/major and reject anything other than `1` / `4`
4. Compute CRC32(file[0..n-4)) and compare with footer
5. Read DirOffset; verify HeaderSize <= DirOffset <= n-12
6. Directory slice [DirOffset .. n-12) → LZ4 decompress; parse records
7. For MANI: slice [Begin, Begin+Length) → LZMA Alone decompress → JSON + last IconSize bytes is icon
8. For EXEC: assemble segments (sum of lengths = compressed size) → single decompress per Compression
9. For FILE: similarly assemble segments → decompress → path/mime/meta; UnpackedSize / Crc32 / LZMA UncompressedLength must match
10. Optionally validate FILE CRC / UnpackedSize against FileMeta
```

---

## 9. Example (Logical)

```text
MODPAK64 | 1 | 4
[ LZMA(MANI json+icon) ][ LZMA(JS exec) ][ LZ4(file1) ]
LZ4( MANI begin len iconSize mime | EXEC … trigger … | FILE …+meta )
DirOffset | CRC32
```