﻿namespace MBrace.Core

open System
open System.Collections
open System.Collections.Generic
open System.Runtime.Serialization
open System.Text
open System.IO

open MBrace.Core
open MBrace.Core.Internals
open MBrace.Core
open MBrace.Core.Internals

#nowarn "444"

/// <summary>
///     Ordered, immutable collection of values persisted in a single cloud file.
/// </summary>
[<DataContract; StructuredFormatDisplay("{StructuredFormatDisplay}")>]
type FilePersistedSequence<'T> =

    // https://visualfsharp.codeplex.com/workitem/199
    [<DataMember(Name = "Path")>]
    val mutable private path : string
    [<DataMember(Name = "ETag")>]
    val mutable private etag : ETag
    [<DataMember(Name = "Count")>]
    val mutable private count : int64 option
    [<DataMember(Name = "Deserializer")>]
    val mutable private deserializer : (Stream -> seq<'T>) option

    internal new (path, etag, count, deserializer) =
        { path = path ; etag = etag ; count = count ; deserializer = deserializer }

    /// Returns an enumerable that lazily fetches elements of the cloud sequence from store.
    member c.ToEnumerable() : Local<seq<'T>> = local {
        let! config = Cloud.GetResource<CloudFileStoreConfiguration> ()
        let! deserializer = local {
            match c.deserializer with
            | Some ds -> return ds
            | None ->
                let! serializer = Cloud.GetResource<ISerializer> ()
                return fun s -> serializer.SeqDeserialize<'T>(s, leaveOpen = false)
        }

        // wrap stream inside enumerator to enforce correct IEnumerable behaviour
        let fileStore = config.FileStore
        let path = c.path
        let mkEnumerator () =
            let streamOpt = fileStore.ReadETag(path, c.etag) |> Async.RunSync
            match streamOpt with
            | None -> raise <| new InvalidDataException(sprintf "CloudSequence: incorrect etag in file '%s'." c.path)
            | Some stream -> deserializer(stream).GetEnumerator()

        return Seq.fromEnumerator mkEnumerator
    }

    /// Fetches all elements of the cloud sequence and returns them as a local array.
    member c.ToArray () : Local<'T []> = local { 
        let! seq = c.ToEnumerable()
        return Seq.toArray seq 
    }


    /// Path to Cloud sequence in store
    member c.Path = c.path

    member c.ETag = c.etag

    /// Cloud sequence element count
    member c.Count = local {
        match c.count with
        | Some l -> return l
        | None ->
            // this is a potentially costly operation
            let! seq = c.ToEnumerable()
            let l = int64 <| Seq.length seq
            c.count <- Some l
            return l
    }

    /// Underlying sequence size in bytes
    member c.Size = local {
        let! config = Cloud.GetResource<CloudFileStoreConfiguration> ()
        return! config.FileStore.GetFileSize c.path
    }

    interface ICloudDisposable with
        member c.Dispose () = local {
            let! config = Cloud.GetResource<CloudFileStoreConfiguration> ()
            return! config.FileStore.DeleteFile c.path
        }

    interface ICloudCollection<'T> with
        member c.IsKnownCount = Option.isSome c.count
        member c.IsKnownSize = true
        member c.Count = c.Count
        member c.Size = c.Size
        member c.ToEnumerable() = c.ToEnumerable()

    override c.ToString() = sprintf "CloudSequence[%O] at %s" typeof<'T> c.path
    member private c.StructuredFormatDisplay = c.ToString()  

/// Partitionable implementation of cloud file line reader
[<DataContract>]
type private TextLineSequence(path : string, etag : ETag, ?encoding : Encoding) =
    inherit FilePersistedSequence<string>(path, etag, None, Some(fun stream -> TextReaders.ReadLines(stream, ?encoding = encoding)))

    interface IPartitionableCollection<string> with
        member cs.GetPartitions(weights : int []) = local {
            let! size = CloudFile.GetSize cs.Path

            let mkRangedSeqs (weights : int[]) =
                let getDeserializer s e stream = TextReaders.ReadLinesRanged(stream, max (s - 1L) 0L, e, ?encoding = encoding)
                let mkRangedSeq rangeOpt =
                    match rangeOpt with
                    | Some(s,e) -> new FilePersistedSequence<string>(cs.Path, cs.ETag, None, Some(getDeserializer s e)) :> ICloudCollection<string>
                    | None -> new SequenceCollection<string>([||]) :> _

                let partitions = Array.splitWeightedRange weights 0L size
                Array.map mkRangedSeq partitions

            if size < 512L * 1024L then
                // partition lines in-memory if file is particularly small.
                let! count = cs.Count
                if count < int64 weights.Length then
                    let! lines = cs.ToArray()
                    let liness = Array.splitWeighted weights lines
                    return liness |> Array.map (fun lines -> new SequenceCollection<string>(lines) :> ICloudCollection<_>)
                else
                    return mkRangedSeqs weights
            else
                return mkRangedSeqs weights
        }

type FilePersistedSequence =

    /// <summary>
    ///     Creates a new Cloud sequence by persisting provided sequence as a cloud file in the underlying store.
    /// </summary>
    /// <param name="values">Input sequence.</param>
    /// <param name="path">Path to persist cloud value in File Store. Defaults to a random file name.</param>
    /// <param name="serializer">Serializer used in sequence serialization. Defaults to execution context.</param>
    static member New(values : seq<'T>, ?path : string, ?serializer : ISerializer) : Local<FilePersistedSequence<'T>> = local {
        let! config = Cloud.GetResource<CloudFileStoreConfiguration> ()
        let path = 
            match path with
            | Some p -> p
            | None -> config.FileStore.GetRandomFilePath config.DefaultDirectory

        let! _serializer = local {
            match serializer with
            | None -> return! Cloud.GetResource<ISerializer> ()
            | Some s -> return s
        }

        let deserializer = serializer |> Option.map (fun ser stream -> ser.SeqDeserialize<'T>(stream, leaveOpen = false))
        let writer (stream : Stream) = async {
            return _serializer.SeqSerialize<'T>(stream, values, leaveOpen = false) |> int64
        }
        let! etag, length = config.FileStore.WriteETag(path, writer)
        return new FilePersistedSequence<'T>(path, etag, Some length, deserializer)
    }

    /// <summary>
    ///     Creates a collection of partitioned cloud sequences by persisting provided sequence as cloud files in the underlying store.
    ///     A new partition will be appended to the collection as soon as the 'maxPartitionSize' is exceeded in bytes.
    /// </summary>
    /// <param name="values">Input sequence.</param>
    /// <param name="maxPartitionSize">Maximum size in bytes per cloud sequence partition.</param>
    /// <param name="directory">FileStore directory used for Cloud sequence. Defaults to execution context.</param>
    /// <param name="serializer">Serializer used in sequence serialization. Defaults to execution context.</param>
    static member NewPartitioned(values : seq<'T>, maxPartitionSize : int64, ?directory : string, ?serializer : ISerializer) : Local<FilePersistedSequence<'T> []> = local {
        let! config = Cloud.GetResource<CloudFileStoreConfiguration> ()
        let directory = defaultArg directory config.DefaultDirectory
        let! _serializer = local {
            match serializer with
            | None -> return! Cloud.GetResource<ISerializer> ()
            | Some s -> return s
        }

        let deserializer = serializer |> Option.map (fun ser stream -> ser.SeqDeserialize<'T>(stream, leaveOpen = false))
        return! async {
            if maxPartitionSize <= 0L then return invalidArg "maxPartitionSize" "Must be greater that 0."

            let seqs = new ResizeArray<FilePersistedSequence<'T>>()
            let currentStream = ref Unchecked.defaultof<Stream>
            let splitNext () = currentStream.Value.Position >= maxPartitionSize
            let partitionedValues = PartitionedEnumerable.ofSeq splitNext values
            for partition in partitionedValues do
                let path = config.FileStore.GetRandomFilePath directory
                let writer (stream : Stream) = async {
                    currentStream := stream
                    return _serializer.SeqSerialize<'T>(stream, partition, leaveOpen = false) |> int64
                }
                let! etag, length = config.FileStore.WriteETag(path, writer)
                let seq = new FilePersistedSequence<'T>(path, etag, Some length, deserializer)
                seqs.Add seq

            return seqs.ToArray()
        }
    }

    /// <summary>
    ///     Defines a CloudSequence from provided cloud file path with user-provided deserialization function.
    ///     This is a lazy operation unless the optional 'force' parameter is enabled.
    /// </summary>
    /// <param name="path">Path to file.</param>
    /// <param name="deserializer">Sequence deserializer function.</param>
    /// <param name="force">Check integrity by forcing deserialization on creation. Defaults to false.</param>
    static member OfCloudFile<'T>(path : string, ?deserializer : Stream -> seq<'T>, ?force : bool) : Local<FilePersistedSequence<'T>> = local {
        let! config = Cloud.GetResource<CloudFileStoreConfiguration> ()
        let! etag = config.FileStore.TryGetETag path
        match etag with
        | None -> return raise <| new FileNotFoundException(path)
        | Some et ->
            let cseq = new FilePersistedSequence<'T>(path, et, None, deserializer)
            if defaultArg force false then
                let! _ = cseq.Count in ()

            return cseq
    }

    /// <summary>
    ///     Defines a CloudSequence from provided cloud file path with user-provided serializer implementation.
    ///     This is a lazy operation unless the optional 'force' parameter is enabled.
    /// </summary>
    /// <param name="path">Path to Cloud sequence.</param>
    /// <param name="serializer">Serializer implementation used for element deserialization.</param>
    /// <param name="force">Check integrity by forcing deserialization on creation. Defaults to false.</param>
    static member OfCloudFile<'T>(path : string, serializer : ISerializer, ?force : bool) : Local<FilePersistedSequence<'T>> = local {
        let deserializer stream = serializer.SeqDeserialize<'T>(stream, leaveOpen = false)
        return! FilePersistedSequence.OfCloudFile<'T>(path, deserializer = deserializer, ?force = force)
    }

    /// <summary>
    ///     Defines a CloudSequence from provided cloud file path with user-provided text deserialization function.
    ///     This is a lazy operation unless the optional 'force' parameter is enabled.
    /// </summary>
    /// <param name="path">Path to file.</param>
    /// <param name="textDeserializer">Text deserializer function.</param>
    /// <param name="encoding">Text encoding. Defaults to UTF8.</param>
    /// <param name="force">Check integrity by forcing deserialization on creation. Defaults to false.</param>
    static member OfCloudFile<'T>(path : string, textDeserializer : StreamReader -> seq<'T>, ?encoding : Encoding, ?force : bool) : Local<FilePersistedSequence<'T>> = local {
        let deserializer (stream : Stream) =
            let sr = 
                match encoding with
                | None -> new StreamReader(stream)
                | Some e -> new StreamReader(stream, e)

            textDeserializer sr 
        
        return! FilePersistedSequence.OfCloudFile<'T>(path, deserializer, ?force = force)
    }

    /// <summary>
    ///     Defines a CloudSequence from provided cloud file path with user-provided text deserialization function.
    ///     This is a lazy operation unless the optional 'force' parameter is enabled.
    /// </summary>
    /// <param name="path">Path to file.</param>
    /// <param name="encoding">Text encoding. Defaults to UTF8.</param>
    /// <param name="force">Check integrity by forcing deserialization on creation. Defaults to false.</param>
    static member FromLineSeparatedTextFile(path : string, ?encoding : Encoding, ?force : bool) : Local<FilePersistedSequence<string>> = local {
        let! config = Cloud.GetResource<CloudFileStoreConfiguration> ()
        let! etag = config.FileStore.TryGetETag path
        match etag with
        | None -> return raise <| new FileNotFoundException(path)
        | Some et ->
            let cseq = new TextLineSequence(path, et, ?encoding = encoding)
            if defaultArg force false then
                let! _ = cseq.Count in ()

            return cseq :> FilePersistedSequence<string>
    }