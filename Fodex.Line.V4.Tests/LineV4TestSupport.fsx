#if INTERACTIVE
#I "bin/Debug/net10.0/win-x64"
#load "../Fodex.Line.V4/LineV4References.fsx"
#r "Expecto.dll"
#load "../Fodex.Supervisor.Contract/LoggerAdapter.fsx"
#load "../Fodex.Supervisor.Tests/QuotationTestScope.fsx"
#else
module LineV4TestSupport
#endif
open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Data.SqlClient
open LineV4StoreAsync
open LineV4OrderedOutput
open LineV4OrderedStore
open LineV4OrderedAdmission
open LineV4Transport
module G=PulseTrade.Comm.ResourceNode.Line.LineGeneratorProtocol
module RN=PulseTrade.Comm.ResourceNode.Line.LineGeneratorStore

let withNode action=
    Fodex.Supervisor.FodexFunctions.initialize()
    let node=Akka.Actor.ActorSystem.Create("line-v4-test-"+Guid.NewGuid().ToString("N"))
    try QuotationTestScope.attach node |> ignore;action node
    finally node.Terminate().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> ignore
let wait (work:Task<'T>)=work.WaitAsync(TimeSpan.FromSeconds 30.).GetAwaiter().GetResult()
let finish (work:Task)=work.WaitAsync(TimeSpan.FromSeconds 30.).GetAwaiter().GetResult()
let ct=CancellationToken.None
let withDatabase action=QuotationTestScope.withDatabase "FodexTestsV300_" action
let settings cs:Settings=
    {ConnectionString=cs;WorkingDirectory="F:/fodex";Model="known";Effort="high";OutputDirectory="E:/deletable/fodex/1119-v4/scope"
     MaxActive=4;IdleTtl=TimeSpan.FromMinutes 1.;MaxPending=16;SupervisorAddress="/user/fodex-supervisor"
     MainAgentType="codex";NativeAdapter="exec";WaitSlice=TimeSpan.FromSeconds 1.;TurnTimeout=TimeSpan.FromSeconds 30.;NativeDefaults=None}
let scalar cs sql=
    use connection=new SqlConnection(cs)
    connection.Open()
    use command=new SqlCommand(sql,connection)
    command.ExecuteScalar()
let eventRequest (store:OrderedStore) group sender sequence text=
    let source=G.objectElement(["type",G.element "group";"groupId",G.element group] @ (sender |> Option.map(fun value -> ["userId",G.element value]) |> Option.defaultValue []))
    let raw=JsonSerializer.SerializeToUtf8Bytes {|``type``="message";source=source;message={|``type``="text";id=Guid.NewGuid().ToString("N");text=text|}|}
    let hash=RN.conversationKeyHash "test" "group" group
    let header:G.Header={RequestRoute=Guid.NewGuid();LogicalBindingId="binding";BindingRevision=1L;Scope=G.ProtocolScope.Conversation {Account="test";ConversationKeyHash=hash;GeneratorProviderId="fodex-line-v3";StorageIdentity=store.StorageIdentity}}
    let identity:G.AdmissionIdentity={RequestRoute=header.RequestRoute;ConversationKeyHash=hash;AdmissionSequence=sequence;InputFingerprintVersion=1;InputFingerprint=G.inputFingerprint "test" (G.strictUtf8.GetString raw) |> required;LogicalBindingId="binding";BindingRevision=1L}
    header,identity,raw

