namespace uqoz

open Avalonia

module Program =

    [<EntryPoint>]
    let main (args: string array) =
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args)