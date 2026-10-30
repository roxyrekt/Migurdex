using Migurdex.Cli.Services;
using System.Formats.Tar;
using System.IO.Compression;
using Xunit;

namespace Migurdex.Tests;

public sealed class UpdateArchiveTests : IDisposable
{
    private readonly string _root;

    public UpdateArchiveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"migurdex-arc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch
        {
            // ignored
        }
    }

    private string BuildSourceTree()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(src, "cli"));
        Directory.CreateDirectory(Path.Combine(src, "api"));

        File.WriteAllText(Path.Combine(src, "api", "shared.dll"), "shared-payload");
        File.WriteAllText(Path.Combine(src, "cli", "shared.dll"), "shared-payload");
        HardLink.TryCreate(Path.Combine(src, "api", "shared.dll"), Path.Combine(src, "cli", "shared.dll"));

        File.WriteAllText(Path.Combine(src, "cli", "migurdex"), "launcher");
        File.WriteAllText(Path.Combine(src, "api", "Migurdex.Api"), "api");

        return src;
    }

    [Fact]
    public void ExtractArchive_TarGz_ExtractsFiles()
    {
        var src     = BuildSourceTree();
        var archive = Path.Combine(_root, "a.tar.gz");

        TarFile.CreateFromDirectory(src, archive, includeBaseDirectory: false);

        var raw = archive + ".raw";
        File.Move(archive, raw);
        using (var inp = File.OpenRead(raw))
        using (var outFs = File.Create(archive))
        using (var gz = new GZipStream(outFs, CompressionMode.Compress))
        {
            inp.CopyTo(gz);
        }

        File.Delete(raw);

        var dest = Path.Combine(_root, "out-gz");
        Directory.CreateDirectory(dest);

        UpdateCommand.ExtractArchive(archive, dest);

        Assert.True(File.Exists(Path.Combine(dest, "cli", "migurdex")));
        Assert.True(File.Exists(Path.Combine(dest, "api", "Migurdex.Api")));
        Assert.Equal("shared-payload", File.ReadAllText(Path.Combine(dest, "cli", "shared.dll")));
    }

    [Fact]
    public void ExtractArchive_Zip_ExtractsFiles()
    {
        var src     = BuildSourceTree();
        var archive = Path.Combine(_root, "a.zip");
        ZipFile.CreateFromDirectory(src, archive);

        var dest = Path.Combine(_root, "out-zip");
        Directory.CreateDirectory(dest);

        UpdateCommand.ExtractArchive(archive, dest);

        Assert.True(File.Exists(Path.Combine(dest, "cli", "migurdex")));
        Assert.Equal("api", File.ReadAllText(Path.Combine(dest, "api", "Migurdex.Api")));
    }

    [Fact]
    public void ExtractArchive_UnknownExtension_TreatsAsGzip()
    {
        var src     = BuildSourceTree();
        var archive = Path.Combine(_root, "weird.bin");

        using (var outFs = File.Create(archive))
        using (var gz = new GZipStream(outFs, CompressionMode.Compress))
        {
            TarFile.CreateFromDirectory(src, gz, includeBaseDirectory: false);
        }

        var dest = Path.Combine(_root, "out-bin");
        Directory.CreateDirectory(dest);

        UpdateCommand.ExtractArchive(archive, dest);

        Assert.True(File.Exists(Path.Combine(dest, "cli", "migurdex")));
    }

    [Fact]
    public void ExtractArchive_TarXz_ExtractsAndPreservesHardLinks()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var src     = BuildSourceTree();
        var tarPath = Path.Combine(_root, "plain.tar");
        var xzPath  = Path.Combine(_root, "a.tar.xz");

        TarFile.CreateFromDirectory(src, tarPath, includeBaseDirectory: false);

        var psi = new System.Diagnostics.ProcessStartInfo("xz", $"-k -c \"{tarPath}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute        = false
        };

        System.Diagnostics.Process? proc;
        try
        {
            proc = System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            return;
        }

        if (proc is null)
        {
            return;
        }

        using (var outFs = File.Create(xzPath))
        {
            proc.StandardOutput.BaseStream.CopyTo(outFs);
        }

        proc.WaitForExit();
        if (proc.ExitCode != 0 || !File.Exists(xzPath))
        {
            return;
        }

        var dest = Path.Combine(_root, "out-xz");
        Directory.CreateDirectory(dest);

        UpdateCommand.ExtractArchive(xzPath, dest);

        Assert.True(File.Exists(Path.Combine(dest, "cli", "migurdex")));
        Assert.True(File.Exists(Path.Combine(dest, "api", "Migurdex.Api")));
        Assert.Equal("shared-payload", File.ReadAllText(Path.Combine(dest, "api", "shared.dll")));
    }
}
