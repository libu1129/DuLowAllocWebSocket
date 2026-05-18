using Xunit;

namespace DuLowAllocWebSocket.Tests;

public sealed class NativeZlibLoadCandidateTests
{
    [Fact]
    public void NativeAssets_IncludeLinuxX64ZlibNgCompatLibrary()
    {
        string repoRoot = FindRepoRoot();
        string linuxZlib = Path.Combine(repoRoot, "runtimes", "linux-x64", "native", "libz.so.1");

        Assert.True(File.Exists(linuxZlib), $"Missing native asset: {linuxZlib}");
        Assert.True(new FileInfo(linuxZlib).Length > 0);
    }

    [Fact]
    public void GetZlibLoadCandidates_WhenLinux_PrefersPackagedAndOptZlibNgBeforeSystemZlib()
    {
        const string baseDirectory = "/app";
        string packagedLinuxZlib = Path.Combine(baseDirectory, "runtimes", "linux-x64", "native", "libz.so.1");
        var candidates = DeflateInflater.GetZlibLoadCandidates(isLinux: true, baseDirectory);

        Assert.Equal(packagedLinuxZlib, candidates[0]);
        Assert.True(
            Array.IndexOf(candidates, "/opt/zlib-ng/lib/libz.so.1") >
            Array.IndexOf(candidates, packagedLinuxZlib));
        Assert.True(
            Array.IndexOf(candidates, "libz.so.1") >
            Array.IndexOf(candidates, "/opt/zlib-ng/lib/libz.so.1"));
    }

    [Fact]
    public void GetZlibLoadCandidates_WhenNotLinux_KeepsPlatformNames()
    {
        var candidates = DeflateInflater.GetZlibLoadCandidates(isLinux: false, baseDirectory: @"C:\app");

        Assert.Equal(@"C:\app\runtimes\win-x64\native\zlib1.dll", candidates[0]);
        Assert.Contains("libz.dylib", candidates);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DuLowAllocWebSocket.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("DuLowAllocWebSocket repository root was not found.");
    }
}
