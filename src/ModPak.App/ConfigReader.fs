namespace ModPak.App

open System
open System.IO
open Tomlyn
open Tomlyn.Model

type ConfigReader() =
    let appData = Environment.GetFolderPath Environment.SpecialFolder.ApplicationData
    let dir = Path.Combine(appData, "Kingston", "ModPak")
    let file = Path.Combine(dir, "settings.toml")

    let same (left: string) (right: string) =
        left.Equals(right, StringComparison.OrdinalIgnoreCase)

    let named names (key: string) =
        names |> List.exists (same key)

    let read () =
        if File.Exists file then
            try
                Toml.ToModel(File.ReadAllText file)
            with _ ->
                TomlTable()
        else
            TomlTable()

    let mutable document = read ()

    let section names =
        document
        |> Seq.tryPick (fun pair ->
            match pair.Value with
            | :? TomlTable as table when named names pair.Key -> Some table
            | _ -> None)

    let existingKey (table: TomlTable) key =
        table.Keys |> Seq.tryFind (same key)

    let write (table: TomlTable) key value =
        match existingKey table key with
        | Some name -> table[name] <- value
        | None -> table[key] <- value

    let sectionForWrite name =
        match section [ name ] with
        | Some table -> table
        | None ->
            let created = TomlTable()
            document[name] <- created
            created

    member self.RemoveSection(names: string list) =
        match document.Keys |> Seq.tryFind (named names) with
        | Some name -> document.Remove(name)
        | None -> false

    member self.RemoveKey(sections: string list, keys: string list) =
        match section sections with
        | None -> false
        | Some table ->
            match table.Keys |> Seq.tryFind (named keys) with
            | Some name -> table.Remove(name)
            | None -> false

    member self.Path = file

    member self.GetString(sections, keys) =
        match section sections with
        | None -> ""
        | Some table ->
            match table |> Seq.tryPick (fun pair -> if named keys pair.Key then Some pair.Value else None) with
            | Some (:? string as value) -> value.Trim()
            | _ -> ""

    member self.SetString(sectionName, key, value: string) =
        write (sectionForWrite sectionName) key value

    member self.GetInt(sections, keys, fallback) =
        match section sections with
        | None -> fallback
        | Some table ->
            match table |> Seq.tryPick (fun pair -> if named keys pair.Key then Some pair.Value else None) with
            | Some (:? int as value) -> value
            | Some (:? int64 as value) -> int value
            | Some (:? string as value) ->
                match Int32.TryParse(value.Trim()) with
                | true, parsed -> parsed
                | false, _ -> fallback
            | _ -> fallback

    member self.SetInt(sectionName, key, value: int) =
        write (sectionForWrite sectionName) key value

    member self.GetBool(sections, keys, fallback) =
        match section sections with
        | None -> fallback
        | Some table ->
            match table |> Seq.tryPick (fun pair -> if named keys pair.Key then Some pair.Value else None) with
            | Some (:? bool as value) -> value
            | Some (:? string as value) ->
                match value.Trim().ToLowerInvariant() with
                | "true" | "1" | "yes" | "on" -> true
                | "false" | "0" | "no" | "off" -> false
                | _ -> fallback
            | _ -> fallback

    member self.SetBool(sectionName, key, value: bool) =
        write (sectionForWrite sectionName) key value

    member self.Refresh() =
        document <- read ()

    member self.Save() =
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText(file, Toml.FromModel document)
