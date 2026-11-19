module LineV4Logging

open System
open System.IO

/// Configure before constructing any node/logger. Raw /show output uses its own hub.
let configure outputDirectory =
    let logs=Path.Combine(Path.GetFullPath outputDirectory,"logs")
    Directory.CreateDirectory logs |> ignore
    let config=new NLog.Config.XmlLoggingConfiguration(Path.Combine(AppContext.BaseDirectory,"NLog.LineV4.config"))
    config.Variables["lineV4LogDirectory"]<-NLog.Layouts.Layout.FromString logs
    NLog.LogManager.Configuration<-config

