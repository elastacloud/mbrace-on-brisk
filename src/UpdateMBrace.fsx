#r @"..\packages\Paket.Core\lib\net45\Paket.Core.dll"
#r @"..\packages\Fake\tools\FakeLib.dll"
#load @"..\packages\FSharp.Azure.StorageTypeProvider\StorageTypeProvider.fsx"
#load "Elastacloud.Brisk.Synchronisation.fsx"
#r @"..\packages\DotNetZip\lib\net20\Ionic.Zip.dll"

open Elastacloud.Brisk
open Fake
open Fake.Azure.CloudServices
open Fake.Git
open FSharp.Azure.StorageTypeProvider
open Paket
open System
open System.IO

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

let createTargetsFor mbraceVersion vmSize =
    let targetForVm name func =
        let targetName = (sprintf "%s-%s: %s" vmSize mbraceVersion name)
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
            PackageRole { CloudService = "MBraceCloudService"; WorkerRole = "MBraceWorkerRole"; SdkVersion = None; OutputPath = Some @"MBraceCloudService\bin\Release\app.publish" }
            |> ignore)

        targetForVm "Upload to Azure" (fun _ -> 
            let file = Path.Combine(packageOutputDir, "MBraceCloudService.cspkg")
            let destination = Path.Combine(packageOutputDir, sprintf "mbrace-%s-%s.cspkg" mbraceVersion (vmSize.ToLower()))
            DeleteFile destination
            file |> Rename destination
            EUNorthStorage.Containers.cspackages.Upload destination |> Async.RunSynchronously)
    ]

let mbraceVersion = 
    Paket.LockFile
         .LoadFrom("..\paket.lock")
         .ResolvedPackages
         .[Domain.NormalizedPackageName (Domain.PackageName "MBrace.Azure")]
         .Version

let vmTargets = [ "Medium"; "Large"; "ExtraLarge" ] |> List.collect (createTargetsFor mbraceVersion.AsString)

Target "Synchronise Depots" (fun _ ->
    Synchronisation.SyncAllDepots(Synchronisation.BizsparkCert, Synchronisation.SourceDepot, Synchronisation.TargetDepots)
    |> Async.RunSynchronously
    |> Seq.filter(fst >> (<>) Synchronisation.FileSyncResult.FileAlreadyExists)
    |> Seq.sortBy fst
    |> Seq.iter(fun (res, (_, dest)) -> printfn "%A - %A" res dest))

Target "Commit Label and Push" (fun _ ->
    let newMbraceVersion = (LockFile.LoadFrom "..\paket.lock").ResolvedPackages.[Domain.NormalizedPackageName(Domain.PackageName "Mbrace.Azure")].Version.ToString()
    sourceFolder |> StageAll
    sourceFolder |> Commit <| sprintf "Update MBrace.Azure %s" newMbraceVersion
    sourceFolder |> push
    tag sourceFolder newMbraceVersion
    pushTag sourceFolder "origin" newMbraceVersion
)

vmTargets |> List.reduce (==>)
//==> "Commit Label and Push"
==> "Synchronise Depots"
==> "Run All"

TargetHelper.PrintDependencyGraph true "Run All"

Run "Run All"
