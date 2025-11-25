using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Opc.Ua;
using Opc.Ua.Client;
using Serilog;
using System.Threading.Tasks;
using System.Timers;


public class WorkerService : BackgroundService
{

    // Your static fields moved from Program
    private static Session _opcSession;
    private static FileSystemWatcher _watcher;
    private static SessionReconnectHandler reconnectHandler = null;
    private static object lockObj = new object();
    public static System.Timers.Timer tmr;
    private static bool FileMove = true;
    private static string filename = string.Empty;
    private static readonly object _timerLock = new object();
    private static bool _isRunning = false; // optional but recommended

    private static byte ToBcd(int value) => (byte)(((value / 10) << 4) | (value % 10));

    // ====================================================================
    //  WORKER SERVICE ENTRY POINT (Replaces your Main)
    // ====================================================================
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // -------- Load config (same as your Main) ----------
            var builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

            IConfiguration configuration = builder.Build();

            Config.Load(configuration);

            // ------------------------ Logging ------------------------


            Log.Information("=== Worker Service Started ===");

            // ---------------------- OPC CONNECT -------------------
            await ConnectOPC();

            Log.Information("Watching folder: " + Config.WatchFolder);

            // ---------------------- TIMER -------------------------
            tmr = new System.Timers.Timer(15000);
            tmr.Elapsed += Tmr_Elapsed;
            tmr.Start();

            // Worker service loop (keeps service alive)
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Worker Service crashed.");
        }
    }

    private static async void Tmr_Elapsed(object? sender, ElapsedEventArgs e)
    {
        try
        {
            if (_isRunning)
                return; // prevent re-entry

            lock (_timerLock)
            {
                _isRunning = true;
            }
            if (!FileMove && !string.IsNullOrEmpty(filename))
            {
                // If someone manually deleted/moved the file, stop retrying.
                if (!File.Exists(filename))
                {
                    Log.Warning($"Retry skipped. File not found: {filename}. It may have been moved manually.");
                    filename = string.Empty;   // clear so it won't retry again
                    FileMove = true;           // consider the move as resolved
                    return;
                }
                Log.Information("Trying to move the file again: " + filename);
                MoveToBackup(filename);
                if (FileMove)
                {
                    Log.Information("File moved successfully at retry.");
                    filename = string.Empty;

                }
                return;
            }
            var latestFiles = new DirectoryInfo(Config.WatchFolder)
           .GetFiles("*.txt")
           .OrderByDescending(f => f.CreationTime)
           .ToList();
            //.FirstOrDefault();
            if (latestFiles.Count == 0)
                return;
            if (latestFiles.Count == 1)
            {
                var latestFile = latestFiles[0];
                if (latestFile != null)
                {
                    if (_opcSession != null)
                    {
                        Log.Information("-----------------Cycle Start.------------------------");

                        Log.Information($"Trying to read file : {latestFile.FullName}");

                        var data = FileParser.Parse(latestFile.FullName);
                        if (data == null)
                        {
                            return;
                        }
                        if (await PlcOperation(data))
                        {
                            MoveToBackup(latestFile.FullName);
                            Log.Information("-----------------Cycle Completed.------------------------");
                        }
                    }
                    else
                    {
                        await ConnectOPC();
                    }
                }
            }
            else
            {
                Log.Warning("Multiple files detected. Remove unnecessary Files.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("File event error: {err}", ex.Message);
        }
        finally
        {
            lock (_timerLock)
            {
                _isRunning = false;
            }
        }
    }

    // OPC CONNECT (unchanged)
    [Obsolete]
    private static async Task ConnectOPC()
    {
        try
        {
            if (_opcSession == null)
            {
                Utils.SetTraceOutput(Utils.TraceOutput.Off);
                var config = new ApplicationConfiguration()
                {
                    ServerConfiguration = new ServerConfiguration
                    {
                        UserTokenPolicies = new UserTokenPolicyCollection(new[] { new UserTokenPolicy(UserTokenType.UserName) }),
                    },
                    ApplicationName = "MyConfig",
                    ApplicationType = ApplicationType.Client,
                    SecurityConfiguration = new SecurityConfiguration
                    {
                        ApplicationCertificate = new CertificateIdentifier
                        {
                            StoreType = @"Windows",
                            StorePath = @"CurrentUser\My",
                            SubjectName = Utils.Format(@"CN={0}, DC={1}", "MyHomework", System.Net.Dns.GetHostName())
                        },
                        TrustedPeerCertificates = new CertificateTrustList
                        {
                            StoreType = @"Windows",
                            StorePath = @"CurrentUser\TrustedPeople",
                        },
                        NonceLength = 32,
                        AutoAcceptUntrustedCertificates = true
                    },
                    ClientConfiguration = new ClientConfiguration { }
                };

                config.CertificateValidator = new CertificateValidator();
                config.CertificateValidator.CertificateValidation += (s, certificateValidationEventArgs) =>
                {
                    certificateValidationEventArgs.Accept = true;
                };

                _opcSession = await Session.Create(config,
                        new ConfiguredEndpoint(null, new EndpointDescription(Config.OpcUrl)),
                        true,
                        "",
                        60000,
                        new UserIdentity(),
                        null);

                if (_opcSession.Connected)
                {
                    _opcSession.KeepAlive += _opcSession_KeepAlive;
                    Log.Information("OPC UA Connected Successfully.");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Error at ConnectOPCSession" + ex.Message);
        }
    }

    private static async void _opcSession_KeepAlive(ISession session, KeepAliveEventArgs e)
    {
        try
        {
            if (e.Status != null && ServiceResult.IsNotGood(e.Status))
            {
                Log.Error("❌ PLC Disconnected: " + e.Status);

                lock (lockObj)
                {
                    if (reconnectHandler == null)
                    {
                        reconnectHandler = new SessionReconnectHandler();

                        reconnectHandler.BeginReconnect(
                            session,
                            3000,
                            Client_ReconnectComplete
                        );

                        Log.Warning("🔄 Reconnecting...");
                    }
                }
            }
        }
        catch (Exception)
        {
            throw;
        }
    }

    private static void Client_ReconnectComplete(object? sender, EventArgs e)
    {
        lock (lockObj)
        {
            if (sender is SessionReconnectHandler handler)
            {
                _opcSession = (Session)handler.Session;
                reconnectHandler = null;

                Log.Information("🟢 PLC Reconnected Successfully!");
            }
        }
    }

    private static async void File_Created(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (_opcSession != null)
            {
                Log.Information("Cycle Start.");

                WaitForFile(e.FullPath);
                Log.Information($"File arrived: {e.FullPath}");

                var data = FileParser.Parse(e.FullPath);
                if (data == null)
                {
                    Log.Error("File parsing failed or missing required data.");
                    return;
                }
                if (await PlcOperation(data))
                {
                    MoveToBackup(e.FullPath);
                    Log.Information("Cycle Completed.");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("File event error: {err}", ex.Message);
        }
    }

    private static void WaitForFile(string file)
    {
        while (true)
        {
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return;
            }
            catch { System.Threading.Thread.Sleep(200); }
        }
    }

    private static byte[] ConvertToSiemensDT(DateTime dt)
    {
        byte[] data = new byte[8];

        int year = dt.Year % 100;

        data[0] = ToBcd(year);
        data[1] = ToBcd(dt.Month);
        data[2] = ToBcd(dt.Day);
        data[3] = ToBcd(dt.Hour);
        data[4] = ToBcd(dt.Minute);
        data[5] = ToBcd(dt.Second);

        int ms = dt.Millisecond;
        data[6] = ToBcd(ms / 10);
        data[7] = ToBcd(ms % 10);

        return data;
    }

    private static async Task<bool> PlcOperation(TestData data)
    {
        if (_opcSession != null)
        {
            var ctd = new CancellationTokenSource();
            CancellationToken token = ctd.Token;
            string? serial = "";

            byte[] siemensDT = ConvertToSiemensDT(data.TestDate);

            try
            {
                var value = await _opcSession.ReadValueAsync(Config.Node_SerialNo);
                serial = value.Value?.ToString();
            }
            catch (Exception ex)
            {
                if (ex.Message == "BadNotConnected" || ex.Message == "BadConnectionClosed")
                {
                    Log.Error("PLC Communication issue {err} :", ex.Message);
                }
                else
                {
                    Log.Error("Error while read Serial No: {err}", ex.Message);
                }

                return false;
            }

            try
            {
                if (!string.IsNullOrEmpty(serial))
                {
                    var writes = new WriteValueCollection()
                    {
                        new WriteValue {
                            NodeId = Config.Node_SerialNo,
                            AttributeId = Attributes.Value,
                            Value = new DataValue(serial)
                        },
                        new WriteValue {
                            NodeId = Config.Node_TestValue,
                            AttributeId = Attributes.Value,
                            Value = new DataValue(data.TestValue)
                        },
                        new WriteValue {
                            NodeId = Config.Node_TestDate,
                            AttributeId = Attributes.Value,
                            Value = new DataValue(new Variant(siemensDT))
                        },
                        new WriteValue {
                            NodeId = Config.Node_Status,
                            AttributeId = Attributes.Value,
                            Value = new DataValue(data.Status)
                        },
                        new WriteValue {
                            NodeId = Config.TOL_HI,
                            AttributeId = Attributes.Value,
                            Value = new DataValue(data.TolHi)
                        },
                        new WriteValue {
                            NodeId = Config.TOL_LO,
                            AttributeId = Attributes.Value,
                            Value = new DataValue(data.TolLo)
                        },
                        new WriteValue {
                            NodeId = Config.TOL_OK,
                            AttributeId = Attributes.Value,
                            Value = new DataValue(data.TolOk)
                        }
                    };

                    WriteResponse response = await _opcSession.WriteAsync(null, writes, token);

                    List<bool> resultList = response.Results.Select(r => r == StatusCodes.Good).ToList();

                    if (resultList.All(x => x))
                    {
                        Log.Information($"Parameter Write to PLC - TestValue : {data.TestValue} | High Toll : {data.TolHi} | Set Value : {data.TolOk} | Low Toll : {data.TolLo} | Test Date : {data.TestDate} | Status : {data.Status}");

                        try
                        {
                            var writes2 = new WriteValueCollection()
                            {
                                new WriteValue {
                                    NodeId = Config.Node_ScanFlag,
                                    AttributeId = Attributes.Value,
                                    Value = new DataValue(new Variant(false))
                                }
                            };

                            WriteResponse response2 = await _opcSession.WriteAsync(null, writes2, token);

                            if (response2.Results[0] == StatusCodes.Good)
                            {
                                return true;
                            }
                            else
                            {
                                Log.Error("Cycle bit not write to the PLC");
                                return false;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Error while Write Cycle flag to PLC: {err}", ex.Message);
                            return false;
                        }
                    }
                    else
                    {
                        Log.Error("All data not write to the PLC.");
                        return false;
                    }
                }
                else
                {
                    Log.Error("Serial No is getting Empty/null or waiting for cycle start.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error while Write Machine Data to PLC: {err}", ex.Message);
                return false;
            }
        }
        else
        {
            return false;
        }
    }

    private static void MoveToBackup(string file)
    {
        try
        {
            //throw new IOException("Simulated file move error."); // Simulate an error for testing
            string backupDir = Config.BackupFolder;
            Directory.CreateDirectory(backupDir);

            string dest = Path.Combine(backupDir, Path.GetFileName(file));
            File.Move(file, dest, true);
            FileMove = true;
            Log.Information($"File moved to: {dest}");
        }
        catch (Exception ex)
        {
            FileMove = false;
            filename = file;
            Log.Error("File Move Error: " + ex.Message);
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Log.Information("=== Worker Service stopping... ===");

        try
        {
            // ---- Stop the Timer ----
            if (tmr != null)
            {
                tmr.Stop();
                tmr.Elapsed -= Tmr_Elapsed;
                tmr.Dispose();
                Log.Information("Timer stopped.");
            }

            // ---- Dispose OPC Session ----
            if (_opcSession != null)
            {
                try
                {
                    _opcSession.Close();
                    _opcSession.Dispose();
                    Log.Information("OPC session closed.");
                }
                catch (Exception ex)
                {
                    Log.Error("Error while closing OPC session: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Error in StopAsync: " + ex.Message);
        }

        Log.Information("=== Worker Service stopped ===");

        await base.StopAsync(cancellationToken);
    }

}

// ============================================================
//  STATIC CLASSES: KEPT EXACTLY SAME AS YOUR ORIGINAL CODE
// ============================================================

public static class Config
{
    public static string WatchFolder { get; private set; } = string.Empty;
    public static string BackupFolder { get; private set; } = string.Empty;

    public static string OpcUrl { get; private set; } = string.Empty;

    public static string Node_SerialNo { get; private set; } = string.Empty;
    public static string Node_TestValue { get; private set; } = string.Empty;
    public static string Node_TestDate { get; private set; } = string.Empty;
    public static string Node_Status { get; private set; } = string.Empty;

    public static string TOL_HI { get; private set; } = string.Empty;
    public static string TOL_OK { get; private set; } = string.Empty;
    public static string TOL_LO { get; private set; } = string.Empty;

    public static string Node_ScanFlag { get; private set; } = string.Empty;

    public static void Load(IConfiguration config)
    {
        WatchFolder = config["AppConfig:WatchFolder"] ?? string.Empty;
        BackupFolder = config["AppConfig:BackupFolder"] ?? string.Empty;
        OpcUrl = config["AppConfig:OpcUrl"] ?? string.Empty;
        Node_SerialNo = config["AppConfig:Node_SerialNo"] ?? string.Empty;
        Node_TestValue = config["AppConfig:Node_TestValue"] ?? string.Empty;
        Node_TestDate = config["AppConfig:Node_TestDate"] ?? string.Empty;
        Node_Status = config["AppConfig:Node_Status"] ?? string.Empty;
        TOL_HI = config["AppConfig:TOL_HI"] ?? string.Empty;
        TOL_OK = config["AppConfig:TOL_OK"] ?? string.Empty;
        TOL_LO = config["AppConfig:TOL_LO"] ?? string.Empty;
        Node_ScanFlag = config["AppConfig:Node_ScanFlag"] ?? string.Empty;
    }
}

public class TestData
{
    public float TestValue { get; set; }
    public float TolHi { get; set; }
    public float TolOk { get; set; }
    public float TolLo { get; set; }
    public string Status { get; set; }
    public DateTime TestDate { get; set; }
}

public static class FileParser
{
    public static TestData Parse(string file)
    {
        try
        {
            var dict = new Dictionary<string, string>();

            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.Contains("=")) continue;

                var parts = line.Split('=', 2);
                string key = parts[0].Trim();
                string value = parts[1].Trim();

                dict[key] = value;
            }
            if (IsMissing(dict, "Test_Value") || IsMissing(dict, "TOL_HI") || IsMissing(dict, "TOL_OK")
                || IsMissing(dict, "TOL_LO") || IsMissing(dict, "Test_Date"))
            {
                Log.Error("File parsing failed or missing required data.");
                return null;    // return null if ANY input missing
            }
            float testValue = GetDouble(dict, "Test_Value");
            float tolHi = GetDouble(dict, "TOL_HI");
            float tolOk = GetDouble(dict, "TOL_OK");
            float tolLo = GetDouble(dict, "TOL_LO");
            DateTime date = GetDate(dict, "Test_Date");

            double lowerLimit = tolOk - tolLo;
            double upperLimit = tolOk + tolHi;


            string status = (testValue >= lowerLimit && testValue <= upperLimit)
                              ? "OK"
                              : "NG";

            return new TestData
            {
                TestValue = testValue,
                TolHi = tolHi,
                TolOk = tolOk,
                TolLo = tolLo,
                TestDate = date,
                Status = status
            };
        }
        catch (Exception ex)
        {
            Log.Error("File Read Error: " + ex.Message);
            return null;
        }
    }
    private static bool IsMissing(Dictionary<string, string> dict, string key)
    {
        return !dict.ContainsKey(key) || string.IsNullOrWhiteSpace(dict[key]);
    }

    private static float GetDouble(Dictionary<string, string> dict, string key)
    {
        return dict.ContainsKey(key) && float.TryParse(dict[key], out float val)
            ? val
            : 0;
    }

    private static DateTime GetDate(Dictionary<string, string> dict, string key)
    {
        return dict.ContainsKey(key) && DateTime.TryParse(dict[key], out DateTime dt)
            ? dt
            : DateTime.MinValue;
    }
}
