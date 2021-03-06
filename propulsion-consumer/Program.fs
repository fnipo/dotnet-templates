﻿module ConsumerTemplate.Program

open Serilog
open System

module EnvVar =

    let tryGet varName : string option = Environment.GetEnvironmentVariable varName |> Option.ofObj
    let set varName value : unit = Environment.SetEnvironmentVariable(varName, value)

module Settings =

    let private initEnvVar var key loadF =
        if None = EnvVar.tryGet var then
            printfn "Setting %s from %A" var key
            EnvVar.set var (loadF key)

    let initialize () =
        // e.g. initEnvVar     "EQUINOX_COSMOS_COLLECTION"    "CONSUL KEY" readFromConsul
        () // TODO add any custom logic preprocessing commandline arguments and/or gathering custom defaults from external sources, etc

module CmdParser =

    exception MissingArg of string
    let private getEnvVarForArgumentOrThrow varName argName =
        match EnvVar.tryGet varName with
        | None -> raise (MissingArg(sprintf "Please provide a %s, either as an argument or via the %s environment variable" argName varName))
        | Some x -> x
    let private defaultWithEnvVar varName argName = function
        | None -> getEnvVarForArgumentOrThrow varName argName
        | Some x -> x
    open Argu
    [<NoEquality; NoComparison>]
    type Parameters =
        | [<AltCommandLine "-g"; Unique>]   Group of string
        | [<AltCommandLine "-b"; Unique>]   Broker of string
        | [<AltCommandLine "-t"; Unique>]   Topic of string
        | [<AltCommandLine "-w"; Unique>]   MaxDop of int
        | [<AltCommandLine "-m"; Unique>]   MaxInflightGb of float
        | [<AltCommandLine "-l"; Unique>]   LagFreqM of float
        | [<AltCommandLine "-v"; Unique>]   Verbose

        interface IArgParserTemplate with
            member a.Usage = a |> function
                | Group _ ->                "specify Kafka Consumer Group Id. (optional if environment variable PROPULSION_KAFKA_GROUP specified)."
                | Broker _ ->               "specify Kafka Broker, in host:port format. (optional if environment variable PROPULSION_KAFKA_BROKER specified)."
                | Topic _ ->                "specify Kafka Topic name. (optional if environment variable PROPULSION_KAFKA_TOPIC specified)."
                | MaxDop _ ->               "maximum number of items to process in parallel. Default: 1024."
                | MaxInflightGb _ ->        "maximum GB of data to read ahead. Default: 0.5."
                | LagFreqM _ ->             "specify frequency (minutes) to dump lag stats. Default: off."
                | Verbose _ ->              "request verbose logging."
    type Arguments(a : ParseResults<Parameters>) =
        member __.Broker =                  a.TryGetResult Broker |> defaultWithEnvVar "PROPULSION_KAFKA_BROKER" "Broker" |> Uri
        member __.Topic =                   a.TryGetResult Topic  |> defaultWithEnvVar "PROPULSION_KAFKA_TOPIC"  "Topic"
        member __.Group =                   a.TryGetResult Group  |> defaultWithEnvVar "PROPULSION_KAFKA_GROUP"  "Group"
        member __.MaxDop =                  match a.TryGetResult MaxDop with Some x -> x | None -> 1024
        member __.MaxInFlightBytes =        (match a.TryGetResult MaxInflightGb with Some x -> x | None -> 0.5) * 1024. * 1024. *1024. |> int64
        member __.LagFrequency =            a.TryGetResult LagFreqM |> Option.map TimeSpan.FromMinutes
        member __.Verbose =                 a.Contains Verbose

    /// Parse the commandline; can throw exceptions in response to missing arguments and/or `-h`/`--help` args
    let parse argv =
        let programName = Reflection.Assembly.GetEntryAssembly().GetName().Name
        let parser = ArgumentParser.Create<Parameters>(programName = programName)
        parser.ParseCommandLine argv |> Arguments

module Logging =

    let initialize verbose =
        Log.Logger <-
            LoggerConfiguration()
                .Destructure.FSharpTypes()
                .Enrich.FromLogContext()
            |> fun c -> if verbose then c.MinimumLevel.Debug() else c
            |> fun c -> let theme = Sinks.SystemConsole.Themes.AnsiConsoleTheme.Code
                        if not verbose then c.WriteTo.Console(theme=theme)
                        else c.WriteTo.Console(theme=theme, outputTemplate="[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}|{Properties}{NewLine}{Exception}")
            |> fun c -> c.CreateLogger()

let start (args : CmdParser.Arguments) =
    let c =
        FsKafka.KafkaConsumerConfig.Create(
            "ConsumerTemplate",
            args.Broker, [args.Topic], args.Group,
            maxInFlightBytes = args.MaxInFlightBytes, ?statisticsInterval = args.LagFrequency)
    //MultiMessages.BatchesSync.Start(c)
    //MultiMessages.BatchesAsync.Start(c, args.MaxDop)
    //NultiMessages.Parallel.Start(c, args.MaxDop)
    MultiStreams.start(c, args.MaxDop)

let run argv =
    try let args = CmdParser.parse argv
        Logging.initialize args.Verbose
        Settings.initialize ()
        use consumer = start args
        consumer.AwaitCompletion() |> Async.RunSynchronously
        if consumer.RanToCompletion then 0 else 2
    with :? Argu.ArguParseException as e -> eprintfn "%s" e.Message; 1
        | CmdParser.MissingArg msg -> eprintfn "%s" msg; 1
        // If the handler throws, we exit the app in order to let an orchestrator flag the failure
        | e -> Log.Fatal(e, "Exiting"); 1

[<EntryPoint>]
let main argv =
    try run argv
    finally Log.CloseAndFlush()