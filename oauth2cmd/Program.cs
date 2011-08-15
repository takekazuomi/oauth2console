using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using Kayak;
using Kayak.Http;
using System.Web;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

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
            var ato = new AccessTokenObserver(scheduler);

            scheduler.Post(() =>
            {
                var reqD = new RequestDelegate();
                ato.Subscribe(reqD);
                
                KayakServer.Factory
                    .CreateHttp(reqD, scheduler)
                    .Listen(new IPEndPoint(IPAddress.Loopback, 8080));
            });

            var kayak = Task.Factory.StartNew(() =>
            {
                scheduler.Start();
            });

            Debug.WriteLine(ato.AccessToken);

            kayak.Wait();
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


        class RequestDelegate : IHttpRequestDelegate, IObservable<string>
        {
            List<IObserver<string>> observers = new List<IObserver<string>>();
            ReaderWriterLockSlim olock = new ReaderWriterLockSlim();

            string Get(string uri)
            {
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

                    OnAuthorization(AccessToken);
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

            public IDisposable Subscribe(IObserver<string> observer)
            {
                olock.EnterUpgradeableReadLock();
                try
                {
                    if (!observers.Contains(observer))
                    {
                        olock.EnterWriteLock();
                        try
                        {
                            observers.Add(observer);
                        }
                        finally {
                            olock.ExitWriteLock();
                        }
                    }
                }
                finally {
                    olock.ExitUpgradeableReadLock();
                }
                return new Unsubscriber(this, observer);
            }

            private void UnSubscribe(IObserver<string> observer)
            {
                olock.EnterUpgradeableReadLock();
                try
                {
                    if (!observers.Contains(observer))
                    {
                        olock.EnterWriteLock();
                        try
                        {
                            observers.Remove(observer);
                        }
                        finally
                        {
                            olock.ExitWriteLock();
                        }
                    }
                }
                finally
                {
                    olock.ExitUpgradeableReadLock();
                }
            }

            public void OnAuthorization(string accesToken)
            {
                foreach (var observer in observers)
                {
                    if (accesToken == null)
                        observer.OnError(new Exception("OAuth error"));
                    else
                        observer.OnNext(accesToken);
                }
            }

            private class Unsubscriber : IDisposable
            {
                RequestDelegate observers;
                IObserver<string> observer;

                public Unsubscriber(RequestDelegate observers, IObserver<string> observer)
                {
                    this.observers = observers;
                    this.observer = observer;
                }

                public void Dispose()
                {
                    if (observer != null) {
                        observers.UnSubscribe(observer);
                    }
                }
            }

        }

        public class AccessTokenObserver : IObserver<string>
        {
            private IDisposable unsubscriber;
            private IScheduler scheduler;
            public string AccessToken { set;get;}

            public AccessTokenObserver(IScheduler scheduler)
            {
                this.scheduler = scheduler;
            }

            public virtual void Subscribe(IObservable<string> provider)
            {
                if (provider != null)
                    unsubscriber = provider.Subscribe(this);
            }

            public virtual void OnCompleted()
            {
                Console.WriteLine("OnCompleted");
                this.Unsubscribe();
            }

            public virtual void OnError(Exception e)
            {
                Console.WriteLine(e.Message);
            }

            public virtual void OnNext(string value)
            {
                Console.WriteLine(value);
                AccessToken = value;
            }

            public virtual void Unsubscribe()
            {
                unsubscriber.Dispose();
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



