﻿module Telplin.Lambda.Implementation

open System
open System.IO
open System.Collections.Generic
open System.Net
open System.Reflection
open FSharp.Compiler.CodeAnalysis
open Microsoft.Net.Http.Headers
open Thoth.Json.Net
open Amazon.Lambda.APIGatewayEvents
open Amazon.Lambda.Core
open Telplin.Common
open Telplin.Core

[<RequireQualifiedAccess>]
module HeaderValues =
    [<Literal>]
    let ApplicationText = "application/text"

    [<Literal>]
    let TextPlain = "text/plain"

    [<Literal>]
    let ApplicationJson = "application/json"

module Encoding =
    let encodeRange (r : RangeProxy) =
        Encode.object
            [
                "startLine", Encode.int r.StartLine
                "startColumn", Encode.int r.StartColumn
                "endLine", Encode.int r.EndLine
                "endColumn", Encode.int r.EndColumn
            ]

    let encodeDiagnostic (d : FSharpDiagnosticInfo) =
        Encode.object
            [
                "severity",
                Encode.string (
                    match d.Severity with
                    | FSharpDiagnosticInfoSeverity.Warning -> "warning"
                    | FSharpDiagnosticInfoSeverity.Error -> "error"
                    | _ -> "unknown"
                )
                "message", Encode.string d.Message
                "errorNumber", Encode.string d.ErrorNumber
                "range", encodeRange d.Range
            ]

    let encodeInvalidImplementationFile (diagnostics : FSharpDiagnosticInfo array) =
        Encode.object
            [
                "type", Encode.string "invalidImplementationFile"
                "diagnostics", Encode.array (Array.map encodeDiagnostic diagnostics)
            ]
        |> Encode.toString 4

    let encodeInvalidSignatureFile (diagnostics : FSharpDiagnosticInfo array) signatureContent =
        Encode.object
            [
                "type", Encode.string "invalidSignatureFile"
                "diagnostics", Encode.array (Array.map encodeDiagnostic diagnostics)
                "signature", Encode.string signatureContent
            ]
        |> Encode.toString 4

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[<assembly : LambdaSerializer(typeof<Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer>)>]
()

let resolvedAssemblies =
    let dir =
#if RELEASE
        Path.Combine (FileInfo(Assembly.GetExecutingAssembly().Location).Directory.FullName, "ref")
#else
        Path.Combine (__SOURCE_DIRECTORY__, "..", "..", "reference")
#endif

    Directory.EnumerateFiles dir |> Seq.map (sprintf "-r:%s") |> Seq.toArray

let projectOptions : FSharpProjectOptions =
    {
        ProjectFileName = "A"
        ProjectId = None
        SourceFiles = [| "A.fs" |]
        OtherOptions =
            [|
                "-g"
                "--debug:portable"
                "--noframework"
                "--define:TRACE"
                "--define:DEBUG"
                "--define:NET"
                "--define:NET7_0"
                "--define:NETCOREAPP"
                "--define:NET5_0_OR_GREATER"
                "--define:NET6_0_OR_GREATER"
                "--define:NET7_0_OR_GREATER"
                "--define:NETCOREAPP1_0_OR_GREATER"
                "--define:NETCOREAPP1_1_OR_GREATER"
                "--define:NETCOREAPP2_0_OR_GREATER"
                "--define:NETCOREAPP2_1_OR_GREATER"
                "--define:NETCOREAPP2_2_OR_GREATER"
                "--define:NETCOREAPP3_0_OR_GREATER"
                "--define:NETCOREAPP3_1_OR_GREATER"
                "--optimize-"
                "--tailcalls-"
                yield! resolvedAssemblies
                "--target:library"
                "--nowarn:IL2121"
                "--warn:3"
                "--warnaserror:3239,FS0025"
                "--fullpaths"
                "--flaterrors"
                "--highentropyva+"
                "--targetprofile:netcore"
                "--nocopyfsharpcore"
                "--deterministic+"
                "--simpleresolution"
            |]
        ReferencedProjects = [||]
        IsIncompleteTypeCheckEnvironment = false
        UseScriptResolutionRules = false
        LoadTime = DateTime.UtcNow
        UnresolvedReferences = None
        OriginalLoadReferences = []
        Stamp = None
    }

let mkProcessRequest<'t>
    (onValidSignature : string -> 't)
    (onInvalidImplementationFile : string -> 't)
    (onInvalidSignatureFile : string -> 't)
    (onInternalError : string -> 't)
    (implementation : string)
    : 't
    =
    try
        let verification =
            TelplinInternalApi.VerifySignatureWithImplementation (implementation, projectOptions)

        match verification with
        | SignatureVerificationResult.ValidSignature signatureContent -> onValidSignature signatureContent
        | SignatureVerificationResult.InvalidImplementationFile diags ->
            onInvalidImplementationFile (Encoding.encodeInvalidImplementationFile diags)
        | SignatureVerificationResult.InvalidSignatureFile (signatureContent, diags) ->
            onInvalidSignatureFile (Encoding.encodeInvalidSignatureFile diags signatureContent)
        | SignatureVerificationResult.ImplementationFileAborted ->
            onInternalError "Could not type check the implementation file. Type checking was aborted."
        | SignatureVerificationResult.FailedToCreateSignatureFile error ->
            onInternalError $"Internal error when creating signature file:\n{error}"
    with ex ->
        onInternalError ex.Message

let createHeaders headers =
    Seq.fold
        (fun (acc : Dictionary<string, string>) (key, value) ->
            acc.[key] <- value
            acc
        )
        (Dictionary<string, string> ())
        headers

let mkAPIGatewayProxyResponse (statusCode : HttpStatusCode, contentTypeHeaderValue : string, body : string) =
    APIGatewayProxyResponse (
        StatusCode = int statusCode,
        Body = body,
        Headers = createHeaders [ HeaderNames.ContentType, contentTypeHeaderValue ]
    )

let PostSignature (request : APIGatewayProxyRequest) (_context : ILambdaContext) =
    mkProcessRequest
        (fun signature -> mkAPIGatewayProxyResponse (HttpStatusCode.OK, HeaderValues.ApplicationText, signature))
        (fun json -> mkAPIGatewayProxyResponse (HttpStatusCode.BadRequest, HeaderValues.ApplicationJson, json))
        (fun json -> mkAPIGatewayProxyResponse (HttpStatusCode.BadRequest, HeaderValues.ApplicationJson, json))
        (fun error -> mkAPIGatewayProxyResponse (HttpStatusCode.InternalServerError, HeaderValues.TextPlain, error))
        request.Body
