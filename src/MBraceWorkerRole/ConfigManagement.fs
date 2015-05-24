/// Responds to changes in configuration settings on the cloud service.
module MBraceWorkerRole.ConfigManagement

open MBrace.Azure
open MBrace.Azure.Runtime
open Microsoft.Azure
open Microsoft.WindowsAzure.ServiceRuntime

let private getSetting = CloudConfigurationManager.GetSetting

/// Gets the current MBrace configuration.
let getConfig() =
    { Configuration.Default
        with StorageConnectionString = getSetting "StorageConnection"
             ServiceBusConnectionString = getSetting "ServiceBusConnection" }

/// Updates the mbrace service with the latest configuration.
let updateServiceConfiguration (mbraceSvc:Service) (args:RoleEnvironmentChangedEventArgs) =
    mbraceSvc.Stop()
    mbraceSvc.Configuration <- getConfig()
    mbraceSvc.Start()

/// Checks whether the supplied role environment change is a configuration change.
let configurationHasChanged (args:RoleEnvironmentChangedEventArgs) =
    args.Changes
    |> Seq.exists(function
        :? RoleEnvironmentConfigurationSettingChange -> true
        | _ -> false)