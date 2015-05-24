namespace MBraceWorkerRole

open MBrace.Azure
open MBrace.Azure.Runtime
open Microsoft.WindowsAzure.ServiceRuntime
open System
open System.Diagnostics
open System.Net

type WorkerRole() =
    inherit RoleEntryPoint() 
    let setEnv key value = Environment.SetEnvironmentVariable(key, value)

    let mbraceSvc =
        Service(ConfigManagement.getConfig(),
            serviceId = RoleEnvironment.CurrentRoleInstance.Id,
            MaxConcurrentJobs = Environment.ProcessorCount * 8)

    do mbraceSvc.AttachLogger(CustomLogger(fun text -> Trace.WriteLine text))

    override __.Run() =
        mbraceSvc.StartAsync() |> Async.Start

        RoleEnvironment.Changed
        |> Event.filter ConfigManagement.configurationHasChanged
        |> Event.add (ConfigManagement.updateServiceConfiguration mbraceSvc)

        let mbraceEndpoint = RoleEnvironment.CurrentRoleInstance.InstanceEndpoints.["MBraceStats"].IPEndpoint
        WebHost.startHosting (ConfigManagement.getConfig()) (mbraceEndpoint.Address, mbraceEndpoint.Port)

    override __.OnStart() =
        let customTempLocalResourcePath = RoleEnvironment.GetLocalResource("CustomTempLocalStore").RootPath
        customTempLocalResourcePath |> setEnv "TMP"
        customTempLocalResourcePath |> setEnv "TEMP"
        ServicePointManager.DefaultConnectionLimit <- 512

        base.OnStart()