//https://blog.darkthread.net/blog/-net6-args-parsing/
using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

class Program
{
    static async Task<int> Main(string[] args)
    {
        //args = new[] { "parse", "--file", "u_ex230412.log" };
        var logPathArgument = new Argument<FileInfo?>(
            "logPath",
            () => Directory.GetFiles(Directory.GetCurrentDirectory(), "u_ex*.log").Select(f => new FileInfo(f)).OrderByDescending(f => f.Name).FirstOrDefault(),
            description: "The IIS log file to parse. Default is the newest u_ex*.log file in current directory."
            );
        var methodOption = new Option<string>(
            new[] { "--method", "-m" },
            () => "*",
            "HTTP method to filter."
            )
        .FromAmong("*", "GET", "POST");

        var pathOption = new Option<string>(
            new[] { "--urlPath", "-p" },
            () => ".+",
            "Regular expression pattern to filter URL path."
            );

        var jsonFilePath = new Option<FileInfo?>(new[] { "--output", "-o" }, "The output file path to save parsed data JSON.");
        var rootCommand = new RootCommand("IIS burnout chart tool ver 0.9b");
        rootCommand.SetHandler(() => rootCommand.InvokeAsync("--help"));

        var parseCommand = new Command("parse", "Parse IIS log file and save result as json.");
        parseCommand.AddArgument(logPathArgument);
        parseCommand.AddOption(methodOption);
        parseCommand.AddOption(pathOption);
        parseCommand.AddOption(jsonFilePath);

        parseCommand.SetHandler((file, method, path, jsonFile) =>
            {
                var data = ParseLogFile(file!, method, path);
                jsonFile = jsonFile ?? new FileInfo(Path.ChangeExtension(file!.FullName, ".json"));
                Console.WriteLine($"Save parsed data to {jsonFile.FullName}.");
                File.WriteAllText(jsonFile.FullName, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            },
            logPathArgument, methodOption, pathOption, jsonFilePath);
        rootCommand.AddCommand(parseCommand);


        var jsonPathArgument = new Argument<FileInfo?>(
            "jsonPath",
            () => Directory.GetFiles(Directory.GetCurrentDirectory(), "u_ex*.json").Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTime).FirstOrDefault(),
            description: "The IIS load data json file to preview. Default is the newest u_ex*.json file in current directory."
            );
        var unitOption = new Option<string>(new[] { "--unit", "-u" },
            () => "m", "Time unit (hour/minute/second)").FromAmong("h", "m", "s");
        var startTimeOption = new Option<DateTime?>(new[] { "--startTime", "-s" }, "The start time of target period.");
        var endTimeOption = new Option<DateTime?>(new[] { "--endTime", "-e" }, "The end time of target period.");
        var durationOption = new Option<TimeSpan?>(new[] { "--dura", "-d" }, "The duration (TimeSpan in format 'hh:mm:ss') of target period.");

        var previewCommand = new Command("preview", "Preview IIS load data json file.");
        previewCommand.AddArgument(jsonPathArgument);
        previewCommand.AddOption(unitOption);
        previewCommand.AddOption(startTimeOption);
        previewCommand.AddOption(endTimeOption);
        previewCommand.AddOption(durationOption);
        previewCommand.SetHandler((jsonFile, unit, startTime, endTime, dura) =>
        {
            var timeFmt = "yyyy-MM-dd HH:mm:ss";
            if (unit == "h") timeFmt = "yyyy-MM-dd HH:00:00";
            else if (unit == "m") timeFmt = "yyyy-MM-dd HH:mm:00";
            var rawData = JsonSerializer.Deserialize<DataPoint[]>(File.ReadAllText(jsonFile!.FullName));
            var baseTime = rawData!.First().Time;
            (startTime, endTime) = GetTimeRange(baseTime, startTime, endTime, dura);

            var data = rawData!
                .Where(o => (startTime == null || o.Time >= startTime) && (endTime == null || o.Time <= endTime))
                .GroupBy(o => o.Time.ToString(timeFmt))
                .ToDictionary(
                    g => g.Key,
                    g => new DataPoint(DateTime.Parse(g.Key))
                    {
                        ReqCount = g.Sum(o => o.ReqCount),
                        SuccCount = g.Sum(o => o.SuccCount),
                        FailCount = g.Sum(o => o.FailCount),
                        TotalSuccDura = g.Sum(o => o.TotalSuccDura),
                        ErrCodes = g.SelectMany(o => o.ErrCodes.ToArray()).GroupBy(o => o.Key).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value))
                    });
            Console.WriteLine("      Time          |  Req (rps) | Succ (rps) | Fail (rps) | AvgDura(ms)|");
            Console.WriteLine("--------------------+------------+------------+------------+------------+");
            foreach (var kv in data.OrderBy(o => o.Key))
            {
                Console.WriteLine($"{kv.Key} | {kv.Value.ReqCount,10:n0} | {kv.Value.SuccCount,10:n0} | {kv.Value.FailCount,10:n0} | {kv.Value.AvgSuccDura,10:n0} |");
            }

        }, jsonPathArgument, unitOption, startTimeOption, endTimeOption, durationOption);
        rootCommand.AddCommand(previewCommand);


        var titleOption = new Option<string>(new[] { "--title", "-t" }, "The title of burnout chart.");
        var htmlFileOption = new Option<FileInfo?>(new[] { "--output", "-o" },
            () => new FileInfo(Path.Combine("htmls", $"{DateTime.Now:yyyyMMddHHmmss}.html")),
            "The output file path to save burnout chart html.");
        var chartCommand = new Command("chart", "Generate burnout chart from IIS load data json file.");
        chartCommand.AddArgument(jsonPathArgument);
        chartCommand.AddOption(startTimeOption);
        chartCommand.AddOption(endTimeOption);
        chartCommand.AddOption(durationOption);
        chartCommand.AddOption(titleOption);
        chartCommand.AddOption(htmlFileOption);
        chartCommand.SetHandler((jsonFile, startTime, endTime, dura, htmlFile, title) =>
        {
            var rawData = JsonSerializer.Deserialize<DataPoint[]>(File.ReadAllText(jsonFile!.FullName));
            var baseTime = rawData!.First().Time;
            (startTime, endTime) = GetTimeRange(baseTime, startTime, endTime, dura);
            var data = rawData!
                .Where(o => (startTime == null || o.Time >= startTime) && (endTime == null || o.Time <= endTime));
            if (!data.Any())
            {
                Console.WriteLine("No data matched.");
                return;
            }
            DrawBurnoutChart(title ?? $"Burnout Chart - {data.First().Key}", data, htmlFile!);
            //launch browser and open the html file
            Process.Start(new ProcessStartInfo(htmlFile!.FullName) { UseShellExecute = true });
        }, jsonPathArgument, startTimeOption, endTimeOption, durationOption, htmlFileOption, titleOption);
        rootCommand.AddCommand(chartCommand);

        return await rootCommand.InvokeAsync(args);
    }

    static (DateTime?, DateTime?) GetTimeRange(DateTime baseTime, DateTime? startTime, DateTime? endTime, TimeSpan? dura)
    {
        if (startTime != null && !startTime.Value.Date.Equals(baseTime.Date))
            startTime = baseTime.Date.AddSeconds(startTime.Value.Second).AddMinutes(startTime.Value.Minute).AddHours(startTime.Value.Hour);
        if (endTime == null && startTime != null && dura != null)
            endTime = startTime.Value.Add(dura.Value);
        if (endTime != null && !endTime.Value.Date.Equals(baseTime.Date))
            endTime = baseTime.Date.AddSeconds(endTime.Value.Second).AddMinutes(endTime.Value.Minute).AddHours(endTime.Value.Hour);
        return (startTime, endTime);
    }


    static IEnumerable<DataPoint> ParseLogFile(FileInfo file, string method, string path)
    {
        var points = new Dictionary<string, DataPoint>();
        var hdrLine = false;
        var regex = new Regex(path, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        int dateIdx = 0, timeIdx = 1, csMethodIdx = 3, csUriStemIdx = 4, scStatusIdx = 11, timeTakenIdx = 14;
        using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using (var sr = new StreamReader(fs))
            {
                string? line;
                int lineCount = 0;
                Console.WriteLine($"Parsing {file.FullName}...");
                var running = true;
                var reportProgressTask = Task.Factory.StartNew(() =>
                {
                    while (running) {
                        Console.CursorLeft = 0;
                        Console.Write($"{lineCount:n0} lines parsed.");
                        Thread.Sleep(1000);
                    }
                });
                while ((line = sr.ReadLine()) != null)
                {
                    lineCount++;
                    if (!hdrLine)
                    {
                        if (line.StartsWith("#Fields: "))
                        {
                            var fields = line.Substring(9).Split(' ');
                            for (int i = 0; i < fields.Length; i++)
                            {
                                switch (fields[i])
                                {
                                    case "date":
                                        dateIdx = i;
                                        break;
                                    case "time":
                                        timeIdx = i;
                                        break;
                                    case "cs-method":
                                        csMethodIdx = i;
                                        break;
                                    case "cs-uri-stem":
                                        csUriStemIdx = i;
                                        break;
                                    case "sc-status":
                                        scStatusIdx = i;
                                        break;
                                    case "time-taken":
                                        timeTakenIdx = i;
                                        break;
                                }
                            }
                        }
                        hdrLine = true;
                        continue;
                    }
                    else if (!line.StartsWith("#"))
                    {
                        var fields = line.Split(' ');
                        var respTime = DateTime.Parse(fields[dateIdx] + " " + fields[timeIdx]).ToLocalTime();
                        var timeTaken = fields[timeTakenIdx];
                        var reqTime = respTime.AddMilliseconds(-int.Parse(timeTaken));
                        reqTime = reqTime.AddMilliseconds(-reqTime.Millisecond);
                        if (method != "*" && fields[csMethodIdx] != method) continue;
                        if (!regex.IsMatch(fields[csUriStemIdx])) continue;
                        var scStatus = fields[scStatusIdx];
                        var reqTimeKey = reqTime.ToString("HH:mm:ss");
                        if (!points.ContainsKey(reqTimeKey))
                            points.Add(reqTimeKey, new DataPoint(reqTime));
                        var reqPoint = points[reqTimeKey];
                        var respTimeKey = respTime.ToString("HH:mm:ss");
                        if (!points.ContainsKey(respTimeKey))
                            points.Add(respTimeKey, new DataPoint(respTime));
                        var respPoint = points[respTimeKey];
                        reqPoint.ReqCount++;
                        if (scStatus.StartsWith("2") || scStatus.StartsWith("3"))
                        {
                            respPoint.SuccCount++;
                            respPoint.TotalSuccDura += int.Parse(timeTaken);
                        }
                        else
                        {
                            respPoint.FailCount++;
                            if (!respPoint.ErrCodes.ContainsKey(scStatus))
                                respPoint.ErrCodes.Add(scStatus, 0);
                            respPoint.ErrCodes[scStatus]++;
                        }
                    }
                }
                running = false;
                Console.CursorLeft = 0;
                Console.WriteLine($"{lineCount:n0} lines parsed.");
            }
        }
        return points.Values.OrderBy(p => p.Time);
    }

    static void DrawBurnoutChart(string title, IEnumerable<DataPoint> rawData, FileInfo htmlFile)
    {
        var baseTime = rawData.First().Time;
        var endTime = rawData.Last().Time;
        var totalSecs = (endTime - baseTime).TotalSeconds;
        var dict = rawData.ToDictionary(o => o.Time, o => o);
        var data = new List<DataPoint>();
        for (int i = 0; i < totalSecs; i++)
        {
            var time = baseTime.AddSeconds(i);
            data.Add(dict.ContainsKey(time) ? dict[time] : new DataPoint(time));
        }

        var chartOpt = new
        {
            labels = data.Select(o => (o.Time - baseTime).ToString(@"mm\:ss")).ToArray(),
            datasets = new List<object>()
        };
        void addDataSet(string label, IEnumerable<int?> data, string color, bool fill = false, string yAxisID = "y1", string backgroundColor = "rgba(0,0,0,0.1)")
        {
            chartOpt!.datasets.Add(new
            {
                label,
                data,
                backgroundColor,
                borderColor = color,
                borderWidth = 1,
                fill,
                yAxisID,
                pointRadius = 0
            });
        }
        addDataSet("Arrival (rps)", data.Select(o => o.ReqCount).Cast<int?>(), "orange");
        addDataSet("Succ (rps)", data.Select(o => o.SuccCount).Cast<int?>(), "green");
        addDataSet("Fail (rps)", data.Select(o => o.FailCount).Cast<int?>(), "red");
        addDataSet("Avg Dura (ms)", data.Select(o => o.AvgSuccDura), "blue", yAxisID: "y2");
        var html = File.ReadAllText("chart.html")
            .Replace("<script></script>", @$"<script>
setChartTitle({JsonSerializer.Serialize(title)}, '${baseTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")}');
let data={JsonSerializer.Serialize(chartOpt)};drawChart(data);
</script>");
        Directory.CreateDirectory(Path.GetDirectoryName(htmlFile.FullName)!);
        File.WriteAllText(htmlFile.FullName, html);
    }

    public class DataPoint
    {
        public DateTime Time { get; set; }
        public int ReqCount { get; set; }
        public int SuccCount { get; set; }
        public int FailCount { get; set; }
        public int TotalSuccDura { get; set; }
        public int? AvgSuccDura =>
            SuccCount > 0 ? TotalSuccDura / SuccCount : null;
        public int MaxSuccDura { get; set; }
        public Dictionary<string, int> ErrCodes = new Dictionary<string, int>();

        [JsonIgnore]
        public string Key => Time.ToString("HH:mm:ss");
        public DataPoint(DateTime time)
        {
            Time = time;
        }
    }
}
