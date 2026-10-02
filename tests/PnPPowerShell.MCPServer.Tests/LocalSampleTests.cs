using PnPPowerShell.MCPServer.Models;
using PnPPowerShell.MCPServer.Services;
using PnPPowerShell.MCPServer.Tools;
using System.Text.Json;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>The user's own samples, read from the folders PNP_SCRIPT_SAMPLES_PATH lists.</summary>
public sealed class LocalSampleTests : IDisposable
{
    private const string Script = """
        <#
        .SYNOPSIS
        Export list items to CSV
        .DESCRIPTION
        Writes every item of a list
        to a CSV file for auditing.
        .PARAMETER List
        The list to export.
        #>
        param($List)
        Get-PnPListItem -List $List | Export-Csv items.csv
        get-pnplistitem -List $List
        Connect-PnPOnline -Url https://contoso.sharepoint.com
        """;

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("pnp-local-samples");

    public void Dispose()
    {
        ScriptSampleIndex.Reload();
        DeleteTree(_folder.FullName);
    }

    /// <summary>Git writes its objects read-only, which Directory.Delete refuses on Windows.</summary>
    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_folder.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_script_is_described_by_its_comment_based_help()
    {
        var path = Write(Path.Combine("reports", "Export List.ps1"), Script);
        using var env = new EnvVar("PNP_SCRIPT_SAMPLES_PATH", _folder.FullName);

        var sample = Assert.Single(ScriptSampleIndex.ReadLocal());

        Assert.Equal("reports-Export-List", sample.Name);
        Assert.Equal("Export list items to CSV", sample.Title);
        Assert.Equal("Writes every item of a list to a CSV file for auditing.", sample.Description);
        Assert.Equal(["Get-PnPListItem", "Connect-PnPOnline"], sample.Tags);
        Assert.Equal(path, sample.LocalPath);
        Assert.Equal(new Uri(path).AbsoluteUri, sample.Url);
    }

    [Fact]
    public void A_script_without_help_is_titled_by_its_file_name()
    {
        Write("Get-Webs.ps1", "Get-PnPWeb");
        using var env = new EnvVar("PNP_SCRIPT_SAMPLES_PATH", _folder.FullName);

        var sample = Assert.Single(ScriptSampleIndex.ReadLocal());

        Assert.Equal("Get-Webs", sample.Title);
        Assert.Empty(sample.Description);
    }

    [Fact]
    public void Every_listed_folder_is_read_and_a_missing_one_is_skipped()
    {
        Write(Path.Combine("one", "a.ps1"), "Get-PnPWeb");
        Write(Path.Combine("two", "b.ps1"), "Get-PnPList");
        var missing = Path.Combine(_folder.FullName, "missing");
        using var env = new EnvVar(
            "PNP_SCRIPT_SAMPLES_PATH",
            $"{Path.Combine(_folder.FullName, "one")}; {missing} ;{Path.Combine(_folder.FullName, "two")}");

        Assert.Equal(["a", "b"], ScriptSampleIndex.ReadLocal().Select(s => s.Name));
    }

    [Fact]
    public void A_manifest_with_wrongly_typed_values_skips_only_its_sample()
    {
        Write(Path.Combine("scripts", "spo-good", "assets", "sample.json"), """[{"title":"Good"}]""");
        Write(Path.Combine("scripts", "spo-number", "assets", "sample.json"), """[{"title":1}]""");
        Write(Path.Combine("scripts", "spo-not-object", "assets", "sample.json"), "[1]");
        Write(Path.Combine("scripts", "spo-tag", "assets", "sample.json"), """[{"title":"Tag","tags":[1]}]""");
        using var env = new EnvVar("PNP_SCRIPT_SAMPLES_PATH", _folder.FullName);

        Assert.Equal(["spo-good"], ScriptSampleIndex.ReadLocal().Select(s => s.Name));
    }

    [Fact]
    public void Relative_paths_and_scp_style_urls_are_ignored()
    {
        Write("a.ps1", "Get-PnPWeb");
        using var env = new EnvVar("PNP_SCRIPT_SAMPLES_PATH", $"samples;git@github.com:contoso/samples.git;{_folder.FullName}");

        Assert.Equal(["a"], ScriptSampleIndex.ReadLocal().Select(s => s.Name));
        Assert.Equal(_folder.FullName, ScriptSampleIndex.SaveFolder());
    }

    /// <summary>A pattern spanning lines took 57 s on this input, holding the first sample call.</summary>
    [Fact]
    public void Help_that_never_closes_is_parsed_in_linear_time()
    {
        Write("open.ps1", "<#\n.SYNOPSIS" + new string('\n', 4_000));
        using var env = new EnvVar("PNP_SCRIPT_SAMPLES_PATH", _folder.FullName);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var sample = Assert.Single(ScriptSampleIndex.ReadLocal());

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Took {watch.Elapsed}.");
        Assert.Equal("open", sample.Title);
    }

    [Fact]
    public void A_folder_link_is_not_followed_so_a_loop_ends()
    {
        Write("x.ps1", "Get-PnPWeb");
        var link = Path.Combine(_folder.FullName, "loop");

        try
        {
            LinkFolder(link, _folder.FullName);
            using var env = new EnvVar("PNP_SCRIPT_SAMPLES_PATH", _folder.FullName);

            Assert.Equal(["x"], ScriptSampleIndex.ReadLocal().Select(s => s.Name));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task A_folder_swapped_for_a_link_after_indexing_is_not_read()
    {
        Write(Path.Combine("listed", "sub", "x.ps1"), "Get-PnPWeb");
        Write(Path.Combine("elsewhere", "x.ps1"), "NOT-FOR-THE-MODEL");
        using var env = new EnvVar("PNP_SCRIPT_SAMPLES_PATH", Path.Combine(_folder.FullName, "listed"));
        ScriptSampleIndex.Reload();
        Assert.Contains(ScriptSampleIndex.Samples, s => s.Name == "sub-x");

        var sub = Path.Combine(_folder.FullName, "listed", "sub");
        Directory.Delete(sub, recursive: true);

        try
        {
            LinkFolder(sub, Path.Combine(_folder.FullName, "elsewhere"));

            Assert.DoesNotContain("NOT-FOR-THE-MODEL", await ScriptSampleTools.GetScriptSample("sub-x"), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(sub);
        }
    }

    [Fact]
    public void A_clone_manifest_reached_through_a_link_is_not_read()
    {
        Write(Path.Combine("clone", "scripts", "spo-good", "assets", "sample.json"), """[{"title":"Good"}]""");
        Write(Path.Combine("elsewhere", "assets", "sample.json"), """[{"title":"Outside"}]""");
        var link = Path.Combine(_folder.FullName, "clone", "scripts", "spo-linked");

        try
        {
            LinkFolder(link, Path.Combine(_folder.FullName, "elsewhere"));
            using var env = new EnvVar("PNP_SCRIPT_SAMPLES_PATH", Path.Combine(_folder.FullName, "clone"));

            Assert.Equal(["spo-good"], ScriptSampleIndex.ReadLocal().Select(s => s.Name));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>Listed first, a plain folder could otherwise serve its README under a clone sample's name.</summary>
    [Fact]
    public async Task A_clone_sample_is_read_only_from_its_own_clone()
    {
        Write(Path.Combine("plain", "scripts", "spo-demo", "README.md"), "```powershell\nSPOOFED\n```");
        Write(Path.Combine("clone", "scripts", "spo-demo", "README.md"), "```powershell\nGet-PnPWeb\n```");
        Write(Path.Combine("clone", "scripts", "spo-demo", "assets", "sample.json"), """[{"title":"Demo"}]""");
        using var env = new EnvVar(
            "PNP_SCRIPT_SAMPLES_PATH",
            $"{Path.Combine(_folder.FullName, "plain")};{Path.Combine(_folder.FullName, "clone")}");
        ScriptSampleIndex.Reload();

        var output = await ScriptSampleTools.GetScriptSample("spo-demo");

        Assert.Contains("Get-PnPWeb", output, StringComparison.Ordinal);
        Assert.DoesNotContain("SPOOFED", output, StringComparison.Ordinal);
    }

    /// <summary>A junction on Windows, which needs no privilege; a symbolic link elsewhere.</summary>
    private static void LinkFolder(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            using var mklink = System.Diagnostics.Process.Start("cmd", ["/c", "mklink", "/J", link, target]);
            mklink.WaitForExit();
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }

        Assert.True(Directory.Exists(link));
    }

    /// <summary>A shared repository could otherwise link leak.ps1 to any file this user can read.</summary>
    [RequiresSymlinksFact]
    public void A_script_that_is_a_link_is_not_read()
    {
        Write(Path.Combine("listed", "real.ps1"), "Get-PnPWeb");
        var secret = Write("secret.txt", "not a sample");
        File.CreateSymbolicLink(Path.Combine(_folder.FullName, "listed", "leak.ps1"), secret);
        using var env = new EnvVar("PNP_SCRIPT_SAMPLES_PATH", Path.Combine(_folder.FullName, "listed"));

        Assert.Equal(["real"], ScriptSampleIndex.ReadLocal().Select(s => s.Name));
    }

    [Fact]
    public async Task Local_samples_join_the_index_replace_a_namesake_and_return_their_whole_script()
    {
        var namesake = ScriptSampleIndex.Samples.First(s => s.LocalPath.Length == 0).Name;
        Write(namesake + ".ps1", Script);
        using var env = new EnvVar("PNP_SCRIPT_SAMPLES_PATH", _folder.FullName);
        ScriptSampleIndex.Reload();

        Assert.NotEmpty(Assert.Single(ScriptSampleIndex.Samples, s => s.Name == namesake).LocalPath);
        Assert.Equal(namesake, ScriptSampleIndex.Search("export list items csv", 1)[0].Document.Name);
        Assert.Contains("Plus 1 from PNP_SCRIPT_SAMPLES_PATH.", ScriptSampleIndex.Provenance, StringComparison.Ordinal);

        var output = await ScriptSampleTools.GetScriptSample(namesake);

        Assert.StartsWith(ScriptSampleTools.FetchedContentNotice, output, StringComparison.Ordinal);
        Assert.Contains(Script, output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://github.com/contoso/samples.git", true)]
    [InlineData("ssh://git@github.com/contoso/samples.git", true)]
    [InlineData("http://example.com/samples.git", false)]
    [InlineData(@"C:\samples", false)]
    [InlineData("/home/me/samples", false)]
    [InlineData("samples", false)]
    public void Only_https_and_ssh_entries_are_git_repositories(string entry, bool expected) =>
        Assert.Equal(expected, SampleRepository.IsUrl(entry));

    /// <summary>Git's own order: GIT_SSH_COMMAND, then core.sshCommand, then GIT_SSH, then ssh.</summary>
    [Theory]
    [InlineData(null, null, null, "ssh -o BatchMode=yes")]
    [InlineData("ssh -i env", "ssh -i config", null, "ssh -i env -o BatchMode=yes")]
    [InlineData(null, "ssh -i config\n", "plink.exe", "ssh -i config -o BatchMode=yes")]
    [InlineData(" ", "", "plink.exe", null)]
    public void Ssh_runs_in_batch_mode_on_top_of_the_configured_command(string? env, string? config, string? gitSsh, string? expected) =>
        Assert.Equal(expected, SampleRepository.BatchSsh(env, config, gitSsh));

    [Fact]
    public void A_git_repository_is_cloned_updated_and_recloned_once_broken()
    {
        var repo = Path.Combine(_folder.FullName, "repo");
        var copy = SampleRepository.Folder(repo);

        try
        {
            Assert.True(SampleRepository.Git("init", "--quiet", repo));
            Commit(repo, "first.ps1");
            SampleRepository.Sync(repo);
            Commit(repo, "second.ps1");

            Assert.Equal(copy, SampleRepository.Sync(repo));
            Assert.True(File.Exists(Path.Combine(copy, "first.ps1")));
            Assert.True(File.Exists(Path.Combine(copy, "second.ps1")));

            // What a fetch killed by the timeout leaves behind; every later fetch then refuses to run.
            File.WriteAllText(Path.Combine(copy, ".git", "index.lock"), string.Empty);
            File.WriteAllText(Path.Combine(copy, ".git", "shallow.lock"), string.Empty);
            Commit(repo, "third.ps1");

            SampleRepository.Sync(repo);
            Assert.True(File.Exists(Path.Combine(copy, "third.ps1")));
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(copy)!, Path.GetFileName(copy) + ".*", new EnumerationOptions()));
        }
        finally
        {
            DeleteTree(copy);
        }
    }

    private void Commit(string repo, string file)
    {
        Write(Path.Combine(repo, file), "Get-PnPWeb");
        Assert.True(SampleRepository.Git("-C", repo, "add", "-A"));
        Assert.True(SampleRepository.Git(
            "-C", repo, "-c", "user.name=test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false",
            "commit", "--quiet", "-m", file));
    }

    [Fact]
    public void New_samples_go_to_the_first_plain_folder()
    {
        // A clone whose only manifest is broken is still a clone.
        var clone = Path.Combine(_folder.FullName, "clone");
        Write(Path.Combine(clone, "scripts", "spo-demo", "assets", "sample.json"), "not json");
        var plain = Path.Combine(_folder.FullName, "plain");
        using var env = new EnvVar("PNP_SCRIPT_SAMPLES_PATH", $"https://example.invalid/samples.git;{clone};{plain}");

        Assert.Equal(plain, ScriptSampleIndex.SaveFolder());
    }

    [Fact]
    public async Task A_saved_script_is_found_and_fetched_at_once()
    {
        using var env = new EnvVar("PNP_SCRIPT_SAMPLES_PATH", _folder.FullName);

        var saved = ScriptSampleTools.SaveScriptSample("Archive Inactive Sites", "Archive sites with no activity", "Get-PnPTenantSite");

        Assert.Contains("Saved as 'archive-inactive-sites'", saved, StringComparison.Ordinal);
        Assert.Equal("<#\n.SYNOPSIS\nArchive sites with no activity\n#>\n\nGet-PnPTenantSite\n", File.ReadAllText(Path.Combine(_folder.FullName, "archive-inactive-sites.ps1")));
        Assert.Equal("archive-inactive-sites", ScriptSampleIndex.Search("archive inactive sites", 1)[0].Document.Name);
        Assert.Contains("Get-PnPTenantSite", await ScriptSampleTools.GetScriptSample("archive-inactive-sites"), StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_refuses_a_script_too_large_to_be_indexed()
    {
        using var env = new EnvVar("PNP_SCRIPT_SAMPLES_PATH", _folder.FullName);

        var saved = ScriptSampleTools.SaveScriptSample("huge", "x", new string('Z', (int)ScriptSampleIndex.MaxScriptBytes));

        Assert.Contains("nothing was saved", saved, StringComparison.Ordinal);
        Assert.Empty(_folder.EnumerateFiles());
    }

    [Fact]
    public void Saving_never_overwrites()
    {
        var path = Write("taken.ps1", "original");
        using var env = new EnvVar("PNP_SCRIPT_SAMPLES_PATH", _folder.FullName);

        Assert.Contains("already exists", ScriptSampleTools.SaveScriptSample("taken", "x", "Get-PnPWeb"), StringComparison.Ordinal);
        Assert.Equal("original", File.ReadAllText(path));
    }

    [Fact]
    public void Saving_refuses_a_name_the_index_already_has_in_any_case()
    {
        var clone = Path.Combine(_folder.FullName, "clone");
        Write(Path.Combine(clone, "scripts", "spo-demo", "assets", "sample.json"), """[{"title":"Demo"}]""");
        var plain = Path.Combine(_folder.FullName, "plain");
        using var env = new EnvVar("PNP_SCRIPT_SAMPLES_PATH", $"{clone};{plain}");
        ScriptSampleIndex.Reload();
        var community = ScriptSampleIndex.Samples.First(s => s.LocalPath.Length == 0 && s.Name != "spo-demo").Name;

        Assert.Contains("already exists", ScriptSampleTools.SaveScriptSample("SPO-Demo", "x", "Get-PnPWeb"), StringComparison.Ordinal);
        Assert.Contains("already exists", ScriptSampleTools.SaveScriptSample(community.ToUpperInvariant(), "x", "Get-PnPWeb"), StringComparison.Ordinal);
        Assert.False(Directory.Exists(plain));
    }

    [Theory]
    [InlineData(false, "spo-x", "No sample folder is set up")]
    [InlineData(true, "..", "cannot be used as a file name")]
    [InlineData(true, "/", "cannot be used as a file name")]
    public void Saving_refuses_without_a_folder_or_a_usable_name(bool configured, string name, string expected)
    {
        using var env = new EnvVar("PNP_SCRIPT_SAMPLES_PATH", configured ? _folder.FullName : null);

        Assert.Contains(expected, ScriptSampleTools.SaveScriptSample(name, "x", "Get-PnPWeb"), StringComparison.Ordinal);
        Assert.Empty(_folder.EnumerateFiles());
    }

    [Fact]
    public void An_index_file_cannot_set_a_local_path()
    {
        var root = JsonSerializer.Deserialize(
            """{"samples":[{"name":"x","title":"t","localPath":"C:\\Windows\\win.ini"}]}""",
            ScriptSampleJsonContext.Default.SamplesRoot)!;

        Assert.Empty(root.Samples[0].LocalPath);
    }
}
