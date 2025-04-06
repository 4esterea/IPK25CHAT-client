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

        public TcpProtocol(string server, int port)
        {
            Console.WriteLine("Waiting to connect to server...");
            
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
                
                // Set timeout for connection operation
                using var timeoutCts = new CancellationTokenSource(5000); // 5 seconds timeout
                
                // Create connection task
                var connectTask = _client.ConnectAsync(server, port);
                
                // Wait for either connection or timeout
                await Task.WhenAny(connectTask, Task.Delay(4500, timeoutCts.Token));
                
                if (!connectTask.IsCompleted)
                {
                    throw new TimeoutException($"Connection timeout exceeded for {server}:{port}");
                }
                
                // If connection task completed with error, throw exception
                if (connectTask.IsFaulted)
                {
                    throw connectTask.Exception;
                }
                
                // Initialize streams
                _stream = _client.GetStream();
                _reader = new StreamReader(_stream, Encoding.UTF8);
                _writer = new StreamWriter(_stream, Encoding.UTF8);
                _writer.AutoFlush = true;
                _isConnected = true;

                // Start message receiving task
                _receiveTask = Task.Run(ReceiveMessagesAsync);
            }
            catch (Exception ex)
            {
                _isConnected = false;
                OnError($"Failed to connect to {server}:{port}", ex);
                throw;
            }
        }

        private async Task ReceiveMessagesAsync()
        {
            try
            {
                while (_isConnected && !_isDisposing)
                {
                    string message = await _reader.ReadLineAsync();
                    if (message == null)
                    {
                        // Connection closed by server
                        OnMessageReceived(MessageType.Bye, "Connection closed by server");
                        break;
                    }

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
                    OnMessageReceived(MessageType.Bye, "Connection lost");
                }
            }
            catch (Exception ex)
            {
                if (!_isDisposing)
                {
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

                // Remove REPLY prefix if present
                string content = message;
                string displayName = null;
                MessageType messageType = MessageType.Message;

                if (message.StartsWith("REPLY"))
                {
                    messageType = MessageType.Reply;
                    content = message.Substring(5).Trim();
                }
                else if (message.StartsWith("ERR"))
                {
                    messageType = MessageType.Error;
                    content = message.Substring(3).Trim();
                }
                else if (message.StartsWith("BYE"))
                {
                    messageType = MessageType.Bye;
                    content = message.Substring(3).Trim();
                }
                else
                {
                    // Process message in the correct format
                    int colonIndex = message.IndexOf(" FROM ");
                    if (colonIndex > 0)
                    {
                        displayName = message.Substring(colonIndex + 6).Trim(); // Extract username
                        content = message.Substring(0, colonIndex).Trim(); // Extract message
                    }
                }

                OnMessageReceived(messageType, content, displayName);
            }
            catch (Exception ex)
            {
                OnError($"Error processing message: {message}", ex);
            }
        }

        public override async Task SendAuthAsync(string username, string displayName, string secret)
        {
            await SendCommandAsync($"AUTH {username} AS {displayName} USING {secret}");
        }

        public override async Task SendJoinAsync(string channel, string displayName)
        {
            await SendCommandAsync($"JOIN {channel} AS {displayName}");
        }

        public override async Task SendMessageAsync(string displayName, string message)
        {
            await SendCommandAsync($"MSG FROM {displayName} IS {message}");
        }
        
        public override async Task SendByeAsync(string displayName)
        {
            await SendCommandAsync($"BYE FROM {displayName}");
        }
        
        public override async Task DisconnectAsync()
        {
            if (_isDisposing)
                return;
                
            _isDisposing = true;
            
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
                        // Flush buffers and close streams with timeout
                        var flushTask = _writer.FlushAsync();
                        await Task.WhenAny(flushTask, Task.Delay(500, timeoutCts.Token));
                    }
                    catch (OperationCanceledException)
                    {
                        // Ignore cancellation - continue closing
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error flushing writer: {ex.Message}");
                    }
                }
                
                // Close resources in correct order
                try 
                {
                    _reader?.Dispose();
                    _writer?.Dispose();
                    
                    // Proper connection shutdown without RST flag [RFC9293]
                    if (_client?.Connected == true)
                    {
                        // Proper termination - close socket with timeout
                        try 
                        {
                            _client?.Client?.Shutdown(SocketShutdown.Both);
                        }
                        catch 
                        {
                            // Ignore errors when closing socket
                        }
                        _client?.Close();
                    }
                    
                    _client?.Dispose();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error disposing resources: {ex.Message}");
                }
                
                _reader = null;
                _writer = null;
                _stream = null;
                _client = null;
            }
            catch (Exception ex)
            {
                OnError("Failed to disconnect", ex);
            }
        }

        private async Task SendCommandAsync(string command)
        {
            if (!_isConnected)
            {
                throw new InvalidOperationException("Not connected to server");
            }

            try
            {
                await _writer.WriteLineAsync(command);
                await _writer.FlushAsync();
            }
            catch (Exception ex)
            {
                OnError($"Failed to send command: {command}", ex);
                throw;
            }
        }
    }
} 