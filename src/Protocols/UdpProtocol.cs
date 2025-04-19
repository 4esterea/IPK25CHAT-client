using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IPK25_CHAT
{
    public class UdpProtocol : ChatProtocolBase
    {
        private UdpClient? _client;
        private IPEndPoint? _serverEndPoint;
        private IPEndPoint? _dynamicServerEndPoint;
        private bool _isRunning;
        private Task? _receiveTask;
        private CancellationTokenSource? _cancellationTokenSource;
        private ushort _messageId = 0;
        private readonly HashSet<ushort> _processedMessageIds = new();
        private readonly Dictionary<ushort, TaskCompletionSource<bool>> _pendingConfirmations = new();
        private readonly Dictionary<ushort, (byte[] data, int retryCount)> _pendingMessages = new();
        
        private readonly int _confirmationTimeoutMs;
        private readonly int _maxRetries;
        private bool _authenticated;
        private IPEndPoint? _localEndPoint;
        
        private bool _Logging = true;
        
        public bool Logging 
        { 
            get { return _Logging; } 
            set { _Logging = value; }
        }
        
        private enum ProtocolState
        {
            NotConnected,
            Connected,
            WaitingForAuthReply,
            Authenticated,
            WaitingForJoinReply,
            JoinedChannel
        }
        
        private ProtocolState _currentState = ProtocolState.NotConnected;
        private CancellationTokenSource? _authTimeoutCts;
        
        public UdpProtocol(string server, int port, bool logging, int confirmationTimeoutMs = 250, int maxRetries = 3)
        {
            _confirmationTimeoutMs = confirmationTimeoutMs;
            _maxRetries = maxRetries;
            _Logging = logging;
            LogDebug($"Connecting to {server}:{port} using UDP...");
            
            // Start a loop for periodic connection attempts
            bool connected = false;
            while (!connected)
            {
                try
                {
                    InitializeConnection(server, port);
                    connected = true;
                    LogDebug("UDP socket initialized successfully!");
                    
                    // Bind to a local endpoint to receive messages
                    if (_client?.Client?.LocalEndPoint is IPEndPoint localEndPoint)
                    {
                        _localEndPoint = localEndPoint;
                    }
                    LogDebug($"Local UDP endpoint: {_localEndPoint!.Address}:{_localEndPoint.Port}");
                }
                catch (Exception ex)
                {
                    // On connection error, wait and retry
                    Console.WriteLine($"UDP initialization error: {ex.Message}");
                    Thread.Sleep(1000);
                }
            }
        }

        private void InitializeConnection(string server, int port)
        {
            try
            {
                _cancellationTokenSource = new CancellationTokenSource();
                // Create a UDP client in non-connected mode
                _client = new UdpClient();
                
                // Store the server endpoint for initial communication
                _serverEndPoint = new IPEndPoint(IPAddress.Parse(ResolveHostName(server)), port);
                LogDebug($"Initial server endpoint: {_serverEndPoint}");
                
                // Bind the client to a local endpoint to receive responses
                _client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                
                _isRunning = true;

                // Start message receiving task
                _receiveTask = Task.Run(ReceiveMessagesAsync);
            }
            catch (Exception ex)
            {
                OnError($"Failed to initialize UDP connection to {server}:{port}", ex);
                throw;
            }
        }

        private string ResolveHostName(string hostName)
        {
            try
            {
                // Try to convert directly to IP address
                if (IPAddress.TryParse(hostName, out IPAddress? ipAddress))
                {
                    return hostName;
                }

                // If it's localhost or loopback, return 127.0.0.1
                if (hostName.ToLower() == "localhost" || hostName.ToLower() == "loopback")
                {
                    return "127.0.0.1";
                }

                // If failed, try to resolve hostname with timeout
                IPHostEntry ipHostInfo;
                
                // Create cancellation object
                using (var timeoutCancellationTokenSource = new CancellationTokenSource())
                {
                    // Set timeout - 5 seconds
                    timeoutCancellationTokenSource.CancelAfter(5000);
                    
                    try
                    {
                        // Asynchronously request DNS record and wait for result with timeout
                        var dnsTask = Task.Run(() => Dns.GetHostEntry(hostName));
                        
                        if (!dnsTask.Wait(4500))
                        {
                            throw new TimeoutException($"DNS request for {hostName} exceeded timeout");
                        }
                        
                        ipHostInfo = dnsTask.Result;
                    }
                    catch
                    {
                        // If failed to resolve name, return default IP address
                        // This allows to continue connection attempts
                        Console.WriteLine($"Failed to resolve hostname {hostName}, using 127.0.0.1");
                        return "127.0.0.1";
                    }
                }
                
                // Take first IPv4 address
                foreach (IPAddress ip in ipHostInfo.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return ip.ToString();
                    }
                }

                // If IPv4 address not found, take first available
                if (ipHostInfo.AddressList.Length > 0)
                {
                    return ipHostInfo.AddressList[0].ToString();
                }

                // If no address found, return loopback
                return "127.0.0.1";
            }
            catch (Exception ex)
            {
                OnError($"Error resolving hostname: {hostName}", ex);
                // Return loopback address to continue operation
                return "127.0.0.1";
            }
        }

        private async Task ReceiveMessagesAsync()
        {
            try
            {
                LogDebug("Starting UDP message receiver task...");
                while (_isRunning && !_cancellationTokenSource!.Token.IsCancellationRequested)
                {
                    LogDebug("Waiting for UDP messages...");
                    UdpReceiveResult result = await _client!.ReceiveAsync();
                    
                    LogDebug($"Received {result.Buffer.Length} bytes from {result.RemoteEndPoint}");
                    
                    if (_Logging)
                    {
                        LogReceivedBytes(result.Buffer);
                    }
                    
                    // Store the dynamic server endpoint after receiving first message
                    if (_dynamicServerEndPoint == null || !_authenticated)
                    {
                        _dynamicServerEndPoint = result.RemoteEndPoint;
                        LogDebug($"Updated dynamic server endpoint to {_dynamicServerEndPoint}");
                    }
                    
                    await ProcessReceivedDataAsync(result.Buffer);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal termination when cancelled
                LogDebug("UDP receiver task cancelled");
            }
            catch (Exception ex)
            {
                if (_isRunning)
                {
                    Console.WriteLine($"UDP receiver error: {ex.Message}");
                    OnError("Error receiving UDP messages", ex);
                }
            }
        }
        
        private void LogDebug(string message)
        {
            if (_Logging)
            {
                Console.Error.WriteLine($"UDP DEBUG: {message}");
            }
        }
        
        private void LogReceivedBytes(byte[]? data)
        {
            if (data == null || data.Length == 0)
            {
                Console.WriteLine("Received empty data");
                return;
            }
            byte messageType = data[0];
                
                switch (messageType)
                {
                    case 0x00:
                        LogDebug("Message Type: CONFIRM");
                        break;
                    case 0x01:
                        LogDebug("Message Type: REPLY");
                        break;
                    case 0x02:
                        LogDebug("Message Type: AUTH");
                        break;
                    case 0x03:
                        LogDebug("Message Type: JOIN");
                        break;
                    case 0x04:
                        LogDebug("Message Type: MSG");
                        break;
                    case 0xFD:
                        LogDebug("Message Type: PING");
                        break;
                    case 0xFE:
                        LogDebug("Message Type: ERR");
                        break;
                    case 0xFF:
                        LogDebug("Message Type: BYE");
                        break;
                    default:
                        LogDebug($"Unknown Message Type: 0x{messageType:X2}");
                        break;
                }
            }

        private async Task ProcessReceivedDataAsync(byte[]? data)
        {
            if (data == null || data.Length < 1)
            {
                OnError("Received invalid UDP message (empty)");
                return;
            }

            try
            {
                if (data.Length < 3)
                {
                    OnError("Received invalid UDP binary message (too short)");
                    return;
                }

                byte messageType = data[0];
                ushort messageId = BitConverter.ToUInt16(data, 1);

                LogDebug($"Processing binary message: Type=0x{messageType:X2}, ID={messageId}, CurrentState={_currentState}");

                // Send CONFIRM for all messages except PING and BYE
                if (messageType != 0x00)
                {
                    LogDebug($"Sending CONFIRM for message ID {messageId}");
                    await SendConfirmAsync(messageId);
                }

                // Check if message ID is already processed
                if (messageType != 0x00 && messageType != 0x01)
                {
                    if (_processedMessageIds.Contains(messageId))
                    {
                        LogDebug($"Message ID {messageId} already processed, ignoring");
                        return;
                    }
                    _processedMessageIds.Add(messageId);
                }
                else if (messageType == 0x01)
                {
                    if (_processedMessageIds.Contains(messageId) && 
                        _currentState != ProtocolState.WaitingForAuthReply && 
                        _currentState != ProtocolState.WaitingForJoinReply)
                    {
                        LogDebug($"REPLY message ID {messageId} already processed and not in waiting state, ignoring");
                        return;
                    }
                    if (!_processedMessageIds.Contains(messageId))
                    {
                        _processedMessageIds.Add(messageId);
                    }
                }

                switch (messageType)
                {
                    case 0x00: // CONFIRM
                        if (data.Length >= 3)
                        {
                            ushort confirmedMessageId = BitConverter.ToUInt16(data, 1);
                            LogDebug($"Received CONFIRM for message ID {confirmedMessageId}");
                            HandleConfirmation(confirmedMessageId);
                        }
                        break;

                    case 0x01: // REPLY
                        if (data.Length >= 7)
                        {
                            byte result = data[3];
                            ushort refMessageId = BitConverter.ToUInt16(data, 4);
                            string content = ExtractNullTerminatedString(data, 6);
                            
                            // Convert to text format
                            string tcpFormattedMsg = $"REPLY {(result == 1 ? "OK" : "NOK")} IS {content}";
                            ProcessMessage(tcpFormattedMsg);
                            
                            // Additional processing
                            if (_currentState == ProtocolState.WaitingForAuthReply)
                            {
                                _authenticated = (result == 1);
                                _currentState = result == 1 ? ProtocolState.Authenticated : ProtocolState.Connected;
                                
                                if (_authTimeoutCts != null && !_authTimeoutCts.IsCancellationRequested)
                                {
                                    _authTimeoutCts.Cancel();
                                }
                            }
                            else if (_currentState == ProtocolState.WaitingForJoinReply)
                            {
                                _currentState = result == 1 ? ProtocolState.JoinedChannel : ProtocolState.Authenticated;
                            }
                        }
                        break;

                    case 0x04: // MSG
                        if (data.Length >= 4)
                        {
                            int offset = 3;
                            string displayName = ExtractNullTerminatedString(data, offset);
                            offset += displayName.Length + 1;
                            
                            if (offset < data.Length)
                            {
                                string content = ExtractNullTerminatedString(data, offset);
                                // Convert to text format
                                string tcpFormattedMsg = $"MSG FROM {displayName} IS {content}";
                                ProcessMessage(tcpFormattedMsg);
                            }
                        }
                        break;

                    case 0xFD: // PING 
                        LogDebug($"Received PING message (ID: {messageId})");
                        break;

                    case 0xFE: // ERR
                        if (data.Length >= 4)
                        {
                            int offset = 3;
                            string displayName = ExtractNullTerminatedString(data, offset);
                            offset += displayName.Length + 1;
                            
                            if (offset < data.Length)
                            {
                                string content = ExtractNullTerminatedString(data, offset);
                                // Конвертируем в текстовый формат
                                string tcpFormattedMsg = $"ERR FROM {displayName} IS {content}";
                                ProcessMessage(tcpFormattedMsg);
                                await DisconnectAsync();
                            }
                        }
                        break;

                    case 0xFF: // BYE
                        if (data.Length >= 4)
                        {
                            int offset = 3;
                            string displayName = ExtractNullTerminatedString(data, offset);
                            
                            // Convert to text format
                            string tcpFormattedMsg = $"BYE FROM {displayName}";
                            ProcessMessage(tcpFormattedMsg);
                            await DisconnectAsync();
                        }
                        break;

                    default:
                        LogDebug($"Received unknown message type: 0x{messageType:X2}");
                        OnError($"Received unknown message type: {messageType}");
                        break;
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error processing UDP message: {ex.Message}");
                OnError($"Error processing UDP message", ex);
            }
        }
        
        private void HandleConfirmation(ushort messageId)
        {
            if (_pendingConfirmations.TryGetValue(messageId, out var tcs))
            {
                tcs.TrySetResult(true);
                _pendingConfirmations.Remove(messageId);
                _pendingMessages.Remove(messageId);
            }
        }
        
        private string ExtractNullTerminatedString(byte[] data, int startIndex)
        {
            if (startIndex >= data.Length)
            {
                LogDebug($"WARNING: startIndex {startIndex} is outside data bounds (length: {data.Length})");
                return string.Empty;
            }
                
            int endIndex = startIndex;
            while (endIndex < data.Length && data[endIndex] != 0)
            {
                endIndex++;
            }
            
            int length = endIndex - startIndex;
            if (length <= 0)
            {
                LogDebug($"WARNING: Empty string at position {startIndex}");
                return string.Empty;
            }
            
            string result = Encoding.ASCII.GetString(data, startIndex, length);
            LogDebug($"Extracted string from bytes {startIndex}-{endIndex}: '{result}'");
            return result;
        }

        private void ProcessMessage(string message)
        {
            try
            {
                if (string.IsNullOrEmpty(message))
                    return;

                LogDebug($"Processing message: '{message}'");
                
                string content = message;
                string? displayName = null;
                MessageType messageType = MessageType.Message;

                if (message.StartsWith("REPLY"))
                {
                    messageType = MessageType.Reply;
                    content = message.Substring(5).Trim();
                    LogDebug($"Parsed as REPLY: '{content}'");
                }
                else if (message.StartsWith("ERR"))
                {
                    messageType = MessageType.Error;
                    displayName = message.Split(' ')[2];
                    content = message.Substring(message.IndexOf("IS", StringComparison.Ordinal) + 2).Trim();
                    LogDebug($"Parsed as ERR: '{content}'");
                }
                else if (message.StartsWith("BYE"))
                {
                    messageType = MessageType.Bye;
                    content = message.Substring(3).Trim();
                    LogDebug($"Parsed as BYE: '{content}'");
                }
                else if (message.StartsWith("MSG FROM"))
                {
                    messageType = MessageType.Message;
                    
                    // Delete "MSG FROM" 
                    string msgBody = message.Substring(9).Trim();
                    
                    // Find " IS " separator
                    int isIndex = msgBody.IndexOf(" IS ", StringComparison.Ordinal);
                    if (isIndex > 0)
                    {
                        // Extract displayName and content
                        displayName = msgBody.Substring(0, isIndex).Trim();
                        content = msgBody.Substring(isIndex + 4).Trim();
                        LogDebug($"Parsed as MSG: displayName='{displayName}', content='{content}'");
                    }
                    else
                    {
                        // If " IS " separator not found, treat as unknown format
                        displayName = "Unknown";
                        content = msgBody;
                        LogDebug($"Parsed as MSG without IS separator: content='{content}'");
                    }
                }
                else
                {
                    // Unknown format
                    LogDebug($"Unknown message format: '{message}'");
                }

                LogDebug($"Forwarding message: Type={messageType}, Content='{content}', DisplayName='{displayName}'");
                OnMessageReceived(messageType, content, displayName!);
            }
            catch (Exception ex)
            {
                LogDebug($"Error processing message '{message}': {ex.Message}");
                OnError($"Error processing message: {message}", ex);
            }
        }

        public override async Task SendAuthAsync(string? username, string? displayName, string? secret)
        {
            
            // Create AUTH message
            using (var ms = new System.IO.MemoryStream())
            {
                // Message type (AUTH = 0x02)
                ms.WriteByte(0x02);
                
                try
                {
                    byte[]? message = CreateBinaryMessage(0x02, username!, displayName!, secret!);
                    ushort msgId = BitConverter.ToUInt16(message!, 1);
                    
                    // Set current state to waiting for AUTH reply
                    _currentState = ProtocolState.WaitingForAuthReply;
                    
                    LogDebug($"Sending AUTH message: Username={username}, DisplayName={displayName}, MsgID={msgId}");
                    
                    // Try with confirmation first
                    bool success = await SendWithConfirmationAsync(message, msgId);
                    
                    if (!success)
                    {
                        // If confirmation fails, try sending without waiting for confirmation
                        LogDebug("Server did not confirm AUTH message receipt, proceeding anyway...");
                        await SendRawAsync(message);
                    }
                    
                    LogDebug("AUTH message was sent to server");
                    
                    // Create a cancellation token for the authentication timeout
                    _authTimeoutCts = new CancellationTokenSource(15000); 
                    
                    try
                    {
                        await Task.Delay(15000, _authTimeoutCts.Token);
                        
                        // If we get here without cancellation, it means we didn't receive a REPLY
                        if (!_authenticated)
                        {
                            Console.WriteLine("Authentication timeout - server did not send REPLY");
                            _currentState = ProtocolState.Connected;
                            OnError("Authentication failed - server did not reply in time");
                            throw new TimeoutException("Server did not reply to authentication request");
                        }
                        else
                        {
                            LogDebug("Authentication successful (confirmed by REPLY)");
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        // Task was cancelled, meaning we received a REPLY
                    }
                }
                catch (Exception ex)
                {
                    _currentState = ProtocolState.Connected;
                    OnError($"Authentication failed - {ex.Message}", ex);
                    throw;
                }
            }
        }

        public override async Task SendJoinAsync(string? channel, string? displayName)
        {
            try
            {
                // Create JOIN message
                byte[]? data = CreateBinaryMessage(0x03, channel!, displayName!);
                ushort msgId = BitConverter.ToUInt16(data!, 1);
                
                _currentState = ProtocolState.WaitingForJoinReply;
                
                LogDebug($"Sending JOIN message: Channel={channel}, DisplayName={displayName}, MsgID={msgId}, Data length={data!.Length} bytes");
                LogDebug($"JOIN data: First 3 bytes: [{data[0]:X2} {data[1]:X2} {data[2]:X2}]");
                LogDebug($"JOIN packet full structure: Type(0x03), MsgID({msgId}), Channel({channel}\\0), DisplayName({displayName}\\0)");
                
                // Try with confirmation first
                bool success = await SendWithConfirmationAsync(data, msgId);
                
                if (!success)
                {
                    // If confirmation fails, try sending without waiting for confirmation
                    LogDebug("Server did not confirm JOIN message receipt, proceeding anyway...");
                    await SendRawAsync(data);
                }
                
                LogDebug("JOIN message sent to server");
                LogDebug("Waiting for join confirmation from server...");
                
                using (var timeoutCts = new CancellationTokenSource(5000))
                {
                    try
                    {
                        await Task.Delay(5000, timeoutCts.Token);
                        
                        // If we get here without cancellation, it means we didn't receive a REPLY
                        if (_currentState == ProtocolState.WaitingForJoinReply)
                        {
                            Console.WriteLine("Join timeout - server did not send REPLY");
                            _currentState = ProtocolState.Authenticated; 
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        // Task was cancelled, meaning we received a REPLY
                        LogDebug("Join task was cancelled");
                    }
                }
                
            }
            catch (Exception ex)
            {
                LogDebug($"Error joining channel: {ex.Message}");
                throw;
            }
        }

        public override async Task SendMessageAsync(string? displayName, string message)
        {
            if (!_authenticated)
            {
                throw new InvalidOperationException("Cannot send message before authentication");
            }
            
            LogDebug($"Preparing to send message. Auth state: {_authenticated}, State: {_currentState}");
            
            // Create MSG message
            using (var ms = new System.IO.MemoryStream())
            {
                byte[]? data = CreateBinaryMessage(0x04, displayName!, message);
                ushort msgId = BitConverter.ToUInt16(data!, 1);
                
                LogDebug($"Sending MSG: DisplayName={displayName}, Content='{message}', Length={data!.Length} bytes, MsgID={msgId}");
                
                try
                {
                    // Try with confirmation first
                    bool success = await SendWithConfirmationAsync(data, msgId);
                    
                    if (!success)
                    {
                        // If confirmation fails, try sending without waiting for confirmation
                        LogDebug("Server did not confirm MSG receipt, proceeding anyway...");
                        await SendRawAsync(data);
                    }
                    
                    LogDebug("Message sent successfully");
                }
                catch (Exception ex)
                {
                    LogDebug($"Error sending message: {ex.Message}");
                    OnError($"Failed to send message: {ex.Message}", ex);
                    throw;
                }
            }
        }
        
        public override async Task SendByeAsync(string? displayName)
        {
            byte[]? data = CreateBinaryMessage(0xFF, displayName!);
            ushort msgId = BitConverter.ToUInt16(data!, 1);
                
            try
            {
                // Try with confirmation first
                bool success = await SendWithConfirmationAsync(data, msgId);
                
                if (!success)
                {
                    // If confirmation fails, try sending without waiting for confirmation
                    LogDebug("Server did not confirm BYE message receipt, proceeding anyway...");
                    await SendRawAsync(data);
                }
                
                LogDebug("BYE message sent to server");
            }
            catch (Exception ex)
            {
                    Console.WriteLine($"Error sending BYE message: {ex.Message}");
            }
        }
        
        private byte[]? CreateConfirmMessage(ushort confirmMessageId)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(0x00); // CONFIRM type
            byte[] msgIdBytes = BitConverter.GetBytes(confirmMessageId);
            ms.Write(msgIdBytes, 0, 2);
            return ms.ToArray();
        }

        private async Task SendConfirmAsync(ushort messageId)
        {
            try
            {
                byte[]? data = CreateConfirmMessage(messageId);
                await SendRawAsync(data);
            }
            catch (Exception ex)
            {
                OnError("Failed to send CONFIRM message", ex);
            }
        }
        
        public override async Task DisconnectAsync()
        {
            try
            {
                // Cancel message receiving and stop operation
                _isRunning = false;
                
                // Use timeout for termination operations - 1 second
                using var timeoutCts = new CancellationTokenSource(1000);
                
                // Cancel tasks
                if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
                {
                    try 
                    {
                        _cancellationTokenSource.Cancel();
                    }
                    catch 
                    {
                        // Ignore errors during cancellation
                    }
                }
                
                // Close UDP client
                try 
                {
                    _client?.Close();
                    _client?.Dispose();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error closing UDP client: {ex.Message}");
                }
                _client = null;
                
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                OnError("Failed to disconnect UDP client", ex);
            }
        }

        private async Task<bool> SendWithConfirmationAsync(byte[]? data, ushort messageId)
        {
            var tcs = new TaskCompletionSource<bool>();
            _pendingConfirmations[messageId] = tcs;
            _pendingMessages[messageId] = (data!, 0);
            
            // First attempt
            await SendRawAsync(data);
            
            // Wait for confirmation with timeout
            for (int retry = 0; retry < _maxRetries; retry++)
            {
                try
                {
                    using (var cts = new CancellationTokenSource(_confirmationTimeoutMs))
                    {
                        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(_confirmationTimeoutMs, cts.Token));
                        
                        if (completedTask == tcs.Task && await tcs.Task)
                        {
                            // Message confirmed
                            return true;
                        }
                        
                        // If we're out of retries, fail
                        if (retry >= _maxRetries)
                        {
                            break;
                        }
                        
                        // Otherwise retry
                        await SendRawAsync(data);
                    }
                }
                catch (TaskCanceledException)
                {
                    // Timeout occurred, will retry if retries left
                }
            }
            
            // If we get here, all retries failed
            _pendingConfirmations.Remove(messageId);
            _pendingMessages.Remove(messageId);
            return false;
        }

        private async Task SendRawAsync(byte[]? data)
        {
            if (_client == null)
            {
                throw new InvalidOperationException("UDP client is not initialized");
            }

            try
            {
                // Use dynamic server endpoint if available, otherwise use the initial endpoint
                var endpoint = _dynamicServerEndPoint ?? _serverEndPoint;
                LogDebug($"Sending {data!.Length} bytes to {endpoint}");
                if (_Logging)
                {
                    LogReceivedBytes(data); // Reuse the same method for logging sent data
                }
                await _client.SendAsync(data, data.Length, endpoint);
            }
            catch (Exception ex)
            {
                LogDebug($"Error sending UDP data: {ex.Message}");
                OnError($"Failed to send UDP data", ex);
                throw;
            }
        }

        private byte[]? CreateBinaryMessage(byte messageType, params string[] strings)
        {
            // Create a memory stream to build the message
            using var ms = new MemoryStream();
    
            // Write the message type byte
            ms.WriteByte(messageType);
    
            // Generate and write a unique message ID (2 bytes)
            ushort msgId = _messageId++;
            byte[] msgIdBytes = BitConverter.GetBytes(msgId);
            ms.Write(msgIdBytes, 0, 2);
    
            // Write each string followed by a null terminator (0x00)
            foreach (var str in strings)
            {
                byte[] strBytes = Encoding.ASCII.GetBytes(str);
                ms.Write(strBytes, 0, strBytes.Length);
                ms.WriteByte(0); 
            }

            // Convert the stream to a byte array and return it
            return ms.ToArray();
        }
    }
} 