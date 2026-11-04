// TEST-ONLY: preload the Supervisor dependency versions to diagnose the downstream chain.
// This is not the production target startup order; its original binding failure remains open.
#r @"F:\fodex\Fodex.Supervisor\bin\Debug\net10.0\win-x64\Argu.dll"
#r @"F:\fodex\Fodex.Supervisor\bin\Debug\net10.0\win-x64\ModelContextProtocol.Core.dll"
#r @"F:\fodex\Fodex.Supervisor\bin\Debug\net10.0\win-x64\ModelContextProtocol.dll"
#r @"F:\fodex\Fodex.Supervisor\bin\Debug\net10.0\win-x64\ModelContextProtocol.AspNetCore.dll"
#r @"F:\fodex\Fodex.Supervisor\bin\Debug\net10.0\win-x64\FAkka.Shared.dll"
#r @"F:\fodex\Fodex.Supervisor\bin\Debug\net10.0\win-x64\FAkka.FCell2.dll"
#r @"F:\fodex\Fodex.Supervisor\bin\Debug\net10.0\win-x64\FsPickler.dll"
#r @"F:\fodex\Fodex.Supervisor\bin\Debug\net10.0\win-x64\FsPickler.Json.dll"
#r @"F:\fodex\Fodex.Supervisor\bin\Debug\net10.0\win-x64\Akka.Proc.Supervisor.dll"
#load "M018LineFodexE2E.fsx"
