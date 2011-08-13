using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using Kayak;
using Kayak.Http;
using System.Web;

namespace oauth2cmd
{
    class Program
    {
        static readonly string APP_ID = "241855485843490";
        static readonly string APP_SECRET = "7116b8d09037d005b1cfb33833112fc0";
        static readonly string ENDPOINT = "https://graph.facebook.com";
        static readonly string REDIRECT_URI = "http://localhost:8080/";
        static string AccessToken;

        static void OpenBrowser(string url)
        {
            var ps = new ProcessStartInfo();

            ps.FileName = url;
            ps.CreateNoWindow = true;
            ps.UseShellExecute = true;

            Process.Start(ps);
        }

        static string EncodeQuery(IDictionary<string,string> args)
        {
            var ret = new StringBuilder();

            if (args != null)
            {
                args.Keys.Aggregate((result, next) =>
                {
                    if (result != null)
                    {
                        ret.AppendFormat("{0}={1}", Uri.EscapeDataString(result), Uri.EscapeDataString(args[result]));
                    }
                    ret.AppendFormat("&{0}={1}", Uri.EscapeDataString(next), Uri.EscapeDataString(args[next]));

                    return null;
                });
            }
            return ret.ToString();
        }

        static string BuildUrl(string path, Dictionary<string, string> args = null)
        {
            return ENDPOINT + path + '?' + EncodeQuery(args);

        }

        void Run(string[] args)
        {
#if DEBUG
            Debug.Listeners.Add(new TextWriterTraceListener(Console.Out));
            Debug.AutoFlush = true;
#endif
            var url = BuildUrl("/oauth/authorize",
                        new Dictionary<string, string>()
                        {
                           { "client_id", APP_ID },
                           { "redirect_uri", REDIRECT_URI },
                           { "scope", "read_stream" },
                        });
                
                
            OpenBrowser(url);

            var scheduler = KayakScheduler.Factory.Create(new SchedulerDelegate());
            scheduler.Post(() =>
            {
                KayakServer.Factory
                    .CreateHttp(new RequestDelegate(), scheduler)
                    .Listen(new IPEndPoint(IPAddress.Loopback, 8080));
            });

            // runs scheduler on calling thread. this method will block until
            // someone calls Stop() on the scheduler.
            scheduler.Start();
        }



        static void Main(string[] args)
        {
#if DEBUG
            Debug.Listeners.Add(new TextWriterTraceListener(Console.Out));
            Debug.AutoFlush = true;
#endif
            try
            {
                var p = new Program();
                p.Run(args);
            }
            catch (Exception e)
            {
                Debug.WriteLine("Error on main.");
                e.DebugStackTrace();
            }

        }


        class SchedulerDelegate : ISchedulerDelegate
        {
            public void OnException(IScheduler scheduler, Exception e)
            {
                Debug.WriteLine("Error on scheduler.");
                e.DebugStackTrace();
            }

            public void OnStop(IScheduler scheduler)
            {
                Debug.WriteLine("Stop on scheduler.");
            }
        }

        class RequestDelegate : IHttpRequestDelegate
        {
            string Get(string uri) {
                var client = new WebClient();
                return client.DownloadString(uri);
            }

            public void OnRequest(HttpRequestHead request, IDataProducer requestBody,
                IHttpResponseDelegate response)
            {
                if (request.Uri.StartsWith("/?") && request.Method == "GET")
                {
                    var query = HttpUtility.ParseQueryString(request.Uri.Substring(2));
                    var message = String.Empty;

                    var code = query["code"];
                    if (code == null)
                    {
                        message = "Sorry, authentication failed.";
                        Debug.WriteLine(message);
                    }
                    else
                    {

                        var result = Get(
                            BuildUrl("/oauth/access_token",
                            new Dictionary<string, string>()
                        {
                           { "client_id", APP_ID },
                           { "redirect_uri", REDIRECT_URI },
                           { "client_secret", APP_SECRET},
                           { "code", code},
                        }));

                        var result2 = HttpUtility.ParseQueryString(result);
                        AccessToken = result2["access_token"];

                        message = "You have successfully logged in to facebook. token=" + AccessToken;
                        Debug.WriteLine(message);

                    }

                    var body = new BufferedProducer(message);

                    var headers = new HttpResponseHead()
                    {
                        Status = "200 OK",
                        Headers = new Dictionary<string, string>()
                        {
                           { "Content-Type", "text/html" },
                           { "Content-Length", body.Length.ToString()},
                        }
                    };

                    response.OnResponse(headers, body);
                }
                else
                {
                    var responseBody = "The resource you requested ('" + request.Uri + "') could not be found.";
                    var body = new BufferedProducer(responseBody);

                    var headers = new HttpResponseHead()
                    {
                        Status = "404 Not Found",
                        Headers = new Dictionary<string, string>()
                    {
                        { "Content-Type", "text/plain" },
                        { "Content-Length", body.Length.ToString() }
                    }
                    };

                    response.OnResponse(headers, body);
                }
            }
        }

        class BufferedProducer : IDataProducer
        {
            ArraySegment<byte> data;

            public BufferedProducer(string data) : this(data, Encoding.UTF8) { }
            public BufferedProducer(string data, Encoding encoding) : this(encoding.GetBytes(data)) { }
            public BufferedProducer(byte[] data) : this(new ArraySegment<byte>(data)) { }
            public BufferedProducer(ArraySegment<byte> data)
            {
                this.data = data;
            }

            public int Length
            {
               get { return data.Array.Length; }
            }

            public IDisposable Connect(IDataConsumer channel)
            {
                // null continuation, consumer must swallow the data immediately.
                channel.OnData(data, null);
                channel.OnEnd();
                return null;
            }
        }
    }
}



