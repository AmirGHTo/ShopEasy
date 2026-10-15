namespace ShopEasy.UnitTests.Support;

// Reads files of the repository itself (appsettings, Compose, manifests) for the tests that make
// sure the places a setting is written down agree with each other and with the code.
public static class RepositoryFiles
{
    public static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShopEasy.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("ShopEasy.sln not found above the test output.");
    }

    public static string Read(params string[] path) => File.ReadAllText(Path.Combine([Root, .. path]));
}
