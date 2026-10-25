// 数据库问答：MCP stdio 客户端 + OpenAI 兼容 tool 循环。
// 与 Tauri/Dart 版逐行为等价移植：
// - 与 scripts/db_mcp_server.py（只读 DuckDB MCP server）按行 JSON-RPC 2.0 交互
// - 工具定义由 tools/list 运行时发现（不硬编码）
// - 最多 5 轮工具检索，预算用尽/空正文后追加一轮禁用工具的强制汇总
// - OCR 与查库在同一后台任务内连续执行，答复在服务端先落盘再回传 UI

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SnapVision.Models;

namespace SnapVision.Services;

public sealed class DbTurn
{
    public DbTurn(string role, string text)
    {
        Role = role;
        Text = text;
    }

    public string Role { get; }
    public string Text { get; }
}

public sealed class DbOcrText
{
    public DbOcrText(int index, string text)
    {
        Index = index;
        Text = text;
    }

    public int Index { get; }
    public string Text { get; }
}

public sealed class DbChatOutcome
{
    public string Reply { get; set; }
    public List<DbOcrText> OcrTexts { get; set; }
}

public sealed class DbMcpService
{
    public static DbMcpService Instance { get; } = new();

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);
    private const int MaxToolRounds = 5;

    // ---------- 进程管理 ----------

    private sealed class McpProc : IDisposable
    {
        public Process Process;
        public LineQueue LineQueue;
        public StreamWriter Stdin;
        public int NextId = 1;
        public JsonArray Tools = new();
        public string SchemaText = "";

        public void Dispose()
        {
            LineQueue?.Dispose();
            try { if (Process != null && !Process.HasExited) Process.Kill(); } catch { }
            try { Process?.Dispose(); } catch { }
        }
    }

    /// <summary>行队列：把 stdout 流切成行，带等待者排队（Monitor 实现）。</summary>
    private sealed class LineQueue : IDisposable
    {
        private readonly Queue<string> _lines = new();
        private readonly object _gate = new();
        private bool _done;
        private bool _disposed;

        public LineQueue(StreamReader reader)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) OnLine(line);
                }
                catch { }
                finally
                {
                    OnDone();
                }
            })
            { IsBackground = true, Name = "mcp-stdout" };
            thread.Start();
        }

        private void OnLine(string line)
        {
            lock (_gate)
            {
                if (_done) return;
                _lines.Enqueue(line);
                Monitor.Pulse(_gate);
            }
        }

        private void OnDone()
        {
            lock (_gate)
            {
                _done = true;
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>取下一行；流结束返回 null；超时抛 TimeoutException。</summary>
        public string Next(TimeSpan timeout)
        {
            lock (_gate)
            {
                var deadline = DateTime.UtcNow + timeout;
                while (_lines.Count == 0 && !_done)
                {
                    var remain = deadline - DateTime.UtcNow;
                    if (remain <= TimeSpan.Zero || !Monitor.Wait(_gate, remain))
                    {
                        if (_lines.Count > 0 || _done) break;
                        throw new TimeoutException("line timeout");
                    }
                }
                return _lines.Count > 0 ? _lines.Dequeue() : null;
            }
        }

        public void Dispose()
        {
            lock (_gate) { _disposed = true; }
            OnDone();
        }
    }

    private McpProc _proc;
    private string _dbPathOverride;

    /// <summary>
    /// MCP server 定位：开发机源码热迭代优先，打包版兜底。
    /// ① .venv-ocr + scripts/db_mcp_server.py   ② sidecars/db_mcp_server/db_mcp_server.exe
    /// </summary>
    private static List<string> LocateRuntime()
    {
        var dir = new DirectoryInfo(AppPaths.ExeDir);
        for (var i = 0; i < 5; i++)
        {
            var py = Path.Combine(dir.FullName, ".venv-ocr", "Scripts", "python.exe");
            var script = Path.Combine(dir.FullName, "scripts", "db_mcp_server.py");
            if (File.Exists(py) && File.Exists(script)) return new List<string> { py, script };
            var bundled = Path.Combine(dir.FullName, "sidecars", "db_mcp_server", "db_mcp_server.exe");
            if (File.Exists(bundled)) return new List<string> { bundled };
            if (dir.Parent == null) break;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// 数据库定位：手动挂载路径 > snap_vision.json 的 dbPath > exe 同目录 snap_vision.duckdb
    /// > 向上五层 selftest/db_mcp/test_db.duckdb（开发测试夹具）。
    /// </summary>
    private string LocateDb()
    {
        if (_dbPathOverride != null && File.Exists(_dbPathOverride)) return _dbPathOverride;
        var configured = Config.Field("dbPath");
        if (configured != null && File.Exists(configured)) return configured;
        var dir = new DirectoryInfo(AppPaths.ExeDir);
        for (var i = 0; i < 5; i++)
        {
            var direct = Path.Combine(dir.FullName, "snap_vision.duckdb");
            if (File.Exists(direct)) return direct;
            var fixture = Path.Combine(dir.FullName, "selftest", "db_mcp", "test_db.duckdb");
            if (File.Exists(fixture)) return fixture;
            if (dir.Parent == null) break;
            dir = dir.Parent;
        }
        return null;
    }

    private async Task SpawnAndHandshake()
    {
        var runtime = LocateRuntime();
        if (runtime == null)
            throw new Exception(
                "未找到 MCP 运行时：需要 .venv-ocr+scripts/db_mcp_server.py（开发）或 sidecars/db_mcp_server/db_mcp_server.exe（便携）");
        var db = LocateDb();
        if (db == null)
            throw new Exception("未找到数据库：请在 exe 同目录 snap_vision.json 配置 dbPath，或放置 snap_vision.duckdb");
        var psi = new ProcessStartInfo
        {
            FileName = runtime[0],
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in runtime.Skip(1)) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add(db);
        var process = Process.Start(psi) ?? throw new Exception("MCP 进程启动失败");
        var proc = new McpProc
        {
            Process = process,
            Stdin = process.StandardInput,
            LineQueue = new LineQueue(process.StandardOutput),
        };
        _proc = proc;
        try
        {
            var id = SendRequest(proc, "initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-03-26",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "snap-vision", ["version"] = "0.1.0" },
            });
            await ReadResponse(proc, id).ConfigureAwait(false);
            proc.Stdin.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            proc.Stdin.Flush();
            var listId = SendRequest(proc, "tools/list", new JsonObject());
            var result = await ReadResponse(proc, listId).ConfigureAwait(false);
            proc.Tools = result?["tools"] as JsonArray ?? new JsonArray();
            if (proc.Tools.Count == 0) throw new Exception("MCP server 未提供任何工具");
            AppLog.Info($"mcp: handshake ok, {proc.Tools.Count} tools, db={db}, runtime={runtime[0]}");
        }
        catch
        {
            Kill();
            throw;
        }
    }

    private void Kill()
    {
        var p = _proc;
        _proc = null;
        p?.Dispose();
    }

    private Task EnsureServer()
    {
        if (_proc == null || _proc.Process.HasExited)
        {
            Kill();
            return SpawnAndHandshake();
        }
        return Task.CompletedTask;
    }

    private int SendRequest(McpProc proc, string method, JsonObject parameters)
    {
        var id = proc.NextId++;
        var req = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters?.DeepClone(),
        };
        proc.Stdin.WriteLine(req.ToJsonString());
        proc.Stdin.Flush();
        return id;
    }

    /// <summary>读到与 id 匹配的响应为止（跳过通知）；30s 超时杀进程重置。</summary>
    private async Task<JsonObject> ReadResponse(McpProc proc, int id)
    {
        var deadline = DateTime.UtcNow + CallTimeout;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                Kill();
                throw new Exception("MCP 响应超时（30s），进程已重置");
            }
            string line;
            try
            {
                line = await Task.Run(() => proc.LineQueue.Next(remaining)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Kill();
                throw new Exception("MCP 响应超时（30s），进程已重置");
            }
            if (line == null) throw new Exception("MCP server 无响应（已退出）");
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject value;
            try
            {
                value = JsonNode.Parse(line) as JsonObject
                        ?? throw new Exception("MCP 响应解析失败：非 JSON 对象");
            }
            catch (Exception e)
            {
                throw new Exception($"MCP 响应解析失败：{e.Message}");
            }
            try
            {
                if (value["id"] != null && value["id"].GetValue<int>() == id)
                {
                    if (value["error"] != null)
                        throw new Exception($"MCP 错误：{value["error"].ToJsonString()}");
                    return value["result"] as JsonObject;
                }
            }
            catch (Exception e) when (e.Message.StartsWith("MCP 错误"))
            {
                throw;
            }
            catch
            {
                // id 非数值或类型不匹配：视为通知，忽略
            }
        }
    }

    /// <summary>tools/call：返回 content[0].text；isError 转 Err。</summary>
    private async Task<string> CallTool(string name, JsonNode arguments)
    {
        var proc = _proc ?? throw new Exception("MCP server 未初始化");
        var id = SendRequest(proc, "tools/call", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments?.DeepClone() ?? new JsonObject(),
        });
        var result = await ReadResponse(proc, id).ConfigureAwait(false);
        var isError = false;
        try { isError = result["isError"]?.GetValue<bool>() == true; } catch { }
        var text = "";
        try
        {
            var t = (result["content"] as JsonArray)?[0]?["text"];
            text = t?.GetValue<string>() ?? "";
        }
        catch { }
        if (isError) throw new Exception($"工具 {name} 失败：{text}");
        return text;
    }

    /// <summary>会话级 schema 缓存（12000 字符防御性截断，与 Rust 版一致）。</summary>
    private async Task<string> SchemaText()
    {
        var proc = _proc ?? throw new Exception("MCP server 未初始化");
        if (proc.SchemaText.Length == 0)
        {
            var schema = await CallTool("get_schema", new JsonObject()).ConfigureAwait(false);
            if (schema.Length > 12000) schema = schema.Substring(0, 12000);
            proc.SchemaText = schema;
        }
        return proc.SchemaText;
    }

    /// <summary>手动挂载：校验路径 → 杀旧 MCP 进程 → 以新库重启握手 → 返回表摘要。</summary>
    public async Task<string> Mount(string path)
    {
        var file = path.Trim();
        if (!File.Exists(file)) throw new Exception($"文件不存在：{file}");
        Kill();
        _dbPathOverride = file;
        await EnsureServer().ConfigureAwait(false);
        var schema = await SchemaText().ConfigureAwait(false);
        JsonObject value = null;
        try { value = JsonNode.Parse(schema) as JsonObject; } catch { }
        var tables = new List<string>();
        if (value?["tables"] is JsonArray arr)
        {
            foreach (var t in arr)
            {
                if (t is not JsonObject o) continue;
                var name = TryString(o["table"]);
                if (name == null) continue;
                int n = 0;
                try { n = o["row_count"]?.GetValue<int>() ?? 0; } catch { }
                tables.Add($"{name} {n} 行");
            }
        }
        AppLog.Info($"dbmcp: mounted {file}");
        return $"已挂载 {file}：{tables.Count} 张表（{string.Join("、", tables)}）";
    }

    // ---------- 模型配置 ----------

    /// <summary>
    /// 问答模型解析："deepseek-flash"（默认，快）或 "glm-5.3-flash"。
    /// deepseek：snap_vision.json 的 dbBaseUrl/dbApiKey（key 必须显式配置）；
    /// glm：复用 vision 模块配置（zai-coding-cn）。
    /// </summary>
    private static VisionConfig ResolveModel(string choice)
    {
        if (choice.StartsWith("glm")) return Config.LoadVision();
        var apiKey = Config.Field("dbApiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new Exception("deepseek 未配置：请在 exe 同目录 snap_vision.json 里加 {\"dbApiKey\": \"sk-...\"}");
        var baseUrl = (Config.Field("dbBaseUrl") ?? "https://api.deepseek.com").TrimEnd('/');
        return new VisionConfig(baseUrl, apiKey, "deepseek-flash");
    }

    /// <summary>数据库问答主流程：OCR → MCP 工具循环 → 汇总，答复先落盘再返回。</summary>
    public async Task<DbChatOutcome> DbChatFlowAsync(
        List<ChatMsg> messages, string dbModel, OcrService ocr,
        Action<string> onProgress = null)
    {
        var model = string.IsNullOrEmpty(dbModel) ? "deepseek-flash" : dbModel;
        AppLog.Info($"db_chat: {messages.Count} turns, model={model}");
        var turns = new List<DbTurn>();
        var ocrTexts = new Dictionary<int, string>();
        for (var index = 0; index < messages.Count; index++)
        {
            var m = messages[index];
            string text;
            if (!string.IsNullOrEmpty(m.DbText))
            {
                text = $"[截图 OCR 结果（可能不完整）]\n{m.DbText}";
            }
            else if (m.ImagePath != null)
            {
                onProgress?.Invoke("ocr");
                AppLog.Info($"db_chat: OCR start, message={index}");
                var r = await ocr.Run(m.ImagePath).ConfigureAwait(false);
                var ocrText = r.Text.Length == 0 ? "（未识别到文字）" : r.Text;
                AppLog.Info($"db_chat: OCR done, message={index}, inference={r.ElapsedMs}ms");
                ocrTexts[index] = ocrText;
                text = $"[截图 OCR 结果（可能不完整）]\n{ocrText}";
            }
            else
            {
                text = m.Text;
            }
            turns.Add(new DbTurn(m.Role, text));
        }
        onProgress?.Invoke("query");
        AppLog.Info("db_chat: query starting (no UI round trip)");
        var reply = await DbChatAsync(turns, model).ConfigureAwait(false);
        AppLog.Info("db_chat: query completed");
        // 答复由服务端直接落盘：界面冻结/进程被杀都不丢结果
        try
        {
            Storage.AppendMessage("assistant", reply);
            AppLog.Info("db_chat: reply persisted (backend)");
        }
        catch (Exception e)
        {
            AppLog.Info($"db_chat: backend persist failed: {e.Message}");
        }
        return new DbChatOutcome
        {
            Reply = reply,
            OcrTexts = ocrTexts.Select(kv => new DbOcrText(kv.Key, kv.Value)).ToList(),
        };
    }

    /// <summary>工具循环主体。</summary>
    private async Task<string> DbChatAsync(List<DbTurn> turns, string modelChoice)
    {
        var cfg = ResolveModel(modelChoice);
        await EnsureServer().ConfigureAwait(false);
        var proc = _proc!;
        var schema = proc.SchemaText;
        if (schema.Length == 0) schema = await SchemaText().ConfigureAwait(false);
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = DbSystemPrompt(schema) },
        };
        foreach (var turn in turns)
        {
            var role = turn.Role == "assistant" ? "assistant" : "user";
            messages.Add(new JsonObject { ["role"] = role, ["content"] = turn.Text });
        }
        return await RunToolLoop(cfg, proc.Tools, messages).ConfigureAwait(false);
    }

    private async Task<string> RunToolLoop(VisionConfig cfg, JsonArray tools, JsonArray messages)
    {
        var forceSummary = false;
        for (var round = 1; round <= MaxToolRounds + 1; round++)
        {
            var summarize = forceSummary || round > MaxToolRounds;
            if (summarize)
            {
                AppLog.Info(forceSummary
                    ? "db_chat: empty answer, retry final summary (tools disabled)"
                    : "db_chat: tool budget reached, final summary (tools disabled)");
                messages.Add(new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = "现在进入最终汇总阶段。停止调用工具，基于已返回的查询结果回答本轮用户问题。"
                        + "明确区分已核实的结果、未匹配项和仍需核实的部分；信息不足时说明不足，"
                        + "不得编造数据或声称已完整核实。不要继续规划检索，也不要只回复还需要查询。",
                });
            }
            AppLog.Info($"db_chat: round {round}, model={cfg.Model}");
            var body = new JsonObject
            {
                ["model"] = cfg.Model,
                ["messages"] = messages.DeepClone(),
                ["tools"] = OpenaiTools(tools),
                ["tool_choice"] = summarize ? "none" : "auto",
                ["max_tokens"] = 6000,
                ["thinking"] = new JsonObject { ["type"] = "disabled" },
                ["temperature"] = 0.2,
            };
            var value = await VisionClient.HttpChatAsync(cfg, body).ConfigureAwait(false);
            string finish = null;
            try
            {
                finish = (value["choices"] as JsonArray)?[0]?["finish_reason"]?.GetValue<string>();
            }
            catch { }
            AppLog.Info($"db_chat: response round={round}, finish={finish}");
            var msg = VisionClient.MessageOf(value) ?? throw new Exception("响应缺少 message");
            JsonArray toolCalls = msg["tool_calls"] as JsonArray;
            var empty = toolCalls == null || toolCalls.Count == 0;
            if (empty)
            {
                string content = null;
                try { content = msg["content"]?.GetValue<string>(); } catch { }
                if (!string.IsNullOrWhiteSpace(content)) return content.Trim();
                if (!summarize)
                {
                    // 空正文不展示 reasoning_content；仅补一次禁用工具的最终答复。
                    forceSummary = true;
                    continue;
                }
                throw new Exception("模型在补充汇总后仍未返回正文，请重试本轮问题");
            }
            if (summarize)
                throw new Exception("模型在汇总阶段仍请求调用工具，未返回最终答复，请重试本轮问题");

            // 回放 assistant 轮：deepseek 等思考模型必须回传 reasoning_content
            var replay = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = TryString(msg["content"]) ?? "",
                ["tool_calls"] = toolCalls.DeepClone(),
            };
            var reasoning = TryString(msg["reasoning_content"]);
            if (reasoning != null) replay["reasoning_content"] = reasoning;
            messages.Add(replay);

            foreach (var tc in toolCalls)
            {
                if (tc is not JsonObject call) continue;
                var fn = call["function"] as JsonObject;
                var name = fn?["name"]?.GetValue<string>() ?? "";
                var argsRaw = fn?["arguments"]?.GetValue<string>() ?? "{}";
                JsonNode args;
                try { args = JsonNode.Parse(argsRaw); }
                catch { args = new JsonObject(); }
                AppLog.Info($"db_chat: tool {name} {argsRaw}");
                // 工具失败不再炸整轮：把错误回传给模型，让它换关键词/参数重试
                string content;
                try
                {
                    content = await CallTool(name, args).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    AppLog.Info($"db_chat: tool {name} FAILED: {e.Message}");
                    content = $"工具调用失败：{e.Message}。请调整参数重试（换更精确关键词、减小 limit、"
                        + "改用 and 逻辑），或基于已有信息直接作答。";
                }
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = TryString(call["id"]) ?? "",
                    ["content"] = content,
                });
            }
        }
        throw new Exception("unreachable");
    }

    private static JsonArray OpenaiTools(JsonArray procTools)
    {
        var result = new JsonArray();
        foreach (var t in procTools)
        {
            if (t is not JsonObject tool) continue;
            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = TryString(tool["name"]) ?? "",
                    ["description"] = TryString(tool["description"]) ?? "",
                    ["parameters"] = tool["inputSchema"]?.DeepClone()
                        ?? new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject(),
                        },
                },
            });
        }
        return result;
    }

    // ---------- 提示词 ----------

    /// <summary>内置默认提示词（db_prompt.txt 缺失时的兜底）。{schema} 会被替换为库结构。</summary>
    private const string DefaultDbPrompt = """
你是数据库问答助手。用户给出系统界面截图的 OCR 结果，可能不完整：部分列被截断、部分区域被遮挡。数据库结构如下（JSON）：
{schema}

工作流程：
1) 从 OCR 文本里选 2~4 个高置信度完整关键词（姓名、科室、编号等），调用 search_rows 检索；
   可能被截断的词去掉末尾 1~2 字再用；先用 and 精确找，命中 0 行再放宽为 or；
2) 库中可能有多张关联表（患者主表、检验结果、医嘱用药、手术排程、收费明细等）：
   先定位患者（姓名/住院号），再用其住院号或姓名跨表检索，回答"异常指标、在用药物、
   手术安排、费用汇总"等关联问题；多次调 search_rows 是允许的；
3) 拿到记录后输出：完整记录（逐字段）+ 标注哪些字段在截图中缺失/截断/被遮挡、由数据库补全
   + 匹配正确性判断（唯一命中/存在近似记录）；
4) 涉及计算（欠费、天数、变化趋势）时列出算式；不要臆测库中没有的数据；
   纯追问不需要查库时可直接回答。
""";

    /// <summary>系统提示词：热更优先——exe 同目录 db_prompt.txt 存在则每次现读
    /// （# 开头行作为注释剔除，{schema} 占位符替换），否则用编译内置默认。</summary>
    private static string DbSystemPrompt(string schema)
    {
        try
        {
            var raw = File.ReadAllText(AppPaths.DbPromptPath);
            var template = string.Join("\n",
                raw.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
            if (!string.IsNullOrWhiteSpace(template))
            {
                AppLog.Info("dbmcp: system prompt <- db_prompt.txt（外部热更）");
                return template.Replace("{schema}", schema);
            }
        }
        catch { }
        return DefaultDbPrompt.Replace("{schema}", schema);
    }

    private static string TryString(JsonNode node)
    {
        try { return node?.GetValue<string>(); }
        catch { return null; }
    }
}
