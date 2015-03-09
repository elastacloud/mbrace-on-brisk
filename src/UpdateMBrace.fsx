#load "Fake.Azure.CloudServices.fsx"
#load @"..\packages\FSharp.Azure.StorageTypeProvider\StorageTypeProvider.fsx"
#load "Elastacloud.Brisk.Synchronisation.fsx"
#r @"..\packages\DotNetZip\lib\net20\Ionic.Zip.dll"

open Paket
open Fake
open Fake.Azure.CloudServices
open Fake.Git
open System
open System.IO
open FSharp.Azure.StorageTypeProvider
open Elastacloud.Brisk

Environment.CurrentDirectory <- __SOURCE_DIRECTORY__
let packageOutputDir = @"MBraceCloudService\bin\Release\app.publish"
let sourceFolder = (DirectoryInfo __SOURCE_DIRECTORY__).Parent.FullName
type EUNorthStorage = AzureTypeProvider<"briskdepoteun", "dPwxkxWGJzaWrxKLRUYOuNqoWHTUf0Xc3KzSSXew9ojTUuiIW+s/owwv0FRBNeGp+i69XL9W5hKsuyuL9TKYiQ==">

Target "Upgrade MBrace" (fun _ ->
    sourceFolder |> Git.Reset.ResetHard
    UpdateProcess.UpdatePackage("..\paket.dependencies", Domain.PackageName "MBrace.Azure", None, false, false, false)
    if sourceFolder |> Git.Information.isCleanWorkingCopy then
        failwith "No changes have been detected."
)

Target "Run All" DoNothing

let createTargetsFor vmSize =
    let targetForVm name func =
        let targetName = (sprintf "%s: %s" vmSize name)
        let t = Target targetName func
        targetName
    [
        targetForVm "Modify config" (fun _ -> 
            let csdefPath = @"MBraceCloudService\ServiceDefinition.csdef"
            csdefPath
            |> File.ReadAllText 
            |> XMLHelper.XMLDoc
            |> XMLHelper.XPathReplaceNS
                "/svchost:ServiceDefinition/svchost:WorkerRole/@vmsize"
                vmSize
                [ "svchost", "http://schemas.microsoft.com/ServiceHosting/2008/10/ServiceDefinition" ]
            |> fun doc -> doc.Save csdefPath
        )

        targetForVm "Clean" (fun _ -> CleanDirs [ @"MBraceWorkerRole\bin"; @"MBraceCloudService\bin" ])

        targetForVm "Rebuild" (fun _ -> 
            !!("Elastacloud.Brisk.MBraceOnAzure.sln")
            |> MSBuildRelease "" "Rebuild"
            |> ignore)

        targetForVm "Create CS Package" (fun _ -> 
            // create the cspackage
            PackageRole { CloudService = "MBraceCloudService"; WorkerRole = "MBraceWorkerRole"; SdkVersion = None; OutputPath = Some @"MBraceCloudService\bin\Release\app.publish" }
            |> ignore)

        targetForVm "Upload to Azure" (fun _ -> 
            let file = Path.Combine(packageOutputDir, "MBraceCloudService.cspkg")
            let destination = Path.Combine(packageOutputDir, sprintf "mbrace-%s.cspkg" (vmSize.ToLower()))
            DeleteFile destination
            file |> Rename destination
            EUNorthStorage.Containers.cspackages.Upload destination |> Async.RunSynchronously)
    ]

let vmTargets = [ "Medium"; "Large"; "ExtraLarge" ] |> List.collect createTargetsFor

Target "Synchronise Depots" (fun _ ->
    Synchronisation.SyncAllDepots(Synchronisation.BizsparkCert, Synchronisation.SourceDepot, Synchronisation.TargetDepots)
    |> Async.RunSynchronously
    |> Seq.filter(fst >> (<>) Synchronisation.FileSyncResult.FileAlreadyExists)
    |> Seq.sortBy fst
    |> Seq.iter(fun (res, (_, dest)) -> printfn "%A - %A" res dest))

Target "Commit Label and Push" (fun _ ->
    let newMbraceVersion = (LockFile.LoadFrom "..\paket.lock").ResolvedPackages.[Domain.NormalizedPackageName(Domain.PackageName "Mbrace.Azure")].Version.ToString()
    Commit sourceFolder <| sprintf "Update MBrace.Azure %s" newMbraceVersion
    push sourceFolder
    tag sourceFolder newMbraceVersion
    pushTag sourceFolder "origin" newMbraceVersion
)

"Upgrade MBrace" ==> vmTargets.Head |> ignore
vmTargets |> List.reduce (==>)
==> "Commit Label and Push"
==> "Synchronise Depots"
==> "Run All"

TargetHelper.PrintDependencyGraph true "Run All"

Run "Run All"