﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;
using BF2Statistics.ASP;
using BF2Statistics.Database;
using BF2Statistics.Logging;
using BF2Statistics.Web.ASP;
using System.Linq;
using BF2Statistics.ASP.StatsProcessor;

namespace BF2Statistics.Web
{
    /// <summary>
    /// The ASP Server is used to emulate the official Gamespy BF2 Stats
    /// Server HTTP Requests, and provide players with the ability to run 
    /// thier own BF2 Ranking system on thier personal PC's.
    /// </summary>
    class HttpServer
    {
        /// <summary>
        /// The HTTPListner for the webserver
        /// </summary>
        private static HttpListener Listener;

        /// <summary>
        /// The StatsDebug.log file
        /// </summary>
        public static LogWriter AspStatsLog { get; protected set; }

        /// <summary>
        /// THe Http Server Access log
        /// </summary>
        public static LogWriter HttpAccessLog { get; protected set; }

        /// <summary>
        /// A List of local IP addresses for this machine
        /// </summary>
        public static readonly List<IPAddress> LocalIPs;

        /// <summary>
        /// An array of http request methods that this server will accept
        /// </summary>
        public static readonly string[] AcceptableMethods = { "GET", "POST", "HEAD" };

        /// <summary>
        /// Number of session web requests
        /// </summary>
        public static int SessionRequests { get; protected set; }

        /// <summary>
        /// Is the webserver running?
        /// </summary>
        public static bool IsRunning
        {
            get 
            {
                try 
                {
                    return Listener.IsListening;
                }
                catch (ObjectDisposedException) 
                {
                    Listener = new HttpListener();
                    Listener.Prefixes.Add("http://*/ASP/");
                    Listener.Prefixes.Add("http://*/bf2stats/");
                    return false;
                }
            }
        }

        /// <summary>
        /// Contains a list of Mime types for the response
        /// </summary>
        private static readonly Dictionary<string, string> MIMETypes = new Dictionary<string, string>
        {
            {"css", "text/css"},
            {"gif", "image/gif"},
            {"htm", "text/html"},
            {"html", "text/html"},
            {"ico", "image/x-icon"},
            {"jpeg", "image/jpeg"},
            {"jpg", "image/jpeg"},
            {"js", "application/x-javascript"},
            {"png", "image/png"}
        };

        /// <summary>
        /// Occurs when the server is started.
        /// </summary>
        public static event EventHandler Started;

        /// <summary>
        /// Occurs when the server is about to stop.
        /// </summary>
        public static event EventHandler Stopping;

        /// <summary>
        /// Occurs when the server is stopped.
        /// </summary>
        public static event EventHandler Stopped;

        /// <summary>
        /// Event fired when ASP server recieves a connection
        /// </summary>
        public static event AspRequest RequestRecieved;


        /// <summary>
        /// Static constructor
        /// </summary>
        static HttpServer()
        {
            // Create our Server and Access logs
            AspStatsLog = new LogWriter(Path.Combine(Program.RootPath, "Logs", "AspServer.log"));
            HttpAccessLog = new LogWriter(Path.Combine(Program.RootPath, "Logs", "AspAccess.log"), true);

            // Get a list of all our local IP addresses
            LocalIPs = new List<IPAddress>(Dns.GetHostAddresses(Dns.GetHostName()));
            SessionRequests = 0;

            // Create our HttpListener instance
            Listener = new HttpListener();
            Listener.Prefixes.Add("http://*/ASP/");
            Listener.Prefixes.Add("http://*/bf2stats/");
        }

        /// <summary>
        /// Start the ASP listener, and Connects to the stats database
        /// </summary>
        public static void Start()
        {
            if (!IsRunning)
            {
                // Try to connect to the database
                using (StatsDatabase Database = new StatsDatabase())  
                {
                    if (!Database.TablesExist)
                    {
                        string message = "In order to use the Private Stats feature of this program, we need to setup a database. "
                            + "You may choose to do this later by clicking \"Cancel\". Would you like to setup the database now?";
                        DialogResult R = MessageBox.Show(message, "Stats Database Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        if (R == DialogResult.Yes)
                            SetupManager.ShowDatabaseSetupForm(DatabaseMode.Stats);

                        // Call the stopped event to Re-enable the main forms buttons
                        Stopped(null, EventArgs.Empty);
                        return;
                    }

                    // Initialize the stats manager
                    StatsManager.Load(Database);

                    // Drop the SQLite ip2nation country tables
                    if (Database.DatabaseEngine == DatabaseEngine.Sqlite)
                    {
                        var Rows = Database.Query("SELECT COUNT(1) AS count FROM sqlite_master WHERE type='table' AND (name='ip2nation' OR name='ip2nationcountries');");
                        if (Rows.Count > 0 && Int32.Parse(Rows[0]["count"].ToString()) > 0)
                        {
                            Database.Execute("DROP TABLE IF EXISTS 'ip2nation';");
                            Database.Execute("DROP TABLE IF EXISTS 'ip2nationcountries';");
                            Database.Execute("VACUUM;");
                        }
                    }
                }

                // Load XML stats and awards files
                Bf2Stats.StatsData.Load();
                BackendAwardData.BuildAwardData();

                // Start the Listener and accept new connections
                try
                {
                    Listener.Start();
                    Listener.BeginGetContext(HandleRequest, Listener);
                }
                catch(ObjectDisposedException)
                {
                    // If we are disposed (happens when port 80 was in use already before, and we tried to start)
                    // Then we need to start over with a new Listener
                    Listener = new HttpListener();
                    Listener.Prefixes.Add("http://*/ASP/");
                    Listener.Prefixes.Add("http://*/bf2stats/");
                    Listener.Start();
                    Listener.BeginGetContext(HandleRequest, Listener);
                }
                
                // Fire Startup Event
                Started(null, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Stops the ASP listener, and unbinds from the port.
        /// </summary>
        public static void Stop()
        {
            if (IsRunning)
            {
                // Call Stopping Event to disconnect clients
                if (Stopping != null)
                    Stopping(null, EventArgs.Empty);

                try
                {
                    // Stop listening for HTTP requests
                    Listener.Stop();

                    // Fire the stopped event
                    Stopped(null, EventArgs.Empty);
                }
                catch(Exception E)
                {
                    Program.ErrorLog.Write("ERROR: [HttpServer.Stop] " + E.Message);
                }

                SessionRequests = 0;
            }
        }

        /// <summary>
        /// Accepts the connection
        /// </summary>
        private static void HandleRequest(IAsyncResult Sync)
        {
            try
            {
                // Finish accepting the client
                HttpListenerContext Context = Listener.EndGetContext(Sync);
                Task.Run(() => { ProcessRequest(new HttpClient(Context)); });
            }
            catch (HttpListenerException E)
            {
                // Thread abort, or application abort request... Ignore
                if (E.ErrorCode == 995)
                    return;

                // Log error
                Program.ErrorLog.Write(
                    "ERROR: [HttpServer.HandleRequest] \r\n\t - {0}\r\n\t - ErrorCode: {1}", 
                    E.Message, 
                    E.ErrorCode
                );
            }
            catch (Exception E)
            {
                Program.ErrorLog.Write("ERROR: [HttpServer.HandleRequest] \r\n\t - {0}", E.Message);
            }

            // Begin Listening again
            if(IsRunning) 
                Listener.BeginGetContext(HandleRequest, Listener);
        }

        /// <summary>
        /// Handles the Http Connecting client in a new thread
        /// </summary>
        private static void ProcessRequest(HttpClient Client)
        {
            // Update client count, and fire connection event
            StatsDatabase Database;
            SessionRequests++;
            RequestRecieved();

            // Make sure our stats Database is online
            try
            {
                // If we arent suppossed to be running, show a 503
                if (!IsRunning)
                    throw new Exception("Unable to accept client because the server is not running");

                // If database is offline, Try to re-connect
                Database = new StatsDatabase();
            }
            catch(Exception e)
            {
                Program.ErrorLog.Write("ERROR: [HttpServer.ProcessRequest] " + e.Message);

                // Set service is unavialable
                Client.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                Client.Response.Send();
                return;
            }

            // Make sure request method is supported
            if (!AcceptableMethods.Contains(Client.Request.HttpMethod))
            {
                //Program.ErrorLog.Write("NOTICE: [HttpServer.HandleRequest] Invalid HttpMethod {0} used by client", Client.Request.HttpMethod);
                Client.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                Client.Response.Send();
                return;
            }

            // Process Request
            try
            {
                // Get our requested document
                string Document = Client.Request.Url.AbsolutePath.ToLower().Trim();
                if (Client.IsASPRequest)
                {
                    DoASPResponse(Client, Document, Database);
                }
                else
                {
                    // If we dont have BF2S enabled, deny page access
                    if (!Program.Config.BF2S_Enabled)
                    {
                        // Set service is unavialable
                        Client.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                        Client.Response.Send();
                    }
                    else
                    {
                        DoStatsResponse(Client, Document, Database);
                    }
                }
            }
            catch (Exception E)
            {
                Program.ErrorLog.Write("ERROR: [HttpServer.ProcessRequest] " + E.Message);
                ExceptionHandler.GenerateExceptionLog(E);
                if (!Client.ResponseSent)
                {
                    // Internal service error
                    Client.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    Client.Response.Send();
                }
            }
            finally
            {
                // Make sure a response is sent to prevent client hang
                if (!Client.ResponseSent)
                    Client.Response.Send();

                Database.Dispose();
            }
        }

        private static void DoASPResponse(HttpClient Client, string Document, StatsDatabase Database)
        {
            switch (Document.Replace("/asp/", ""))
            {
                case "bf2statistics.php":
                    new SnapshotPost(Client, Database);
                    break;
                case "createplayer.aspx":
                    new CreatePlayer(Client, Database);
                    break;
                case "getbackendinfo.aspx":
                    new GetBackendInfo(Client);
                    break;
                case "getawardsinfo.aspx":
                    new GetAwardsInfo(Client, Database);
                    break;
                case "getclaninfo.aspx":
                    new GetClanInfo(Client, Database);
                    break;
                case "getleaderboard.aspx":
                    new GetLeaderBoard(Client, Database);
                    break;
                case "getmapinfo.aspx":
                    new GetMapInfo(Client, Database);
                    break;
                case "getplayerid.aspx":
                    new GetPlayerID(Client, Database);
                    break;
                case "getplayerinfo.aspx":
                    new GetPlayerInfo(Client, Database);
                    break;
                case "getrankinfo.aspx":
                    new GetRankInfo(Client, Database);
                    break;
                case "getunlocksinfo.aspx":
                    new GetUnlocksInfo(Client, Database);
                    break;
                case "ranknotification.aspx":
                    new RankNotification(Client, Database);
                    break;
                case "searchforplayers.aspx":
                    new SearchForPlayers(Client, Database);
                    break;
                case "selectunlock.aspx":
                    new SelectUnlock(Client, Database);
                    break;
                default:
                    Client.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    Client.Response.Send();
                    break;
            }
        }

        private static void DoStatsResponse(HttpClient Client, string Document, StatsDatabase Database)
        {
            // remove bf2stats folder and trim out the forward slashes
            Document = Document.Replace("/bf2stats", "").Trim(new char[] { '/' });

            // image, css, and js files
            if (Path.HasExtension(Document))
            {
                string Cpath = Path.Combine(Document.Split(new char[] { '/', '\\' }));
                string Rpath = Path.Combine(Program.RootPath, "Web", "Bf2Stats", "Resources", Cpath);
                if (File.Exists(Rpath))
                {
                    // Set the content type based from our extension
                    string Ext = Path.GetExtension(Cpath).TrimStart('.');
                    if(MIMETypes.ContainsKey(Ext))
                        Client.Response.ContentType = MIMETypes[Ext];

                    // Send response
                    Client.Response.Send(File.ReadAllBytes(Rpath));
                }
                else
                {
                    Client.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    Client.Response.Send();
                }
            }
            else
            {
                switch (Document)
                {
                    case "":
                    case "home":
                        Bf2Stats.HomePage Page = new Bf2Stats.HomePage(Client, Database);
                        Client.Response.ResponseBody.Append(Page.TransformText());
                        Client.Response.Send();
                        break;
                    case "search":
                        Bf2Stats.SearchPage SPage = new Bf2Stats.SearchPage(Client, Database);
                        Client.Response.ResponseBody.Append(SPage.TransformText());
                        Client.Response.Send();
                        break;
                    case "myleaderboard":
                        Bf2Stats.MyLeaderboardPage LPage = new Bf2Stats.MyLeaderboardPage(Client, Database);
                        Client.Response.ResponseBody.Append(LPage.TransformText());
                        Client.Response.Send();
                        break;
                    case "player":
                        Bf2Stats.PlayerPage PPage = new Bf2Stats.PlayerPage(Client, Database);
                        Client.Response.ResponseBody.Append(PPage.TransformHtml());
                        Client.Response.Send();
                        break;
                    case "rankings":
                        Bf2Stats.RankingsPage RPage = new Bf2Stats.RankingsPage(Client, Database);
                        Client.Response.ResponseBody.Append(RPage.TransformHtml());
                        Client.Response.Send();
                        break;
                    default:
                        Client.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        Client.Response.Send();
                        break;
                }
            }
        }
    }
}
