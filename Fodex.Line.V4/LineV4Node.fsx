#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4Node
#endif
open System
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.Cluster
open Akka.Configuration
open PulseTrade.Comm.ResourceNode.Dynamic
open PulseTrade.Comm.ResourceNode.DurableProxy

let requireAvailablePort (host:string) port =
    let address=
        match IPAddress.TryParse host with
        | true,value -> value
        | _ ->
            let addresses=Dns.GetHostAddresses host
            addresses |> Array.tryFind(fun value -> value.AddressFamily=AddressFamily.InterNetwork)
                      |> Option.defaultWith(fun () -> addresses |> Array.head)
    let listener=new TcpListener(address,port)
    try
        try
            listener.Server.ExclusiveAddressUse<-true
            listener.Start()
        with :? SocketException as error ->
            raise(InvalidOperationException(sprintf "LINE v4 cannot bind %s:%d; the existing listener was left running" host port,error))
    finally listener.Stop()

/// The caller owns the sole node. Seeds are installed before construction and
/// readiness is awaited asynchronously, outside every receive callback.
let start (options:ProviderClusterOptions) (ct:CancellationToken) = task {
    requireAvailablePort options.Host options.Port
    use http=new HttpClient(Timeout=TimeSpan.FromSeconds 8.)
    let! bootstrap=ProviderCluster.resolveBootstrapAddressAsync http options
    let address=bootstrap |> Result.defaultWith(fun reason -> invalidOp("RN bootstrap failed: "+reason))
    let config=ConfigurationFactory.ParseString("akka.cluster.seed-nodes=["+System.Text.Json.JsonSerializer.Serialize(address.ToString())+"]")
                   .WithFallback(Fodex.Supervisor.ActorLogging.config)
                   .WithFallback(ActorArguFCell2StringSerialization.config())
                   .WithFallback(ConfigurationFactory.ParseString "akka.cluster.failure-detector.acceptable-heartbeat-pause=9s")
                   .WithFallback(ProviderCluster.config options)
    let node=ActorSystem.Create(DynamicCluster.ActorSystemName,config)
    try
        let cluster=Cluster.Get node
        let ready=TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        let listener=node.ActorOf(Props.Create(fun () -> ProviderClusterReadyListener(cluster,ready)),"line-v4-cluster-ready")
        try
            cluster.Join address
            do! ready.Task.WaitAsync(options.JoinTimeout,ct)
        finally node.Stop listener
        return {ActorSystem=node;Cluster=cluster}
    with error ->
        do! node.Terminate().WaitAsync(TimeSpan.FromSeconds 10.)
        return raise error }
