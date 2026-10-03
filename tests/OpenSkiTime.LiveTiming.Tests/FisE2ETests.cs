using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenSkiTime.LiveTiming.Harness;
using OpenSkiTime.LiveTiming.Publishing;
using Xunit;

namespace OpenSkiTime.LiveTiming.Tests;

[Collection("Live timing processes")]
public sealed class FisE2ETests
{
    [Fact]
    public async Task TcpWireAcknowledgementsFragmentationCorrectionsAndReconnectRestoreAllRuns()
    {
        using var stop=new CancellationTokenSource();
        using var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        var port=((IPEndPoint)listener.LocalEndpoint).Port;
        var messages=new ConcurrentQueue<string>(); var connections=0;
        var server=Task.Run(async()=>
        {
            try
            {
                while(!stop.IsCancellationRequested)
                {
                    using var tcp=await listener.AcceptTcpClientAsync(stop.Token); Interlocked.Increment(ref connections);
                    var stream=tcp.GetStream(); var buffer=new byte[4096]; var pending="";
                    while(!stop.IsCancellationRequested)
                    {
                        var count=await stream.ReadAsync(buffer,stop.Token); if(count==0) { break; }
                        pending+=Encoding.UTF8.GetString(buffer,0,count);
                        var end=pending.IndexOf("</livetiming>",StringComparison.Ordinal);
                        if(end<0) { continue; } end+="</livetiming>".Length;
                        var xml=pending[..end]; pending=pending[end..]; messages.Enqueue(xml);
                        var sequence=(string)XDocument.Parse(xml).Root!.Attribute("sequence")!;
                        var reply=Encoding.UTF8.GetBytes($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><livetiming sequence=\"{sequence}\"></livetiming>");
                        await stream.WriteAsync(reply.AsMemory(0,17),stop.Token); await Task.Delay(2,stop.Token);
                        await stream.WriteAsync(reply.AsMemory(17),stop.Token);
                    }
                }
            }
            catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
            catch(IOException) when(stop.IsCancellationRequested) { }
        },stop.Token);
        var logs=new List<string>();
        await using var publisher=new FisPublisher(new FisTcpTransport("127.0.0.1",port,logs.Add),"synthetic-test-secret");
        var state=SyntheticRace.Create(18); await publisher.PublishAsync(state,true,CancellationToken.None);
        foreach(var next in SyntheticRace.Simulate(state)) { state=next; await publisher.PublishAsync(state,false,CancellationToken.None); }
        state=SyntheticRace.Update(state,1,LiveStatus.Finished,5012); await publisher.PublishAsync(state,false,CancellationToken.None);
        var first=state.Runs[0];
        state=state with { CurrentRun=2,Runs=[first,first with { Number=2,Results=first.Results.Select(r=>r with { Status=LiveStatus.Ready,Hundredths=null,StartedAt=null,Intermediates=null }).ToArray() }] };
        publisher.Disconnect(); await publisher.PublishAsync(state,true,CancellationToken.None);
        state = SyntheticRace.Update(state, 1, LiveStatus.Finished, 6000);
        await publisher.PublishAsync(state, false, CancellationToken.None);
        state = SyntheticRace.Update(state, 2, LiveStatus.Finished, 5900);
        await publisher.PublishAsync(state, false, CancellationToken.None);
        // Correct run 1 while run 2's rows stay unchanged: the server must receive both runs again.
        var beforeCorrection = messages.Count;
        var changedFirst = first with { Results = first.Results.Select(r => r.Bib == 1 ? r with { Hundredths = 7000 } : r).ToArray() };
        state = state with { Version = state.Version + 1, Runs = [changedFirst, state.Runs[1]] };
        await publisher.PublishAsync(state, false, CancellationToken.None);
        var restored = messages.Skip(beforeCorrection).Select(XDocument.Parse).ToArray();
        Assert.Contains(restored, d => d.Root!.Element("startlist")?.Attribute("runno")?.Value == "1");
        Assert.Contains(restored, d => d.Root!.Element("startlist")?.Attribute("runno")?.Value == "2");
        Assert.Contains(restored, d => d.Root!.Elements("raceevent").Elements("finish").Any(e => e.Attribute("bib")?.Value == "1" && e.Element("time")?.Value == "1:10.00"));
        var runTwoFinish = restored.SelectMany(d => d.Root!.Elements("raceevent").Elements("finish")).Last(e => e.Attribute("bib")?.Value == "1");
        Assert.Equal("1:00.00", runTwoFinish.Element("time")!.Value);
        Assert.Equal("2", runTwoFinish.Element("rank")!.Value);
        Assert.Equal("13.26", runTwoFinish.Element("diff")!.Value);
        await publisher.KeepAliveAsync(CancellationToken.None);
        Assert.Equal(2,connections);
        ValidateProtocol(messages.ToArray());
        Assert.DoesNotContain(logs,l=>l.Contains("synthetic-test-secret",StringComparison.Ordinal));
        Assert.Contains(logs,l=>l.Contains("[REDACTED]",StringComparison.Ordinal));
        Assert.Contains(messages,m=>m.Contains("correction=\"y\"",StringComparison.Ordinal));
        var docs=messages.Select(XDocument.Parse).ToArray();
        Assert.Contains(docs,d=>d.Root!.Element("keepalive") is not null);
        Assert.Equal(3,docs.Count(d=>d.Root!.Element("startlist")?.Attribute("runno")?.Value=="1"));
        Assert.Contains(docs,d=>d.Root!.Element("startlist")?.Attribute("runno")?.Value=="2");
        await publisher.DisposeAsync(); stop.Cancel(); listener.Stop(); await server;
    }
    [Fact]
    public async Task HttpsRealTlsPostsRawUtf8ChecksSequenceAndRejectsProtocolErrors()
    {
        using var key=RSA.Create(2048);
        var request=new CertificateRequest("CN=localhost",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        var san=new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(san.Build());
        using var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
        using var certificate=X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx),null);
        var port=ProcessFixture.Port(); var messages=new ConcurrentQueue<string>(); var reject=false;
        var builder=WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o=>o.Listen(IPAddress.Loopback,port,o=>o.UseHttps(certificate)));
        await using var app=builder.Build();
        app.MapPost("/al/",async (HttpContext context)=>
        {
            Assert.StartsWith("text/plain",context.Request.ContentType);
            using var reader=new StreamReader(context.Request.Body); var xml=await reader.ReadToEndAsync(); messages.Enqueue(xml);
            var seq=(string)XDocument.Parse(xml).Root!.Attribute("sequence")!;
            context.Response.ContentType="application/xml";
            if(reject) { context.Response.StatusCode=400; await context.Response.WriteAsync("<livetiming><error>Rejected</error></livetiming>"); }
            else { await context.Response.WriteAsync($"<livetiming sequence=\"{seq}\"/>"); }
        });
        await app.StartAsync();
        var logs=new List<string>();
        using var handler=new HttpClientHandler { AllowAutoRedirect=false,ServerCertificateCustomValidationCallback=(_,cert,_,_)=>cert?.Thumbprint==certificate.Thumbprint };
        await using var publisher=new FisPublisher(new FisHttpsTransport($"https://127.0.0.1:{port}/al/",logs.Add,handler),"synthetic-test-secret");
        var state=SyntheticRace.Create(18); await publisher.PublishAsync(state,true,CancellationToken.None);
        foreach(var next in SyntheticRace.Simulate(state)) { state=next; await publisher.PublishAsync(state,false,CancellationToken.None); }
        ValidateProtocol(messages.ToArray());
        Assert.DoesNotContain(logs,l=>l.Contains("synthetic-test-secret",StringComparison.Ordinal));
        reject=true; await Assert.ThrowsAsync<HttpRequestException>(()=>publisher.KeepAliveAsync(CancellationToken.None));
        Assert.Throws<IOException>(()=>FisProtocol.ValidateAck("<livetiming sequence=\"wrong\"/>",1));
        Assert.Throws<IOException>(()=>FisProtocol.ValidateAck("<livetiming sequence=\"1\"><error>Rejected</error></livetiming>",1));
        await app.StopAsync();
    }
    private static void ValidateProtocol(string[] messages)
    {
        var docs=messages.Select(XDocument.Parse).ToArray();
        Assert.NotNull(docs[0].Root!.Element("raceinfo")); Assert.NotNull(docs[1].Root!.Element("startlist")); Assert.NotNull(docs[2].Root!.Element("command"));
        Assert.All(docs,d=> { Assert.Equal("9754",d.Root!.Attribute("codex")!.Value); Assert.NotNull(d.Root.Attribute("timestamp")); });
        var events=docs.SelectMany(d=>d.Root!.Elements("raceevent").Elements()).ToArray();
        foreach(var tag in new[]{"start","inter","finish","dns","dnf","dq"}) { Assert.Contains(events,e=>e.Name==tag); }
        Assert.All(events.Where(e=>e.Name=="finish"||e.Name=="inter"),e=> { Assert.NotNull(e.Element("time")); Assert.NotNull(e.Element("diff")); Assert.NotNull(e.Element("rank")); });
        Assert.Equal(docs.Length,docs.Select(d=>d.Root!.Attribute("sequence")!.Value).Distinct().Count());
    }
}
