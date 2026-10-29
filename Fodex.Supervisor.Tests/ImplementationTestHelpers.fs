namespace Fodex.Supervisor.Tests

open System
open System.IO
open Expecto
open LoggerUtil
open Microsoft.Data.SqlClient
open Fodex.Supervisor

module ImplementationTestHelpers =
    let context =
        let logger =
            { new LoggerUtil.UniLogger() with
                member _.Info _ = ()
                member _.Debug _ = ()
                member _.Warn _ = ()
                member _.Error _ = ()
                member _.infof format = Printf.kprintf ignore format
                member _.debugf format = Printf.kprintf ignore format
                member _.warnf format = Printf.kprintf ignore format
                member _.errorf format = Printf.kprintf ignore format
                member _.pInfof format = Printf.kprintf ignore format
                member _.pDebugf format = Printf.kprintf ignore format
                member _.pWarnf format = Printf.kprintf ignore format
                member _.pErrorf format = Printf.kprintf ignore format
                member _.fInfof format = Printf.kprintf (fun message -> raise (Exception message)) format
                member _.fDebugf format = Printf.kprintf (fun message -> raise (Exception message)) format
                member _.fWarnf format = Printf.kprintf (fun message -> raise (Exception message)) format
                member _.fErrorf format = Printf.kprintf (fun message -> raise (Exception message)) format
                member _.pfErrorf format = Printf.kprintf (fun message -> raise (Exception message)) format }
        { logger = logger; usage = [ "Tests" ]; caller = [] }

    let storeRoot =
        Path.Combine(
            Path.GetTempPath(),
            "Fodex.Supervisor.Tests",
            Guid.NewGuid().ToString("N")
        )
        |> Path.GetFullPath

    let databaseName = "FodexTests_" + Guid.NewGuid().ToString("N")
    let masterConnection = "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5"
    let databaseConnection = "Server=localhost;Database=" + databaseName + ";Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5"
    let configureStoreRoot () =
        Environment.SetEnvironmentVariable("FODEX_SQL_CONNECTION_STRING", databaseConnection)

    let createTestDatabase () =
        use connection = new SqlConnection(masterConnection)
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- "CREATE DATABASE [" + databaseName + "]"
        command.ExecuteNonQuery() |> ignore
        configureStoreRoot ()
        match ReActDataStoreV100.initialize () with Ok () -> () | Error ex -> raise ex

    let removeTestDatabase () =
        // The only permitted target is the unique database created by this test process.
        if databaseName.StartsWith("FodexTests_", StringComparison.Ordinal)
           && Guid.TryParseExact(databaseName.Substring(11), "N") |> fst then
            SqlConnection.ClearAllPools()
            use connection = new SqlConnection(masterConnection)
            connection.Open()
            use command = connection.CreateCommand()
            command.CommandText <- "ALTER DATABASE [" + databaseName + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [" + databaseName + "]"
            command.ExecuteNonQuery() |> ignore

    let expectOk label (result: Result<'Value, exn>) =
        match result with
        | Ok value -> value
        | Error error -> failtestf "%s returned Error: %s" label error.Message

    let removeStoreRoot () =
        let allowedParent =
            Path.Combine(Path.GetTempPath(), "Fodex.Supervisor.Tests")
            |> Path.GetFullPath

        let fullRoot = Path.GetFullPath storeRoot
        let allowedPrefix =
            allowedParent.TrimEnd(Path.DirectorySeparatorChar)
            + string Path.DirectorySeparatorChar

        if
            fullRoot.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists fullRoot
        then
            Directory.Delete(fullRoot, true)

