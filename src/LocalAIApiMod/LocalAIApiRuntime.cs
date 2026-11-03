using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LocalAIApiMod
{
    public static class LocalAIApiRuntime
    {
        public static bool Enabled = true;
        public static string ApiEndpoint = "http://127.0.0.1:3425/v1/chat/completions";
        public static string ApiKey = "magpie";
        public static string ModelName = "group/never-stuck";
        public static int TimeoutSeconds = 60;
        public static float Temperature = 0.7f;
        public static int MaxTokens = 512;
        public static bool DebugLogging = true;
        public static bool AutoDiscoverAtomicChat = false;

        private static bool _configLoaded = false;
        private static string _gameBaseDir = null;

        private static string GetGameBaseDir()
        {
            if (_gameBaseDir != null) return _gameBaseDir;
            try
            {
                var dllPath = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(dllPath))
                {
                    var managed = Path.GetDirectoryName(dllPath);
                    var data    = Path.GetDirectoryName(managed);
                    var game    = Path.GetDirectoryName(data);
                    if (!string.IsNullOrEmpty(game) && Directory.Exists(game))
                    {
                        _gameBaseDir = game;
                        return _gameBaseDir;
                    }
                }
            }
            catch { }
            _gameBaseDir = Directory.GetCurrentDirectory();
            return _gameBaseDir;
        }

        private static string GetLogPath()
        {
            try { return Path.Combine(GetGameBaseDir(), "LocalAIApi_runtime.log"); }
            catch { return "LocalAIApi_runtime.log"; }
        }

        public static void Log(string message)
        {
            try
            {
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message;
                var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                using (var fs = new FileStream(GetLogPath(), FileMode.Append, FileAccess.Write))
                    fs.Write(bytes, 0, bytes.Length);
            }
            catch { }
        }

        private static string ReadTextFile(string path)
        {
            var bytes = File.ReadAllBytes(path);
            return Encoding.UTF8.GetString(bytes);
        }

        public static void EnsureConfig()
        {
            if (_configLoaded) return;
            _configLoaded = true;
            try
            {
                var baseDir = GetGameBaseDir();
                var cfgPath = Path.Combine(baseDir, "BepInEx", "plugins", "LocalAIApi", "LocalAIApi.cfg");
                if (!File.Exists(cfgPath))
                    cfgPath = Path.Combine(baseDir, "LocalAIApi.cfg");
                Log("EnsureConfig called, cfgPath=" + cfgPath);
                if (File.Exists(cfgPath))
                {
                    var text = ReadTextFile(cfgPath);
                    var rawLines = text.Split(new char[] { '\n' });
                    foreach (var rawLine in rawLines)
                    {
                        var line = rawLine.Trim().TrimEnd('\r');
                        if (line.Length == 0 || line[0] == '#') continue;
                        var eqIdx = line.IndexOf('=');
                        if (eqIdx < 1) continue;
                        var key = line.Substring(0, eqIdx).Trim();
                        var val = line.Substring(eqIdx + 1).Trim();
                        var ci = val.IndexOf('#');
                        if (ci >= 0) val = val.Substring(0, ci).Trim();
                        switch (key)
                        {
                            case "Enabled": bool.TryParse(val, out Enabled); break;
                            case "ApiEndpoint": ApiEndpoint = val; break;
                            case "ApiKey": ApiKey = val; break;
                            case "ModelName": ModelName = val; break;
                            case "TimeoutSeconds": int.TryParse(val, out TimeoutSeconds); break;
                            case "Temperature": float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out Temperature); break;
                            case "MaxTokens": int.TryParse(val, out MaxTokens); break;
                            case "DebugLogging": bool.TryParse(val, out DebugLogging); break;
                            case "AutoDiscoverAtomicChat": bool.TryParse(val, out AutoDiscoverAtomicChat); break;
                        }
                    }
                }
                Log("Config loaded. Endpoint=" + ApiEndpoint + " Model=" + ModelName + " Enabled=" + Enabled);
            }
            catch (Exception ex)
            {
                Log("LoadConfig failed: " + ex);
            }
        }

        public static string GetCustomEndpointOverride(string originalEndpoint)
        {
            EnsureConfig();
            if (!Enabled || string.IsNullOrEmpty(ApiEndpoint)) return originalEndpoint;
            return ApiEndpoint;
        }

        public static string GetCustomApiKeyOverride(string originalApiKey)
        {
            EnsureConfig();
            if (!Enabled || string.IsNullOrEmpty(ApiEndpoint)) return originalApiKey;
            var apiKey = (ApiKey ?? "").Trim();
            return string.IsNullOrEmpty(apiKey) ? "local-endpoint" : apiKey;
        }

        public static UniTask<JObject> CompleteChatCompletionAsync(JObject body, CancellationToken cancellationToken)
        {
            EnsureConfig();
            return UniTask.RunOnThreadPool(() => SendLocalChatRequest(body));
        }

        private static JObject SendLocalChatRequest(JObject sourceBody)
        {
            try
            {
                EnsureConfig();
                Log("SendLocalChatRequest: sending request to " + ApiEndpoint);

                // Clone request body
                var requestJson = (JObject)sourceBody.DeepClone();

                // Override model if configured
                if (!string.IsNullOrEmpty(ModelName))
                {
                    requestJson["model"] = ModelName;
                }

                // Clean up game-specific metadata fields so local LLM server is not confused
                string[] removeKeys = new string[] {
                    "lobby_session_id", "ai_category", "backend_prompt_length",
                    "prompt_revision", "gameplay_route", "gameplay_player",
                    "gameplay_owner_revision", "prompt_sources", "model_settings",
                    "conversation_protocol", "prompt_version", "prompt_appendix",
                    "conversation_diagnostics"
                };
                foreach (var k in removeKeys)
                {
                    requestJson.Remove(k);
                }

                var jsonStr = requestJson.ToString(Newtonsoft.Json.Formatting.None);
                var postBytes = Encoding.UTF8.GetBytes(jsonStr);

                var sw = Stopwatch.StartNew();
                var key = (ApiKey ?? "").Trim();
                var timeout = TimeoutSeconds > 0 ? TimeoutSeconds : 60;
                string responseText = SendHttpPostRaw(ApiEndpoint, key, postBytes, timeout);
                sw.Stop();

                Log("SendLocalChatRequest: HTTP response in " + sw.ElapsedMilliseconds + "ms (len=" + responseText.Length + ")");

                var cleanJson = ExtractJsonBody(responseText);
                var responseObj = JObject.Parse(cleanJson);

                // If choices[0].message exists, verify content is set properly
                var choices = responseObj["choices"] as JArray;
                if (choices != null && choices.Count > 0)
                {
                    var msg = choices[0]["message"] as JObject;
                    if (msg != null)
                    {
                        var content = msg["content"]?.ToString();
                        if (string.IsNullOrEmpty(content))
                        {
                            var reasoning = msg["reasoning_content"]?.ToString();
                            if (!string.IsNullOrEmpty(reasoning))
                            {
                                msg["content"] = reasoning;
                                Log("SendLocalChatRequest: transferred reasoning_content to content");
                            }
                        }
                        var preview = (msg["content"]?.ToString() ?? "");
                        if (preview.Length > 80) preview = preview.Substring(0, 80) + "...";
                        Log("SendLocalChatRequest: choice message: " + preview);
                    }
                }

                return responseObj;
            }
            catch (WebException webEx)
            {
                string errDetail = webEx.Message;
                if (webEx.Response != null)
                {
                    try
                    {
                        using (var errStream = webEx.Response.GetResponseStream())
                        using (var ms = new MemoryStream())
                        {
                            byte[] buf = new byte[2048];
                            int r;
                            while ((r = errStream.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, r);
                            errDetail += " | Body: " + Encoding.UTF8.GetString(ms.ToArray());
                        }
                    }
                    catch { }
                }
                Log("SendLocalChatRequest: WebException: " + errDetail);
                throw;
            }
            catch (Exception ex)
            {
                Log("SendLocalChatRequest: Exception: " + ex);
                throw;
            }
        }

        private static string SendHttpPostRaw(string uriString, string apiKey, byte[] bodyBytes, int timeoutSeconds)
        {
            var uri = new Uri(uriString);
            var host = uri.Host;
            var port = uri.Port > 0 ? uri.Port : 80;
            var pathAndQuery = uri.PathAndQuery;

            using (var client = new System.Net.Sockets.TcpClient())
            {
                client.SendTimeout = timeoutSeconds * 1000;
                client.ReceiveTimeout = timeoutSeconds * 1000;
                client.Connect(host, port);

                using (var stream = client.GetStream())
                {
                    var sb = new StringBuilder();
                    sb.Append("POST ").Append(pathAndQuery).Append(" HTTP/1.0\r\n");
                    sb.Append("Host: ").Append(host).Append(":").Append(port).Append("\r\n");
                    sb.Append("Content-Type: application/json; charset=utf-8\r\n");
                    sb.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        sb.Append("Authorization: Bearer ").Append(apiKey).Append("\r\n");
                    }
                    sb.Append("Connection: close\r\n\r\n");

                    var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
                    stream.Write(headerBytes, 0, headerBytes.Length);
                    stream.Write(bodyBytes, 0, bodyBytes.Length);
                    stream.Flush();

                    using (var ms = new MemoryStream())
                    {
                        var buf = new byte[8192];
                        int read;
                        while ((read = stream.Read(buf, 0, buf.Length)) > 0)
                        {
                            ms.Write(buf, 0, read);
                        }
                        var allBytes = ms.ToArray();
                        int bodyIndex = -1;
                        for (int i = 0; i < allBytes.Length - 3; i++)
                        {
                            if (allBytes[i] == 13 && allBytes[i + 1] == 10 && allBytes[i + 2] == 13 && allBytes[i + 3] == 10)
                            {
                                bodyIndex = i + 4;
                                break;
                            }
                        }
                        if (bodyIndex < 0)
                            throw new Exception("Invalid HTTP response: header delimiter not found.");

                        return Encoding.UTF8.GetString(allBytes, bodyIndex, allBytes.Length - bodyIndex);
                    }
                }
            }
        }

        private static string ExtractJsonBody(string rawBody)
        {
            var trimmed = rawBody.Trim();
            if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
                return trimmed;
            int firstBrace = rawBody.IndexOf('{');
            int lastBrace = rawBody.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                return rawBody.Substring(firstBrace, lastBrace - firstBrace + 1);
            }
            return trimmed;
        }
    }
}
