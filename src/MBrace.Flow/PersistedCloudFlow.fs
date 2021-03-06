﻿namespace MBrace.Flow

open System
open System.IO
open System.Collections
open System.Collections.Generic
open System.Runtime.Serialization
open System.Threading
open System.Threading.Tasks

open MBrace.Core
open MBrace.Core.Internals
open MBrace.Library

open MBrace.Flow.Internals

open MBrace.Runtime.Utils.PrettyPrinters

#nowarn "444"

/// CloudFlow instance whose elements are persisted across an MBrace cluster
[<Sealed; DataContract; StructuredFormatDisplay("{StructuredFormatDisplay}")>]
type PersistedCloudFlow<'T> internal (partitions : (IWorkerRef * CloudArray<'T>) []) =

    [<DataMember(Name = "Partitions")>]
    let partitions = partitions

    /// Number of partitions in the vector.
    member __.PartitionCount = partitions.Length

    member __.Item with get i = partitions.[i]

    /// Gets the common StorageLevel of all persisted partitions.
    member __.StorageLevel : StorageLevel =
        if partitions.Length = 0 then StorageLevel.Memory
        else
            partitions |> Array.fold (fun sl (_,p) -> sl &&& p.StorageLevel) (enum Int32.MaxValue)

    /// Gets all supported storage level by contained partitions
    /// of persisted CloudFlow.
    member __.StorageLevels : StorageLevel [] = 
        partitions
        |> Seq.map (fun (_,p) -> p.StorageLevel)
        |> Seq.distinct
        |> Seq.sort
        |> Seq.toArray

    /// Gets the CloudSequence partitions of the PersistedCloudFlow
    member __.GetPartitions () = partitions
    /// Computes the size (in bytes) of the PersistedCloudFlow
    member __.Size: int64 = partitions |> Array.sumBy (fun (_,p) -> p.Size)
    /// Computes the element count of the PersistedCloudFlow
    member __.Count: int64 = partitions |> Array.sumBy (fun (_,p) -> int64 p.Length)

    /// Gets an enumerable for all elements in the PersistedCloudFlow
    member private __.ToEnumerable() : seq<'T> =
        match partitions with
        | [||] -> Seq.empty
        | [| (_,p) |] -> p.Value :> seq<'T>
        | _ -> partitions |> Seq.collect snd

    interface seq<'T> with
        member x.GetEnumerator() = x.ToEnumerable().GetEnumerator() :> IEnumerator
        member x.GetEnumerator() = x.ToEnumerable().GetEnumerator()

    interface ITargetedPartitionCollection<'T> with
        member cv.IsKnownSize = true
        member cv.IsKnownCount = true
        member cv.IsMaterialized = false
        member cv.GetSizeAsync(): Async<int64> = async { return cv.Size }
        member cv.GetCountAsync(): Async<int64> = async { return cv.Count }
        member cv.GetPartitions(): Async<ICloudCollection<'T> []> = async { return partitions |> Array.map (fun (_,p) -> p :> ICloudCollection<'T>) }
        member cv.GetTargetedPartitions() :Async<(IWorkerRef * ICloudCollection<'T>) []> = async { return partitions |> Array.map (fun (w,ca) -> w, ca :> _) }
        member cv.PartitionCount: Async<int> = async { return partitions.Length }
        member cv.GetEnumerableAsync() = async { return cv.ToEnumerable() }

    interface CloudFlow<'T> with
        member cv.DegreeOfParallelism = None
        member cv.WithEvaluators(collectorf : CloudLocal<Collector<'T,'S>>) (projection: 'S -> CloudLocal<'R>) (combiner: 'R [] -> CloudLocal<'R>): Cloud<'R> = cloud {
            let flow = CloudCollection.ToCloudFlow(cv)
            return! flow.WithEvaluators collectorf projection combiner
        }

    interface ICloudDisposable with
        member __.Dispose () = async {
            return!
                partitions
                |> Array.map (fun (_,p) -> (p :> ICloudDisposable).Dispose())
                |> Async.Parallel
                |> Async.Ignore
        }

    /// Gets printed information on the persisted CloudFlow.
    member __.GetInfo() : string = PersistedCloudFlowReporter<'T>.Report(partitions, title = "PersistedCloudFlow partition info:")
    /// Prints information on the persisted CloudFlow to stdout.
    member __.ShowInfo() = Console.WriteLine(__.GetInfo())

    override __.ToString() = PersistedCloudFlowReporter<'T>.Report(partitions)
    member private __.StructuredFormatDisplay = __.ToString()

and PersistedCloudFlow private () =

    static let defaultTreshold = 1024L * 1024L * 1024L

    /// <summary>
    ///     Creates a new persisted CloudFlow instance out of given enumeration.
    /// </summary>
    /// <param name="elems">Input sequence.</param>
    /// <param name="elems">Storage level used for caching.</param>
    /// <param name="partitionThreshold">Partition threshold in bytes. Defaults to 1GiB.</param>
    static member internal New(elems : seq<'T>, ?storageLevel : StorageLevel, ?partitionThreshold : int64) : CloudLocal<PersistedCloudFlow<'T>> = local {
        let partitionThreshold = defaultArg partitionThreshold defaultTreshold
        let! currentWorker = Cloud.CurrentWorker
        let! partitions = CloudValue.NewArrayPartitioned(elems, ?storageLevel = storageLevel, partitionThreshold = partitionThreshold)
        return new PersistedCloudFlow<'T>(partitions |> Array.map (fun p -> (currentWorker,p)))
    }

    /// <summary>
    ///     Creates a CloudFlow from a collection of provided cloud sequences.
    /// </summary>
    /// <param name="cloudArrays">Cloud sequences to be evaluated.</param>
    static member OfCloudArrays (cloudArrays : seq<#CloudArray<'T>>) : CloudLocal<PersistedCloudFlow<'T>> = local {
        let! workers = Cloud.GetAvailableWorkers()
        let partitions = cloudArrays |> Seq.mapi (fun i ca -> workers.[i % workers.Length], ca :> CloudArray<'T>) |> Seq.toArray
        return new PersistedCloudFlow<'T>(Seq.toArray partitions)
    }
    
    /// <summary>
    ///     Concatenates a series of persisted CloudFlows into one.
    /// </summary>
    /// <param name="vectors">Input CloudFlows.</param>
    static member Concat (vectors : seq<PersistedCloudFlow<'T>>) : PersistedCloudFlow<'T> = 
        let partitions = vectors |> Seq.collect (fun v -> v.GetPartitions()) |> Seq.toArray
        new PersistedCloudFlow<'T>(partitions)

    /// <summary>
    ///     Create a persisted copy of provided CloudFlow.
    /// </summary>
    /// <param name="flow">Input CloudFlow.</param>
    /// <param name="storageLevel">StorageLevel to be used. Defaults to implementation default.</param>
    /// <param name="partitionThreshold">PersistedCloudFlow partition threshold in bytes. Defaults to 1GiB.</param>
    static member OfCloudFlow (flow : CloudFlow<'T>, ?storageLevel : StorageLevel, ?partitionThreshold:int64) : Cloud<PersistedCloudFlow<'T>> = cloud {
        let! defaultLevel = CloudValue.DefaultStorageLevel
        let storageLevel = defaultArg storageLevel defaultLevel
        let! isSupportedStorageLevel = CloudValue.IsSupportedStorageLevel storageLevel

        do
            if not isSupportedStorageLevel then invalidArg "storageLevel" "Specified storage level not supported by the current runtime."
            if partitionThreshold |> Option.exists (fun p -> p <= 0L) then invalidArg "partitionThreshold" "Must be positive value."

        match flow with
        | :? PersistedCloudFlow<'T> as pcf -> return pcf
        | _ ->
            let collectorf (cct : ICloudCancellationToken) = local { 
                let results = new List<List<'T>>()
                let cts = CancellationTokenSource.CreateLinkedTokenSource(cct.LocalToken)
                return 
                  { new Collector<'T, seq<'T>> with
                    member self.DegreeOfParallelism = flow.DegreeOfParallelism 
                    member self.Iterator() = 
                        let list = new List<'T>()
                        results.Add(list)
                        {   Index = ref -1; 
                            Func = (fun value -> list.Add(value));
                            Cts = cts }
                    member self.Result = ResizeArray.concat results }
            }

            use! cts = Cloud.CreateLinkedCancellationTokenSource()
            let createVector (ts : seq<'T>) = PersistedCloudFlow.New(ts, storageLevel = storageLevel, ?partitionThreshold = partitionThreshold)
            return! flow.WithEvaluators (collectorf cts.Token) createVector (fun result -> local { return PersistedCloudFlow.Concat result })
    }


and private PersistedCloudFlowReporter<'T> () =
    static let template : Field<int * IWorkerRef * CloudArray<'T>> list =
        [   
            Field.create "Partition Id" Left (fun (i,_,_) -> "Partition #" + string i)
            Field.create "Assigned Worker" Left (fun (_,w,_) -> w.Id)
            Field.create "Element Count" Left (fun (_,_,a) -> let l = a.Length in l.ToString("#,##0"))
            Field.create "Partition Size" Left (fun (_,_,a) -> getHumanReadableByteSize a.Size)
            Field.create "Storage Level" Left (fun (_,_,a) -> a.StorageLevel)
            Field.create "Cache Id" Left (fun (_,_,a) -> a.Id)
        ]

    static member Report(partitions : (IWorkerRef * CloudArray<'T>) [], ?title : string) =
        let partitions = partitions |> Seq.mapi (fun i (w,p) -> i,w,p) |> Seq.toList
        Record.PrettyPrint(template, partitions, ?title = title, useBorders = false)