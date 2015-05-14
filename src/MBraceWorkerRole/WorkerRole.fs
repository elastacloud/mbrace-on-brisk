namespace MBraceWorkerRole

open MBrace.Azure
open MBrace.Azure.Client
open MBrace.Azure.Runtime
open Microsoft.Azure
open Microsoft.WindowsAzure
open Microsoft.WindowsAzure.ServiceRuntime
open System
open System.Diagnostics
open System.Net

type WorkerRole() =
    inherit RoleEntryPoint() 

    let getSetting = CloudConfigurationManager.GetSetting
    let setEnv key value = Environment.SetEnvironmentVariable(key, value)
    
    let config =
        { Configuration.Default
            with StorageConnectionString = getSetting "StorageConnection"
                 ServiceBusConnectionString = getSetting "ServiceBusConnection" }
    
    let mbraceSvc =
        Service(config,
            serviceId = RoleEnvironment.CurrentRoleInstance.Id,
            MaxConcurrentJobs = Environment.ProcessorCount * 8)

    do
        mbraceSvc.AttachLogger(CustomLogger(fun text -> Trace.WriteLine text))

    override __.Run() =
        mbraceSvc.StartAsync() |> Async.Start
        let mbraceEndpoint = RoleEnvironment.CurrentRoleInstance.InstanceEndpoints.["MBraceStats"].IPEndpoint
        WebHost.startHosting config (mbraceEndpoint.Address, mbraceEndpoint.Port)

    override __.OnStart() =
        let customTempLocalResourcePath = RoleEnvironment.GetLocalResource("CustomTempLocalStore").RootPath
        customTempLocalResourcePath |> setEnv "TMP"
        customTempLocalResourcePath |> setEnv "TEMP"
        ServicePointManager.DefaultConnectionLimit <- 512
        base.OnStart()