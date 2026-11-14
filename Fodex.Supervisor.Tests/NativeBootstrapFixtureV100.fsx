#if !INTERACTIVE
module NativeBootstrapFixtureV100
#endif
open System
open System.IO
open System.Text
open System.Text.Json

/// 測試peer只模擬「讀artifact後回footer」，不是模型；CLI的argv仍由Argu解析。
let answer folder (prompt:string)=
    let prefix="Read the ENTIRE UTF-8 handover artifact at "
    if not(prompt.StartsWith(prefix,StringComparison.Ordinal)) then "FIXTURE_OK" else
    let marker=prompt.IndexOf(", including its final control section.",prefix.Length,StringComparison.Ordinal)
    if marker<0 then invalidOp "Fixture handover prompt has no artifact boundary"
    let path=JsonSerializer.Deserialize<string>(prompt.Substring(prefix.Length,marker-prefix.Length)) |> Path.GetFullPath
    let root=Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar)+string Path.DirectorySeparatorChar
    if not(path.StartsWith(root,if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal)) then
        invalidOp "Fixture cannot read outside its test-owned artifact root"
    let token=File.ReadAllText(path,UTF8Encoding(false,true)).TrimEnd('\n').Split('\n') |> Array.last
    if not(token.StartsWith("FODEX_HANDOVER_READY_",StringComparison.Ordinal)) || token.Length<>53 then invalidOp "Fixture artifact footer missing"
    token
