using System.Net;
using System.Security.Cryptography;
using HospitalPm.Domain.Updates;
using HospitalPm.Infrastructure.Operations;
using HospitalPm.Infrastructure.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Fetching an update over the network.
///
/// The download buys no trust, and these tests are mostly about proving that.
/// A server that serves a manifest we did not sign, an installer that does not
/// match its hash, a redirect to plaintext, a response that never ends — all
/// of it has to leave the machine exactly as it was, with nothing in the
/// update folder for someone to find later and wonder about.
///
/// Two layers are covered. UpdateDownloader is exercised against a real
/// HttpClient over a real loopback socket, because its job is entirely about
/// what HTTP does. UpdateService is exercised against a scripted downloader,
/// because its job is what happens to the bytes afterwards.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class UpdateDownloadTests(PostgresFixture fixture) : IDisposable
{
    private readonly List<string> _directories = [];
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch (ObjectDisposedException) { /* already gone */ }
        }

        foreach (var directory in _directories)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory is not worth failing a test run.
            }
        }
    }

    private string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "hospitalpm-download-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _directories.Add(path);
        return path;
    }

    // --- The downloader, against a real socket --------------------------------

    /// <summary>
    /// Answers whatever the test says, over http on loopback.
    ///
    /// http rather than https on purpose: every one of these cases must be
    /// refused before a socket is opened, so the listener exists only to prove
    /// nothing ever reaches it.
    /// </summary>
    private sealed class Listener : IDisposable
    {
        private readonly HttpListener _listener = new();
        public int Requests { get; private set; }

        public Listener(Func<HttpListenerContext, Task> handle)
        {
            var port = Random.Shared.Next(20000, 40000);
            Prefix = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Prefix);
            _listener.Start();

            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { return; }

                    Requests++;
                    try { await handle(context); }
                    catch (Exception e) when (e is HttpListenerException or IOException) { /* client gone */ }
                }
            });
        }

        public string Prefix { get; }

        public void Dispose()
        {
            _listener.Close();
        }
    }

    private UpdateDownloader NewDownloader(HttpMessageHandler? handler = null)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(10);
        _disposables.Add(http);
        return new UpdateDownloader(http, NullLogger<UpdateDownloader>.Instance);
    }

    [Fact]
    public async Task Plain_http_is_refused_without_opening_a_socket()
    {
        using var server = new Listener(async c =>
        {
            await c.Response.OutputStream.WriteAsync("should never be read"u8.ToArray());
            c.Response.Close();
        });

        var result = await NewDownloader().GetTextAsync(new Uri(server.Prefix), 1024, default);

        Assert.False(result.Ok);
        Assert.Contains("https", result.Problem!, StringComparison.OrdinalIgnoreCase);

        // The claim is not just "refused" but "refused before asking".
        Assert.Equal(0, server.Requests);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ftp://example.invalid/setup.exe")]
    public async Task Only_https_is_accepted(string url)
    {
        var result = await NewDownloader().GetTextAsync(new Uri(url), 1024, default);

        Assert.False(result.Ok);
        Assert.Contains("https", result.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A hostile or broken server that never stops sending must not be able to
    /// fill a hospital's disk. The cap is enforced on bytes actually written,
    /// not on what the server said it would send.
    /// </summary>
    [Fact]
    public async Task A_download_larger_than_the_limit_is_abandoned_and_deleted()
    {
        var destination = Path.Combine(NewDirectory(), "endless.exe");

        var result = await NewDownloader(new EndlessHandler())
            .GetFileAsync(new Uri("https://example.invalid/endless.exe"), destination, 512 * 1024, default);

        Assert.False(result.Ok);
        Assert.Contains("larger than", result.Problem!, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(destination), "A refused download was left on disk.");
    }

    /// <summary>Streams forever, and lies about its length while doing it.</summary>
    private sealed class EndlessHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new EndlessStream()),
                RequestMessage = request,
            };

            // Deliberately absent, so the running byte count is what stops
            // this rather than a header the server chose.
            response.Content.Headers.ContentLength = null;
            return Task.FromResult(response);
        }
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => count;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_missing_file_says_so_in_words_a_person_can_act_on()
    {
        var result = await NewDownloader(new StatusHandler(HttpStatusCode.NotFound))
            .GetTextAsync(new Uri("https://example.invalid/HospitalPM.update"), 1024, default);

        Assert.False(result.Ok);
        Assert.Contains("nothing at", result.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A hospital PC with no route out is the normal case, not an error case,
    /// and the message has to say what to do rather than name an exception.
    /// </summary>
    [Fact]
    public async Task No_internet_is_explained_rather_than_reported_as_a_crash()
    {
        var result = await NewDownloader(new ThrowingHandler(
                new HttpRequestException("boom", new System.Net.Sockets.SocketException(11001))))
            .GetTextAsync(new Uri("https://example.invalid/HospitalPM.update"), 1024, default);

        Assert.False(result.Ok);
        Assert.Contains("no internet", result.Problem!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("USB", result.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Found by pointing a running install at a self-signed test server: the
    /// general branch answered "The SSL connection could not be established,
    /// see inner exception", which is true and useless to whoever is standing
    /// at the machine.
    /// </summary>
    [Fact]
    public async Task An_untrusted_certificate_is_explained_without_naming_an_exception()
    {
        var result = await NewDownloader(new ThrowingHandler(
                new HttpRequestException("ssl",
                    new System.Security.Authentication.AuthenticationException("bad cert"))))
            .GetTextAsync(new Uri("https://example.invalid/HospitalPM.update"), 1024, default);

        Assert.False(result.Ok);
        Assert.Contains("could not be trusted", result.Problem!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("inner exception", result.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
    }

    private sealed class ThrowingHandler(Exception e) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(e);
    }

    // --- The service, against a scripted downloader ---------------------------

    /// <summary>Serves a manifest and an installer the test controls completely.</summary>
    private sealed class ScriptedDownloader : IUpdateDownloader
    {
        public string? ManifestText { get; init; }
        public byte[]? InstallerBytes { get; init; }
        public string? InstallerProblem { get; init; }
        public List<Uri> Requested { get; } = [];

        public Task<DownloadOutcome> GetTextAsync(Uri url, int maxBytes, CancellationToken ct)
        {
            Requested.Add(url);
            return Task.FromResult(ManifestText is null
                ? DownloadOutcome.Failed("no manifest")
                : DownloadOutcome.Succeeded(ManifestText));
        }

        public async Task<DownloadOutcome> GetFileAsync(
            Uri url, string destination, long maxBytes, CancellationToken ct)
        {
            Requested.Add(url);

            if (InstallerProblem is not null) return DownloadOutcome.Failed(InstallerProblem);
            if (InstallerBytes is null) return DownloadOutcome.Failed("no installer");

            await File.WriteAllBytesAsync(destination, InstallerBytes, ct);
            return DownloadOutcome.Succeeded();
        }
    }

    private sealed record Served(string ManifestText, byte[] InstallerBytes, string PublicKey, string Version);

    private static Served Sign(string version = "9999.5.0", byte[]? installer = null, ECDsa? key = null)
    {
        var bytes = installer ?? RandomNumberGenerator.GetBytes(32 * 1024);
        using var made = key is null ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
        var signer = key ?? made!;

        var manifest = new UpdateManifest(
            version,
            $"HospitalPM-Setup-{version}.exe",
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            bytes.Length,
            DateOnly.FromDateTime(DateTime.UtcNow),
            "Downloaded release.");

        var payload = UpdateManifestFile.Serialise(manifest);
        var signature = signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return new Served(
            UpdateManifestFile.Format(payload, signature),
            bytes,
            Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()),
            version);
    }

    private UpdateService NewService(
        string publicKey, string updatesFolder, IUpdateDownloader downloader, string feedUrl)
    {
        var backupOptions = Options.Create(new BackupOptions { Directory = NewDirectory() });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:HospitalPm"] = fixture.ConnectionString,
            })
            .Build();

        return new UpdateService(
            Options.Create(new UpdateOptions
            {
                PublicKey = publicKey,
                Directory = updatesFolder,
                StagingDirectory = NewDirectory(),
                BackupFirst = false,
                FeedUrl = feedUrl,
            }),
            new UpdateFileSystem(),
            new ThrowingLauncher(),
            downloader,
            new BackupService(
                fixture.CreateContext(),
                new PgToolLocator(backupOptions),
                configuration,
                backupOptions,
                TimeProvider.System,
                new HospitalPm.Infrastructure.Maintenance.HospitalClock(TimeProvider.System, Options.Create(new HospitalPm.Infrastructure.Maintenance.ScheduleOptions())),
                NullLogger<BackupService>.Instance),
            NullLogger<UpdateService>.Instance);
    }

    /// <summary>Checking must never install. If this is reached, something is very wrong.</summary>
    private sealed class ThrowingLauncher : IUpdateLauncher
    {
        public void Launch(string installerPath, string logPath) =>
            throw new InvalidOperationException("Checking for an update must not install one.");
    }

    [Fact]
    public async Task A_check_with_no_feed_configured_says_so_rather_than_failing()
    {
        var served = Sign();
        var service = NewService(served.PublicKey, NewDirectory(), new ScriptedDownloader(), feedUrl: "");

        var result = await service.CheckOnlineAsync(downloadInstaller: false);

        Assert.False(result.Reachable);
        Assert.Contains("No update address", result.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_downloaded_release_lands_exactly_where_a_usb_stick_would_have_put_it()
    {
        var served = Sign();
        var folder = NewDirectory();
        var downloader = new ScriptedDownloader
        {
            ManifestText = served.ManifestText,
            InstallerBytes = served.InstallerBytes,
        };

        var service = NewService(
            served.PublicKey, folder, downloader,
            "https://downloads.example.com/releases/latest/HospitalPM.update");

        var result = await service.CheckOnlineAsync(downloadInstaller: true);

        Assert.True(result.Reachable, result.Problem);
        Assert.Equal(UpdateState.Ready, result.Candidate!.State);

        // The installer URL is built from the feed's own folder, so a signed
        // manifest cannot redirect the download to another host.
        Assert.Equal(
            new Uri($"https://downloads.example.com/releases/latest/HospitalPM-Setup-{served.Version}.exe"),
            downloader.Requested[1]);

        // And what is on disk afterwards is what the offline path expects: the
        // manifest and the installer, in one folder, ready for Scan().
        Assert.True(File.Exists(Path.Combine(folder, $"HospitalPM-Setup-{served.Version}.exe")));
        var scanned = Assert.Single(service.Scan(folder));
        Assert.Equal(UpdateState.Ready, scanned.State);
    }

    /// <summary>
    /// The shape the release workflow actually configures.
    ///
    /// GitHub serves a release asset at
    /// releases/latest/download/&lt;asset name&gt;, so a feed pointing at
    /// HospitalPM.update in that folder puts the installer right beside it.
    /// That is the entire reason the manifest names a bare file name and the
    /// URL is built from the feed rather than from the manifest: get this
    /// wrong and every installed copy fetches a 404, which is only
    /// discoverable by shipping a release.
    /// </summary>
    /// <remarks>
    /// The versions are absurd because the comparison is against this test
    /// host's own assembly version, not against 1.0.0. A realistic-looking
    /// "1.2.0" is older than the runner and never gets as far as fetching
    /// an installer, which is a test that passes by doing nothing.
    /// </remarks>
    [Theory]
    [InlineData("9999.2.0")]
    [InlineData("9999.10.1")]
    public async Task The_installer_is_fetched_from_the_releases_folder_beside_the_manifest(string version)
    {
        const string feed =
            "https://github.com/AryantJadhav/Hospital_Machine_Manegment/releases/latest/download/HospitalPM.update";

        var served = Sign(version: version);
        var downloader = new ScriptedDownloader
        {
            ManifestText = served.ManifestText,
            InstallerBytes = served.InstallerBytes,
        };

        var service = NewService(served.PublicKey, NewDirectory(), downloader, feed);

        var result = await service.CheckOnlineAsync(downloadInstaller: true);

        Assert.Equal(UpdateState.Ready, result.Candidate!.State);
        Assert.Equal(2, downloader.Requested.Count);
        Assert.Equal(
            new Uri("https://github.com/AryantJadhav/Hospital_Machine_Manegment/releases/latest/"
                    + $"download/HospitalPM-Setup-{version}.exe"),
            downloader.Requested[1]);
    }

    /// <summary>
    /// The whole point of the design. A server we do not control serves a
    /// manifest signed by somebody else; nothing may be downloaded and nothing
    /// may be left behind.
    /// </summary>
    [Fact]
    public async Task A_manifest_signed_by_someone_else_is_refused_and_not_kept()
    {
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var theirs = Sign(key: attacker);
        var ours = Sign();

        var folder = NewDirectory();
        var downloader = new ScriptedDownloader
        {
            ManifestText = theirs.ManifestText,
            InstallerBytes = theirs.InstallerBytes,
        };

        var service = NewService(
            ours.PublicKey, folder, downloader, "https://downloads.example.com/HospitalPM.update");

        var result = await service.CheckOnlineAsync(downloadInstaller: true);

        Assert.Equal(UpdateState.NotOurs, result.Candidate!.State);

        // The installer was never asked for: the manifest failed first.
        Assert.Single(downloader.Requested);

        // And nothing was left in the folder pretending to be a pending update.
        Assert.Empty(Directory.GetFiles(folder));
    }

    /// <summary>
    /// A correctly signed manifest, and an installer that is not the file it
    /// describes — a corrupted mirror, a truncated transfer, or a server
    /// serving one thing and signing another.
    /// </summary>
    [Fact]
    public async Task An_installer_that_does_not_match_its_manifest_is_deleted()
    {
        var served = Sign();
        var folder = NewDirectory();

        var downloader = new ScriptedDownloader
        {
            ManifestText = served.ManifestText,
            InstallerBytes = RandomNumberGenerator.GetBytes(served.InstallerBytes.Length),
        };

        var service = NewService(
            served.PublicKey, folder, downloader, "https://downloads.example.com/HospitalPM.update");

        var result = await service.CheckOnlineAsync(downloadInstaller: true);

        Assert.Equal(UpdateState.InstallerAltered, result.Candidate!.State);
        Assert.False(
            File.Exists(Path.Combine(folder, $"HospitalPM-Setup-{served.Version}.exe")),
            "An installer that failed its hash check was left on disk.");
    }

    [Fact]
    public async Task Looking_without_downloading_fetches_only_the_manifest()
    {
        var served = Sign();
        var folder = NewDirectory();
        var downloader = new ScriptedDownloader
        {
            ManifestText = served.ManifestText,
            InstallerBytes = served.InstallerBytes,
        };

        var service = NewService(
            served.PublicKey, folder, downloader, "https://downloads.example.com/HospitalPM.update");

        var result = await service.CheckOnlineAsync(downloadInstaller: false);

        // A few hundred bytes answers "is there anything new" without
        // committing a poor connection to a couple of hundred megabytes.
        Assert.Single(downloader.Requested);
        Assert.Equal("9999.5.0", result.Candidate!.Manifest!.Version);
        Assert.Equal(UpdateState.InstallerMissing, result.Candidate.State);
    }

    [Fact]
    public async Task An_older_release_on_the_server_is_not_downloaded()
    {
        var served = Sign(version: "0.0.1");
        var folder = NewDirectory();
        var downloader = new ScriptedDownloader
        {
            ManifestText = served.ManifestText,
            InstallerBytes = served.InstallerBytes,
        };

        var service = NewService(
            served.PublicKey, folder, downloader, "https://downloads.example.com/HospitalPM.update");

        var result = await service.CheckOnlineAsync(downloadInstaller: true);

        Assert.Equal(UpdateState.NotNewer, result.Candidate!.State);
        Assert.Single(downloader.Requested);
    }

    [Fact]
    public async Task A_failed_installer_download_reports_the_reason_and_keeps_nothing()
    {
        var served = Sign();
        var folder = NewDirectory();
        var downloader = new ScriptedDownloader
        {
            ManifestText = served.ManifestText,
            InstallerProblem = "The download timed out.",
        };

        var service = NewService(
            served.PublicKey, folder, downloader, "https://downloads.example.com/HospitalPM.update");

        var result = await service.CheckOnlineAsync(downloadInstaller: true);

        Assert.True(result.Reachable);
        Assert.Equal("The download timed out.", result.Problem);
        Assert.False(File.Exists(Path.Combine(folder, $"HospitalPM-Setup-{served.Version}.exe")));
    }
}
