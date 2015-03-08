#load "Fake.Azure.CloudServices.fsx"
#load @"..\packages\FSharp.Azure.StorageTypeProvider\StorageTypeProvider.fsx"
#load "Elastacloud.Brisk.Synchronisation.fsx"
#r @"..\packages\DotNetZip\lib\net20\Ionic.Zip.dll"

open Paket
open Fake
open Fake.Azure.CloudServices
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

let uploadToAzure (vmSize:string) =
    let file = Path.Combine(packageOutputDir, "MBraceCloudService.cspkg")
    let destination = Path.Combine(packageOutputDir, sprintf "mbrace-%s.cspkg" (vmSize.ToLower()))
    DeleteFile destination
    file |> Rename destination
    EUNorthStorage.Containers.cspackages.Upload destination |> Async.RunSynchronously

let ProcessVmSize vmSize =   
    // modify csdef with correct VM size
    let csdefPath = @"MBraceCloudService\ServiceDefinition.csdef"
    csdefPath
    |> File.ReadAllText 
    |> XMLHelper.XMLDoc
    |> XMLHelper.XPathReplaceNS
        "/svchost:ServiceDefinition/svchost:WorkerRole/@vmsize"
        vmSize
        [ "svchost", "http://schemas.microsoft.com/ServiceHosting/2008/10/ServiceDefinition" ]
    |> fun doc -> doc.Save csdefPath

    // clean outputs
    CleanDirs [ @"MBraceWorkerRole\bin"; @"MBraceCloudService\bin" ]

    // rebuild solution    
    !!("Elastacloud.Brisk.MBraceOnAzure.sln")
    |> MSBuildRelease "" "Rebuild"
    |> ignore

    // create the cspackage
    PackageRole { CloudService = "MBraceCloudService"; WorkerRole = "MBraceWorkerRole"; SdkVersion = None; OutputPath = Some @"MBraceCloudService\bin\Release\app.publish" }
    |> ignore

    // upload it to azure
    uploadToAzure vmSize
    
[ "Medium"; "Large"; "ExtraLarge" ]
|> List.mapi(fun index vmSize -> 
    let target = (sprintf "Process %s" vmSize) 
    Target target (fun _ -> ProcessVmSize vmSize)    
    if index = 0 then "Upgrade MBrace" ==> target |> ignore
    target)
|> List.reduce(fun first second -> first ==> second)
|> fun finalStage -> finalStage ==> "Run All" |> ignore

Target "Synchronise Depots" (fun _ ->
    Synchronisation.SyncAllDepots(Synchronisation.BizsparkCert, Synchronisation.SourceDepot, Synchronisation.TargetDepots)
    |> Async.RunSynchronously
    |> Seq.toArray
    |> Array.filter(fun (res, _) -> res <> Synchronisation.FileSyncResult.FileAlreadyExists)
    |> Array.sortBy fst
    |> Array.iter(fun (res, (src, dest)) -> printfn "%A - %A" res dest))

Target "Commit Label and Push" (fun _ ->
    let newMbraceVersion = (LockFile.LoadFrom "..\paket.lock").ResolvedPackages.[Domain.NormalizedPackageName(Domain.PackageName "Mbrace.Azure")].Version.ToString()
    Git.Commit.Commit sourceFolder <| sprintf "Update MBrace.Azure %s" newMbraceVersion
    Git.Branches.push sourceFolder
    Git.Branches.tag sourceFolder newMbraceVersion
    Git.Branches.pushTag sourceFolder "origin" newMbraceVersion
    ()
)

"Commit Label and Push"
==> "Synchronise Depots"
==> "Run All"

// commit, label and push

//TODO: Auto update dependencies here and in other other mbrace repo.

TargetHelper.PrintDependencyGraph false "Run All"

Run "Run All"