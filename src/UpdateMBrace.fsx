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
let packageOutputDir = @"Deployment\MBraceCloudService\bin\Release\app.publish"
let sourceFolder = (DirectoryInfo __SOURCE_DIRECTORY__).Parent.FullName
type EUNorthStorage = AzureTypeProvider<"briskdepoteun", "dPwxkxWGJzaWrxKLRUYOuNqoWHTUf0Xc3KzSSXew9ojTUuiIW+s/owwv0FRBNeGp+i69XL9W5hKsuyuL9TKYiQ==">

let updatePackage name version = UpdateProcess.UpdatePackage("Deployment\paket.dependencies", Domain.PackageName name, version, UpdaterOptions.Default)

Target "Upgrade MBrace" (fun _ ->
    sourceFolder |> Git.Reset.ResetHard
    updatePackage "MBrace.Azure" None
    if sourceFolder |> Git.Information.isCleanWorkingCopy then
        failwith "No changes have been detected."
)

Target "Run All" DoNothing

type FscVersion = | FSC_31 | FSC_40

let createTargetsFor vmSize fscVersion =
    let targetForVm name func =
        let targetName = (sprintf "%s-%A: %s" vmSize fscVersion name)
        let t = Target targetName func
        targetName
    let fscVersion = match fscVersion with | FSC_31 -> "3.1.2.5" | FSC_40 -> "4.0.0.1"
    [ targetForVm (sprintf "Set FSharp Core to %s" fscVersion) (fun _ -> updatePackage "FSharp.Core" (Some fscVersion))
      targetForVm "Modify config" (fun _ -> 
          let csdefPath = @"Deployment\MBraceCloudService\ServiceDefinition.csdef"
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
          !!("Deployment\Elastacloud.Brisk.MBraceOnAzure.sln")
          |> MSBuildRelease "" "Rebuild"
          |> ignore)

      targetForVm "Create CS Package" (fun _ -> 
          PackageRole { CloudService = "MBraceCloudService"; WorkerRole = "MBraceWorkerRole"; SdkVersion = None; OutputPath = Some @"Deployment\MBraceCloudService\bin\Release\app.publish" }
          |> ignore)

      targetForVm "Upload to Azure" (fun _ -> 
          let file = Path.Combine(packageOutputDir, "MBraceCloudService.cspkg")
          let destination = Path.Combine(packageOutputDir, sprintf "mbrace-%s.cspkg" (vmSize.ToLower()))
          DeleteFile destination
          file |> Rename destination
          EUNorthStorage.Containers.cspackages.Upload destination |> Async.RunSynchronously)
    ]

let vmTargets =
    [ "Medium"; "Large"; "ExtraLarge" ]
    |> List.collect(fun vmSize -> [ FSC_31; FSC_40 ] |> List.map(fun fscV -> fscV, vmSize))
    |> List.collect(fun (fscVersion, vmSize) -> createTargetsFor vmSize fscVersion)

Target "Synchronise Depots" (fun _ ->
    Synchronisation.SyncAllDepots(Synchronisation.BizsparkCert, Synchronisation.SourceDepot, Synchronisation.TargetDepots)
    |> Async.RunSynchronously
    |> Seq.filter(fst >> (<>) Synchronisation.FileSyncResult.FileAlreadyExists)
    |> Seq.sortBy fst
    |> Seq.iter(fun (res, (_, dest)) -> printfn "%A - %A" res dest))

Target "Commit Label and Push" (fun _ ->
    let newMbraceVersion = (LockFile.LoadFrom "Deployment\paket.lock").ResolvedPackages.[Domain.NormalizedPackageName(Domain.PackageName "Mbrace.Azure")].Version.ToString()
    sourceFolder |> StageAll
    sourceFolder |> Commit <| sprintf "Update MBrace.Azure %s" newMbraceVersion
    sourceFolder |> push
    tag sourceFolder newMbraceVersion
    pushTag sourceFolder "origin" newMbraceVersion
)

vmTargets |> List.reduce (==>)
==> "Commit Label and Push"
==> "Synchronise Depots"
==> "Run All"

TargetHelper.PrintDependencyGraph true "Run All"

Run "Run All"