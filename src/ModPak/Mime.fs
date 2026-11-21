namespace ModPak

open System
open HeyRed.Mime

module Mime =

    let ofPath (path: string) =
        let mime = MimeTypesMap.GetMimeType path
        if String.IsNullOrWhiteSpace mime then "application/octet-stream" else mime
