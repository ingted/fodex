namespace Akka.Proc.Supervisor

open System
open System.Text.Json
open System.Text.Json.Serialization
open Akka.FSI.Contracts

module JsonCodec =
    let defaultOptions =
        lazy
            (let opts = JsonSerializerOptions(JsonSerializerDefaults.Web)
             opts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
             opts.WriteIndented <- false
             opts.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
             opts.Converters.Add(JsonStringEnumConverter())
             opts)

    let options () = defaultOptions.Value

    let explicitNullOptions =
        lazy
            (let opts = JsonSerializerOptions(options ())
             opts.DefaultIgnoreCondition <- JsonIgnoreCondition.Never
             opts)

    let serialize<'T> (value: 'T) =
        JsonSerializer.Serialize(value, options ())

    let serializeWithExplicitNulls<'T> (value: 'T) =
        JsonSerializer.Serialize(value, explicitNullOptions.Value)

    let deserialize<'T> (json: string) =
        JsonSerializer.Deserialize<'T>(json, options ())

    let execCodeFromJson (json: string) = deserialize<ExecCode> json

    let execResultToJson (result: ExecResult) = serialize result

    let errorToJson (error: ErrorInfo) = serialize error

    let serializeObj (value: obj) =
        if isNull value then
            "null"
        else
            let valueType = value.GetType()
            JsonSerializer.Serialize(value, valueType, options ())
