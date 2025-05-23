using System;
using System.Threading.Tasks;
using System.Threading;

namespace IPK25_CHAT
{
    public class ChatClient
    {
        private readonly IChatProtocol _protocol;
        private string? _displayName;
        private bool _isAuthenticated;
        private string? _currentChannel;
        private ClientState _currentState = ClientState.Init;
        private string? _requestedChannel; 
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isTerminating;
        private bool _Logging;

        public ChatClient(IChatProtocol protocol, bool verboseLogging = false)
        {
            _protocol = protocol;
            _protocol.MessageReceived += HandleMessageReceived;
            _protocol.Error += HandleError;
            _cancellationTokenSource = new CancellationTokenSource();
            _Logging = verboseLogging;
            
            // Apply logging settings for protocols
            if (_protocol is TcpProtocol tcpProtocol)
            {
                tcpProtocol.Logging = _Logging;
            }
            else if (_protocol is UdpProtocol udpProtocol)
            {
                udpProtocol.Logging = _Logging;
            }
        }

        public async Task RunAsync()
        {
            try
            {
                Console.CancelKeyPress += HandleCancelKeyPress;
                
                while (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    var input = Console.ReadLine();
                    
                    if (input == null)
                    {
                        LogDebug("End of input detected, terminating gracefully...");
                        await GracefulShutdownAsync();
                        break;
                    }
                    
                    if (string.IsNullOrEmpty(input))
                        continue;

                    if (input.StartsWith("/"))
                    {
                        await HandleCommandAsync(input);
                    }
                    else
                    {
                        await HandleMessageAsync(input);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                LogDebug("Client operation was cancelled");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Fatal error: {ex}");
                
                await GracefulShutdownAsync();
                throw;
            }
            finally
            {
                Console.CancelKeyPress -= HandleCancelKeyPress;
            }
        }

        private void HandleCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            
            Task.Run(async () => 
            {
                try
                {
                    LogDebug("Interrupt signal received (Ctrl+C), terminating gracefully...");
                    
                    using var timeoutCts = new CancellationTokenSource(5000);
                    
                    var shutdownTask = GracefulShutdownAsync();
                    
                    if (await Task.WhenAny(shutdownTask, Task.Delay(4000, timeoutCts.Token)) != shutdownTask)
                    {
                        LogDebug("Shutdown taking too long, forcing exit...");
                        Environment.Exit(1);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error during shutdown: {ex.Message}");
                    Environment.Exit(1);
                }
            });
        }

        private async Task GracefulShutdownAsync(int exitCode = 0)
        {
            if (_isTerminating)
                return;
            
            _isTerminating = true;
            
            try
            {
                _cancellationTokenSource.Cancel();
                
                using var timeoutCts = new CancellationTokenSource(3000);
                
                try
                {
                    if (_isAuthenticated)
                    {
                        LogDebug("Sending BYE message to server...");
                        
                        var byeTask = _protocol.SendByeAsync(_displayName);
                        await Task.WhenAny(byeTask, Task.Delay(1000, timeoutCts.Token));
                        
                        if (!byeTask.IsCompleted)
                        {
                            LogDebug("BYE message send timed out");
                        }
                        
                        if (_protocol is UdpProtocol && !timeoutCts.IsCancellationRequested)
                        {
                            LogDebug("Waiting for server to acknowledge BYE message...");
                            await Task.Delay(500, timeoutCts.Token);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    LogDebug("BYE operation was cancelled due to timeout");
                }
                
                try
                {
                    if (_protocol != null)
                    {
                        var disconnectTask = _protocol.DisconnectAsync();
                        await Task.WhenAny(disconnectTask, Task.Delay(1000, timeoutCts.Token));
                        
                        if (!disconnectTask.IsCompleted)
                        {
                            LogDebug("Disconnect operation timed out");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    LogDebug("Disconnect operation was cancelled due to timeout");
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error during shutdown: {ex.Message}");
            }
            finally
            {
                LogDebug("Shutdown complete");
                Environment.Exit(exitCode);
            }
        }

        private async Task HandleCommandAsync(string command)
        {
            string?[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return;

            switch (parts[0]!.ToLower())
            {
                case "/auth":
                    if (_currentState == ClientState.Init || _currentState == ClientState.Auth)
                    {
                        if (parts.Length != 4)
                        {
                            Console.WriteLine("Usage: /auth <username> <secret> <displayname>");
                            return;
                        }

                        if (!ValidateUsername(parts[1]) ||
                            !ValidateSecret(parts[2]) ||
                            !ValidateDisplayName(parts[3]))
                        {
                            Console.WriteLine("Invalid username, secret or display name");
                            return;
                        }
                        
                        await HandleAuthAsync(parts[1], parts[2], parts[3]);
                    }
                    else
                    {
                        Console.WriteLine("Already authenticated");
                    }
                    break;

                case "/join":
                    if (_currentState == ClientState.Open)
                    {
                        if (parts.Length != 2)
                        {
                            Console.WriteLine("Usage: /join <channel>");
                            return;
                        }
                        
                        if (!ValidateChannelId(parts[1])) 
                        {
                            Console.WriteLine("Invalid channel ID");
                            return;
                        }
                        
                        string? requestedChannel = parts[1];

                        _requestedChannel = requestedChannel;

                        await _protocol.SendJoinAsync(requestedChannel, _displayName);

                        LogDebug(
                            $"Join request sent for channel '{requestedChannel}'. Waiting for server confirmation...");
                    }
                    else
                    {
                        Console.WriteLine("You must be authenticated to join a channel");
                    }
                    break;

                case "/rename":
                    if (parts.Length != 2)
                    {
                        Console.WriteLine("Usage: /rename <displayname>");
                        return;
                    }
                    
                    if (!ValidateDisplayName(parts[1]))
                    {
                        Console.WriteLine("Invalid display name");
                        return;
                    }
                    
                    _displayName = parts[1];
                    Console.WriteLine($"Display name changed to {_displayName}");
                    break;

                case "/help":
                    PrintHelp();
                    break;
                    
                default:
                    Console.WriteLine("Unknown command. Use /help for available commands");
                    break;
            }
        }

        private async Task HandleAuthAsync(string? username, string? secret, string? displayName)
        {
            try
            {
                if (_currentState == ClientState.Init){
                    _currentState = ClientState.Auth;
                }
                
                await _protocol.SendAuthAsync(username, displayName, secret);
                
                _displayName = displayName;
                
                LogDebug($"Authentication request sent as {displayName}. Waiting for server response...");
            }
            catch (Exception ex)
            {
                _currentState = ClientState.Init;
                Console.WriteLine($"Authentication Failure: {ex.Message}");
            }
        }

        private async Task HandleMessageAsync(string message)
        {
            if (!_isAuthenticated)
            {
                Console.WriteLine("You must authenticate first");
                return;
            }

            if (string.IsNullOrEmpty(_currentChannel))
            {
                _currentChannel = "default";
                LogDebug("You are in the default channel");
            }
            
            if (!ValidateMessageContent(message))
            {
                Console.WriteLine("Invalid message content");
                return;
            }
            
            LogDebug($"Sending message to channel {_currentChannel}");
            
            await _protocol.SendMessageAsync(_displayName, message);
        }

        private void HandleMessageReceived(object? sender, MessageReceivedEventArgs e)
        {
            try
            {
                LogDebug($"Received message: {e.Content}, Type: {e.Type}, DisplayName: {e.DisplayName}");
                switch (e.Type)
                {
                    case MessageType.Reply:
                        string? content = e.Content;
                        
                        string[] replyParts = content!.Split(" ");
                        if (replyParts[0] == "OK")
                        {
                            string replyMessage = "";
                            
                            if (content.Contains(" IS "))
                            {
                                string[] splitContent = content.Split(new[] { " IS " }, StringSplitOptions.None);
                                if (splitContent.Length > 1)
                                {
                                    replyMessage = splitContent[1];
                                }
                            }
                            
                            if (!ValidateMessageContent(replyMessage))
                            {
                                HandleMalformedMessage($"REPLY {content}");
                                return;
                            }
                            Console.WriteLine($"Action Success: {replyMessage}");
                            
                            // Save previous state for logging
                            ClientState previousState = _currentState;

                            
                            if (_currentState == ClientState.Auth)
                            {
                                _isAuthenticated = true;
                                _currentChannel = "default";
                                
                                LogDebug("Authentication successful, setting _isAuthenticated=true");
                            }
                            else
                            {
                                _currentChannel = _requestedChannel;
                                _requestedChannel = null;
                            }
                            _currentState = ClientState.Open;
                            
                            // Log state changes only when verbose logging is enabled
                            LogDebug($"State changed: {previousState} -> {_currentState}");
                        }
                        else if (replyParts[0] == "NOK"){
                            string replyMessage = content.Split(new[] { " IS " }, StringSplitOptions.None)[1];
                            
                            if (!ValidateMessageContent(replyMessage))
                            {
                                HandleMalformedMessage($"REPLY {content}");
                                return;
                            }
                            
                            Console.WriteLine($"Action Failure: {replyMessage}");
                            
                            if (_currentState == ClientState.Join) _currentState = ClientState.Open;
                            _requestedChannel = null;
                        }
                        else
                        {
                            HandleMalformedMessage($"REPLY {content}");
                        }
                        break;
                    
                    case MessageType.Message:
                        if (_currentState == ClientState.Open || _currentState == ClientState.Join)
                        {
                            LogDebug($"Processing message: '{e.Content}', DisplayName: '{e.DisplayName}'");
                            
                            if (!string.IsNullOrEmpty(e.DisplayName) && !string.IsNullOrEmpty(e.Content))
                            {
                                if (!ValidateDisplayName(e.DisplayName) ||
                                    !ValidateMessageContent(e.Content))
                                {
                                    HandleMalformedMessage($"MSG FROM {e.DisplayName} IS {e.Content}");
                                    return;
                                }
                                
                                Console.WriteLine($"{e.DisplayName}: {e.Content}");
                            }
                            else
                            {
                                HandleMalformedMessage($"MSG FROM {e.DisplayName} IS {e.Content}");
                            }
                        }
                        break;
                    
                    case MessageType.Error:
                        LogDebug("Received ERR message");
                        
                        if (!ValidateUsername(e.DisplayName) ||
                            !ValidateMessageContent(e.Content))
                        {
                            HandleMalformedMessage($"ERROR FROM {e.DisplayName} IS {e.Content}");
                            return;
                        }
                        
                        Console.WriteLine($"ERROR FROM {e.DisplayName}: {e.Content}");
                        _currentState = ClientState.End;
                        Environment.Exit(0);
                        break;   
                    
                    case MessageType.Bye:
                        LogDebug("Received BYE message from server");
                        _currentState = ClientState.End;
                        Environment.Exit(0);
                        break;    
                    
                    default:
                        LogDebug($"Unknown message type: {e.Type} - {e.Content}");
                        break;
                }
                    LogDebug($"Current state is {_currentState}, isAuthenticated={_isAuthenticated}");
            }
            catch (Exception ex)
            {
                LogDebug($"Error occurred during message parsing: {ex.Message}");
            }
        }
        

        private async void HandleError(object? sender, ErrorEventArgs e)
        {
            Console.WriteLine($"ERROR: {e.Message}");
            if (e.Exception != null)
            {
                LogDebug($"Exception: {e.Exception}");
            }
            
            try
            {
                await _protocol.SendErrorAsync(_displayName, e.Message!);
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to send error message: {ex.Message}");
            }
            
            _currentState = ClientState.End;
            
            try
            {
                await GracefulShutdownAsync(1);
            }
            catch (Exception ex)
            {
                LogDebug($"Error during shutdown: {ex.Message}");
                Environment.Exit(1);
            }
        }

        private void PrintHelp()
        {
            Console.WriteLine("Available commands:");
            Console.WriteLine("  /auth <username> <secret> <displayname> - Authenticate with server");
            Console.WriteLine("  /join <channel> - Join a channel");
            Console.WriteLine("  /rename <displayname> - Change your display name");
            Console.WriteLine("  /help - Show this help message");
            Console.WriteLine("Press Ctrl+C or Ctrl+D to gracefully terminate the client");
        }
        
        private void LogDebug(string message)
        {
            if (_Logging)
            {
                Console.Error.WriteLine($"CLIENT DEBUG: {message}");
            }
        }
        
        private void HandleMalformedMessage(string message)
        {
            string errorMessage = $"Malformed message from server : {message}";
            HandleError(this, new ErrorEventArgs(errorMessage, 
                new FormatException("Malformed server message")));
        }
        
        #region Validation
        
        private bool ValidateUsername(string? username)
        {
            if (string.IsNullOrEmpty(username) || username.Length > 20)
                return false;
    
            return System.Text.RegularExpressions.Regex.IsMatch(username, "^[a-zA-Z0-9_-]+$");
        }

        private bool ValidateChannelId(string? channelId)
        {
            if (string.IsNullOrEmpty(channelId) || channelId.Length > 20)
                return false;
    
            return System.Text.RegularExpressions.Regex.IsMatch(channelId, "^[a-zA-Z0-9_.-]+$");
        }

        private bool ValidateSecret(string? secret)
        {
            if (string.IsNullOrEmpty(secret) || secret.Length > 128)
                return false;
    
            return System.Text.RegularExpressions.Regex.IsMatch(secret, "^[a-zA-Z0-9_-]+$");
        }

        private bool ValidateDisplayName(string? displayName)
        {
            if (string.IsNullOrEmpty(displayName) || displayName.Length > 20)
                return false;
            
            return System.Text.RegularExpressions.Regex.IsMatch(displayName, "^[\x21-\x7E]+$");
        }

        private bool ValidateMessageContent(string? message)
        {
            LogDebug($"Validating Message: {message}");
            if (message is { Length: > 60000 } ||
                string.IsNullOrEmpty(message))
                return false;
            
            return System.Text.RegularExpressions.Regex.IsMatch(message, "^[\x0A\x20-\x7E]*$");
        }
        

        #endregion
    }
    
    public enum ClientState
    {
        Init,
        Auth,
        Open,
        Join,
        End
    }
} 
