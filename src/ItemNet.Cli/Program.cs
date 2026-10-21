using ItemNet.Cli;

// Headless entry points for development and batch work. Shared options:
//   --mo2 <instance base dir or ModOrganizer.ini>   (default G:\MO2Data\SkyrimSE)
//   --profile <name>                                (default: the instance's selected profile)
var opts = CliOptions.Parse(args);
try
{
    return opts.Command switch
    {
        "lo" => Commands.LoadOrder(opts),
        "parity" => Commands.Parity(opts),
        "hashtest" => Commands.HashTest(opts),
        "items" => Commands.Items(opts),
        "nifdump" => Commands.NifDump(opts),
        "plan" => Commands.Plan(opts),
        "render" => Commands.Render(opts),
        "skintest" => Commands.SkinTest(opts),
        "sync" => Commands.Sync(opts),
        "upsert" => Commands.Upsert(opts),
        "import-skyrimnet" => Commands.ImportSkyrimNet(opts),
        "prompt" => Commands.Prompt(opts),
        _ => Usage(),
    };
}
catch (Exception e)
{
    Console.Error.WriteLine(e);
    return 1;
}

static int Usage()
{
    Console.WriteLine("""
        ItemNet.Cli <command> [options]
          lo                       load order + file system summary, checked against MO2's loadorder.txt
          parity --db <path>       map SkyrimNet OmniSight item rows back to records and compare names
          items [--out <file>]     the GUI's item list, sorted by FormKey, for diffing two sources
                [--why <keys>]     plus each override of those FormKeys, with the file it came from
        """);
    return 2;
}
