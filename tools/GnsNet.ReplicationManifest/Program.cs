using System.Reflection;
using System.Runtime.Loader;
using GnsNet.Replication.Schema;

return Run(args);

static int Run(string[] args)
{
    try
    {
        if (args.Length == 0 || args[0] is "--help" or "-h") return Usage();
        return args[0] switch
        {
            "validate" => Validate(args[1..]),
            "verify" => Verify(args[1..]),
            "emit" => Emit(args[1..]),
            _ => Usage()
        };
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"replication-manifest: {exception.Message}");
        return 1;
    }
}

static int Validate(string[] args)
{
    ReplicationManifest current = ReplicationManifestValidator.Load(Required(args, "--baseline"));
    ReplicationManifest candidate = ReplicationManifestValidator.Load(Required(args, "--candidate"));
    return Report(ReplicationManifestValidator.ValidateEvolution(current, candidate));
}

static int Verify(string[] args)
{
    ReplicationManifest manifest = ReplicationManifestValidator.Load(Required(args, "--manifest"));
    string[] paths = Values(args, "--assembly").ToArray();
    if (paths.Length == 0) throw new ArgumentException("verify requires at least one --assembly path.");
    Assembly[] assemblies = paths.Select(LoadAssembly).ToArray();
    return Report(ReplicationManifestValidator.VerifyGeneratedDescriptors(manifest, assemblies));
}

static int Emit(string[] args)
{
    string[] paths = Values(args, "--assembly").ToArray();
    if (paths.Length == 0) throw new ArgumentException("emit requires at least one --assembly path.");
    Console.WriteLine(ReplicationManifestValidator.ToJson(ReplicationManifestValidator.FromGeneratedDescriptors(paths.Select(LoadAssembly))));
    return 0;
}

static Assembly LoadAssembly(string path)
{
    string fullPath = Path.GetFullPath(path);
    if (!File.Exists(fullPath)) throw new FileNotFoundException("Assembly was not found.", fullPath);
    return AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
}

static int Report(ReplicationManifestValidationResult result)
{
    if (result.IsValid) { Console.WriteLine("replication-manifest: valid"); return 0; }
    foreach (string error in result.Errors) Console.Error.WriteLine("replication-manifest: " + error);
    return 2;
}

static string Required(string[] args, string name) => Values(args, name).SingleOrDefault() ?? throw new ArgumentException($"{name} is required exactly once.");
static IEnumerable<string> Values(string[] args, string name)
{
    for (int index = 0; index < args.Length; index++)
        if (StringComparer.Ordinal.Equals(args[index], name))
        {
            if (++index == args.Length || args[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"{name} requires a value.");
            yield return args[index];
        }
}
static int Usage()
{
    Console.Error.WriteLine("Usage: replication-manifest validate --baseline <manifest> --candidate <manifest>");
    Console.Error.WriteLine("       replication-manifest verify --manifest <manifest> --assembly <assembly> [--assembly <assembly>]");
    Console.Error.WriteLine("       replication-manifest emit --assembly <assembly> [--assembly <assembly>]");
    return 64;
}
