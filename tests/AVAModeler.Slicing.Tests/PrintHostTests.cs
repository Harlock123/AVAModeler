using System.Net;
using System.Text;

namespace AVAModeler.Slicing.Tests;

/// <summary>A pretend OctoPrint or Moonraker: records what it was sent and answers as told. No real network.</summary>
internal sealed class FakePrinterServer(HttpStatusCode status = HttpStatusCode.Created, string body = "{}", bool refuse = false) : HttpMessageHandler
{
    public HttpRequestMessage? Request;
    public string Form = "";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        if (refuse) throw new HttpRequestException("Connection refused");
        Request = request;
        Form = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancel);
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json"), ReasonPhrase = status.ToString() };
    }
}

public class PrintHostTests
{
    private static readonly byte[] GCode = Encoding.UTF8.GetBytes(";FLAVOR:Marlin\nG28\n");

    [Fact]
    public async Task OctoPrint_gets_the_file_in_its_upload_with_the_key_in_its_header_and_print_set()
    {
        var server = new FakePrinterServer();
        var host = new PrintHost { Kind = PrintHostKind.OctoPrint, Url = "http://octopi.local/", StartPrint = true };
        string said = await PrintHostClient.SendAsync(host, "SECRETKEY1234", "cube.gcode", GCode, new HttpClient(server), TestContext.Current.CancellationToken);
        Assert.Equal("Sent cube.gcode to octopi.local, and it's printing.", said);
        Assert.Equal(HttpMethod.Post, server.Request!.Method);
        Assert.Equal("http://octopi.local/api/files/local", server.Request.RequestUri!.ToString());
        Assert.Equal("SECRETKEY1234", Assert.Single(server.Request.Headers.GetValues("X-Api-Key")));
        Assert.Contains("name=file; filename=cube.gcode", server.Form);
        Assert.Contains(";FLAVOR:Marlin", server.Form);
        Assert.Contains("name=print", server.Form);
        Assert.Contains("true", server.Form);
    }

    [Fact]
    public async Task Moonraker_gets_its_own_upload_and_needs_no_key()
    {
        var server = new FakePrinterServer();
        var host = new PrintHost { Kind = PrintHostKind.Moonraker, Url = "http://192.168.1.50:7125" };
        string said = await PrintHostClient.SendAsync(host, null, "cube.gcode", GCode, new HttpClient(server), TestContext.Current.CancellationToken);
        Assert.Equal("Sent cube.gcode to 192.168.1.50:7125: start it there when you're ready.", said);
        Assert.Equal("http://192.168.1.50:7125/server/files/upload", server.Request!.RequestUri!.ToString());
        Assert.False(server.Request.Headers.Contains("X-Api-Key"));
        Assert.Contains("false", server.Form);
    }

    [Fact]
    public async Task Failures_are_one_plain_line_and_never_show_the_key()
    {
        var host = new PrintHost { Kind = PrintHostKind.OctoPrint, Url = "http://octopi.local" };
        async Task<string> Fails(FakePrinterServer s) => (await Assert.ThrowsAsync<PrintHostException>(() => PrintHostClient.SendAsync(host, "SECRETKEY1234", "a.gcode", GCode, new HttpClient(s), TestContext.Current.CancellationToken))).Message;
        Assert.Contains("refused the API key", await Fails(new FakePrinterServer(HttpStatusCode.Forbidden)));
        Assert.Contains("has no OctoPrint upload there", await Fails(new FakePrinterServer(HttpStatusCode.NotFound)));
        string busy = await Fails(new FakePrinterServer(HttpStatusCode.Conflict, """{"error":"Printer is not operational, SECRETKEY1234"}"""));
        Assert.Contains("is busy", busy);
        Assert.Contains("Printer is not operational", busy);
        Assert.DoesNotContain("SECRETKEY1234", busy);
        Assert.Contains("Can't reach the printer at octopi.local", await Fails(new FakePrinterServer(refuse: true)));
        Assert.Contains("500", await Fails(new FakePrinterServer(HttpStatusCode.InternalServerError, "oops")));
        // A bad address or no server chosen is said before anything is sent.
        Assert.Throws<ArgumentException>(() => new PrintHost { Kind = PrintHostKind.Moonraker, Url = "printer" }.Checked());
        await Assert.ThrowsAsync<PrintHostException>(() => PrintHostClient.SendAsync(new PrintHost(), null, "a.gcode", GCode, new HttpClient(new FakePrinterServer()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_key_is_kept_in_its_own_owner_only_file_and_shown_masked()
    {
        string dir = Path.Combine(Path.GetTempPath(), "avamodeler-host-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = PrintHostKey.In(dir);
            Assert.Null(store.Get());
            store.Set("  ABCDEF123456 ");
            Assert.Equal("ABCDEF123456", store.Get());
            Assert.Equal("ABCDEF123456", File.ReadAllText(Path.Combine(dir, PrintHostKey.FileName)));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, PrintHostKey.FileName)));
            Assert.Equal("••••3456", PrintHostKey.Mask(store.Get()));
            store.Set("");
            Assert.Null(store.Get());
            Assert.False(File.Exists(Path.Combine(dir, PrintHostKey.FileName)));
            // With no folder (a window made for tests), it's kept for the run only.
            var none = PrintHostKey.In(null);
            none.Set("RUNONLY");
            Assert.Equal("RUNONLY", none.Get());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        // The setup keeps the server but never a key.
        var setup = new PrintSetup { Host = new PrintHost { Kind = PrintHostKind.OctoPrint, Url = "http://octopi.local", StartPrint = true } };
        var back = PrintSetup.FromJson(setup.ToJson());
        Assert.Equal(setup.Host, back.Host);
        Assert.DoesNotContain("Key", setup.ToJson());
    }
}
