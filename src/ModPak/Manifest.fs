namespace ModPak

open System
open System.Text
open System.Text.Json
open System.Text.Json.Serialization

module Manifest =

    let locales = [| "en"; "sk"; "ru"; "uz" |]

    let private options =
        let o = JsonSerializerOptions(WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
        o.DefaultIgnoreCondition <- JsonIgnoreCondition.Never
        o

    let private text (locale: string) (label: string) (caption: string) =
        {
            Locale = locale
            Label = if isNull label then "" else label
            SectionCaption = if isNull caption then "" else caption
        }

    let emptyTexts () =
        locales |> Array.map (fun locale -> text locale "" "")

    let normalizeTexts (texts: ModPakManifestText[]) =
        let source = if isNull (box texts) then [||] else texts
        locales
        |> Array.map (fun locale ->
            match source |> Array.tryFind (fun item -> not (isNull item.Locale) && item.Locale.Equals(locale, StringComparison.OrdinalIgnoreCase)) with
            | Some item -> text locale item.Label item.SectionCaption
            | None -> text locale "" "")

    let empty () : ModPakManifest =
        {
            Id = ""
            Name = ""
            Slug = ""
            Order = 0
            System = false
            IsActive = true
            Visible = true
            IsSection = true
            Dashboard = true
            Locked = false
            Icon = ""
            Tone = "stone"
            IconType = ""
            MaxRoleLevel = Nullable()
            Texts = emptyTexts ()
        }

    let private normalize (record: ModPakManifest) =
        {
            Id = if isNull record.Id then "" else record.Id
            Name = if isNull record.Name then "" else record.Name
            Slug = if isNull record.Slug then "" else record.Slug
            Order = record.Order
            System = record.System
            IsActive = record.IsActive
            Visible = record.Visible
            IsSection = record.IsSection
            Dashboard = record.Dashboard
            Locked = record.Locked
            Icon = if isNull record.Icon then "" else record.Icon
            Tone = if String.IsNullOrWhiteSpace record.Tone then "stone" else record.Tone
            IconType = if isNull record.IconType then "" else record.IconType
            MaxRoleLevel = record.MaxRoleLevel
            Texts = normalizeTexts record.Texts
        }

    let toJson (record: ModPakManifest) =
        JsonSerializer.Serialize(normalize record, options)

    let toBytes (record: ModPakManifest) =
        Encoding.UTF8.GetBytes(toJson record)

    let ofJson (text: string) : ModPakResult<ModPakManifest> =
        try
            let parsed = JsonSerializer.Deserialize<ModPakManifest>(text, options)
            if isNull (box parsed) then
                Error (InvalidManifest "empty MANI document")
            else
                Ok (normalize parsed)
        with error ->
            Error (InvalidManifest error.Message)

    let ofBytes (bytes: byte[]) =
        ofJson (Encoding.UTF8.GetString bytes)
