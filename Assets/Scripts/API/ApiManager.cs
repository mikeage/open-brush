﻿// Copyright 2021 The Open Brush Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;
using WebSocketServer;

namespace TiltBrush
{
    public class ApiManager : MonoBehaviour
    {
        public const string WEBREQUEST_USER_AGENT = "Open Brush API Web Request";
        private const string ROOT_API_URL = "/api/v1";
        private const string BASE_USER_SCRIPTS_URL = "/scripts";
        private const string BASE_EXAMPLE_SCRIPTS_URL = "/examplescripts";
        private const string BASE_HTML = @"<!doctype html><html lang='en'>
<head><meta charset='UTF-8'></head>
<body>{0}</body></html>";


        private FileSystemWatcher m_FileWatcher;
        private string m_UserScriptsPath;
        private Queue m_RequestedCommandQueue = Queue.Synchronized(new Queue());
        private Dictionary<string, string> m_CommandStatuses;
        private Queue m_OutgoingCommandQueue = Queue.Synchronized(new Queue());
        private List<Uri> m_OutgoingApiListeners;
        private static ApiManager m_Instance;
        private Dictionary<string, ApiEndpoint> endpoints;
        private byte[] CameraViewPng;

        private bool cameraViewRequested;
        private bool cameraViewGenerated;

        public enum ForcePaintingMode
        {
            None,
            ForcedOn,
            ForcedOff,
            ForceNewStroke,
            WasForceNewStroke,
        }

        [NonSerialized] public Vector3 BrushOrigin = new Vector3(0, 13, 3);
        [NonSerialized] public Quaternion BrushInitialRotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
        [NonSerialized] public Vector3 BrushPosition = new Vector3(0, 13, 3); // Good origin for monoscopic
        [NonSerialized] public float PathSmoothing = 0.1f;
        [NonSerialized] public Quaternion BrushRotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
        [NonSerialized] public ForcePaintingMode ForcePainting;
        [NonSerialized] public ForcePaintingMode PreviousForcePaintingMode;
        public BaseCommand ActiveUndo { get; set; }

        private Dictionary<string, string> m_UserScripts;
        private Dictionary<string, string> m_ExampleScripts;

        public static ApiManager Instance
        {
            get { return m_Instance; }
        }
        [NonSerialized] public Stack<(Vector3, Quaternion)> BrushTransformStack;
        [NonSerialized] public Dictionary<string, string> CommandExamples;
        public string m_startupScriptName = "startup.sketchscript";

        // Need to set this on the main thread because of localiztion
        private string m_BrushesJson;

        public string UserScriptsPath() { return m_UserScriptsPath; }

        void Awake()
        {
            m_Instance = this;
            m_UserScriptsPath = Path.Combine(App.UserPath(), "Scripts");
            App.HttpServer.AddHttpHandler($"/help", InfoCallback);
            App.HttpServer.AddHttpHandler($"/help/commands", InfoCallback);
            App.HttpServer.AddHttpHandler($"/help/brushes", InfoCallback);
            App.HttpServer.AddHttpHandler($"/device_login/v1", DeviceLoginCallback);
            App.HttpServer.AddRawHttpHandler("/cameraview", CameraViewCallback);
            PopulateApi();

            m_UserScripts = new Dictionary<string, string>();
            m_ExampleScripts = new Dictionary<string, string>();
            m_CommandStatuses = new Dictionary<string, string>();
            PopulateExampleScripts();
            PopulateUserScripts();
            BrushTransformStack = new Stack<(Vector3, Quaternion)>();
            ResetBrushTransform();

            if (!Directory.Exists(m_UserScriptsPath))
            {
                Directory.CreateDirectory(m_UserScriptsPath);
            }
            if (Directory.Exists(m_UserScriptsPath))
            {
                m_FileWatcher = new FileSystemWatcher(m_UserScriptsPath, "*.html");
                m_FileWatcher.NotifyFilter = NotifyFilters.LastWrite;
                m_FileWatcher.Created += OnScriptsDirectoryChanged;
                m_FileWatcher.Changed += OnScriptsDirectoryChanged;
                // m_FileWatcher.FileDeleted += OnScriptsDirectoryChanged; TODO
                m_FileWatcher.EnableRaisingEvents = true;
            }
            App.Instance.StateChanged += RunStartupScript;
        }

        private string DeviceLoginCallback(HttpListenerRequest request)
        {
            // TODO Use AddRawHttpHandler and return appropriate status codes
            var host = $"{request.LocalEndPoint.Address}:{request.LocalEndPoint.Port}";
            host = host.Replace("127.0.0.1", "localhost");
            if (host != $"localhost:{HttpServer.HTTP_PORT}") return "Please login from the local browser";
            string formdata = null;
            if (request.HasEntityBody)
            {
                using (Stream body = request.InputStream)
                {
                    using (var reader = new StreamReader(body, request.ContentEncoding))
                    {
                        formdata = Uri.UnescapeDataString(reader.ReadToEnd()).Trim();
                    }
                }
            }
#if UNITY_EDITOR
            if (string.IsNullOrEmpty(formdata))
            {
                formdata = request.Url.Query;
            }
#endif
            if (string.IsNullOrEmpty(formdata)) return "Invalid request";
            string deviceCodeIfValid = _ValidateandExtractDeviceCode(formdata);
            if (deviceCodeIfValid != null)
            {
                VrAssetService.m_Instance.IcosaDeviceLogin(deviceCodeIfValid);
            }
            return "You can now return to Open Brush";
        }

        void Start()
        {
            // HTTP API String substitutions
            // Don't move to Awake() as that runs too early
            string[] brushNameList = BrushCatalog.m_Instance.GetTagFilteredBrushList()
                .Select(ApiFriendlyBrushName)
                .ToArray();
            m_BrushesJson = JsonConvert.SerializeObject(brushNameList, Formatting.Indented);
        }

        public void ResetBrushTransform()
        {
            // Resets the "turtle" transform back to it's original values
            BrushPosition = BrushOrigin;
            BrushRotation = BrushInitialRotation;
        }

        public void RunStartupScript(App.AppState oldState, App.AppState newState)
        {

            if (!(oldState == App.AppState.LoadingBrushesAndLighting && newState == App.AppState.Standard)) return;

            var startupScriptPath = Path.Combine(m_UserScriptsPath, m_startupScriptName);

            if (File.Exists(startupScriptPath))
            {
                var lines = File.ReadAllLines(startupScriptPath);
                foreach (string pair in lines)
                {
                    EnqueueCommand(pair);
                }
            }
        }

        private class EnqueuedApiCommand
        {
            private Guid m_Handle;
            private string m_Command;
            private string m_Parameters;

            public Guid Handle => m_Handle;
            public string Command => m_Command;
            public string Parameters => m_Parameters;

            public EnqueuedApiCommand(string command, string parameters)
            {
                m_Handle = Guid.NewGuid();
                m_Command = command;
                m_Parameters = parameters;
            }
        }

        private EnqueuedApiCommand EnqueueCommand(string commandString)
        {
            if (string.IsNullOrWhiteSpace(commandString)) return null;
            if (commandString.StartsWith("//")) return null;
            string[] commandPair = commandString.Split(new[] { '=' }, 2);
            if (commandPair.Length < 1) return null;
            string parameters;
            parameters = commandPair.Length == 2 ? UnityWebRequest.UnEscapeURL(commandPair[1]) : "";
            EnqueuedApiCommand cmd = new EnqueuedApiCommand(commandPair[0], parameters);
            m_RequestedCommandQueue.Enqueue(cmd);
            return cmd;
        }

        private string _ValidateandExtractDeviceCode(string formdata)
        {
            // Handle device code login requests from local browser
            var queryParams = formdata.Split("&");
            if (queryParams.Length != 2) return null;
            var secret_param = queryParams[0].Split("=");
            if (secret_param.Length != 2 || secret_param[0] != "client_secret") return null;
            string secret = secret_param[1];
            var device_code_param = queryParams[1].Split("=");
            if (device_code_param.Length != 2 || device_code_param[0] != "device_code") return null;
            string device_code = device_code_param[1];
            if (device_code.Length != 5) return null;
            bool isValidSecret = VrAssetService.m_Instance.IsValidDeviceCodeSecret(secret);
            return isValidSecret ? device_code : null;
        }

        private void OnScriptsDirectoryChanged(object sender, FileSystemEventArgs e)
        {
            var fileinfo = new FileInfo(e.FullPath);
            RegisterUserScript(fileinfo);
        }

        private string InfoCallback(HttpListenerRequest request)
        {
            string html;
            StringBuilder builder;
            switch (request.Url.Segments.Last())
            {
                case "commands":

                    var host = $"{request.LocalEndPoint.Address}:{request.LocalEndPoint.Port}";
                    host = host.Replace("127.0.0.1", "localhost");

                    if (request.Url.Query.Contains("raw"))
                    {
                        html = String.Join("\n", endpoints.Keys);
                    }
                    else if (request.Url.Query.Contains("json"))
                    {
                        html = JsonConvert.SerializeObject(ListApiCommands(), Formatting.Indented);
                    }
                    else
                    {
                        var commandList = ListApiCommandsAsStrings();
                        builder = new StringBuilder("<h3>Open Brush API Commands</h3>");
                        builder.AppendLine($"<p>To run commands a request to this url with http://{host}/api/v1?</p>");
                        builder.AppendLine("<p>Commands are querystring parameters: commandname=parameters</p>");
                        builder.AppendLine("<p>Separate multiple commands with &</p>");
                        builder.AppendLine($"<p>Example: <a href='http://{host}/api/v1?brush.turn.y=45&brush.draw=1'>http://{host}/api/v1?brush.turn.y=45&brush.draw=1</a></p>");
                        builder.AppendLine("<dl>");
                        foreach (var key in commandList.Keys)
                        {
                            string paramList = commandList[key].Item1;
                            if (paramList != "")
                            {
                                paramList = $"({paramList})";
                            }
                            builder.AppendLine($@"<dt><strong>{key}</strong> {paramList}
 <a href=""/api/v1?{getCommandExample(key)}"" target=""_blank"">Try it</a></dt>
<dd>{commandList[key].Item2}<br><br></dd>");
                        }
                        builder.AppendLine("</dl>");
                        html = String.Format(BASE_HTML, builder);
                    }
                    break;
                case "brushes":
                    var brushes = BrushCatalog.m_Instance.AllBrushes.Where(x => x.DurableName != "");
                    if (request.Url.Query.Contains("raw"))
                    {
                        html = String.Join("\n", brushes.Select(x => x.DurableName));
                    }
                    else
                    {
                        builder = new StringBuilder("<h3>Open Brush Brushes</h3>");
                        builder.AppendLine("<ul>");
                        foreach (var b in brushes)
                        {
                            builder.AppendLine($"<li>{b.DurableName}</li>");
                        }
                        builder.AppendLine("</ul>");
                        html = String.Format(BASE_HTML, builder);
                    }
                    break;
                case "help":
                default:
                    html = $@"<h3>Open Brush API Help</h3>
<ul>
<li>List of API commands: <a href='/help/commands'>/help/commands</a></li>
<li>List of brushes: <a href='/help/brushes'>/help/brushes</a></li>
<li>User Scripts: <a href='{BASE_USER_SCRIPTS_URL}'>{BASE_USER_SCRIPTS_URL}</a></li>
<li>Example Scripts: <a href='{BASE_EXAMPLE_SCRIPTS_URL}'>{BASE_EXAMPLE_SCRIPTS_URL}</a></li>
</ul>";
                    break;
            }
            return html;
        }

        private string getCommandExample(string key)
        {
            if (CommandExamples.ContainsKey(key))
            {
                return $"{key}={CommandExamples[key]}";
            }
            else
            {
                return key;
            }
        }

        private void PopulateExampleScripts()
        {
            App.HttpServer.AddHttpHandler(BASE_EXAMPLE_SCRIPTS_URL, ExampleScriptsCallback);
            var exampleScripts = Resources.LoadAll("ScriptExamples", typeof(TextAsset));
            foreach (TextAsset htmlFile in exampleScripts)
            {
                string filename = $"{BASE_EXAMPLE_SCRIPTS_URL}/{htmlFile.name}.html";
                m_ExampleScripts[filename] = htmlFile.ToString();
                App.HttpServer.AddHttpHandler(filename, ExampleScriptsCallback);
            }
        }

        private void PopulateUserScripts()
        {
            App.HttpServer.AddHttpHandler(BASE_USER_SCRIPTS_URL, UserScriptsCallback);
            if (!Directory.Exists(m_UserScriptsPath))
            {
                Directory.CreateDirectory(m_UserScriptsPath);
            }
            if (Directory.Exists(m_UserScriptsPath))
            {
                var dirInfo = new DirectoryInfo(m_UserScriptsPath);
                FileInfo[] AllFileInfo = dirInfo.GetFiles();
                foreach (FileInfo fileinfo in AllFileInfo)
                {
                    RegisterUserScript(fileinfo);
                }
            }
        }

        private void RegisterUserScript(FileInfo file)
        {
            if (file.Extension == ".html" || file.Extension == ".htm")
            {
                var f = file.OpenText();
                string filename = $"{BASE_USER_SCRIPTS_URL}/{file.Name}";
                m_UserScripts[filename] = f.ReadToEnd();
                f.Close();
                if (!App.HttpServer.HttpHandlerExists(filename))
                {
                    App.HttpServer.AddHttpHandler(filename, UserScriptsCallback);
                }
            }
        }

        private void PopulateApi()
        {
            endpoints = new Dictionary<string, ApiEndpoint>();
            var types = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(t => t.GetTypes())
                .Where(t => t.IsClass && t.Namespace == "TiltBrush");

            foreach (var type in types)
            {
                foreach (MethodInfo methodInfo in type.GetMethods())
                {
                    var attrs = Attribute.GetCustomAttributes(methodInfo, typeof(ApiEndpoint));
                    foreach (Attribute attr in attrs)
                    {
                        ApiEndpoint apiEndpoint = (ApiEndpoint)attr;
                        bool valid = false;
                        if (type.IsAbstract && type.IsSealed) // therefore is static
                        {
                            apiEndpoint.instance = null;
                            valid = true;
                        }
                        else if (type.IsSubclassOf(typeof(MonoBehaviour)))
                        {
                            apiEndpoint.instance = FindObjectOfType(type);
                            if (apiEndpoint.instance != null)
                            {
                                valid = true;
                            }
                            else
                            {
                                Debug.LogWarning($"No instance found for ApiEndpoint on: {type}");
                            }
                        }

                        if (valid)
                        {
                            apiEndpoint.type = type;
                            apiEndpoint.methodInfo = methodInfo;
                            apiEndpoint.parameterInfo = methodInfo.GetParameters();
                            endpoints[apiEndpoint.Endpoint] = apiEndpoint;
                        }
                        else
                        {
                            Debug.LogWarning($"ApiEndpoint declared on invalid class: {type}");
                        }
                    }
                }
            }
            App.HttpServer.AddHttpHandler(ROOT_API_URL, ApiCommandCallback);
        }

        private string InvokeEndpoint(EnqueuedApiCommand command)
        {
            if (endpoints.ContainsKey(command.Command))
            {
                var endpoint = endpoints[command.Command];
                var parameters = endpoint.DecodeParams(command.Parameters);
                return endpoint.Invoke(parameters)?.ToString();
            }
            if (!command.Command.StartsWith("//"))
            {
                Debug.LogError($"Invalid API command: {command.Command}");
            }
            return null;
        }

        [ContextMenu("Log Api Commands")]
        public void LogCommandsList()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("Please run in play mode");
            }
            else
            {
                var builder = new StringBuilder();
                var commands = ListApiCommandsAsStrings();
                foreach (var k in commands.Keys)
                {
                    builder.AppendLine($"{k} ({commands[k].Item2}): {commands[k].Item2}");
                }
            }
        }

        public (string paramInfo, string Description) GetCommandInfo(string endpoint)
        {
            var paramInfoText = new List<string>();
            foreach (var param in endpoints[endpoint].parameterInfo)
            {
                string typeName = param.ParameterType.Name
                    .Replace("Single", "float")
                    .Replace("Int32", "int")
                    .Replace("String", "string");
                paramInfoText.Add($"{typeName} {param.Name}");
            }
            string paramInfo = String.Join(", ", paramInfoText);
            return (paramInfo, endpoints[endpoint].Description);
        }

        public (string paramInfo, string Description) GetRuntimeCommandInfo(string luaName)
        {
            var paramInfoText = new List<string>();
            // Convert lua name to Http Api name (split camel case parts and add ".")
            string[] parts = Regex.Split(luaName, @"(?<!^)(?=[A-Z])");
            string endpoint = string.Join(".", parts).ToLower();
            foreach (var param in endpoints[endpoint].parameterInfo)
            {
                paramInfoText.Add($"{param.Name}");
            }
            string paramInfo = String.Join(", ", paramInfoText);
            return (paramInfo, endpoints[endpoint].Description);
        }

        Dictionary<string, (string, string)> ListApiCommandsAsStrings()
        {
            var commandList = new Dictionary<string, (string, string)>();
            foreach (var endpoint in endpoints.Keys)
            {
                commandList[endpoint] = GetCommandInfo(endpoint);
            }
            return commandList;
        }

        Dictionary<string, object> ListApiCommands()
        {
            var commandList = new Dictionary<string, object>();
            foreach (var endpoint in endpoints.Keys)
            {
                commandList[endpoint] = new
                {
                    parameters = endpoints[endpoint].ParamsAsDict(),
                    description = endpoints[endpoint].Description
                };
            }
            return commandList;
        }

        private string UserScriptsCallback(HttpListenerRequest request)
        {
            string html;
            if (request.Url.Segments.Length == 2)
            {
                var builder = new StringBuilder("<h3>Open Brush User Scripts</h3>");
                builder.AppendLine("<ul>");
                foreach (var e in m_UserScripts)
                {
                    builder.AppendLine($"<li><a href='{e.Key}'>{e.Key}</a></li>");
                }

                // Only show this button on Windows
                // TODO Update this is ApiMethods.OpenUserFolder is ever cross platform
                // (Also see similar global commands that will need updating)
                if (Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor)
                {
                    builder.AppendLine($"<button onclick=\"fetch('{ROOT_API_URL}?showfolder.scripts');\">Open Scripts Folder</button>");
                }
                builder.AppendLine("</ul>");
                html = String.Format(BASE_HTML, builder);
            }
            else
            {
                html = m_UserScripts[Uri.UnescapeDataString(request.Url.AbsolutePath)];
            }
            return ScriptTemplateSubstitution(html);
        }

        private string ExampleScriptsCallback(HttpListenerRequest request)
        {
            string html;
            if (request.Url.Segments.Length == 2)
            {
                var builder = new StringBuilder("<h3>Open Brush Example Scripts</h3>");
                builder.AppendLine("<ul>");
                foreach (var e in m_ExampleScripts)
                {
                    builder.AppendLine($"<li><a href='{e.Key}'>{e.Key}</a></li>");
                }
                builder.AppendLine("</ul>");
                html = String.Format(BASE_HTML, builder);
            }
            else
            {
                html = m_ExampleScripts[Uri.UnescapeDataString(request.Url.AbsolutePath)];
            }
            return ScriptTemplateSubstitution(html);
        }

        private string ScriptTemplateSubstitution(string html)
        {

            // TODO Document these
            html = html.Replace("{{brushesJson}}", m_BrushesJson);

            string pointFamilies = JsonConvert.SerializeObject(Enum.GetNames(typeof(SymmetryGroup.R)), Formatting.Indented);
            html = html.Replace("{{pointFamiliesJson}}", pointFamilies);

            string wallpaperGroups = JsonConvert.SerializeObject(Enum.GetNames(typeof(PointSymmetry.Family)), Formatting.Indented);
            html = html.Replace("{{wallpaperGroupsJson}}", wallpaperGroups);

            string[] environmentNameList = EnvironmentCatalog.m_Instance.m_EnvironmentDescriptions
                .Select(x => x.Replace(" ", ""))
                .ToArray();

            string environmentsJson = JsonConvert.SerializeObject(environmentNameList, Formatting.Indented);
            html = html.Replace("{{environmentsJson}}", environmentsJson);

            string commandsJson = JsonConvert.SerializeObject(ListApiCommands(), Formatting.Indented);
            html = html.Replace("{{commandsJson}}", commandsJson);

            var toolScripts = new List<string>();
            var symmetryScripts = new List<string>();
            var pointerScripts = new List<string>();
            var backgroundScripts = new List<string>();

            if (LuaManager.Instance.IsInitialized)
            {
                toolScripts = LuaManager.Instance.GetScriptNames(LuaApiCategory.ToolScript);
                symmetryScripts = LuaManager.Instance.GetScriptNames(LuaApiCategory.SymmetryScript);
                pointerScripts = LuaManager.Instance.GetScriptNames(LuaApiCategory.PointerScript);
                backgroundScripts = LuaManager.Instance.GetScriptNames(LuaApiCategory.BackgroundScript);
            }

            html = html.Replace("{{toolScripts}}", JsonConvert.SerializeObject(toolScripts));
            html = html.Replace("{{symmetryScripts}}", JsonConvert.SerializeObject(symmetryScripts));
            html = html.Replace("{{pointerScripts}}", JsonConvert.SerializeObject(pointerScripts));
            html = html.Replace("{{backgroundScripts}}", JsonConvert.SerializeObject(backgroundScripts));

            return html;
        }

        public static string ApiFriendlyBrushName(BrushDescriptor brush)
        {
            if (brush.Description == null)
            {
                Debug.LogWarning($"Brush {brush.m_DurableName} has no description");
                return "";
            }
            return brush.Description
                .Replace(" ", "")
                .Replace(".", "")
                .Replace("(", "")
                .Replace(")", "");
        }

        public void ReceiveWebSocketMessage(WebSocketMessage message)
        {
            foreach (var cmd in message.data.Split("&"))
            {
                EnqueueCommand(cmd);
            }
        }

        string ApiCommandCallback(HttpListenerRequest request)
        {
            // GET commands
            List<string> commandStrings = request.Url.Query.TrimStart('?').Split('&').ToList();

            // POST commands
            if (request.HasEntityBody)
            {
                using (Stream body = request.InputStream)
                {
                    using (var reader = new StreamReader(body, request.ContentEncoding))
                    {
                        var formdata = Uri.UnescapeDataString(reader.ReadToEnd());
                        var formdataCommands = formdata.Replace("+", " ")
                            .Split('&')
                            .Where(s => s.Trim().Length > 0)
                            .ToList();

                        // TODO also accept JSON
                        commandStrings.AddRange(formdataCommands);
                    }
                }
            }

            List<string> responses = new List<string>();

            foreach (string commandString in commandStrings)
            {
                if (commandString.StartsWith("query."))
                {
                    responses.Add(HandleApiQuery(commandString));
                }
                else
                {
                    EnqueueCommand(commandString);
                }
            }

            return String.Join("\n", responses);
        }

        private string HandleApiQuery(string commandString)
        {

            // API queries are distinct from commands in that they return immediate results and never change the scene

            string[] commandPair = commandString.Split(new[] { '=' }, 2);
            if (commandPair.Length < 1) return null;
            switch (commandPair[0])
            {
                case "query.queue":
                    return m_OutgoingCommandQueue.Count.ToString();
                case "query.command":
                    if (m_CommandStatuses.ContainsKey(commandPair[1]))
                    {
                        return m_CommandStatuses[commandPair[1]];
                    }
                    else
                    {
                        return $"pending";
                    }
                case "query.spectator.position":
                    return ApiMainThreadObserver.Instance.SpectatorCamPosition.ToString();
                case "query.spectator.rotation":
                    return ApiMainThreadObserver.Instance.SpectatorCamRotation.eulerAngles.ToString();
                case "query.spectator.target":
                    return ApiMainThreadObserver.Instance.SpectatorCamTargetPosition.ToString();
            }
            return "unknown query";
        }

        public bool HasOutgoingListeners => m_OutgoingApiListeners != null && m_OutgoingApiListeners.Count > 0;

        // TODO Find a better home for this. It won't always be API specific
        public CHRFont TextFont
        {
            get
            {
                if (m_TextFont == null)
                {
                    TextFont = Resources.Load<CHRFont>("arcade");
                }
                return m_TextFont;
            }

            set
            {
                m_TextFont = value;
            }
        }
        private CHRFont m_TextFont;

        public void SetTextFont(string chrData)
        {
            TextFont.DataRaw = chrData;
            TextFont.Initialize();
        }

        public void EnqueueOutgoingCommands(List<KeyValuePair<string, string>> commands)
        {
            if (!HasOutgoingListeners) return;
            foreach (var command in commands)
            {
                m_OutgoingCommandQueue.Enqueue(command);
            }
        }

        public void AddOutgoingCommandListener(Uri uri)
        {
            if (m_OutgoingApiListeners == null) m_OutgoingApiListeners = new List<Uri>();
            if (m_OutgoingApiListeners.Contains(uri)) return;
            m_OutgoingApiListeners.Add(uri);

        }

        private void OutgoingApiCommand()
        {
            if (!HasOutgoingListeners) return;

            KeyValuePair<string, string> command;
            try
            {
                command = (KeyValuePair<string, string>)m_OutgoingCommandQueue.Dequeue();
            }
            catch (InvalidOperationException)
            {
                return;
            }

            foreach (var listenerUrl in m_OutgoingApiListeners)
            {
                string getUri = $"{listenerUrl}?{command.Key}={command.Value}";
                if (getUri.Length < 512)  // Actually limit is 2083 but let's be conservative
                {
                    StartCoroutine(GetRequest(getUri));
                }
                else
                {
                    var formData = new Dictionary<string, string>
                    {
                        {command.Key, command.Value}
                    };
                    StartCoroutine(PostRequest(listenerUrl.ToString(), formData));
                }
            }
        }

        IEnumerator GetRequest(string uri)
        {
            using (UnityWebRequest webRequest = UnityWebRequest.Get(uri))
            {
                yield return webRequest.SendWebRequest();
            }
        }

        IEnumerator PostRequest(string uri, Dictionary<string, string> formData)
        {
            using (UnityWebRequest webRequest = UnityWebRequest.Post(uri, formData))
            {
                yield return webRequest.SendWebRequest();
            }
        }

        private bool HandleApiCommand()
        {
            EnqueuedApiCommand command;
            try
            {
                command = (EnqueuedApiCommand)m_RequestedCommandQueue.Dequeue();
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            var result = Instance.InvokeEndpoint(command);
            m_CommandStatuses[command.Handle.ToString()] = result;
            return true;
        }

        private void Update()
        {
            HandleApiCommand();
            OutgoingApiCommand();
            UpdateCameraView();
        }


        IEnumerator ScreenCap()
        {
            yield return new WaitForEndOfFrame();
            var rt = new RenderTexture(Screen.width, Screen.height, 0);
            ScreenCapture.CaptureScreenshotIntoRenderTexture(rt);
            var oldTex = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
            FlipTextureVertically(tex);
            tex.Apply();
            RenderTexture.active = oldTex;
            CameraViewPng = tex.EncodeToPNG();
            Destroy(tex);

            cameraViewRequested = false;
            cameraViewGenerated = true;
        }

        public static void FlipTextureVertically(Texture2D original)
        {
            // ScreenCap is upside down so flip it
            // Orientation might be platform specific so we might need some logic around this

            var originalPixels = original.GetPixels();

            Color[] newPixels = new Color[originalPixels.Length];

            int width = original.width;
            int rows = original.height;

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < rows; y++)
                {
                    newPixels[x + y * width] = originalPixels[x + (rows - y - 1) * width];
                }
            }

            original.SetPixels(newPixels);
            original.Apply();
        }

        private void UpdateCameraView()
        {
            if (cameraViewRequested) StartCoroutine(ScreenCap());
        }

        private HttpListenerContext CameraViewCallback(HttpListenerContext ctx)
        {

            cameraViewRequested = true;
            while (cameraViewGenerated == false)
            {
                Thread.Sleep(5);
            }
            cameraViewGenerated = false;

            ctx.Response.AddHeader("Content-Type", "image/png");
            ctx.Response.ContentLength64 = CameraViewPng.Length;
            try
            {
                if (ctx.Response.OutputStream.CanWrite)
                {
                    ctx.Response.OutputStream.Write(CameraViewPng, 0, CameraViewPng.Length);
                }
            }
            catch (SocketException e)
            {
                Debug.LogWarning(e.Message);
            }
            finally
            {
                ctx.Response.Close();
            }
            ctx = null;
            return ctx;
        }

        public void LoadPolyModel(string assetId)
        {
            StartCoroutine(SpawnModelCoroutine(assetId, "API"));
        }

        public void LoadPolyModel(Uri uri)
        {
            string assetId = UnityWebRequest.EscapeURL(uri.ToString());
            StartCoroutine(SpawnModelCoroutine(assetId, "API"));
        }

        private static IEnumerator SpawnModelCoroutine(string assetId, string reason)
        {
            // Same as calling Model.RequestModelPreload -> RequestModelLoadInternal, except
            // this won't ignore the request if the load-into-memory previously failed.
            App.IcosaAssetCatalog.RequestModelLoad(assetId, reason);

            // It is possible from this section forward that the user may have moved on to a different page
            // on the Poly panel, which is why we use a local copy of 'model' rather than m_Model.
            Model model;
            // A model in the catalog will become non-null once the gltf has been downloaded or is in the
            // cache.
            while ((model = App.IcosaAssetCatalog.GetModel(assetId)) == null)
            {
                yield return null;
            }

            // A model becomes valid once the gltf has been successfully read into a Unity mesh.
            if (!model.m_Valid)
            {
                // The model might be in the "loaded with error" state, but it seems harmless to try again.
                // If the user keeps clicking, we'll keep trying.
                yield return model.LoadFullyCoroutine(reason);
                Debug.Assert(model.m_Valid || model.Error != null);
            }

            if (!model.m_Valid)
            {
                OutputWindowScript.Error($"Couldn't load model: {model.Error?.message}", model.Error?.detail);
            }
            else
            {
                TrTransform xfSpawn = new TrTransform();
                CreateWidgetCommand createCommand = new CreateWidgetCommand(
                    WidgetManager.m_Instance.ModelWidgetPrefab, xfSpawn, Quaternion.identity, true
                );
                SketchMemoryScript.m_Instance.PerformAndRecordCommand(createCommand);
                ModelWidget modelWidget = createCommand.Widget as ModelWidget;
                modelWidget.Model = model;
                modelWidget.Show(true);
                createCommand.SetWidgetCost(modelWidget.GetTiltMeterCost());

                WidgetManager.m_Instance.WidgetsDormant = false;
                SketchControlsScript.m_Instance.EatGazeObjectInput();
                SelectionManager.m_Instance.RemoveFromSelection(false);
            }
        }

        public void HandleStrokeListeners(IEnumerable<PointerManager.ControlPoint> controlPoints, Guid guid, Color color, float size)
        {
            if (!HasOutgoingListeners) return;
            var pointsAsStrings = new List<string>();
            foreach (var cp in controlPoints)
            {
                var pos = cp.m_Pos;
                var rot = cp.m_Orient.eulerAngles;
                pointsAsStrings.Add($"[{pos.x},{pos.y},{pos.z},{rot.x},{rot.y},{rot.z},{cp.m_Pressure}]");
            }
            EnqueueOutgoingCommands(
                new List<KeyValuePair<string, string>>
                {
                    new ("brush.type", guid.ToString()),
                    new ("brush.size.set", size.ToString()),
                    new ("color.set.rgb", $"{color.r},{color.g},{color.b}"),
                    new ("draw.stroke", string.Join(",", pointsAsStrings))
                }
            );
        }

        // Undo currently only affects stroke creation
        // but any command can be supported as long as you set it's parent to ActiveUndo
        // Mainly used by lua scripts at the moment.

        public void StartUndo()
        {
            ActiveUndo = new BaseCommand();
        }

        public void EndUndo()
        {
            if (ActiveUndo != null && ActiveUndo.HasChildren)
            {
                SketchMemoryScript.m_Instance.PerformAndRecordCommand(ActiveUndo);
            }
            ActiveUndo = null;
        }

        public static bool ParameterRequiresScriptingKeyword(string parameter)
        {
            return parameter is
                "_ClipStart" or
                "_ClipEnd" or
                "_Dissolve" or
                "_TimeOverrideValue" or
                "_TimeBlend" or
                "_TimeSpeed";
        }
    }
}
