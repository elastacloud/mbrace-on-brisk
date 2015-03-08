namespace MBraceWorkerRole

open MBrace.Azure
open MBrace.Azure.Runtime
open MBrace.Azure.Runtime.Common
open Microsoft.WindowsAzure
open Microsoft.WindowsAzure.ServiceRuntime
open System
open System.Diagnostics
open System.Net

type WorkerRole() =
    inherit RoleEntryPoint() 

    let getSetting = CloudConfigurationManager.GetSetting
    let setEnv key value = Environment.SetEnvironmentVariable(key, value)
    let maxId = int UInt16.MaxValue

    let svc =
        lazy
            let config =
                Configuration
                    .Default
                    .WithStorageConnectionString(getSetting "StorageConnection")
                    .WithServiceBusConnectionString(getSetting "ServiceBusConnection")
            let svc =
                Service(config,
                    serviceId = RoleEnvironment.CurrentRoleInstance.Id,
                    MaxConcurrentJobs = Environment.ProcessorCount * 8)

            svc.AttachLogger(CustomLogger(fun text -> Trace.WriteLine text))
            svc

    let log message (kind:string) = Trace.TraceInformation(message, kind)

    override __.Run() = svc.Value.Start()
    override __.OnStart() =
        let customTempLocalResourcePath = RoleEnvironment.GetLocalResource("CustomTempLocalStore").RootPath
        customTempLocalResourcePath |> setEnv "TMP"
        customTempLocalResourcePath |> setEnv "TEMP"
        ServicePointManager.DefaultConnectionLimit <- 512
        svc.Force() |> ignore
        base.OnStart()        
    override __.OnStop() = base.OnStop()