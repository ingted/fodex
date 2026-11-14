#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open System.Buffers
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts

/// Service wire DTO。來源root/正式檔名/上限不來自client；只允許服務配置的本機LINE下載區。
[<CLIMutable>]
type SharedAssetImportV100={sourcePath:string;sha256:string;bytes:int64;mediaType:string}
[<CLIMutable>]
type SharedAssetReferenceV100={assetId:string;path:string;sha256:string;bytes:int64;mediaType:string}
/// artifactRoot, allowedLineImageRoot, maxBytes, isLocalCaller, request, cancellation。
type SharedAssetInputV100=string*string*int64*bool*SharedAssetImportV100*CancellationToken

module SharedAssetFodexFunV100 =
    let version=System.Version(1,0,0)
    let target="SharedAsset.Import"
    let defaultMaxBytes=20L*1024L*1024L
    [<Literal>]
    let referenceProperty="fodexAssets"
    let comparison=if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal
    let within (root:string) (path:string)=
        let prefix=Path.TrimEndingDirectorySeparator(root)+string Path.DirectorySeparatorChar
        path.StartsWith(prefix,comparison)
    let canonical name value=
        if String.IsNullOrWhiteSpace value || not(Path.IsPathFullyQualified value) then invalidArg name "Expected a configured absolute local path"
        Path.GetFullPath value
    /// 拒絕允許區內的junction/symlink，避免lexical prefix合法卻讀到區外檔案。
    let regularPath root path=
        if not(within root path) then invalidArg "sourcePath" "Asset source is outside the configured LINE image root"
        let relative=Path.GetRelativePath(root,path)
        let mutable current=root
        if File.GetAttributes(current).HasFlag FileAttributes.ReparsePoint then invalidOp "Asset root cannot be a reparse point"
        for component in relative.Split([|Path.DirectorySeparatorChar;Path.AltDirectorySeparatorChar|],StringSplitOptions.RemoveEmptyEntries) do
            current<-Path.Combine(current,component)
            if File.GetAttributes(current).HasFlag FileAttributes.ReparsePoint then invalidOp "Asset paths cannot traverse a reparse point"

    /// 只做與現有v3 download相同的JPEG signature檢查；不轉碼，不宣稱完整圖像decode驗證。
    let jpeg (header:byte array)=header.Length>=3 && header[0]=0xFFuy && header[1]=0xD8uy && header[2]=0xFFuy
    let verifyExisting path length expectedHash ct=task {
        use source=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,81920,FileOptions.Asynchronous ||| FileOptions.SequentialScan)
        if source.Length<>length then invalidOp "SharedAssetCorrupt: existing asset length differs"
        let! digest=SHA256.HashDataAsync(source,ct)
        if not(String.Equals(Convert.ToHexString(digest),expectedHash,StringComparison.OrdinalIgnoreCase)) then invalidOp "SharedAssetCorrupt: existing asset hash differs" }

    /// 例：Import(serverArtifacts, configuredImages, 20MiB, true, JPEG receipt, ct)。
    /// 同hash的併發匯入只產生一個正式檔；client清理原下載檔不影響已接受的asset。
    /// 遠端路徑直接拒絕，不能把client的C:/解讀成service自己的C:/。
    let import ((artifactRoot,allowedRoot,maxBytes,isLocal,request,ct):SharedAssetInputV100)=task {
        let mutable temporary:string option=None
        try
            try
                ct.ThrowIfCancellationRequested()
                if not isLocal then invalidOp "UnsupportedRemoteAsset"
                if isNull(box request) then nullArg "request"
                if maxBytes<3L || request.bytes<3L || request.bytes>maxBytes then invalidArg "bytes" "Asset size exceeds the configured image limit"
                if request.mediaType<>"image/jpeg" then invalidArg "mediaType" "LINE native assets currently require image/jpeg"
                if isNull request.sha256 || request.sha256.Length<>64 || not(request.sha256 |> Seq.forall Uri.IsHexDigit) then invalidArg "sha256" "Expected a SHA-256 hex digest"
                let expected=request.sha256.ToLowerInvariant()
                let sourceRoot=canonical "allowedLineImageRoot" allowedRoot |> Path.TrimEndingDirectorySeparator
                let artifacts=canonical "artifactRoot" artifactRoot |> Path.TrimEndingDirectorySeparator
                if String.Equals(sourceRoot,artifacts,comparison) || within sourceRoot artifacts then invalidArg "artifactRoot" "Service assets must outlive and remain outside the LINE download directory"
                let sourcePath=canonical "sourcePath" request.sourcePath
                if not(File.Exists sourcePath) then invalidOp "Asset source is unavailable"
                regularPath sourceRoot sourcePath
                use source=new FileStream(sourcePath,FileMode.Open,FileAccess.Read,FileShare.Read,81920,FileOptions.Asynchronous ||| FileOptions.SequentialScan)
                // 開檔後再檢查路徑；FileShare.Read不允許本次copy期間寫入或刪除來源檔。
                regularPath sourceRoot sourcePath
                if source.Length<>request.bytes then invalidArg "bytes" "Actual image length differs from the supplied length"
                let header=Array.zeroCreate<byte> 3
                do! source.ReadExactlyAsync(header.AsMemory(),ct).AsTask()
                if not(jpeg header) then invalidOp "Asset image signature is not JPEG"
                let directory=Path.Combine(artifacts,"shared-assets")
                Directory.CreateDirectory directory |> ignore
                let destination=Path.Combine(directory,expected+".jpg") |> Path.GetFullPath
                let pending=Path.Combine(directory,Guid.NewGuid().ToString("N")+".incoming") |> Path.GetFullPath
                if not(within directory destination && within directory pending) then invalidOp "Invalid service asset destination"
                temporary<-Some pending
                use hash=IncrementalHash.CreateHash HashAlgorithmName.SHA256
                let buffer=ArrayPool<byte>.Shared.Rent 81920
                try
                    do! task {
                        use output=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,FileOptions.Asynchronous ||| FileOptions.SequentialScan)
                        hash.AppendData header
                        do! output.WriteAsync(header.AsMemory(),ct).AsTask()
                        let mutable copied=3L
                        let mutable reading=true
                        while reading do
                            let! count=source.ReadAsync(buffer.AsMemory(0,81920),ct).AsTask()
                            if count=0 then reading<-false else
                            copied<-copied+int64 count
                            if copied>maxBytes || copied>request.bytes then invalidOp "Image size changed during import"
                            hash.AppendData(buffer,0,count)
                            do! output.WriteAsync(buffer.AsMemory(0,count),ct).AsTask()
                        if copied<>request.bytes then invalidOp "Image length changed during import"
                        let actual=Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()
                        if actual<>expected then invalidArg "sha256" "Actual image hash differs from the supplied digest"
                        do! output.FlushAsync ct
                        output.Flush true }
                finally ArrayPool<byte>.Shared.Return(buffer,true)
                // pending已關閉才rename；其他匯入若搶先落檔，只驗既有內容，不覆蓋。
                try File.Move(pending,destination,false)
                with :? IOException when File.Exists destination -> ()
                do! verifyExisting destination request.bytes expected ct
                return Ok {assetId="sha256:"+expected;path=destination;sha256=expected;bytes=request.bytes;mediaType="image/jpeg"}
            with error -> return Error error
        finally
            temporary |> Option.iter(fun path -> if File.Exists path then File.Delete path) }

    let quotation:Expr<SharedAssetInputV100 -> FeasibleT<_,Task<Result<SharedAssetReferenceV100,exn>>,exn>> =
        <@ fun _ -> fun input -> Ok(import input) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
