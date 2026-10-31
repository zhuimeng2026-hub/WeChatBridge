using WeChatBridge.Windows.Core;

if (args.Length != 3 || !int.TryParse(args[1], out var processId) || !int.TryParse(args[2], out var count))
    return 2;

var root = args[0];
Directory.CreateDirectory(Path.Combine(root, "Logs"));
File.WriteAllText(Path.Combine(root, $"ready-{processId}"), "ready");
var start = Path.Combine(root, "start");
var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(30);
while (!File.Exists(start) && DateTime.UtcNow < timeout) await Task.Delay(10);
if (!File.Exists(start)) return 3;

var paths = new InboxPaths(root);
for (var i = 0; i < count; i++)
    DiagnosticLog.Info(paths, $"process-{processId:D2}-event-{i:D4}-" + new string('x', 512));
return 0;
