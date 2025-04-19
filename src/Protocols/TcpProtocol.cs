using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace IPK25_CHAT
{
    public class TcpProtocol : ChatProtocolBase
    {
        private TcpClient _client;
        private NetworkStream _stream;
        private StreamReader _reader;
        private StreamWriter _writer;
        private bool _isConnected = false;
        private Task _receiveTask;
        private bool _isDisposing = false;
        
        // Включение/выключение подробного логирования
        private bool _Logging = true;
        
        // Публичное свойство для управления логированием
        public bool Logging 
        { 
            get { return _Logging; } 
            set { _Logging = value; }
        }

        public TcpProtocol(string server, int port, bool logging)
        {
            _Logging = logging;
            LogDebug($"Connecting to {server}:{port} using TCP...");
            
            // Start a loop for periodic connection attempts
            bool connected = false;
            while (!connected)
            {
                try
                {
                    // Use timeout for connection
                    var connectTask = InitializeConnection(server, port);
                    connectTask.Wait(5000); // Wait maximum 5 seconds
                    
                    if (connectTask.IsCompleted && !connectTask.IsFaulted)
                    {
                        connected = true;
                    }
                    else 
                    {
                        // On failure, output a dot and wait before next attempt
                        Console.Write(".");
                        Thread.Sleep(1000); // 1 second between attempts
                    }
                }
                catch (Exception)
                {
                    // On connection error, wait and retry
                    Console.Write(".");
                    Thread.Sleep(1000);
                }
            }
        }

        // Вспомогательный метод для логирования
        private void LogDebug(string message)
        {
            if (_Logging)
            {
                Console.Error.WriteLine($"TCP DEBUG: {message}");
            }
        }

        private async Task InitializeConnection(string server, int port)
        {
            try
            {
                // Create new client for each connection attempt
                if (_client != null)
                {
                    try { _client.Dispose(); } catch { }
                }
                
                _client = new TcpClient();
                LogDebug($"Attempting to connect to {server}:{port}...");
                
                // Set timeout for connection operation
                using var timeoutCts = new CancellationTokenSource(5000); // 5 seconds timeout
                
                // Create connection task
                var connectTask = _client.ConnectAsync(server, port);
                
                // Wait for either connection or timeout
                await Task.WhenAny(connectTask, Task.Delay(4500, timeoutCts.Token));
                
                if (!connectTask.IsCompleted)
                {
                    LogDebug($"Connection timeout exceeded for {server}:{port}");
                    throw new TimeoutException($"Connection timeout exceeded for {server}:{port}");
                }
                
                // If connection task completed with error, throw exception
                if (connectTask.IsFaulted)
                {
                    LogDebug($"Connection failed with error: {connectTask.Exception?.InnerException?.Message}");
                    throw connectTask.Exception;
                }
                
                LogDebug($"Connected to {server}:{port} successfully");
                
                // Initialize streams
                _stream = _client.GetStream();
                _reader = new StreamReader(_stream, new UTF8Encoding(false));
                _writer = new StreamWriter(_stream, new UTF8Encoding(false)) 
                { 
                    NewLine = "\r\n",
                    AutoFlush = true 
                };
                _isConnected = true;

                LogDebug($"TCP streams initialized with NewLine=\\r\\n, starting message receiver task");
                
                // Check if connection is actually established
                if (_client.Connected)
                {
                    LogDebug($"Socket is confirmed as connected: {_client.Connected}");
                    LogDebug($"Local endpoint: {_client.Client.LocalEndPoint}, Remote endpoint: {_client.Client.RemoteEndPoint}");
                }
                else
                {
                    LogDebug("Warning: Socket reports as not connected despite successful connection task");
                }
                
                // Start message receiving task
                _receiveTask = Task.Run(ReceiveMessagesAsync);
            }
            catch (Exception ex)
            {
                _isConnected = false;
                LogDebug($"Failed to connect: {ex.Message}");
                OnError($"Failed to connect to {server}:{port}", ex);
                throw;
            }
        }

        private async Task ReceiveMessagesAsync()
        {
            try
            {
                LogDebug("Message receiver task started, waiting for incoming messages");
                
                while (_isConnected && !_isDisposing)
                {
                    string message = await _reader.ReadLineAsync();
                    if (message == null)
                    {
                        // Connection closed by server
                        LogDebug("Connection closed by server (null message received)");
                        OnMessageReceived(MessageType.Bye, "Connection closed by server");
                        break;
                    }

                    LogDebug($"Received raw message: '{message}'");
                    ProcessMessage(message);
                }
            }
            catch (IOException ioe) when (ioe.InnerException is SocketException se && 
                                         (se.SocketErrorCode == SocketError.ConnectionReset || 
                                          se.SocketErrorCode == SocketError.ConnectionAborted ||
                                          se.SocketErrorCode == SocketError.Interrupted ||
                                          se.SocketErrorCode == SocketError.NotConnected))
            {
                if (!_isDisposing)
                {
                    LogDebug($"Connection lost: {ioe.Message}, SocketError: {((SocketException)ioe.InnerException).SocketErrorCode}");
                    OnMessageReceived(MessageType.Bye, "Connection lost");
                }
            }
            catch (Exception ex)
            {
                if (!_isDisposing)
                {
                    LogDebug($"Error receiving messages: {ex.Message}");
                    OnError("Error receiving messages", ex);
                }
            }
            finally
            {
                _isConnected = false;
            }
        }

        private void ProcessMessage(string message)
        {
            try
            {
                if (string.IsNullOrEmpty(message))
                    return;

                LogDebug($"Processing message: '{message}'");
                
                string content = message;
                string displayName = null;
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
                    content = message.Substring(3).Trim();
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
                    
                    // Удаляем префикс "MSG FROM "
                    string msgBody = message.Substring(9).Trim();
                    
                    // Находим разделитель " IS "
                    int isIndex = msgBody.IndexOf(" IS ");
                    if (isIndex > 0)
                    {
                        // Извлекаем имя и содержание
                        displayName = msgBody.Substring(0, isIndex).Trim();
                        content = msgBody.Substring(isIndex + 4).Trim();
                        LogDebug($"Parsed as MSG: displayName='{displayName}', content='{content}'");
                    }
                    else
                    {
                        // Если нет разделителя, считаем всё содержимым
                        displayName = "Unknown";
                        content = msgBody;
                        LogDebug($"Parsed as MSG without IS separator: content='{content}'");
                    }
                }
                else
                {
                    // Для других типов сообщений
                    LogDebug($"Unknown message format: '{message}'");
                }

                LogDebug($"Forwarding message: Type={messageType}, Content='{content}', DisplayName='{displayName}'");
                OnMessageReceived(messageType, content, displayName);
            }
            catch (Exception ex)
            {
                LogDebug($"Error processing message '{message}': {ex.Message}");
                OnError($"Error processing message: {message}", ex);
            }
        }

        public override async Task SendAuthAsync(string username, string displayName, string secret)
        {
            LogDebug($"Sending AUTH: username='{username}', displayName='{displayName}', secret='{secret}'");
            await SendCommandAsync($"AUTH {username} AS {displayName} USING {secret}");
        }

        public override async Task SendJoinAsync(string channel, string displayName)
        {
            LogDebug($"Sending JOIN: channel='{channel}', displayName='{displayName}'");
            await SendCommandAsync($"JOIN {channel} AS {displayName}");
        }

        public override async Task SendMessageAsync(string displayName, string message)
        {
            LogDebug($"Sending MSG: displayName='{displayName}', message='{message}'");
            await SendCommandAsync($"MSG FROM {displayName} IS {message}");
        }
        
        public override async Task SendByeAsync(string displayName)
        {
            LogDebug($"Sending BYE: displayName='{displayName}'");
            await SendCommandAsync($"BYE FROM {displayName}");
        }
        
        private async Task SendCommandAsync(string command)
        {
            if (!_isConnected)
            {
                LogDebug("Cannot send command - not connected to server");
                throw new InvalidOperationException("Not connected to server");
            }

            try
            {
                // Проверка состояния соединения перед отправкой
                if (_client?.Client?.Connected != true)
                {
                    LogDebug($"Warning: Socket reports disconnected state, but _isConnected={_isConnected}");
                    LogDebug("Attempting to send command anyway...");
                }

                LogDebug($"Sending raw command: '{command}'");
                
                // Добавим явный вывод о том, что и как отправляется
                byte[] bytes = Encoding.UTF8.GetBytes(command + "\r\n");
                LogDebug($"Sending {bytes.Length} bytes: [{BitConverter.ToString(bytes)}]");
                
                await _writer.WriteLineAsync(command);
                await _writer.FlushAsync();
                LogDebug("Command sent successfully, waiting for response...");
                
                // Ожидание короткое время для получения ответа
                await Task.Delay(100);
                if (_client?.Available > 0)
                {
                    LogDebug($"Server immediately responded with {_client.Available} bytes available");
                }
                else
                {
                    LogDebug("No immediate response from server");
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to send command '{command}': {ex.Message}");
                LogDebug($"Exception type: {ex.GetType().Name}, Stack trace: {ex.StackTrace}");
                OnError($"Failed to send command: {command}", ex);
                throw;
            }
        }

        public override async Task DisconnectAsync()
        {
            if (_isDisposing)
                return;
        
            _isDisposing = true;
            LogDebug("Starting TCP disconnect process");
            
            try
            {
                // Safely close TCP connection
                _isConnected = false;
                
                // Use timeout for termination operations - 1 second
                using var timeoutCts = new CancellationTokenSource(1000);
                
                if (_writer != null)
                {
                    try 
                    {
                        LogDebug("Flushing writer buffer");
                        // Flush buffers and close streams with timeout
                        var flushTask = _writer.FlushAsync();
                        await Task.WhenAny(flushTask, Task.Delay(500, timeoutCts.Token));
                    }
                    catch (OperationCanceledException)
                    {
                        LogDebug("Writer flush operation cancelled due to timeout");
                        // Ignore cancellation - continue closing
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"Error flushing writer: {ex.Message}");
                        Console.Error.WriteLine($"Error flushing writer: {ex.Message}");
                    }
                }
                
                // Close resources in correct order
                try 
                {
                    LogDebug("Disposing stream resources");
                    _reader?.Dispose();
                    _writer?.Dispose();
                    
                    LogDebug("Shutting down socket connection");
                    // Proper connection shutdown without RST flag [RFC9293]
                    if (_client?.Connected == true)
                    {
                        // Proper termination - close socket with timeout
                        try 
                        {
                            _client?.Client?.Shutdown(SocketShutdown.Both);
                            LogDebug("Socket shutdown completed");
                        }
                        catch (Exception ex)
                        {
                            LogDebug($"Socket shutdown error: {ex.Message}");
                            // Ignore errors when closing socket
                        }
                        _client?.Close();
                    }
                    
                    _client?.Dispose();
                    LogDebug("All TCP resources disposed");
                }
                catch (Exception ex)
                {
                    LogDebug($"Error disposing resources: {ex.Message}");
                    Console.Error.WriteLine($"Error disposing resources: {ex.Message}");
                }
                
                _reader = null;
                _writer = null;
                _stream = null;
                _client = null;
                LogDebug("TCP disconnect process completed");
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to disconnect: {ex.Message}");
                OnError("Failed to disconnect", ex);
            }
        }
    }
    
} 