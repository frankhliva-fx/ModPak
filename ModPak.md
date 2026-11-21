# MODPAK64 — špecifikácia formátu

Binárny kontajner pre Kingston CMS moduly (prípona **`.mpx`**).

Inšpirované IFF (FourCC chunky) a WAD (adresár na konci súboru).

**Verzia kontajnera (header):** major `4`, minor `1`. Reader berie len túto dvojicu.  
**Endianness:** **little-endian (LE)** — celý MODPAK64 kontajner: všetky viacbajtové celočíselné polia (`uint16` / `uint32` / `uint64` / `int64`, offsety, dĺžky, CRC, časy, permissions, …) sú little-endian  
**Znamienka:** offsety (`Begin`, `End`, `DirOffset`) a MANI `Length` sú **signed** (`int64` / `int32`). Časy a `UnpackedSize` sú **unsigned** (`uint64`). Reader musí odmietnuť záporné offsety/`Length`, `End ≤ Begin` a sčítanie `Begin + Length` / `sum(End − Begin)` s pretečením. MANI compressed size je prakticky limitovaná `int32` (~2 GiB − 1).  
**Prípona súboru:** `.mpx`  
**Kódovanie textov:** UTF-8 (JSON, cesty, MIME)

App / knižnica: [README.md](README.md).

---

## 1. Celková štruktúra súboru

```text
┌──────────────────────────────────────┐
│ Header          (12 B)               │
├──────────────────────────────────────┤
│ Payload         (premenlivá dĺžka)   │  ← skomprimované bajty lumpov
├──────────────────────────────────────┤
│ Directory       (premenlivá dĺžka)   │  ← LZ4 block
├──────────────────────────────────────┤
│ DirOffset       (int64, 8 B)         │
│ CRC32           (uint32, 4 B)        │
└──────────────────────────────────────┘
```

Offsety v directory (`begin` / segmenty) sú **absolútne od začiatku súboru** (0 = prvý bajt headeru).  
Directory je na disku **LZ4** (rovnaký wire layout ako FILE codec 2); po dekompresii sa parsujú záznamy.

---

## 2. Header (12 bajtov)

| Offset | Veľkosť | Typ | Popis |
|--------|---------|-----|--------|
| 0 | 8 | ASCII | `"MODPAK64"` |
| 8 | 2 | `uint16` LE | **Minor** (aktuálne `0`) |
| 10 | 2 | `uint16` LE | **Major** (aktuálne `5`) |

Poradie vo file: **minor, potom major**.

Breaking zmeny → zvýš `Major`. Aditívne zmeny → `Minor`.

---

## 3. Payload

Súvislý blob skomprimovaných (alebo raw) dát. Žiadna vnútorná štruktúra — všetko je v directory.

Odporúčané poradie zápisu (nie je povinné):

1. `MANI` (ak je)
2. `EXEC` (0…N)
3. `FILE` lump-y

---

## 4. Directory

Po dekompresii: sekvencia záznamov.  
Každý záznam začína **FourCC** (4 ASCII bajty).

### 4.1 `MANI` (LZMA blob, modul + ikona)

| Pole | Veľkosť | Typ | Popis |
|------|---------|-----|--------|
| `Id` | 4 | ASCII | `MANI` |
| `Begin` | 8 | `int64` LE | absolútny offset v súbore |
| `Length` | 4 | `int32` LE | dĺžka **skomprimovaných** bajtov |
| `IconSize` | 4 | `uint32` LE | dĺžka ikony po dekompresii (`0` = bez ikony) |
| `MimeLength` | 1 | `uint8` | `0…255` |
| `Mime` | `MimeLength` | UTF-8 | MIME typ ikony |

Kompresia je **vždy LZMA Alone**. **Žiadny codec byte.** `MANI` je **voliteľný** (0 alebo 1).

Surový blob pred LZMA je UTF-8 JSON a hneď za ním bajty ikony. Po dekompresii je ikona posledných `IconSize` bajtov, zvyšok je JSON. JSON je camelCase a nesie stĺpce riadku `modules` (`id`, `name`, `slug`, `order`, `system`, `isActive`, `visible`, `isSection`, `dashboard`, `locked`, `icon`, `tone`, `iconType`, `maxRoleLevel`) plus `texts[]` (`locale`, `label`, `sectionCaption`) pre `en`, `sk`, `ru` a `uz`. Formulár pýta label a section caption raz a zapíše ich do všetkých štyroch jazykov. Pole `id` vo formulári nie je; pri zápise balíka sa prázdne `id` doplní o nový GUID. Pole `icon` je cesta v MinIO `icons/{slug}`, nie bajty. `iconType` je ten istý MIME typ ako v directory. `order` v balíku je `0`; pri pridaní modulu ho obchod nastaví na najvyšší `order` plus jedna.

### 4.1b `EXEC` (segmentovaný executable)

**0…N** záznamov. **Nie je to `FILE`** — CMS ho číta z archívu; unpack na disk ho nezapisuje.

`Technology` je číselník runtime / jazyka — hovorí CMS, **čím** má payload spustiť alebo interpretovať (WASM, JS, LUA, MySQL, PostgreSQL, SQLite, alebo custom).  
`ExecutionTrigger` je číselník **kedy** CMS skript pustí (mount / start / unmount / manuálne).  
Ani jedno nie je kompresia ani MIME — to riešia `Compression` a pri `FILE` ešte `Mime` / `Path`.

| Pole | Veľkosť | Typ | Popis |
|------|---------|-----|--------|
| `Id` | 4 | ASCII | `EXEC` |
| `NameSize` | 1 | `uint8` | `0` = bez mena |
| `Name` | `NameSize` | UTF-8 | voliteľný názov |
| `Technology` | 1 | `uint8` | pozri tabuľku nižšie |
| `CustomLen` | 1 | `uint8` | **len ak** `Technology = 0`; inak pole **nie je na disku** (0 B, nie dĺžka 0) |
| `CustomName` | `CustomLen` | UTF-8 | **len ak** `Technology = 0`; inak pole **nie je na disku** |
| `ExecutionTrigger` | 1 | `uint8` | pozri tabuľku; **hneď po** Technology / Custom* |
| `SegmentCount` | 1 | `uint8` | `1…255` — hneď po `ExecutionTrigger` |
| `Segments` | `N × 16` | | rovnaké ako `FILE` |
| `Compression` | 1 | `uint8` | vždy **za** segmentmi; rovnaké kódy ako `FILE` (**default `1` LZMA**) |

Wire poradie podľa `Technology`:

```text
Technology = 0:  Id | NameSize | Name | Technology | CustomLen | CustomName | ExecutionTrigger | SegmentCount | Segments | Compression
Technology ≠ 0:  Id | NameSize | Name | Technology | ExecutionTrigger | SegmentCount | Segments | Compression
```

| Technology | Význam |
|------------|--------|
| `0` | Custom — za `Technology` musia byť `CustomLen` (≥ 1) a neprázdny `CustomName` |
| `1` | Web Assembly |
| `2` | JS |
| `3` | LUA |
| `4` | MySQL |
| `5` | PostgreSQL |
| `6` | SQLite |

| ExecutionTrigger | Význam |
|------------------|--------|
| `0` | Manual |
| `1` | BeforeMount |
| `2` | OnMount |
| `3` | OnStart (**default** pri packu z appky) |
| `4` | BeforeUnmount |
| `5` | OnUnmount |

Payload: spoj segmenty → **jeden** decompress podľa `Compression` (nie stream na segment).  
Invarianty segmentov/kompresie rovnaké ako pri `FILE` (§4.2).  
ModPak.App / knižnica EXEC **nespúšťa** — len balí a číta. Spúšťanie / výber triggeru je na CMS.

### 4.2 `FILE` chunk

Directory záznam má dve časti: **header** (FourCC → path) a **file meta trailer** (fixných 38 B).

**Directory header**

| Pole | Veľkosť | Typ | Popis |
|------|---------|-----|--------|
| `Id` | 4 | ASCII | `"FILE"` |
| `SegmentCount` | 1 | `uint8` | `1…255` |
| `Segments` | `N × 16` | | pozri nižšie |
| `Compression` | 1 | `uint8` | codec (pozri §5) |
| `MimeLength` | 1 | `uint8` | `0…255` |
| `Mime` | `MimeLength` | UTF-8 | MIME typ (bez NUL) |
| `PathLength` | 4 | `uint32` LE | dĺžka cesty |
| `Path` | `PathLength` | UTF-8 | relatívna cesta k CMS root |

**File meta trailer** (vždy **38 B**, hneď za `Path` v tom istom directory zázname — nie samostatný blob)

| Pole | Veľkosť | Typ | Popis |
|------|---------|-----|--------|
| `CreatedTime` | 8 | `uint64` LE | Unix epoch **ms** UTC |
| `ModifiedTime` | 8 | `uint64` LE | Unix epoch **ms** UTC |
| `AccessedTime` | 8 | `uint64` LE | Unix epoch **ms** UTC |
| `UnpackedSize` | 8 | `uint64` LE | veľkosť po dekompresii |
| `Permissions` | 2 | `uint16` LE | Unix mode bits (napr. `0o644`) |
| `Crc32` | 4 | `uint32` LE | CRC-32 **neskomprimovaných** bajtov |

**Segment** (`16` B):

| Pole | Veľkosť | Typ | Popis |
|------|---------|-----|--------|
| `Begin` | 8 | `int64` LE | absolútny začiatok (inclusive); musí byť `≥ 0` |
| `End` | 8 | `int64` LE | absolútny koniec (**exclusive**); musí byť `> Begin` |

Interval je **`[Begin, End)`**.

**Multi-segment + kompresia** (platí aj pre `EXEC`):

- Segmenty sa skladajú **v poradí** do jedného contiguous blobu.
- `sum(Endᵢ − Beginᵢ)` = dĺžka **skomprimovaných** bajtov (jeden codec stream).
- Až potom sa na výsledok aplikuje `Compression` — **nie** samostatný stream na každý segment.
- Pri LZMA Alone (§5.1) musí `UncompressedLength` v streame zodpovedať veľkosti po dekompresii celého súboru/execu (`UnpackedSize` pri `FILE`).

`MimeLength = 0` → MIME neznáme / default `application/octet-stream` pri potrebe.

**Path pravidlá:**

- UTF-8, oddeľovač `/`
- bez leading `/`
- bez `..` segmentov
- relatívna k CMS root daného modulu

Príklad: `assets/logo.png`, `sql/schema.sql`

---

## 5. Kompresia

### 5.1 `MANI`

Vždy **LZMA Alone** (`Lzma.Net` / `xz --format=lzma` kompatibilné — vhodné pre JS `lzma`).

```text
Properties         : 5 B
UncompressedLength : int64 LE
Payload            : LZMA stream
```

### 5.2 `FILE` — pole `Compression`

| Hodnota | Názov | Wire formát |
|---------|-------|-------------|
| `0` | None | raw bajty |
| `1` | LZMA | LZMA Alone (rovnaký ako §5.1) |
| `2` | LZ4 | pozri nižšie |
| `3` | Zstd | Zstd frame (`ZstdSharp` Wrap/Unwrap) |

**LZ4 wire layout** (vlastný, nie LZ4 frame):

```text
UncompressedLength : uint32 LE
Block              : raw LZ4 block (K4os.Compression.LZ4 / LZ4Codec)
```

Default pri packu z appky / `Pack.write`: **LZ4** pre `FILE`, **LZMA** pre `EXEC`.  
Zstd a LZMA sú voliteľné pre `FILE`.  
`MANI` vždy LZMA.

---

## 6. Footer (12 bajtov)

| Offset od konca | Veľkosť | Typ | Popis |
|-----------------|---------|-----|--------|
| −12 | 8 | `int64` LE | `DirOffset` — začiatok LZ4 directory |
| −4 | 4 | `uint32` LE | CRC32 |

**CRC32** (IEEE, `System.IO.Hashing.Crc32`) sa počíta cez všetky bajty súboru **okrem** samotného CRC poľa, teda cez:

```text
[0 .. fileLength - 4)
```

vrátane headeru, payloadu, directory a `DirOffset`.

**Directory:** bajty `[DirOffset .. fileLength-12)` sú LZ4 (`uint32` unpacked length + block). Po decompress je sekvencia FourCC záznamov vrátane FILE meta traileru.

Reader:

1. overí CRC  
2. načíta `DirOffset`  
3. číta directory `[DirOffset .. fileLength - 12)`

---

## 7. Pack poznámky

`EXEC` sa pridáva cez Scripts tab / API (`ModPakExec`), nie cez súbory v strome.  
DB / lifecycle skripty rieši `EXEC` (Technology + ExecutionTrigger), nie `MANI`.

---

## 8. Čítanie — odporúčaný algoritmus

```text
1. Over dĺžku ≥ 24 B (header 12 + footer 12)
2. Over HeaderId == "MODPAK64"
3. Načítaj minor/major a odmietni čokoľvek iné ako `1` / `4`
4. Spočítaj CRC32(file[0..n-4)) a porovnaj s footerom
5. Načítaj DirOffset; over HeaderSize ≤ DirOffset ≤ n-12
6. Directory slice [DirOffset .. n-12) → LZ4 decompress; parsuj záznamy
7. Pre MANI: slice [Begin, Begin+Length) → LZMA Alone decompress → JSON + posledných IconSize bajtov je ikona
8. Pre EXEC: zlož segmenty (súčet dĺžok = compressed size) → jeden decompress podľa Compression
9. Pre FILE: rovnako zlož segmenty → decompress → path/mime/meta; UnpackedSize / Crc32 / LZMA UncompressedLength musia sedieť
10. Voliteľne validuj FILE CRC / UnpackedSize voči FileMeta
```

---

## 9. Príklad (logický)

```text
MODPAK64 | 1 | 4
[ LZMA(MANI json+icon) ][ LZMA(JS exec) ][ LZ4(file1) ]
LZ4( MANI begin len iconSize mime | EXEC … trigger … | FILE …+meta )
DirOffset | CRC32
```
