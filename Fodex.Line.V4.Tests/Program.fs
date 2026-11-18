module Fodex.Line.V4.Tests.Program
open Expecto

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv (testSequenced (testList "LINE v4" [LineV4ScopeTests.tests;LineV4FocusTests.tests;LineV4DeliverySqlTests.tests;LineV4CliTranscriptTests.tests;LineV4RecoverySqlTests.tests;LineV4HostTests.tests]))
