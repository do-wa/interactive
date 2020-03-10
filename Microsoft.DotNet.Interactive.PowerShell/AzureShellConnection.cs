// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.Interactive.PowerShell.Host;

namespace Microsoft.DotNet.Interactive.PowerShell
{
    #region JsonTypes

    internal class IntToStringConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                ReadOnlySpan<byte> span = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
                if (Utf8Parser.TryParse(span, out int number, out int bytesConsumed) && span.Length == bytesConsumed)
                {
                    return number;
                }

                if (int.TryParse(reader.GetString(), out number))
                {
                    return number;
                }
            }

            return reader.GetInt32();
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }
    }

    internal class CloudShellTerminal
    {
        public string id { get; set; }
        public string socketUri { get; set; }
        [JsonConverter(typeof(IntToStringConverter))]
        public int idleTimeout { get; set; }
        public bool tokenUpdated { get; set; }
        public string rootDirectory { get; set; }
    }

    internal class AuthResponse
    {
        public string token_type { get; set; }
        public string scope { get; set; }
        [JsonConverter(typeof(IntToStringConverter))]
        public int expires_in { get; set; }
        [JsonConverter(typeof(IntToStringConverter))]
        public int ext_expires_in { get; set; }
        [JsonConverter(typeof(IntToStringConverter))]
        public int not_before { get; set; }
        public string resource { get; set; }
        public string access_token { get; set; }
        public string refresh_token { get; set; }
        public string id_token { get; set; }
    }

    internal class AuthResponsePending
    {
        public string error { get; set; }
        public string error_description { get; set; }
        public int[] error_codes { get; set; }
        public string timestamp { get; set; }
        public string trace_id { get; set; }
        public string correlation_id { get; set; }
        public string error_uri { get; set; }
    }

    internal class CloudShellResponse
    {
        public Dictionary<string,string> properties { get; set; }
    }

    internal class DeviceCodeResponse
    {
        public string user_code { get; set; }
        public string device_code { get; set; }
        public string verification_url { get; set; }
        [JsonConverter(typeof(IntToStringConverter))]
        public int expires_in { get; set; }
        [JsonConverter(typeof(IntToStringConverter))]
        public int interval { get; set; }
        public string message { get; set; }
    }

    internal class AzureTenant
    {
        public string id { get; set; }
        public string tenantId { get; set; }
        public string countryCode { get; set; }
        public string displayName { get; set; }
        public string[] domains { get; set; }
    }

    internal class AzureTenantResponse
    {
        public AzureTenant[] value { get; set; }
    }

    #endregion

    internal class AzShellConnectionUtils : IDisposable
    {
        private const string ClientId = "245e1dee-74ef-4257-a8c8-8208296e1dfd";
        private const string UserAgent = "PowerShell.Enter-AzShell";
        private const string CommandToStartPwsh = "stty -echo && cat | pwsh -noninteractive -f - && exit";
        private const string CommandToSetPrompt = @"Remove-Item Function:\Prompt -Force; New-Item -Path Function:\Prompt -Value { ""PS>`n"" } -Options constant -Force > $null; New-Alias -Name help -Value Get-Help -Force";

        private readonly KernelInvocationContext _context;
        private readonly HttpClient _httpClient;
        private readonly ClientWebSocket _socket;
        private readonly Pipe _pipe;
        private readonly byte[] _exitSessionCommand;
        private readonly string _pwshPrompt;

        private string _accessToken;
        private string _refreshToken;
        private bool _sessionInitialized;
        private TaskCompletionSource<object> _codeExecutedTaskSource;

        internal AzShellConnectionUtils(KernelInvocationContext context)
        {
            _context = context;
            _httpClient = new HttpClient();
            _socket = new ClientWebSocket();
            _pipe = new Pipe();
            _exitSessionCommand = new byte[] { 4 };
            _pwshPrompt = "PS>\r\n";
        }

        public void Dispose()
        {
            if (_httpClient != null)
            {
                _httpClient.Dispose();
            }

            if (_socket != null)
            {
                _socket.Dispose();
            }
        }

        internal async Task ConnectAndInitializeAzShell(int terminalWidth, int terminalHeight)
        {
            if (_sessionInitialized)
            {
                throw new InvalidOperationException("Session has already been initialized.");
            }

            Console.WriteLine("Authenticating with Azure...");
            await GetDeviceCode().ConfigureAwait(false);

            string tenantId = await GetTenantId().ConfigureAwait(false);
            await RefreshToken(tenantId).ConfigureAwait(false);

            Console.Write("Requesting Cloud Shell...");
            string cloudShellUri = await RequestCloudShell().ConfigureAwait(false);
            Console.WriteLine("Succeeded.");

            Console.WriteLine("Connecting terminal...");
            string socketUri = await RequestTerminal(cloudShellUri, terminalWidth, terminalHeight).ConfigureAwait(false);

            await _socket.ConnectAsync(new Uri(socketUri), CancellationToken.None).ConfigureAwait(false);
            Task fillPipe = FillPipeAsync(_socket, _pipe.Writer);
            Task readPipe = ReadPipeAsync(_pipe.Reader);

            // Connection has established. Start pwsh.
            await SendCommand(CommandToStartPwsh).ConfigureAwait(false);

            // Wait for 2 seconds for the initialization to finish, e.g. the profile.
            await Task.Delay(2000).ConfigureAwait(false);
            await SendCommand(CommandToSetPrompt).ConfigureAwait(false);
        }

        internal async Task ExitSession()
        {
            await SendCommand(_exitSessionCommand, waitForExecutionCompletion: false);

            string color = VTColorUtils.CombineColorSequences(ConsoleColor.Green, VTColorUtils.DefaultConsoleColor);
            Console.Write($"{color}Azure Cloud Shell session ended.{VTColorUtils.ResetColor}\n");
            Console.Write($"{color}Submitted code will run in the local PowerShell sub kernel.{VTColorUtils.ResetColor}\n");
        }

        internal async Task SendCommand(string command, bool waitForExecutionCompletion = true)
        {
            if (waitForExecutionCompletion)
            {
                _codeExecutedTaskSource = new TaskCompletionSource<object>();
            }

            // The command could contain multi-line statement, which would require double line endings
            // for pwsh to accept the input.
            var buffer = Encoding.UTF8.GetBytes(command + "\n\n");
            await SendCommand(buffer, waitForExecutionCompletion);
        }

        private async Task SendCommand(byte[] command, bool waitForExecutionCompletion)
        {
            if (_socket.State != WebSocketState.Open)
            {
                throw new IOException($"Session closed: {_socket.State}.\nCloseStatus: {_socket.CloseStatus}\nDescription: {_socket.CloseStatusDescription}");
            }

            await _socket.SendAsync(
                command.AsMemory(),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);

            if (waitForExecutionCompletion)
            {
                await _codeExecutedTaskSource.Task;
                _codeExecutedTaskSource = null;
            }
        }

        private async Task FillPipeAsync(ClientWebSocket socket, PipeWriter writer)
        {
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    // Allocate at least 512 bytes from the PipeWriter
                    Memory<byte> memory = writer.GetMemory(512);
                    var receiveResult = await socket.ReceiveAsync(memory, CancellationToken.None);

                    // Tell the PipeWriter how much was read from the Socket
                    writer.Advance(receiveResult.Count);

                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Close message received",
                            CancellationToken.None);
                    }

                    // Make the data available to the PipeReader
                    FlushResult result = await writer.FlushAsync();
                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
            }
            catch (Exception)
            {
                // report error
            }

            // Tell the PipeReader that there's no more data coming
            writer.Complete();
        }

        private async Task ReadPipeAsync(PipeReader reader)
        {
            Stream stdout = Console.OpenStandardOutput();
            bool potentialEndOfExecution = false;

            while (true)
            {
                if (!reader.TryRead(out ReadResult result))
                {
                    if (potentialEndOfExecution)
                    {
                        _codeExecutedTaskSource?.SetResult(null);
                    }
                    result = await reader.ReadAsync();
                }

                potentialEndOfExecution = false;
                ReadOnlySequence<byte> buffer = result.Buffer;

                while (true)
                {
                    // Look for a EOL in the buffer
                    var position = buffer.PositionOf((byte)'\n');
                    if (position == null)
                    {
                        break;
                    }

                    // Read the whole line.
                    position = buffer.GetPosition(1, position.Value);
                    string line = GetUtf8String(buffer.Slice(0, position.Value));

                    // Skip the bytes for that line.
                    buffer = buffer.Slice(position.Value);

                    if (_sessionInitialized)
                    {
                        if (_pwshPrompt.Equals(line, StringComparison.Ordinal) ||
                            (line.EndsWith(_pwshPrompt, StringComparison.Ordinal) &&
                             line.StartsWith(VTColorUtils.EscapeCharacters)))
                        {
                            // The line is the prompt string, either exactly or with some escape sequences prepended.
                            potentialEndOfExecution = buffer.Length == 0;
                        }
                        else
                        {
                            Console.Write(line);
                        }
                    }
                    else
                    {
                        ProcessMessageAtSessionInitialization(line);
                    }
                }

                // Tell the PipeReader how much of the buffer we have consumed
                reader.AdvanceTo(buffer.Start, buffer.End);

                // Stop reading if there's no more data coming
                if (result.IsCompleted)
                {
                    break;
                }
            }

            // Mark the PipeReader as complete
            reader.Complete();
        }

        private void ProcessMessageAtSessionInitialization(string line)
        {
            // Handle incoming messages at session startup.
            if (line.IndexOf(CommandToStartPwsh) != -1)
            {
                // The 'CommandToStartPwsh' will be echoed back, and we don't want to
                // show anything before seeing that echo.
                string color = VTColorUtils.CombineColorSequences(ConsoleColor.Green, VTColorUtils.DefaultConsoleColor);
                Console.Write($"\n{color}Welcome to Azure Cloud Shell!{VTColorUtils.ResetColor}\n");
                Console.Write($"{color}Submitted code will run in the Azure Cloud Shell, type 'exit' to quit.{VTColorUtils.ResetColor}\n\n");
            }
            else if (line.IndexOf("MOTD:") != -1)
            {
                // Let's display the message-of-the-day from PowerShell Azure Shell.
                Console.Write(line);
                // Also, seeing this message means we are now in pwsh.
                _codeExecutedTaskSource?.SetResult(null);
            }
            else if (line.IndexOf("VERBOSE: ") != -1)
            {
                // Let's show the verbose message generated from the profile.
                Console.Write(line);
            }
            else if (line.IndexOf(CommandToSetPrompt) != -1)
            {
                // pwsh will echo the command passed to it.
                // It's okay to show incoming messages after this very first command is echoed back.
                _sessionInitialized = true;
            }
        }

        private string GetUtf8String(ReadOnlySequence<byte> buffer)
        {
            if (buffer.IsSingleSegment)
            {
                return Encoding.UTF8.GetString(buffer.First.Span);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private async Task RefreshToken(string tenantId)
        {
            const string resource = "https://management.core.windows.net/";
            string resourceUri = $"https://login.microsoftonline.com/{tenantId}/oauth2/token";
            string encodedResource = Uri.EscapeDataString(resource);
            string body = $"client_id={ClientId}&resource={encodedResource}&grant_type=refresh_token&refresh_token={_refreshToken}";

            byte[] response = await SendWebRequest(
                resourceUri: resourceUri,
                body: body,
                contentType: "application/x-www-form-urlencoded",
                method: HttpMethod.Post,
                ignoreError: true
            );

            var authResponse = JsonSerializer.Deserialize<AuthResponse>(response);
            _accessToken = authResponse.access_token;
            _refreshToken = authResponse.refresh_token;
        }

        private async Task<string> RequestTerminal(string uri, int width, int height)
        {
            string resourceUri = $"{uri}/terminals?cols={width}&rows={height}&version=2019-01-01&shell=bash";
            byte[] response = await SendWebRequest(
                resourceUri: resourceUri,
                body: string.Empty,
                contentType: "application/json",
                token: _accessToken,
                method: HttpMethod.Post
            );

            var terminal = JsonSerializer.Deserialize<CloudShellTerminal>(response);
            return terminal.socketUri;
        }

        private async Task<string> GetTenantId()
        {
            const string resourceUri = "https://management.azure.com/tenants?api-version=2018-01-01";
            byte[] response = await SendWebRequest(
                resourceUri: resourceUri,
                body: null,
                contentType: null,
                token: _accessToken,
                method: HttpMethod.Get
            );

            var tenant = JsonSerializer.Deserialize<AzureTenantResponse>(response);
            if (tenant.value.Length == 0)
            {
                throw new Exception("No tenants found!");
            }

            return tenant.value[0].tenantId;
        }

        private async Task<string> RequestCloudShell()
        {
            const string resourceUri = "https://management.azure.com/providers/Microsoft.Portal/consoles/default?api-version=2018-10-01";
            const string body = @"
                {
                    ""Properties"": {
                        ""consoleRequestProperties"": {
                        ""osType"": ""linux""
                        }
                    }
                }
                ";

            byte[] response = await SendWebRequest(
                resourceUri: resourceUri,
                body: body,
                contentType: "application/json",
                token: _accessToken,
                method: HttpMethod.Put
            );

            var cloudShellResponse = JsonSerializer.Deserialize<CloudShellResponse>(response);
            return cloudShellResponse.properties["uri"];
        }

        private async Task GetDeviceCode()
        {
            const string resource = "https://management.core.windows.net/";
            string resourceUri = "https://login.microsoftonline.com/common/oauth2/devicecode";
            string encodedResource = Uri.EscapeDataString(resource);
            string body = $"client_id={ClientId}&resource={encodedResource}";
            byte[] response = await SendWebRequest(
                resourceUri: resourceUri,
                body: body,
                contentType: "application/x-www-form-urlencoded",
                method: HttpMethod.Post
            );

            var deviceCode = JsonSerializer.Deserialize<DeviceCodeResponse>(response);
            Console.WriteLine(deviceCode.message);

            resourceUri = "https://login.microsoftonline.com/common/oauth2/token";
            body = $"grant_type=device_code&resource={encodedResource}&client_id={ClientId}&code={deviceCode.device_code}";

            // poll until user authenticates
            for (int count = 0; count < deviceCode.expires_in / deviceCode.interval; count++)
            {
                response = await SendWebRequest(
                    resourceUri: resourceUri,
                    body: body,
                    contentType: "application/x-www-form-urlencoded",
                    method: HttpMethod.Post,
                    ignoreError: true
                );

                var authResponsePending = JsonSerializer.Deserialize<AuthResponsePending>(response);
                if (authResponsePending.error == null)
                {
                    var authResponse = JsonSerializer.Deserialize<AuthResponse>(response);
                    _accessToken = authResponse.access_token;
                    _refreshToken = authResponse.refresh_token;
                    return;
                }

                if (!authResponsePending.error.Equals("authorization_pending"))
                {
                    throw new Exception($"Authentication failed: {authResponsePending.error_description}");
                }

                Thread.Sleep(deviceCode.interval * 1000);
            }
        }

        private async Task<byte[]> SendWebRequest(
            string resourceUri,
            string body,
            string contentType,
            HttpMethod method,
            string token = null,
            bool ignoreError = false)
        {
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            if (token != null)
            {
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            }

            var request = new HttpRequestMessage()
            {
                RequestUri = new Uri(resourceUri),
                Method = method
            };

            if (body != null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, contentType);
            }

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage result = await _httpClient.SendAsync(request);
            if (!ignoreError && !result.IsSuccessStatusCode)
            {
                throw new Exception(result.ToString());
            }

            return await result.Content.ReadAsByteArrayAsync();
        }
    }
}