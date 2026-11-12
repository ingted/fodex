namespace Fodex.Supervisor

open System
open System.IO
open System.Diagnostics
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts

/// Stable logical ownership; actor addresses and OS PIDs may change on restart.
/// Root is the wrapper's durable artifact folder, not a global process-name registry.
type NativeOwnerScopeV302 =
    {Root:string;WrapperId:Guid;ParticipantNumber:int64;Purpose:string}

/// Arguments are input data only. Receipts contain their canonical SHA256, never
/// prompt/stdin/environment or raw argv that could contain credentials.
type NativeOwnerCommandV302 =
    {Executable:string;WorkingDirectory:string;Arguments:string list}

/// A resource receipt is not the Codex SID. SID/history remain in their existing store.
/// JobName/ProcId bind this evidence to the common Proc Supervisor resource authority.
type NativeOwnerReceiptV302 =
    {WrapperId:Guid;ParticipantNumber:int64;Purpose:string;RunId:Guid
     HostPid:int;HostBirthUtc:DateTime;CommandHash:string;Executable:string;WorkingDirectory:string
     ProcId:string;JobName:string;ChildPid:int;ChildBirthUtc:DateTime
     Status:string;CleanupCheckedUtc:DateTime;UpdatedUtc:DateTime}

module NativeOwnerReceiptStorageV302 =
    let read path=JsonSerializer.Deserialize<NativeOwnerReceiptV302>(File.ReadAllText(path,UTF8Encoding(false,true)))
    let write path (receipt:NativeOwnerReceiptV302) =
        let temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp"
        try
            use output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)
            output.Write(JsonSerializer.SerializeToUtf8Bytes receipt)
            output.Flush(true)
            output.Dispose()
            File.Move(temporary,path,true)
        finally
            // Only this freshly generated exact temporary path is eligible for deletion.
            if File.Exists temporary then File.Delete temporary
    let alive receipt =
        try
            use host=Process.GetProcessById receipt.HostPid
            not host.HasExited && host.StartTime.ToUniversalTime()=receipt.HostBirthUtc
        with :? ArgumentException -> false

/// Keep the exclusive OS file handle for the native owner's lifetime. Dispose only
/// releases that handle; it deliberately does NOT claim that an OS job is empty.
/// Example: Complete(ProcJob.verifyOwnedZero) before Dispose after explicit Stop.
type NativeOwnerLeaseV302(path:string,fileLock:FileStream,initial:NativeOwnerReceiptV302) =
    let gate=obj()
    let mutable current=initial
    let mutable disposed=false
    let fence() =
        ObjectDisposedException.ThrowIf(disposed,typeof<NativeOwnerLeaseV302>)
        let saved=NativeOwnerReceiptStorageV302.read path
        if saved.RunId<>initial.RunId || saved.WrapperId<>initial.WrapperId || saved.ParticipantNumber<>initial.ParticipantNumber then
            invalidOp "Native ownership receipt changed while its exclusive lease was held"
    member _.Path=path
    member _.Receipt=lock gate (fun () -> current)
    /// Record the Proc-owned Started notification only after Job assignment succeeds.
    /// Exact PID/birth supplements the Job authority; it never authorizes name-based kill.
    member _.RecordStarted(procId,jobName,pid,birthUtc) = lock gate (fun () ->
        fence()
        if String.IsNullOrWhiteSpace procId || String.IsNullOrWhiteSpace jobName || pid<=0 || birthUtc=DateTime.MinValue then
            invalidArg "resource" "Started requires a Proc id, owned Job name and exact child PID/birth"
        let started={current with ProcId=procId;JobName=jobName;ChildPid=pid;ChildBirthUtc=birthUtc;Status="running";UpdatedUtc=DateTime.UtcNow}
        NativeOwnerReceiptStorageV302.write path started
        current<-started)
    /// The callback must consult the same owned Proc Job and prove no members remain.
    /// Failed Close/verification leaves the receipt byte-for-byte unchanged for retry.
    member _.Complete(verifyOwnedZero:NativeOwnerReceiptV302 -> Result<unit,exn>) = lock gate (fun () ->
        try
            fence()
            verifyOwnedZero current |> Result.bind(fun () ->
                fence()
                let now=DateTime.UtcNow
                let completed={current with Status="closed";CleanupCheckedUtc=now;UpdatedUtc=now}
                NativeOwnerReceiptStorageV302.write path completed
                current<-completed
                Ok())
        with error -> Error error)
    interface IDisposable with
        member _.Dispose() = lock gate (fun () ->
            if not disposed then disposed<-true;fileLock.Dispose())

type NativeOwnerClaimInputV302 = NativeOwnerScopeV302 * NativeOwnerCommandV302 * (NativeOwnerReceiptV302 -> Result<unit,exn>)
type NativeOwnerCompleteInputV302 = NativeOwnerLeaseV302 * (NativeOwnerReceiptV302 -> Result<unit,exn>)

[<AutoOpen>]
module NativeOwnershipLeaseFodexFunV302 =
    let version=System.Version(3,0,2)
    let targetClaim="NativeOwnership.Claim"
    let targetComplete="NativeOwnership.Complete"

    /// Serialize argv as an ARRAY, so ["a b";"c"] cannot collide with ["a";"b c"].
    /// This is exact structural comparison, with no shell concatenation or secret log.
    let commandHash (command:NativeOwnerCommandV302) =
        let canonical=JsonSerializer.SerializeToUtf8Bytes {|executable=command.Executable;workingDirectory=Path.GetFullPath command.WorkingDirectory;arguments=List.toArray command.Arguments|}
        SHA256.HashData canonical |> Convert.ToHexString

    /// Acquire before launch. A stale receipt is replaced only after exact logical
    /// scope, dead PID/birth owner, exclusive lease and common Proc cleanup all pass.
    /// New model/port settings may differ: reconciliation addresses the OLD recorded
    /// job/argv identity, not whatever happens to match a new process command line.
    let claim ((scope,command,reconcile):NativeOwnerClaimInputV302) =
        try
            if scope.WrapperId=Guid.Empty || scope.ParticipantNumber<1L || String.IsNullOrWhiteSpace scope.Purpose then
                invalidArg "scope" "A real wrapper SID, positive participant N and purpose are required"
            let folder=Path.Combine(Path.GetFullPath scope.Root,"native-ownership")
            Directory.CreateDirectory folder |> ignore
            let purpose=SHA256.HashData(Encoding.UTF8.GetBytes scope.Purpose) |> Convert.ToHexString
            let path=Path.Combine(folder,sprintf "%s.%d.%s.json" (scope.WrapperId.ToString("N")) scope.ParticipantNumber purpose)
            let fileLock=new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)
            try
                if File.Exists path then
                    let previous=NativeOwnerReceiptStorageV302.read path
                    if previous.WrapperId<>scope.WrapperId || previous.ParticipantNumber<>scope.ParticipantNumber || previous.Purpose<>scope.Purpose then
                        invalidOp "Native ownership receipt belongs to a different logical scope"
                    if previous.Status<>"closed" then
                        if NativeOwnerReceiptStorageV302.alive previous then invalidOp "Previous native owner is still alive"
                        match reconcile previous with Ok() -> () | Error error -> raise error
                use self=Process.GetCurrentProcess()
                let receipt=
                    {WrapperId=scope.WrapperId;ParticipantNumber=scope.ParticipantNumber;Purpose=scope.Purpose;RunId=Guid.NewGuid()
                     HostPid=self.Id;HostBirthUtc=self.StartTime.ToUniversalTime();CommandHash=commandHash command
                     Executable=command.Executable;WorkingDirectory=Path.GetFullPath command.WorkingDirectory
                     ProcId="";JobName="";ChildPid=0;ChildBirthUtc=DateTime.MinValue
                     Status="prepared";CleanupCheckedUtc=DateTime.MinValue;UpdatedUtc=DateTime.UtcNow}
                NativeOwnerReceiptStorageV302.write path receipt
                Ok(new NativeOwnerLeaseV302(path,fileLock,receipt))
            with error -> fileLock.Dispose();Error error
        with error -> Error error

    let complete ((lease,verify):NativeOwnerCompleteInputV302)=lease.Complete verify
    let claimQuotation:Expr<NativeOwnerClaimInputV302 -> FeasibleT<_,NativeOwnerLeaseV302,exn>> = <@ fun _ -> fun input -> claim input @>
    let completeQuotation:Expr<NativeOwnerCompleteInputV302 -> FeasibleT<_,unit,exn>> = <@ fun _ -> fun input -> complete input @>

    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare targetClaim version claimQuotation
                       FodexFunRegistry.declare targetComplete version completeQuotation|]
